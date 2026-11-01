#!/usr/bin/env python3
"""Plays each file of a playback matrix in a headless browser and records what happened.

Usage: probe.py --browser chromium|firefox|webkit --manifest manifest.json --media DIR --out results.jsonl

The manifest (written by `PlaybackCheck describe`) is a JSON array of
  { "file": "x.mp4", "contentType": "video/mp4", "mime": "video/mp4; codecs=\\"avc1.640014,mp4a.40.2\\"",
    "modes": ["progressive", "mediasource"], "hasVideo": true }
Each file is served from a local HTTP server that honours Range requests, loaded through a <video> (or <audio>) element, and for
"mediasource" additionally appended whole to a MediaSource SourceBuffer of the same type string. One JSON object is written per
(file, mode) with: canPlayType, isTypeSupported, whether currentTime advanced, decoded video frames, audio evidence and the error.

Playwright is only driven through its public Python API; its installed files are never read.
"""
import argparse
import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PAGE = b"<!doctype html><meta charset=utf-8><title>probe</title><body></body>"


def make_handler(media_dir, types):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *a):  # quiet
            pass

        def do_GET(self):
            name = self.path.split("?")[0].lstrip("/")
            if name == "" or name == "probe.html":
                self.send_response(200)
                self.send_header("Content-Type", "text/html")
                self.send_header("Content-Length", str(len(PAGE)))
                self.end_headers()
                self.wfile.write(PAGE)
                return
            path = os.path.join(media_dir, os.path.basename(name))
            if not os.path.isfile(path):
                self.send_error(404)
                return
            size = os.path.getsize(path)
            start, end = 0, size - 1
            status = 200
            m = re.match(r"bytes=(\d*)-(\d*)", self.headers.get("Range", ""))
            if m:
                status = 206
                if m.group(1) == "":
                    start = max(0, size - int(m.group(2)))
                else:
                    start = int(m.group(1))
                    if m.group(2):
                        end = min(end, int(m.group(2)))
            if start >= size:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            self.send_response(status)
            self.send_header("Content-Type", types.get(name, "application/octet-stream"))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(end - start + 1))
            if status == 206:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.end_headers()
            try:
                with open(path, "rb") as f:
                    f.seek(start)
                    left = end - start + 1
                    while left > 0:
                        chunk = f.read(min(65536, left))
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                        left -= len(chunk)
            except (BrokenPipeError, ConnectionResetError):
                pass

    return Handler


COMMON_JS = r"""
const sleep = ms => new Promise(r => setTimeout(r, ms));
function evidence(el, res) {
  res.currentTime = el.currentTime;
  res.readyState = el.readyState;
  res.duration = isFinite(el.duration) ? el.duration : null;
  res.videoWidth = el.videoWidth || 0;
  res.videoHeight = el.videoHeight || 0;
  if (el.getVideoPlaybackQuality) { const q = el.getVideoPlaybackQuality(); res.totalVideoFrames = q.totalVideoFrames; res.droppedVideoFrames = q.droppedVideoFrames; }
  if ('webkitDecodedFrameCount' in el) res.webkitDecodedFrameCount = el.webkitDecodedFrameCount;
  if ('mozDecodedFrames' in el) res.mozDecodedFrames = el.mozDecodedFrames;
  if ('webkitAudioDecodedByteCount' in el) res.audioDecodedBytes = el.webkitAudioDecodedByteCount;
  if ('mozHasAudio' in el) res.mozHasAudio = el.mozHasAudio;
  if (el.audioTracks) res.audioTrackCount = el.audioTracks.length;
  if (el.videoTracks) res.videoTrackCount = el.videoTracks.length;
  if (el.error) res.error = { code: el.error.code, message: el.error.message };
}
// Audio is proven by the signal itself: the media element feeds an analyser and the peak level of the generated tone is read back,
// because a browser may play the video of a file and silently drop an audio track it cannot decode.
async function run(el, res, limitMs, wantAudio) {
  el.playsInline = true;
  let analyser = null;
  if (wantAudio) {
    try {
      const ctx = new AudioContext();
      await ctx.resume();
      const src = ctx.createMediaElementSource(el);
      analyser = ctx.createAnalyser();
      src.connect(analyser);
      const sink = ctx.createGain(); sink.gain.value = 0; analyser.connect(sink); sink.connect(ctx.destination);
      res.audioContextState = ctx.state;
    } catch (e) { res.audioGraphError = e.name + ': ' + e.message; }
  }
  el.muted = !wantAudio || !analyser;
  try { await el.play(); } catch (e) { res.playError = e.name + ': ' + e.message; }
  const buf = analyser ? new Float32Array(analyser.fftSize) : null;
  let peak = 0;
  const deadline = performance.now() + limitMs;
  while (performance.now() < deadline && !el.error && !(el.currentTime > 0.6) && !el.ended) {
    await sleep(50);
    if (analyser) { analyser.getFloatTimeDomainData(buf); for (const v of buf) peak = Math.max(peak, Math.abs(v)); }
  }
  await sleep(200);
  if (analyser) { analyser.getFloatTimeDomainData(buf); for (const v of buf) peak = Math.max(peak, Math.abs(v)); res.audioPeak = peak; }
  evidence(el, res);
}
"""

PROGRESSIVE_JS = "async ({url, mime, tag, wantAudio}) => {" + COMMON_JS + r"""
  const el = document.createElement(tag); document.body.appendChild(el);
  const res = { canPlayType: el.canPlayType(mime) };
  el.preload = 'auto';
  el.src = url;
  await run(el, res, 8000, wantAudio);
  return res;
}"""

MSE_JS = "async ({url, mime, tag, wantAudio}) => {" + COMMON_JS + r"""
  const MS = window.MediaSource || window.ManagedMediaSource;
  const res = {};
  res.hasMediaSource = !!MS;
  if (!MS) return res;
  res.isTypeSupported = MS.isTypeSupported(mime);
  if (!res.isTypeSupported) return res;
  const el = document.createElement(tag); document.body.appendChild(el);
  const ms = new MS();
  if (window.ManagedMediaSource && ms instanceof window.ManagedMediaSource) { el.disableRemotePlayback = true; }
  el.src = URL.createObjectURL(ms);
  await new Promise(r => ms.addEventListener('sourceopen', r, { once: true }));
  let sb;
  try { sb = ms.addSourceBuffer(mime); } catch (e) { res.addSourceBufferError = e.name + ': ' + e.message; return res; }
  const data = await (await fetch(url)).arrayBuffer();
  const done = new Promise(r => { sb.addEventListener('updateend', () => r('updateend'), { once: true }); sb.addEventListener('error', () => r('error'), { once: true }); });
  try { sb.appendBuffer(data); res.append = await done; } catch (e) { res.append = 'throw ' + e.name + ': ' + e.message; }
  try { if (ms.readyState === 'open') ms.endOfStream(); } catch (e) { res.endOfStreamError = e.name; }
  await run(el, res, 8000, wantAudio);
  return res;
}"""


WATCH = {"current": None}


def start_watchdog(out):
    """A page can freeze the whole browser process, which no timer inside the page survives. If one observation takes longer
    than 70 s it is recorded as hung and the process exits with status 3; run it again with --resume to carry on."""
    def loop():
        while True:
            time.sleep(5)
            cur = WATCH["current"]
            if cur and time.time() - cur[2] > 70:
                out.write(json.dumps({"browser": cur[3], "file": cur[0], "mode": cur[1], "mime": "", "timedOut": True, "hung": True}) + "\n")
                out.flush()
                os._exit(3)
    threading.Thread(target=loop, daemon=True).start()


def observe(browser, base, m, mode, browser_name, version):
    ctx = browser.new_context()
    page = ctx.new_page()
    page.set_default_timeout(15000)
    rec = {"browser": browser_name, "browserVersion": version, "file": m["file"], "mode": mode, "mime": m["mime"]}
    try:
        page.goto(base + "/probe.html")
        page.mouse.click(5, 5)  # a user gesture, so unmuted playback is allowed
        tag = "video" if m.get("hasVideo") else "audio"
        want_audio = bool(m.get("hasAudio"))
        if browser_name == "webkit" and (mode == "mediasource" or m["contentType"].endswith("/webm")):
            # WebKit's WebAudio tap delivers silence for MediaSource and WebM playback even while the audio plays,
            # so for those rows only the clock and the video frames are checked.
            want_audio = False
            rec["audioNotChecked"] = True
        url = f"{base}/{m['file']}"
        js = PROGRESSIVE_JS if mode == "progressive" else MSE_JS
        # A page that never answers (a play() promise that stays pending) must not stall the run.
        rec.update(page.evaluate("(a) => Promise.race([(" + js + ")(a), new Promise(r => setTimeout(() => r({ timedOut: true }), 25000))])",
                                 {"url": url, "mime": m["mime"], "tag": tag, "wantAudio": want_audio}))
    except Exception as e:  # a crashed page is an observation too
        rec["harnessError"] = f"{type(e).__name__}: {str(e)[:300]}"
    try:
        ctx.close()
    except Exception:
        pass
    return rec


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--browser", required=True, choices=["chromium", "firefox", "webkit"])
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--media", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="")
    ap.add_argument("--resume", action="store_true", help="append to --out and skip the observations already in it")
    ap.add_argument("--executable", default="", help="drive an installed browser (for example Google Chrome) instead of Playwright's own build")
    args = ap.parse_args()

    home = os.environ.get("ONEMEDIA_HOME", os.path.expanduser("~/onemedia"))
    libs = os.path.join(home, "oracles", "playwright", "libs", "usr", "lib", "x86_64-linux-gnu")
    if os.path.isdir(libs):
        os.environ["LD_LIBRARY_PATH"] = libs + ":" + os.environ.get("LD_LIBRARY_PATH", "")
    os.environ.setdefault("PLAYWRIGHT_BROWSERS_PATH", os.path.join(home, "oracles", "playwright", "browsers"))

    from playwright.sync_api import sync_playwright

    manifest = json.load(open(args.manifest))
    if args.only:
        manifest = [m for m in manifest if re.search(args.only, m["file"])]
    types = {m["file"]: m["contentType"] for m in manifest}
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(args.media, types))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}"

    done = set()
    if args.resume and os.path.exists(args.out):
        for line in open(args.out):
            r = json.loads(line)
            done.add((r["file"], r["mode"]))

    with sync_playwright() as p, open(args.out, "a" if args.resume else "w") as out:
        start_watchdog(out)
        bt = getattr(p, args.browser)
        launch = {}
        if args.browser == "chromium":
            launch["args"] = ["--autoplay-policy=no-user-gesture-required"]
        if args.browser == "firefox":
            launch["firefox_user_prefs"] = {"media.autoplay.default": 0, "media.autoplay.blocking_policy": 0}
        if args.executable:
            launch["executable_path"] = args.executable
        browser = bt.launch(headless=True, **launch)
        version = browser.version
        observed = 0
        for m in manifest:
            for mode in m["modes"]:
                if (m["file"], mode) in done:
                    continue
                WATCH["current"] = (m["file"], mode, time.time(), args.browser)
                for attempt in range(2):  # a stalled page is tried once more before it counts as an observation
                    observed += 1
                    if observed % 12 == 0:
                        # WebKit on macOS slows down after a few dozen pages in one process; a fresh process is quick.
                        browser.close()
                        browser = bt.launch(headless=True, **launch)
                    rec = observe(browser, base, m, mode, args.browser, version)
                    if "harnessError" not in rec and not rec.get("timedOut"):
                        break
                    rec["attempts"] = attempt + 1
                WATCH["current"] = None
                print(m["file"], mode, file=sys.stderr, flush=True)
                out.write(json.dumps(rec) + "\n")
                out.flush()
        browser.close()
    server.shutdown()


if __name__ == "__main__":
    sys.exit(main())

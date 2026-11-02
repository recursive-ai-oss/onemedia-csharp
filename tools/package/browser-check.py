#!/usr/bin/env python3
"""Plays packaged HLS and DASH output in headless browsers through hls.js, dash.js and (WebKit) native HLS.

Usage: browser-check.py --browser chromium|firefox|webkit [--executable PATH] --root DIR --players DIR --out results.jsonl [--only REGEX]

DIR holds one packaged presentation per sub-directory (master.m3u8 and/or manifest.mpd inside). Each is served from a local HTTP
server with Range support. For every presentation and player the page plays to the end muted at 2x, seeks to the middle once the
clock has passed one second, and must still reach the end. One JSON line per observation: player, whether it ended, the clock after
the seek, decoded video frames, the first fatal error. Playwright is only driven through its public API; the hls.js and dash.js bundles
are loaded by the page and never opened.
"""
import argparse, json, os, re, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TYPES = {".m3u8": "application/vnd.apple.mpegurl", ".mpd": "application/dash+xml", ".m4s": "video/iso.segment", ".mp4": "video/mp4",
         ".vtt": "text/vtt", ".ts": "video/mp2t", ".js": "text/javascript", ".html": "text/html"}
PAGE = b"<!doctype html><meta charset=utf-8><title>player</title><body></body>"


def handler(root, players):
    class H(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *a):
            pass

        def do_GET(self):
            name = self.path.split("?")[0].lstrip("/")
            if name in ("", "page.html"):
                data = PAGE; ctype = "text/html"; path = None
            else:
                path = os.path.join(players, name[len("players/"):]) if name.startswith("players/") else os.path.normpath(os.path.join(root, name))
                if not path.startswith(os.path.normpath(root if not name.startswith("players/") else players)) or not os.path.isfile(path):
                    self.send_error(404); return
                ctype = TYPES.get(os.path.splitext(path)[1], "application/octet-stream")
            size = len(data) if path is None else os.path.getsize(path)
            start, end, status = 0, size - 1, 200
            m = re.match(r"bytes=(\d*)-(\d*)", self.headers.get("Range", ""))
            if m and path is not None:
                status = 206
                if m.group(1) == "": start = max(0, size - int(m.group(2)))
                else:
                    start = int(m.group(1))
                    if m.group(2): end = min(end, int(m.group(2)))
            self.send_response(status)
            self.send_header("Content-Type", ctype); self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(end - start + 1))
            if status == 206: self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.end_headers()
            try:
                if path is None: self.wfile.write(data)
                else:
                    with open(path, "rb") as f:
                        f.seek(start); left = end - start + 1
                        while left > 0:
                            c = f.read(min(65536, left))
                            if not c: break
                            self.wfile.write(c); left -= len(c)
            except (BrokenPipeError, ConnectionResetError):
                pass
    return H


JS = r"""async ({player, url}) => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const res = { player };
  const el = document.createElement('video'); el.muted = true; el.playsInline = true; document.body.appendChild(el);
  let fatal = null, handle = null;
  if (player === 'hlsjs') {
    if (!window.Hls || !Hls.isSupported()) { res.unsupported = 'Hls.isSupported() false'; return res; }
    handle = new Hls();
    handle.on(Hls.Events.ERROR, (e, d) => { if (d.fatal && !fatal) fatal = d.type + '/' + d.details; });
    handle.loadSource(url); handle.attachMedia(el);
  } else if (player === 'dashjs') {
    if (!window.dashjs) { res.unsupported = 'dashjs missing'; return res; }
    handle = dashjs.MediaPlayer().create();
    handle.on('error', e => { if (!fatal) fatal = (e.error && (e.error.code + ' ' + e.error.message)) || 'error'; });
    handle.initialize(el, url, false);
  } else { el.src = url; }
  el.playbackRate = 2;
  el.addEventListener('error', () => { if (!fatal && el.error) fatal = 'media ' + el.error.code + ' ' + el.error.message; });
  try { await el.play(); } catch (e) { res.playError = e.name; }
  let t0 = performance.now();
  while (performance.now() - t0 < 30000 && !fatal && !(el.currentTime > 1)) await sleep(50);
  res.beforeSeek = el.currentTime;
  res.duration = isFinite(el.duration) ? el.duration : null;
  if (res.duration && !fatal) {
    const mid = res.duration / 2; el.currentTime = mid; res.seekTarget = mid;
    t0 = performance.now();
    while (performance.now() - t0 < 20000 && !fatal && !(el.currentTime > mid + 0.5)) await sleep(50);
    res.afterSeek = el.currentTime;
  }
  t0 = performance.now();
  while (performance.now() - t0 < 40000 && !fatal && !el.ended && !(res.duration && el.currentTime >= res.duration - 0.3)) await sleep(100);
  res.ended = el.ended || (res.duration && el.currentTime >= res.duration - 0.3);
  res.endTime = el.currentTime;
  res.width = el.videoWidth;
  if (el.getVideoPlaybackQuality) res.frames = el.getVideoPlaybackQuality().totalVideoFrames;
  if ('webkitDecodedFrameCount' in el) res.webkitFrames = el.webkitDecodedFrameCount;
  res.fatal = fatal;
  res.pass = !!(res.ended && !fatal && res.afterSeek !== undefined && res.afterSeek >= res.seekTarget);
  try { if (handle) { handle.destroy ? handle.destroy() : handle.reset(); } } catch (e) {}
  return res;
}"""


WATCH = {"current": None}


def start_watchdog(out):
    """A page can freeze the whole browser, which no timer in the page survives: after 170 s on one observation it is recorded as hung and the
    process exits with status 3; run it again with --resume to carry on."""
    def loop():
        while True:
            time.sleep(5)
            cur = WATCH["current"]
            if cur and time.time() - cur[3] > 170:
                out.write(json.dumps({"browser": cur[4], "presentation": cur[0], "player": cur[1], "hung": True, "pass": False}) + "\n")
                out.flush()
                os._exit(3)
    threading.Thread(target=loop, daemon=True).start()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--browser", required=True, choices=["chromium", "firefox", "webkit"])
    ap.add_argument("--executable", default="")
    ap.add_argument("--root", required=True)
    ap.add_argument("--players", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="")
    ap.add_argument("--resume", action="store_true", help="append to --out and skip what it already holds")
    a = ap.parse_args()
    home = os.environ.get("ONEMEDIA_HOME", os.path.expanduser("~/onemedia"))
    libs = os.path.join(home, "oracles", "playwright", "libs", "usr", "lib", "x86_64-linux-gnu")
    if os.path.isdir(libs): os.environ["LD_LIBRARY_PATH"] = libs + ":" + os.environ.get("LD_LIBRARY_PATH", "")
    os.environ.setdefault("PLAYWRIGHT_BROWSERS_PATH", os.path.join(home, "oracles", "playwright", "browsers"))
    from playwright.sync_api import sync_playwright

    server = ThreadingHTTPServer(("127.0.0.1", 0), handler(a.root, a.players))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    jobs = []
    for d in sorted(os.listdir(a.root)):
        if a.only and not re.search(a.only, d): continue
        if os.path.exists(os.path.join(a.root, d, "master.m3u8")):
            jobs.append((d, "hlsjs", f"{base}/{d}/master.m3u8"))
            if a.browser == "webkit": jobs.append((d, "native-hls", f"{base}/{d}/master.m3u8"))
        if os.path.exists(os.path.join(a.root, d, "manifest.mpd")):
            jobs.append((d, "dashjs", f"{base}/{d}/manifest.mpd"))
    done = set()
    if a.resume and os.path.exists(a.out):
        for line in open(a.out):
            r = json.loads(line)
            done.add((r["presentation"], r["player"]))
    with sync_playwright() as p, open(a.out, "a" if a.resume else "w") as out:
        start_watchdog(out)
        launch = {}
        if a.browser == "chromium": launch["args"] = ["--autoplay-policy=no-user-gesture-required"]
        if a.browser == "firefox": launch["firefox_user_prefs"] = {"media.autoplay.default": 0, "media.autoplay.blocking_policy": 0}
        if a.executable: launch["executable_path"] = a.executable
        browser = getattr(p, a.browser).launch(headless=True, **launch)
        for i, (d, player, url) in enumerate(jobs):
            if (d, player) in done:
                continue
            WATCH["current"] = (d, player, url, time.time(), a.browser)
            rec = {"browser": a.browser, "version": browser.version, "presentation": d, "player": player}
            ctx = browser.new_context(); page = ctx.new_page(); page.set_default_timeout(20000)
            try:
                page.goto(base + "/page.html")
                page.mouse.click(5, 5)
                if player == "hlsjs": page.add_script_tag(url=base + "/players/hls.min.js")
                if player == "dashjs": page.add_script_tag(url=base + "/players/dash.all.min.js")
                rec.update(page.evaluate("(a) => Promise.race([(" + JS + ")(a), new Promise(r => setTimeout(() => r({ timedOut: true }), 120000))])",
                                         {"player": player, "url": url}))
            except Exception as e:
                rec["harnessError"] = f"{type(e).__name__}: {str(e)[:300]}"
            try: ctx.close()
            except Exception: pass
            WATCH["current"] = None
            out.write(json.dumps(rec) + "\n"); out.flush()
            print(d, player, rec.get("pass"), rec.get("fatal") or rec.get("harnessError") or rec.get("unsupported") or "", file=sys.stderr, flush=True)
            if (i + 1) % 10 == 0:
                browser.close(); browser = getattr(p, a.browser).launch(headless=True, **launch)
        browser.close()
    server.shutdown()


if __name__ == "__main__":
    sys.exit(main())

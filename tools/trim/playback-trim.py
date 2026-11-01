#!/usr/bin/env python3
"""Plays trimmed clips in headless browsers and reports which frame each one shows first, in the middle and last.

The source video encodes its own frame number in ten black/white blocks (bit b at x = 32*b + 16), so the frame a browser shows is read back from a
canvas, whatever the codec did to the pixels. A clip cut from frame F to frame T (exclusive) must show F at time 0 and T-1 at its last frame.

  playback-trim.py --browser chromium|firefox|webkit --oracles DIR --tool "dotnet .../onemedia.dll" --work DIR [--codecs h264,vp9]

Playwright is driven only through its public Python API; its installed files are never read.
Prints one TSV line per clip: browser, clip, expected first/mid/last frame, shown first/mid/last, duration, verdict.
"""
import argparse
import json
import os
import subprocess
import sys
import threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

FPS = 25
CASES = [
    # name, video codec args, from frame, to frame, keyframe mode
    ("h264-acc", ["-c:v", "libx264", "-preset", "veryfast", "-g", "48", "-bf", "2", "-sc_threshold", "0"], 53, 143, False),
    ("h264-key", ["-c:v", "libx264", "-preset", "veryfast", "-g", "48", "-bf", "2", "-sc_threshold", "0"], 53, 143, True),
    ("vp9-acc", ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", "48", "-lag-in-frames", "0"], 53, 143, False),
    ("hevc-acc", ["-c:v", "libx265", "-preset", "veryfast", "-x265-params", "keyint=48:min-keyint=48:bframes=2:no-open-gop=1:scenecut=0:log-level=error", "-tag:v", "hvc1"], 53, 143, False),
    ("av1-acc", ["-c:v", "libaom-av1", "-cpu-used", "8", "-g", "48", "-lag-in-frames", "0"], 53, 143, False),
]

READ = """
async ({url, frames, fps}) => {
  const v = document.createElement('video');
  v.muted = true; v.preload = 'auto'; v.playsInline = true; v.src = url; document.body.append(v);
  await new Promise((res, rej) => { v.onloadeddata = res; v.onerror = () => rej(new Error('media error ' + (v.error && v.error.code))); setTimeout(() => rej(new Error('timeout')), 15000); });
  const c = document.createElement('canvas'); c.width = 320; c.height = 180;
  const g = c.getContext('2d', {willReadFrequently: true});
  const read = () => { g.drawImage(v, 0, 0, 320, 180); const d = g.getImageData(0, 0, 320, 180).data; let n = 0;
    for (let b = 0; b < 10; b++) { const i = (32 * 320 + b * 32 + 16) * 4; if ((d[i] + d[i+1] + d[i+2]) / 3 > 128) n |= 1 << b; } return n; };
  const seek = (t) => new Promise((res) => { v.onseeked = res; v.currentTime = t; });
  const out = {duration: v.duration, shown: []};
  for (const k of frames) { await seek(k < 0 ? v.duration - 0.5 / fps : (k + 0.5) / fps); await new Promise(r => setTimeout(r, 120)); out.shown.push(read()); }
  return out;
}
"""


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--browser", required=True)
    p.add_argument("--oracles", required=True)
    p.add_argument("--tool", required=True)
    p.add_argument("--work", required=True)
    p.add_argument("--codecs", default="")
    a = p.parse_args()
    ffmpeg = os.path.join(a.oracles, "ffmpeg")
    os.makedirs(a.work, exist_ok=True)

    clips = []
    for name, vargs, f, t, key in CASES:
        if a.codecs and name.split("-")[0] not in a.codecs.split(","):
            continue
        src = os.path.join(a.work, name + "-src.mp4")
        if not os.path.exists(src):
            gen = [ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=gray:size=320x180:rate={FPS},geq=lum='if(lt(Y,64),255*mod(floor(N/pow(2,floor(X/32))),2),128)':cb=128:cr=128",
                   "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "12", "-pix_fmt", "yuv420p", *vargs, "-c:a", "aac", "-b:a", "64k", src]
            r = run(gen)
            if r.returncode:
                print(f"SKIP\t{name}\tgenerate failed: {r.stderr.strip()[-120:]}")
                continue
        out = os.path.join(a.work, name + ".mp4")
        cmd = a.tool.split() + ["trim", src, out, "--from", f"{f / FPS:.7f}", "--to", f"{t / FPS:.7f}"] + (["--keyframe"] if key else [])
        r = run(cmd)
        if r.returncode:
            print(f"REFUSED\t{name}\t{r.stderr.strip()[:150]}")
            continue
        # The keyframe at or before the requested start (GOP of 48 frames).
        first = (f // 48) * 48 if key else f
        clips.append((name, out, first, t - 1))

    handler = lambda *x, **k: SimpleHTTPRequestHandler(*x, directory=a.work, **k)
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    port = server.server_address[1]

    from playwright.sync_api import sync_playwright
    bad = 0
    with sync_playwright() as pw:
        browser = getattr(pw, a.browser).launch()
        for name, path, first, last in clips:
            page = browser.new_page()
            page.goto(f"http://127.0.0.1:{port}/")
            length = last - first + 1
            mid = length // 2
            verdict, detail = "OK", ""
            try:
                res = page.evaluate(READ, {"url": f"http://127.0.0.1:{port}/{os.path.basename(path)}", "frames": [0, mid, -1], "fps": FPS})
                shown = res["shown"]
                want = [first, first + mid, last]
                if shown != want:
                    verdict = "DIFFERENT"
                detail = f"dur={res['duration']:.3f}"
            except Exception as e:  # noqa: BLE001
                shown, want, verdict = [], [first, first + mid, last], "ERROR"
                detail = str(e)[:100]
            bad += verdict != "OK"
            print(f"{verdict}\t{a.browser}\t{name}\twant={want}\tshown={shown}\t{detail}", flush=True)
            page.close()
        browser.close()
    server.shutdown()
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

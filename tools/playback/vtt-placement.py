#!/usr/bin/env python3
"""Shows where each Playwright browser (and an installed Chrome, with --executable) draws a WebVTT cue: loads a black video with the cue file as a
<track>, takes a screenshot with the cue showing and reports the vertical centre of the bright text pixels as a fraction of the video height
(0 = top edge, 1 = bottom edge).

Usage: vtt-placement.py DIR VIDEO CUEFILE... [--executable /path/to/chrome]
DIR is served over local HTTP and holds VIDEO (a black video of at least two seconds) and the cue files; each cue file should show text from 0 s to 2 s.
Prints one JSON line per browser and file.
"""
import http.server
import json
import os
import sys
import threading

args = sys.argv[1:]
exe = ""
if "--executable" in args:
    i = args.index("--executable")
    exe = args[i + 1]
    del args[i:i + 2]
d, video, cues = args[0], args[1], args[2:]

home = os.environ.get("ONEMEDIA_HOME", os.path.expanduser("~/onemedia"))
libs = os.path.join(home, "oracles", "playwright", "libs", "usr", "lib", "x86_64-linux-gnu")
if os.path.isdir(libs):
    os.environ["LD_LIBRARY_PATH"] = libs + ":" + os.environ.get("LD_LIBRARY_PATH", "")
os.environ.setdefault("PLAYWRIGHT_BROWSERS_PATH", os.path.join(home, "oracles", "playwright", "browsers"))

from playwright.sync_api import sync_playwright  # noqa: E402

PAGE = """<!doctype html><body style="margin:0;background:#000">
<video id=v muted playsinline preload=auto width=640 height=360 style="display:block"><track id=t kind=subtitles default src="%s"></video></body>"""

# Waits until the cue is the active cue, then returns once a frame has been painted.
WAIT = """async () => {
  const v = document.getElementById('v');
  await new Promise(r => { if (v.readyState >= 2) r(); else v.onloadeddata = r; setTimeout(r, 8000); });
  for (const t of v.textTracks) t.mode = 'showing';
  await new Promise(r => setTimeout(r, 500));
  v.currentTime = 1.0;
  await new Promise(r => { v.onseeked = r; setTimeout(r, 4000); });
  await new Promise(r => setTimeout(r, 1000));
  const t = v.textTracks[0];
  return {cues: t && t.cues ? t.cues.length : 0, active: t && t.activeCues ? t.activeCues.length : 0,
          line: t && t.cues && t.cues.length ? String(t.cues[0].line) : null, lineAlign: t && t.cues && t.cues.length ? t.cues[0].lineAlign : null};
}"""


def bright_rows(png):
    # Decode a PNG without third-party packages: zlib + unfiltering for 8-bit RGB(A).
    import struct
    import zlib
    assert png[:8] == b"\x89PNG\r\n\x1a\n"
    pos = 8
    idat = b""
    while pos < len(png):
        n, typ = struct.unpack(">I4s", png[pos:pos + 8])
        data = png[pos + 8:pos + 8 + n]
        if typ == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", data[:10])
        elif typ == b"IDAT":
            idat += data
        pos += 12 + n
    bpp = {2: 3, 6: 4}[ctype]
    raw = zlib.decompress(idat)
    stride = w * bpp
    prev = bytearray(stride)
    rows = []
    p = 0
    for y in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        prev = line
        rows.append(line)
    ys = []
    for y, line in enumerate(rows):
        for x in range(0, stride, bpp):
            if line[x] > 200 and line[x + 1] > 200 and line[x + 2] > 200:
                ys.append(y)
                break
    return h, ys


def main():
    os.chdir(d)
    handler = http.server.SimpleHTTPRequestHandler
    handler.log_message = lambda *a: None
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    with sync_playwright() as p:
        runs = [("chrome-installed", p.chromium, {"executable_path": exe})] if exe else [(n, getattr(p, n), {}) for n in ("chromium", "firefox", "webkit")]
        for name, kind, kw in runs:
            try:
                b = kind.launch(**kw)
            except Exception as e:
                print(json.dumps({"browser": name, "error": str(e)[:200]}))
                continue
            for cue in cues:
                with open("page-" + cue + ".html", "w") as f:
                    f.write(PAGE % cue)
                page = b.new_page(viewport={"width": 640, "height": 360})
                page.goto(f"http://127.0.0.1:{port}/page-{cue}.html")
                page.evaluate(f"document.getElementById('v').src = 'http://127.0.0.1:{port}/{video}'")
                try:
                    r = page.evaluate(WAIT)
                    h, ys = bright_rows(page.screenshot())
                    r["centre"] = round(((ys[0] + ys[-1]) / 2) / h, 3) if ys else None
                except Exception as e:
                    r = {"error": str(e)[:200]}
                r.update(browser=name, version=b.version, file=cue)
                print(json.dumps(r))
                page.close()
            b.close()
    srv.shutdown()


main()

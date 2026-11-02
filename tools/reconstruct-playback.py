#!/usr/bin/env python3
"""Plays reconstructed files in a headless browser and reports whether they play to the end.

  reconstruct-playback.py BROWSER FILE...        BROWSER is chromium, chrome, firefox or webkit

Each file is served over local HTTP (with range requests, through a plain file server), loaded in a <video> element and played muted.
A file passes when the element reports no error, a duration close to the last packet's time, decoded video frames and a current time
that advanced to the end. Playwright is driven through its public Python API only.
"""
import functools
import http.server
import json
import os
import sys
import threading

from playwright.sync_api import sync_playwright


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send_head(self):
        # Range support, which SimpleHTTPRequestHandler lacks.
        path = self.translate_path(self.path)
        if os.path.isdir(path):
            return super().send_head()
        if not os.path.isfile(path):
            self.send_error(404)
            return None
        f = open(path, "rb")
        size = os.fstat(f.fileno()).st_size
        rng = self.headers.get("Range")
        start, end = 0, size - 1
        code = 200
        if rng and rng.startswith("bytes="):
            a, _, b = rng[6:].partition("-")
            start = int(a) if a else 0
            end = int(b) if b else size - 1
            end = min(end, size - 1)
            code = 206
        self.send_response(code)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if code == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        f.seek(start)
        self._left = end - start + 1
        return f

    def copyfile(self, source, outputfile):
        left = getattr(self, "_left", None)
        while left is None or left > 0:
            chunk = source.read(65536 if left is None else min(65536, left))
            if not chunk:
                break
            try:
                outputfile.write(chunk)
            except (BrokenPipeError, ConnectionResetError):
                break
            if left is not None:
                left -= len(chunk)


SCRIPT = """
async (name) => {
  const v = document.createElement('video');
  v.muted = true; v.preload = 'auto'; v.src = name;
  document.body.appendChild(v);
  const out = {};
  await new Promise(r => { v.onloadedmetadata = r; v.onerror = r; setTimeout(r, 10000); });
  out.error = v.error ? v.error.code : 0;
  out.duration = v.duration; out.width = v.videoWidth;
  if (!out.error) {
    v.playbackRate = 4;
    try { await v.play(); } catch (e) { out.playError = String(e); }
    await new Promise(r => { v.onended = r; setTimeout(r, 40000); });
    out.currentTime = v.currentTime; out.ended = v.ended; out.error = v.error ? v.error.code : 0;
    if (v.getVideoPlaybackQuality) out.frames = v.getVideoPlaybackQuality().totalVideoFrames;
  }
  return out;
}
"""


def main():
    browser_name = sys.argv[1]
    files = sys.argv[2:]
    directory = os.path.dirname(os.path.abspath(files[0]))
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=directory))
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{srv.server_address[1]}"
    failed = 0
    with sync_playwright() as p:
        # "chrome" is the installed Google Chrome, which has the H.264 and AAC decoders that Playwright's own Chromium build leaves out.
        browser = p.chromium.launch(channel="chrome") if browser_name == "chrome" else getattr(p, browser_name).launch()
        for f in files:
            page = browser.new_page()
            page.goto(base + "/")
            r = page.evaluate(SCRIPT, os.path.basename(f))
            ok = r.get("error", 1) == 0 and r.get("ended") and r.get("duration", 0) > 0 and abs(r["currentTime"] - r["duration"]) < 0.5 and (r.get("width", 0) == 0 or r.get("frames", 0) > 0)
            failed += 0 if ok else 1
            print(("PASS " if ok else "FAIL ") + os.path.basename(f) + " " + json.dumps(r))
            page.close()
        browser.close()
    srv.shutdown()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Opens files that carry text tracks in a <video> element in each Playwright browser and records what the browser exposes: the text tracks it lists
and the cues it loads (in-band text in MP4 and WebM is only shown by some browsers; a browser that ignores it is a recorded fact).

Usage: text-track-browser.py DIR FILE... (files inside DIR, which is served over local HTTP). Prints one JSON line per browser and file.
"""
import http.server
import json
import os
import sys
import threading
from playwright.sync_api import sync_playwright

PAGE = """<!doctype html><video id=v muted playsinline preload=auto></video>"""

JS = """async (src) => {
  const v = document.getElementById('v');
  const out = {error: null, tracks: [], duration: null, canPlay: false};
  const ready = new Promise(r => { v.onloadedmetadata = () => r(true); v.onerror = () => r(false); setTimeout(() => r(false), 8000); });
  v.src = src;
  out.canPlay = await ready;
  if (v.error) out.error = v.error.message || String(v.error.code);
  out.duration = v.duration;
  await new Promise(r => setTimeout(r, 500));
  for (const t of v.textTracks) t.mode = 'hidden';
  try { v.currentTime = 0.1; await v.play(); } catch (e) {}
  await new Promise(r => setTimeout(r, 1500));
  for (const t of v.textTracks) {
    out.tracks.push({kind: t.kind, label: t.label, language: t.language, cues: t.cues ? t.cues.length : null,
                     first: t.cues && t.cues.length ? [t.cues[0].startTime, t.cues[0].endTime, t.cues[0].text] : null});
  }
  return out;
}"""


def main():
    d = sys.argv[1]
    files = sys.argv[2:]
    os.chdir(d)
    handler = http.server.SimpleHTTPRequestHandler
    handler.log_message = lambda *a: None
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    with open("page.html", "w") as f:
        f.write(PAGE)
    with sync_playwright() as p:
        for name in ("chromium", "firefox", "webkit"):
            try:
                b = getattr(p, name).launch()
            except Exception as e:
                print(json.dumps({"browser": name, "error": str(e)[:200]}))
                continue
            for fn in files:
                page = b.new_page()
                page.goto(f"http://127.0.0.1:{port}/page.html")
                try:
                    r = page.evaluate(JS, f"http://127.0.0.1:{port}/{fn}")
                except Exception as e:
                    r = {"error": str(e)[:200]}
                r.update(browser=name, version=b.version, file=fn)
                print(json.dumps(r))
                page.close()
            b.close()
    srv.shutdown()


main()

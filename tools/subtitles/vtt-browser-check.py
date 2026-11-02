#!/usr/bin/env python3
"""Loads WebVTT files written by onemedia into <track> elements in Chromium, Firefox and WebKit (Playwright) and compares the
cues the browser parsed (count, start, end, text) with what onemedia reads from the same file.

Usage: vtt-browser-check.py <dll> <file.vtt>...   (run with the Playwright venv's python)
"""
import http.server, os, subprocess, sys, threading, functools
from playwright.sync_api import sync_playwright

dll = sys.argv[1]
files = [os.path.abspath(f) for f in sys.argv[2:]]
root = os.path.commonpath([os.path.dirname(f) for f in files])
open(os.path.join(root, "blank.html"), "w").write("<html><body></body></html>")
handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=root)
handler.log_message = lambda *a, **k: None
httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
threading.Thread(target=httpd.serve_forever, daemon=True).start()
port = httpd.server_address[1]


def expected(path):
    r = subprocess.run(["dotnet", dll, "subs", "info", path, "--cues"], capture_output=True, text=True, encoding="utf8")
    rows = [l.split("\t", 4) for l in r.stdout.splitlines() if l.startswith("cue\t")]
    return [(int(a[2]), int(a[3]), a[4].replace("\\n", "\n") if len(a) > 4 else "") for a in rows]


JS = """async (url) => {
  const v = document.createElement('video'); const t = document.createElement('track');
  t.kind = 'subtitles'; t.src = url; t.default = true; v.appendChild(t); document.body.appendChild(v);
  t.track.mode = 'hidden';
  await new Promise((res, rej) => { if (t.readyState === 2) res(); t.addEventListener('load', res); t.addEventListener('error', () => rej('track error')); setTimeout(() => rej('timeout'), 8000); });
  const out = [];
  for (const c of t.track.cues) out.push([Math.round(c.startTime * 1000), Math.round(c.endTime * 1000), c.getCueAsHTML().textContent]);
  return out;
}"""
bad = 0
with sync_playwright() as p:
    for name in ("chromium", "firefox", "webkit"):
        browser = getattr(p, name).launch()
        ver = browser.version
        for f in files:
            exp = expected(f)
            page = browser.new_page()
            page.goto("http://127.0.0.1:%d/blank.html" % port)
            try:
                got = page.evaluate(JS.replace("(url)", "(url)"), "http://127.0.0.1:%d/%s" % (port, os.path.relpath(f, root)))
                ok = len(got) == len(exp) and all(g[0] == e[0] and g[1] == e[1] and g[2].replace("\r", "") == e[2] for g, e in zip(got, exp))
                diff = sum(1 for g, e in zip(got, exp) if (g[0], g[1], g[2]) != e)
                print("%-9s %-8s %-28s cues browser=%d ours=%d mismatching=%d %s" % (name, ver, os.path.basename(f), len(got), len(exp), diff + abs(len(got) - len(exp)), "OK" if ok else "DIFF"))
                if not ok:
                    bad += 1
                    for g, e in list(zip(got, exp)):
                        if (g[0], g[1], g[2]) != e:
                            print("     browser", g, "ours", e)
                            break
            except Exception as ex:
                bad += 1
                print(name, os.path.basename(f), "ERROR", str(ex)[:200])
            page.close()
        browser.close()
sys.exit(1 if bad else 0)

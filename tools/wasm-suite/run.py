#!/usr/bin/env python3
"""Runs the browser-wasm build of the unit-test suite in headless Chromium and prints its summary.

Usage: run.py APPBUNDLE_DIR [--timeout SECONDS] [--log FILE] [--channel chrome] [-- RUNNER ARGS...]

APPBUNDLE_DIR is the AppBundle folder that `dotnet build tests/OneMedia.Tests.Browser -c Release` produces. The runner arguments are
passed on to the suite (--filter TEXT, --list, --quiet, --include-excluded). Needs the Playwright Python package and a Chromium it can
launch (PLAYWRIGHT_BROWSERS_PATH, or --channel chrome for an installed Google Chrome). Exit code: 0 all passed, 1 failures, 2 no result.
"""
import argparse
import http.server
import sys
import threading
import time

from playwright.sync_api import sync_playwright

PAGE = b"""<!doctype html><meta charset=utf-8><title>OneMedia suite</title><script type=module src=./main.mjs></script>"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bundle")
    ap.add_argument("--timeout", type=int, default=3000)
    ap.add_argument("--log")
    ap.add_argument("--channel")
    ap.add_argument("rest", nargs="*")
    a = ap.parse_args()
    runner_args = [x for x in a.rest if x != "--"]

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *args, **kw):
            super().__init__(*args, directory=a.bundle, **kw)

        def log_message(self, *args):
            pass

        def do_GET(self):
            if self.path.split("?")[0] in ("/", "/index.html"):
                self.send_response(200)
                self.send_header("Content-Type", "text/html")
                self.send_header("Content-Length", str(len(PAGE)))
                self.end_headers()
                self.wfile.write(PAGE)
                return
            super().do_GET()

    Handler.extensions_map = {**Handler.extensions_map, ".wasm": "application/wasm", ".mjs": "text/javascript", ".js": "text/javascript"}
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()

    out = open(a.log, "w") if a.log else None
    exit_code = {"v": None}
    result = {"line": None}

    def on_console(msg):
        text = msg.text
        if out:
            out.write(text + "\n")
            out.flush()
        if text.startswith("RESULT "):
            result["line"] = text
        if text.startswith("EXIT "):
            exit_code["v"] = int(text.split()[1])
        if not out and not text.startswith("PASS "):
            print(text, flush=True)

    with sync_playwright() as p:
        kw = {"channel": a.channel} if a.channel else {}
        browser = p.chromium.launch(**kw)
        print("browser", browser.version, flush=True)
        page = browser.new_page()
        page.on("console", on_console)
        page.on("pageerror", lambda e: print("PAGEERROR", e, flush=True))
        page.on("crash", lambda: print("PAGE CRASHED", flush=True))
        query = "+".join(runner_args)
        page.goto(f"http://127.0.0.1:{port}/index.html?args={query}", wait_until="commit")
        deadline = time.time() + a.timeout
        while exit_code["v"] is None and time.time() < deadline and not page.is_closed():
            page.wait_for_timeout(500)
        browser.close()
    srv.shutdown()
    if out:
        out.close()
    print(result["line"] or "no RESULT line", flush=True)
    if exit_code["v"] is None:
        print("timed out or crashed", flush=True)
        return 2
    return exit_code["v"]


if __name__ == "__main__":
    sys.exit(main())

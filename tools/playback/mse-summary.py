#!/usr/bin/env python3
"""Counts what a probe.py run observed: mse-summary.py RESULTS.jsonl LABEL.

A row plays when nothing errored, the clock passed 0.5 s and (MediaSource) the whole-file append ended with `updateend`. MediaSource rows for a
type the browser does not support (Matroska) are counted apart, not as failures.
"""
import json
import sys

rows = [json.loads(line) for line in open(sys.argv[1])]
for mode in ("progressive", "mediasource"):
    selected = [x for x in rows if x.get("mode") == mode]
    ok, unsupported, bad = 0, 0, []
    for x in selected:
        failed = any(k in x for k in ("harnessError", "timedOut", "error", "playError")) or (mode == "mediasource" and x.get("append") != "updateend")
        if mode == "mediasource" and x.get("isTypeSupported") is not True:
            unsupported += 1
        elif not failed and (x.get("currentTime") or 0) > 0.5:
            ok += 1
        else:
            bad.append("%s %s" % (x["file"], x.get("error") or x.get("append") or x.get("harnessError") or x.get("playError") or "t=%s" % x.get("currentTime")))
    print("%s %s: %d play, %d type-unsupported, %d fail of %d" % (sys.argv[2], mode, ok, unsupported, len(bad), len(selected)))
    for b in bad:
        print("   FAIL", b)

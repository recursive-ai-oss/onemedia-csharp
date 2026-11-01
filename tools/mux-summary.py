#!/usr/bin/env python3
"""Totals of run-mux-diff.sh summaries: usage: mux-summary.py <summary.tsv>... (one line of counts and classes per file, then each unexplained class)."""
import ast
import collections
import re
import sys

for path in sys.argv[1:]:
    files = refused = packets = match = diff = box = ff = 0
    classes = collections.Counter()
    unexplained = collections.defaultdict(set)
    why = collections.Counter()
    for line in open(path):
        f = line.rstrip("\n").split("\t")
        files += 1
        m = re.search(r"packets=(\d+)\tmatch=(\d+)\tdiff=(\d+)\t(\{.*?\})?\t", line + "\t")
        if not m:
            refused += 1
            why[re.sub(r"[0-9]+", "N", f[1])[:70]] += 1
            continue
        packets += int(m[1]); match += int(m[2]); diff += int(m[3])
        for k, v in (ast.literal_eval(m[4]) if m[4] else {}).items():
            k = re.sub(r"\(.*\)", "", k)
            classes[k] += v
            if not k.startswith("explained"):
                unexplained[k].add(f[0].rsplit("/", 1)[-1])
        a = re.search(r"mp4box_new=(\d+)\tffmpeg_new=(\d+)", line)
        if a:
            box += int(a[1]); ff += int(a[2])
    print(f"## {path}: files {files}, not muxed {refused} {dict(why)}, packets {packets}, match {match}, diff {diff}, mp4box_new {box}, ffmpeg_new {ff}")
    print("  ", dict(classes))
    for k, v in unexplained.items():
        print("   unexplained", k, len(v), sorted(v)[:6])

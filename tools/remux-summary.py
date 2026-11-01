#!/usr/bin/env python3
"""Summarises the summary.tsv that tools/run-remux-diff.sh writes: one row per input extension.

usage: remux-summary.py <summary.tsv> [--list-diff]

Columns printed: files compared, packets, matched, different, files not written (typed refusals and unreadable inputs), files whose output
gives MP4Box / mkvalidator messages the input did not (struct_new) or ffmpeg messages the input did not (ffmpeg_new).
"""
import re
import sys
from collections import defaultdict

rows = defaultdict(lambda: [0, 0, 0, 0, 0, 0, 0])
diff_files = []
for line in open(sys.argv[1]):
    f = line.rstrip("\n").split("\t")
    name = f[0]
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else "?"
    r = rows[ext]
    if len(f) < 3 or f[2] == "NOT-WRITTEN" or "NOT-WRITTEN" in line[-14:] or f[1].startswith("REFUSED"):
        r[4] += 1
        continue
    text = "\t".join(f)
    pk = re.search(r"\bpackets=(\d+)\tmatch=(\d+)\tdiff=(\d+)", text)
    if not pk:
        r[4] += 1
        continue
    r[0] += 1
    r[1] += int(pk.group(1))
    r[2] += int(pk.group(2))
    r[3] += int(pk.group(3))
    if int(pk.group(3)):
        diff_files.append(name)
    s = re.search(r"struct_new=(\d+)", text)
    g = re.search(r"ffmpeg_new=(\d+)", text)
    r[5] += 1 if s and int(s.group(1)) else 0
    r[6] += 1 if g and int(g.group(1)) else 0
print("ext\tfiles\tpackets\tmatch\tdiff\tnot_written\tstruct_new\tffmpeg_new")
tot = [0] * 7
for ext in sorted(rows):
    print(ext + "\t" + "\t".join(map(str, rows[ext])))
    tot = [a + b for a, b in zip(tot, rows[ext])]
print("TOTAL\t" + "\t".join(map(str, tot)))
if "--list-diff" in sys.argv:
    print("files with unexplained differences:")
    for n in diff_files:
        print("  " + n)

#!/usr/bin/env python3
"""Checks that the Cues of a Matroska file match the key frames of its first video track.

    check-cues.py <file.mkv>

Prints "cues=N keyframes=M ok" or "... FAIL <reason>". Uses mkvinfo -a (cue points) and ffprobe (packet flags), both as black boxes.
"""
import re
import subprocess
import sys


def seconds(text):
    h, m, s = text.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def main():
    path = sys.argv[1]
    info = subprocess.run(["mkvinfo", "-a", path], capture_output=True, text=True, errors="replace").stdout
    cues = [round(seconds(m) * 1000) for m in re.findall(r"Cue time: (\S+)", info)]
    pk = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path],
                        capture_output=True, text=True).stdout
    keys = [round(float(l.split(",")[0]) * 1000) for l in pk.splitlines() if ",K" in l and l.split(",")[0] not in ("", "N/A")]
    if not keys:
        print(f"cues={len(cues)} keyframes=0 n/a (no video)")
        return
    missing = [c for c in cues if c not in set(keys)]
    if not cues:
        print(f"cues=0 keyframes={len(keys)} FAIL no cues")
    elif missing:
        print(f"cues={len(cues)} keyframes={len(keys)} FAIL {len(missing)} cue times are not key frames, first {missing[0]} ms")
    elif len(cues) != len(keys):
        print(f"cues={len(cues)} keyframes={len(keys)} FAIL count differs")
    else:
        print(f"cues={len(cues)} keyframes={len(keys)} ok")


main()

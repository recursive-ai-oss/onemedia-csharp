#!/usr/bin/env python3
"""Extracts each text track of the given files with MP4Box (MP4/MOV/3GP) or mkvextract (Matroska/WebM) and compares times and plain text with
the cues OneMedia reads from the same track.

Usage: text-track-extract-check.py --tool <TextTrackCheck executable> FILE...   (MP4Box, mkvmerge, mkvextract and ffprobe on PATH)

Prints one line per track: file, track, extractor, our cue count, extracted cue count, differences. A track an extractor cannot write is
reported as "extractor wrote nothing" and is not a difference.
"""
import argparse
import html
import json
import os
import re
import subprocess
import sys
import tempfile


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")


def ours(tool, path):
    tracks = []
    for line in run([tool, "cues", path]).stdout.splitlines():
        if line.startswith("# track"):
            tracks.append((int(re.search(r"# track (\d+)", line).group(1)), []))
        elif line.strip():
            p = line.split("\t", 3)
            tracks[-1][1].append((float(p[1]), float(p[2]), p[3] if len(p) > 3 else ""))
    return tracks


def plain(s):
    s = re.sub(r"<[^>]*>", "", s)
    s = re.sub(r"\{\\[^}]*\}", "", s)
    s = html.unescape(s).replace("\\N", " ").replace("\\n", " ").replace("\\h", " ")
    return " ".join(s.split())


def ms(h, m, s, f, digits):
    return ((int(h) * 60 + int(m)) * 60 + int(s)) * 1000 + int(f.ljust(3, "0")[:3]) if digits else 0


def parse(text):
    cues = []
    text = text.replace("\r\n", "\n")
    for block in re.split(r"\n\s*\n", text.strip()):
        lines = block.split("\n")
        for i, l in enumerate(lines):
            m = re.match(r"(\d+):(\d+):(\d+)[.,](\d+) --> (\d+):(\d+):(\d+)[.,](\d+)", l)
            if m:
                g = m.groups()
                cues.append((float(ms(g[0], g[1], g[2], g[3], 1)), float(ms(g[4], g[5], g[6], g[7], 1)), "\n".join(lines[i + 1:])))
                break
    for m in re.finditer(r"^Dialogue: *[^,]*,(\d+):(\d+):(\d+)\.(\d+),(\d+):(\d+):(\d+)\.(\d+),(?:[^,]*,){6}(.*)$", text, re.M):
        g = m.groups()
        cues.append((ms(g[0], g[1], g[2], g[3] + "0", 1), ms(g[4], g[5], g[6], g[7] + "0", 1), g[8]))
    return cues


def compare(mine, theirs):
    theirs = [c for c in theirs if plain(c[2])]
    diffs = []
    if len(mine) != len(theirs):
        diffs.append(f"{len(mine)} cues here, {len(theirs)} extracted")
    for i, (a, b) in enumerate(zip(mine, theirs)):
        if abs(a[0] - b[0]) > 1 or abs(a[1] - b[1]) > 1:
            diffs.append(f"cue {i}: {a[0]:.0f}-{a[1]:.0f} vs {b[0]:.0f}-{b[1]:.0f}")
        if plain(a[2]) != plain(b[2]):
            diffs.append(f"cue {i}: text {plain(a[2])!r} vs {plain(b[2])!r}")
    return diffs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    total = bad = 0
    tmp = tempfile.mkdtemp(prefix="u49x-", dir=os.environ.get("TMPDIR"))
    for path in a.files:
        tracks = ours(a.tool, path)
        if not tracks:
            continue
        if path.endswith((".mkv", ".webm")):
            info = json.loads(run(["mkvmerge", "-J", path]).stdout)
            subs = [t for t in info["tracks"] if t["type"] == "subtitles"]
            for ordinal, (idx, cues) in enumerate(tracks):
                if ordinal >= len(subs):
                    continue
                tid = subs[ordinal]["id"]
                ext = {"S_TEXT/UTF8": "srt", "S_TEXT/ASS": "ass", "S_TEXT/WEBVTT": "vtt"}.get(subs[ordinal]["properties"]["codec_id"], "txt")
                out = os.path.join(tmp, f"t{tid}.{ext}")
                run(["mkvextract", path, "tracks", f"{tid}:{out}"])
                if not os.path.exists(out):
                    print(f"{path}\ttrack {idx}\tmkvextract\twrote nothing")
                    continue
                d = compare(cues, parse(open(out, encoding="utf-8", errors="replace").read()))
                total += len(cues)
                bad += len(d)
                print(f"{path}\ttrack {idx}\tmkvextract\tours={len(cues)}\tdifferences={len(d)}")
                for x in d[:8]:
                    print("    " + x)
        else:
            probe = [m.group(1) for m in re.finditer(r"^# track \d+ id=(\d+) ", run([a.tool, "raw", path]).stdout, re.M)]
            for ordinal, (idx, cues) in enumerate(tracks):
                if ordinal >= len(probe):
                    continue
                tid = int(probe[ordinal], 0)
                out = os.path.join(tmp, f"m{tid}.srt")
                if os.path.exists(out):
                    os.remove(out)
                run(["MP4Box", "-quiet", "-srt", str(tid), path, "-out", out])
                if not os.path.exists(out) or os.path.getsize(out) == 0:
                    print(f"{path}\ttrack {idx}\tMP4Box\twrote nothing")
                    continue
                d = compare(cues, parse(open(out, encoding="utf-8", errors="replace").read()))
                total += len(cues)
                bad += len(d)
                print(f"{path}\ttrack {idx}\tMP4Box\tours={len(cues)}\tdifferences={len(d)}")
                for x in d[:8]:
                    print("    " + x)
    print(f"TOTAL cues={total} differences={bad}")


if __name__ == "__main__":
    sys.exit(main())

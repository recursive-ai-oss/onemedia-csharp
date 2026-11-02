#!/usr/bin/env python3
"""Compares the cues OneMedia reads from container text tracks with what ffmpeg extracts (as SRT) from the same tracks.

Usage: text-track-compare.py --tool <TextTrackCheck executable> [--ffmpeg ffmpeg] [--ffprobe ffprobe] [--mp4box MP4Box] [--mkvextract mkvextract] FILE...

For each file: our `cues` listing against `ffmpeg -map 0:s:N -f srt -`, track by track, in start and end time (milliseconds) and plain text
(markup tags removed from ffmpeg's output, line breaks normalised). Prints one line per file and every difference. Exit status 0 even when
there are differences: the lines say how many.
"""
import argparse
import html
import re
import subprocess
import sys


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)


def ours(tool, path):
    """{subtitle stream ordinal: (format, [(start_ms, end_ms, text)])} from our listing; ordinals count subtitle tracks in file order."""
    out = run([tool, "cues", path]).stdout
    tracks = []
    for line in out.splitlines():
        if line.startswith("# track"):
            fmt = re.search(r"format=(\w+)", line).group(1)
            tracks.append((fmt, []))
        elif line.strip():
            parts = line.split("\t", 3)
            tracks[-1][1].append((float(parts[1]), float(parts[2]), parts[3] if len(parts) > 3 else ""))
    return tracks


def subtitle_streams(ffprobe, path):
    out = run([ffprobe, "-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name", "-of", "csv=p=0", path]).stdout
    return [l.split(",") for l in out.split() if l]


def parse_srt(text):
    cues = []
    for block in re.split(r"\n\s*\n", text.replace("\r\n", "\n").strip()):
        lines = block.split("\n")
        if len(lines) < 2:
            continue
        m = re.match(r"(\d+):(\d+):(\d+)[,.](\d+) --> (\d+):(\d+):(\d+)[,.](\d+)", lines[1] if re.match(r"^\d+$", lines[0]) else lines[0])
        if not m:
            continue
        g = [int(x) for x in m.groups()]
        start = ((g[0] * 60 + g[1]) * 60 + g[2]) * 1000 + g[3]
        end = ((g[4] * 60 + g[5]) * 60 + g[6]) * 1000 + g[7]
        body = lines[2:] if re.match(r"^\d+$", lines[0]) else lines[1:]
        cues.append((float(start), float(end), "\\n".join(body)))
    return cues


def plain(s):
    s = re.sub(r"<[^>]*>", "", s)
    s = re.sub(r"\{\\[^}]*\}", "", s)
    s = html.unescape(s).replace("\\N", "\\n").replace("\\h", " ")
    return s.strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--ffprobe", default="ffprobe")
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    total_cues = total_diff = 0
    for path in a.files:
        mine = ours(a.tool, path)
        streams = subtitle_streams(a.ffprobe, path)
        diffs = []
        n = 0
        for ordinal, (fmt, cues) in enumerate(mine):
            if ordinal >= len(streams):
                diffs.append(f"track {ordinal}: ffprobe lists no such subtitle stream")
                continue
            r = run([a.ffmpeg, "-v", "error", "-i", path, "-map", f"0:s:{ordinal}", "-f", "srt", "-"])
            theirs = parse_srt(r.stdout)
            # Cues ffmpeg lists with no text are gaps it keeps; ours leave them out.
            theirs = [c for c in theirs if plain(c[2])]
            n += len(cues)
            if len(theirs) != len(cues):
                diffs.append(f"track {ordinal} ({fmt}): {len(cues)} cues here, {len(theirs)} from ffmpeg")
            for i, (m, t) in enumerate(zip(cues, theirs)):
                if abs(m[0] - t[0]) > 0.5 or abs(m[1] - t[1]) > 0.5:
                    diffs.append(f"track {ordinal} cue {i}: times {m[0]:.0f}-{m[1]:.0f} vs ffmpeg {t[0]:.0f}-{t[1]:.0f}")
                if plain(m[2]) != plain(t[2]):
                    diffs.append(f"track {ordinal} cue {i}: text {m[2]!r} vs ffmpeg {t[2]!r}")
        total_cues += n
        total_diff += len(diffs)
        print(f"{path}\ttracks={len(mine)}\tcues={n}\tdifferences={len(diffs)}")
        for d in diffs[:20]:
            print("    " + d)
    print(f"TOTAL cues={total_cues} differences={total_diff}")


if __name__ == "__main__":
    sys.exit(main())

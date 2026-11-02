#!/usr/bin/env python3
"""Compares our teletext subtitle cues with a reference extractor's SRT output, cue by cue.

Usage: teletext-compare.py <ours-dir> <ref-dir> [--show N]

Files pair by name (<file>.pid<PID>.p<PAGE>.srt). Times are shifted by the offset that aligns the first cue with equal text, because
the references count from the stream's first timestamp and we report presentation time as written. A cue matches when its normalised
text (whitespace collapsed) is equal and its start is within 100 ms of the shifted reference start.
"""
import re
import statistics
import sys
from pathlib import Path

STAMP = re.compile(r"(\d+):(\d+):(\d+),(\d+) --> (\d+):(\d+):(\d+),(\d+)")


ASCII = "--ascii" in sys.argv
TAGS = re.compile(r"</?(font|i|b|u)[^>]*>")


def normalise(body):
    body = TAGS.sub("", body)
    if ASCII:
        # Compare letters and digits of the ASCII range only: for references that disagree on the character set.
        body = re.sub(r"[^A-Za-z0-9 ]", "", body)
    return " ".join(body.split())


def parse(path):
    cues = []
    text = Path(path).read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n")
    for block in re.split(r"\n\n+", text.strip()):
        lines = block.split("\n")
        for i, line in enumerate(lines):
            m = STAMP.match(line)
            if m:
                g = [int(x) for x in m.groups()]
                start = g[0] * 3600 + g[1] * 60 + g[2] + g[3] / 1000
                end = g[4] * 3600 + g[5] * 60 + g[6] + g[7] / 1000
                body = normalise(" ".join(lines[i + 1:]))
                cues.append((start, end, body))
                break
    return cues


def main():
    ours_dir, ref_dir = Path(sys.argv[1]), Path(sys.argv[2])
    show = int(sys.argv[sys.argv.index("--show") + 1]) if "--show" in sys.argv else 0
    totals = dict(files=0, ours=0, ref=0, matched=0, only_ours=0, only_ref=0)
    start_deltas, end_deltas = [], []
    for ref_file in sorted(ref_dir.glob("*.srt")):
        ours_file = ours_dir / ref_file.name
        if not ours_file.exists():
            continue
        ours, ref = parse(ours_file), parse(ref_file)
        if not ref and not ours:
            continue
        # Offset: first reference cue whose text occurs in ours.
        offset = None
        for rs, _, rt in ref:
            for os_, _, ot in ours:
                if ot == rt:
                    offset = os_ - rs
                    break
            if offset is not None:
                break
        used = set()
        matched = 0
        sd, ed = [], []
        unmatched_ref = []
        for ri, (rs, re_, rt) in enumerate(ref):
            hit = None
            if offset is not None:
                for j, (os_, oe, ot) in enumerate(ours):
                    if j not in used and ot == rt and abs((os_ - offset) - rs) <= 0.1:
                        hit = j
                        break
            if hit is None:
                unmatched_ref.append((rs, rt))
            else:
                used.add(hit)
                matched += 1
                sd.append((ours[hit][0] - offset) - rs)
                if ri == len(ref) - 1:
                    # The last cue of a stream lasts to the end of the input; each tool takes a different end, so it is not compared.
                    totals["last_cues"] = totals.get("last_cues", 0) + 1
                    continue
                ed.append((ours[hit][1] - offset) - re_)
                if "--ends" in sys.argv and abs(ed[-1]) > 0.1:
                    print(f"    end {ref_file.stem} t={rs:9.3f} ours_end-ref_end={ed[-1]*1000:7.0f}ms ref_dur={re_-rs:6.2f}s ours_dur={ours[hit][1]-ours[hit][0]:6.2f}s  {rt[:50]}")
        only_ours = [(o[0], o[2]) for j, o in enumerate(ours) if j not in used]
        totals["files"] += 1
        totals["ours"] += len(ours)
        totals["ref"] += len(ref)
        totals["matched"] += matched
        totals["only_ours"] += len(only_ours)
        totals["only_ref"] += len(unmatched_ref)
        start_deltas += sd
        end_deltas += ed
        mean_s = statistics.mean(sd) * 1000 if sd else float("nan")
        max_e = max((abs(x) for x in ed), default=0) * 1000
        print(f"{ref_file.stem}: ours={len(ours)} ref={len(ref)} matched={matched} only_ours={len(only_ours)} only_ref={len(unmatched_ref)} "
              f"start_mean={mean_s:.0f}ms end_absmax={max_e:.0f}ms")
        for rs, rt in unmatched_ref[:show]:
            print(f"    ref-only {rs:9.3f}: {rt[:110]}")
        for os_, ot in only_ours[:show]:
            print(f"    our-only {os_:9.3f}: {ot[:110]}")
    print("TOTAL", totals)
    if start_deltas:
        print(f"start delta ms: mean={statistics.mean(start_deltas)*1000:.1f} max_abs={max(abs(x) for x in start_deltas)*1000:.0f}")
        print(f"end delta ms: mean={statistics.mean(end_deltas)*1000:.1f} max_abs={max(abs(x) for x in end_deltas)*1000:.0f}")


main()

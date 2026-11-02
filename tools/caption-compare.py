#!/usr/bin/env python3
"""Compares two SRT files cue by cue: text after removing markup, and start/end within a tolerance.
usage: caption-compare.py ours.srt other.srt [tolerance_ms=40]
Prints: cues ours/other, cues whose text matches in order, how many of those also match start and end, and the first differences."""
import re, sys

def parse(path):
    try:
        data = open(path, encoding='utf-8-sig', errors='replace').read().replace('\r', '')
    except FileNotFoundError:
        return []
    cues = []
    for block in re.split(r'\n\s*\n', data.strip()):
        lines = block.split('\n')
        if len(lines) < 3:
            continue
        m = re.match(r'(\d+):(\d+):(\d+)[,.](\d+) --> (\d+):(\d+):(\d+)[,.](\d+)', lines[1])
        if not m:
            continue
        g = [int(x) for x in m.groups()]
        s = ((g[0] * 60 + g[1]) * 60 + g[2]) * 1000 + g[3]
        e = ((g[4] * 60 + g[5]) * 60 + g[6]) * 1000 + g[7]
        text = [re.sub(r'\s+', ' ', re.sub(r'<[^>]+>|\{\\an\d\}', '', t)).strip() for t in lines[2:]]
        cues.append((s, e, '\n'.join(t for t in text if t)))
    return cues

a, b = parse(sys.argv[1]), parse(sys.argv[2])
tol = int(sys.argv[3]) if len(sys.argv) > 3 else 40
# Set-based: each cue of the other tool is looked up by text in ours (the tools differ in when roll-up screens are emitted, so ours may
# hold extra intermediate screens).
found = timed = 0
diffs = []
for o in b:
    cands = [c for c in a if c[2] == o[2]]
    if not cands:
        diffs.append(('missing in ours', o))
        continue
    found += 1
    if any(abs(c[0] - o[0]) <= tol and abs(c[1] - o[1]) <= tol for c in cands):
        timed += 1
    else:
        diffs.append(('time', o, cands[0]))
texts_b = {o[2] for o in b}
extra = sum(1 for c in a if c[2] not in texts_b)
print(f'ours={len(a)} other={len(b)} other_found_in_ours={found} of_which_time_ok={timed} ours_without_counterpart={extra}')
for d in diffs[:4]:
    print(' ', d)

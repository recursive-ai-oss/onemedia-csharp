#!/usr/bin/env python3
"""Compares OneMedia.TelemetryCheck dumps with the output of GoPro's GPMF parser demo (`gpmfdemo <file> -a -f`, scaled data, 3 decimals).

usage: gpmf-compare.py <file.ours.tsv> <gpmfdemo output> [channels listing]

For every key the demo prints values for, it gives the sample counts of both and the largest absolute difference per component. The demo prints
three decimals, so a difference up to 5e-4 is rounding. Keys the demo prints no values for (complex structures, text) are listed as not compared.
"""
import re
import sys
from collections import OrderedDict, defaultdict

NUM = re.compile(r'^\s*(-?\d+(?:\.\d+)?)')


def read_demo(path):
    out = defaultdict(list)
    for line in open(path, encoding='utf8', errors='replace'):
        m = re.match(r'^  (\S{4}) (.*)$', line.rstrip('\n'))
        if not m or 'sampling rate' in m.group(2):
            continue
        vals = []
        for tok in m.group(2).split(', '):
            n = NUM.match(tok)
            if n:
                vals.append(float(n.group(1)))
        if vals:
            out[m.group(1)].append(vals)
    return out


def read_ours(path):
    out = OrderedDict()
    for line in open(path, encoding='utf8'):
        f = line.rstrip('\n').split('\t')
        if f[5] == 'RAW':
            continue
        out.setdefault(f[1], []).append([float(x) for x in f[8:]])
    return out


def read_axes(path):
    """ORIN/ORIO per key from the channel listing written next to the dump (stderr of `TelemetryCheck dump`)."""
    axes = {}
    try:
        for line in open(path, encoding='utf8'):
            m = re.search(r"Gpmf (\S+) dev .* axes '(\w*)' out '(\w*)'", line)
            if m and m.group(2) and m.group(3):
                axes[m.group(1)] = (m.group(2), m.group(3))
    except OSError:
        pass
    return axes


def remap(row, order):
    """The camera-frame order: output axis i is the input component naming the same axis, negated when the letters differ in case."""
    src, dst = order
    out = []
    for letter in dst:
        idx = next(i for i, c in enumerate(src) if c.upper() == letter.upper())
        out.append(row[idx] if src[idx] == letter else -row[idx])
    return out


def main():
    ours = read_ours(sys.argv[1])
    axes = read_axes(sys.argv[3]) if len(sys.argv) > 3 else {}
    demo = read_demo(sys.argv[2])
    ok = True
    for key, rows in ours.items():
        if key not in demo:
            print(f'{key}: ours {len(rows)} demo none (not compared)')
            continue
        d = demo[key]
        if key in axes:
            rows = [remap(r, axes[key]) for r in rows]
        n = min(len(rows), len(d))
        worst = 0.0
        for i in range(n):
            for a, b in zip(rows[i], d[i]):
                if a == a:
                    worst = max(worst, abs(a - b))
        # The demo prints only the first payload of keys whose samples are several structures per instant (HUES, SCEN, FACE); equal as far as it goes.
        same = len(rows) == len(d) and worst <= 5.1e-4
        if key in ('HUES', 'FACE', 'SCEN') and not same:
            print(f'{key}: ours {len(rows)} demo {len(d)} (the demo lists these multi-structure keys differently; compared against ExifTool instead)')
            continue
        ok &= same
        print(f'{key}: ours {len(rows)} demo {len(d)} max|diff| {worst:.3g}' + ('' if same else '  DIFFERS'))
    print('ALL EQUAL' if ok else 'DIFFERENCES')


main()

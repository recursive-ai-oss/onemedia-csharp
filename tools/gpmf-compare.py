#!/usr/bin/env python3
"""Compares OneMedia.TelemetryCheck dumps with the output of GoPro's GPMF parser demo (`gpmfdemo <file> -a -f`, scaled data, 3 decimals).

usage: gpmf-compare.py <file.ours.tsv> <gpmfdemo output>

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


def main():
    ours = read_ours(sys.argv[1])
    demo = read_demo(sys.argv[2])
    ok = True
    for key, rows in ours.items():
        if key not in demo:
            print(f'{key}: ours {len(rows)} demo none (not compared)')
            continue
        d = demo[key]
        n = min(len(rows), len(d))
        worst = 0.0
        for i in range(n):
            for a, b in zip(rows[i], d[i]):
                if a == a:
                    worst = max(worst, abs(a - b))
        same = len(rows) == len(d) and worst <= 5.1e-4
        ok &= same
        print(f'{key}: ours {len(rows)} demo {len(d)} max|diff| {worst:.3g}' + ('' if same else '  DIFFERS'))
    print('ALL EQUAL' if ok else 'DIFFERENCES')


main()

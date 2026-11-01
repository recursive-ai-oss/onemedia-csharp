#!/usr/bin/env python3
"""Prints one row per file from the table.tsv files that run.sh wrote: what each browser did (P = progressive, M = MediaSource;
upper case = played, lower case = failed, '.' = not run) next to our verdict ('=' agrees, '!' disagrees).

Usage: summary.py WORKDIR PROFILE:BROWSER ...      e.g. summary.py ~/onemedia/playback chromium firefox webkit
"""
import os
import sys


def main():
    work = sys.argv[1]
    names = sys.argv[2:]
    rows = {}
    for n in names:
        path = os.path.join(work, n, "table.tsv")
        for line in open(path).read().splitlines()[1:]:
            f = line.split("\t")
            file, mode, ours, seen, result, note = f[0], f[2], f[3], f[4], f[5], f[6] if len(f) > 6 else ""
            r = rows.setdefault(file, {})
            r[(n, mode)] = (seen == "plays", result == "ok", ours, note)
    print(f"{'file':44}" + "".join(f"{n[:9]:>12}" for n in names))
    for file in sorted(rows):
        cells = []
        for n in names:
            c = ""
            for mode, up in (("progressive", "P"), ("mediasource", "M")):
                v = rows[file].get((n, mode))
                if v is None:
                    c += "."
                else:
                    c += (up if v[0] else up.lower()) + ("=" if v[1] else "!")
            cells.append(f"{c:>12}")
        print(f"{file:44}" + "".join(cells))


main()

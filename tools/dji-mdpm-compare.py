#!/usr/bin/env python3
"""Compares OneMedia.TelemetryCheck dumps of DJI djmd tracks and AVCHD (MDPM) streams with `exiftool -ee -n -j -G3 -a` of the same file.

usage: dji-mdpm-compare.py {djmd|mdpm} <file.ours.tsv> <exiftool.json>

Prints, per compared quantity, our sample count, ExifTool's count and the largest absolute difference. Exit status 1 when a count differs or a
difference exceeds 1e-6. For MDPM, ExifTool prints positive coordinates plus a reference letter (and signed composites): only the signed values are compared.
"""
import json
import re
import sys
from collections import defaultdict


def et_values(path):
    doc = json.load(open(path, encoding="utf8"))[0]
    rows = []
    for key, value in doc.items():
        m = re.match(r"(Main|Doc(\d+)(?:-\d+)?):(.+)$", key)
        if m:
            rows.append((0 if m.group(1) == "Main" else int(m.group(2)), m.group(3), value))
    rows.sort(key=lambda r: r[0])
    tags = defaultdict(list)
    for _, tag, value in rows:
        tags[tag].append(value)
    return tags


def ours(path):
    ch = defaultdict(list)
    for line in open(path, encoding="utf8"):
        f = line.rstrip("\n").split("\t")
        if f[5] == "RAW":
            continue
        ch[f[1]].append([float(x) for x in f[8:]])
    return ch


def compare(label, a, b, bad):
    n = min(len(a), len(b))
    diff = max((abs(x - y) for x, y in zip(a, b)), default=0.0)
    ok = len(a) == len(b) and diff <= 1e-6
    if not ok:
        bad.append(label)
    print(f"{'ok  ' if ok else 'DIFF'} {label}: ours {len(a)} exiftool {len(b)} max|diff| {diff:.3g} (compared {n})")


def main():
    mode, mine, theirs = sys.argv[1:4]
    o, e, bad = ours(mine), et_values(theirs), []
    num = lambda tag: [float(x) for x in e.get(tag, [])]
    if mode == "djmd":
        g = o.get("djmd gps", [])
        compare("latitude", [r[0] for r in g], num("GPSLatitude"), bad)
        compare("longitude", [r[1] for r in g], num("GPSLongitude"), bad)
        for name, tag, col in (("absolute altitude", "AbsoluteAltitude", 2), ("GPS altitude", "GPSAltitude", 2)):
            if tag in e:
                compare(name, [r[col] for r in g], num(tag), bad)
        if "RelativeAltitude" in e:
            compare("relative altitude", [r[0] for r in o.get("djmd altitude", [])], num("RelativeAltitude"), bad)
        c = o.get("djmd camera", [])
        if "ISO" in e:
            compare("ISO", [r[0] for r in c], num("ISO"), bad)
        if "ColorTemperature" in e:
            compare("colour temperature", [r[4] for r in c], num("ColorTemperature"), bad)
        for prefix, name in (("Drone", "djmd aircraft"), ("Gimbal", "djmd gimbal")):
            rows = o.get(name, [])
            for tag, col in ((prefix + "Yaw", 0), (prefix + "Pitch", 1), (prefix + "Roll", 2)):
                if tag in e:
                    compare(tag, [r[col] for r in rows], num(tag), bad)
        if "FrameNumber" in e:
            compare("frame number", [r[0] for r in o.get("djmd frame", [])], num("FrameNumber"), bad)
    else:
        g = o.get("mdpm gps", [])
        # signed composite values come last in ExifTool's list for each document
        lat = [float(x) for x in e.get("GPSLatitude", [])]
        lon = [float(x) for x in e.get("GPSLongitude", [])]
        refs_lat = e.get("GPSLatitudeRef", [])
        refs_lon = e.get("GPSLongitudeRef", [])
        signed_lat = [abs(v) * (-1 if r == "S" else 1) for v, r in zip(lat[: len(refs_lat)], refs_lat)]
        signed_lon = [abs(v) * (-1 if r == "W" else 1) for v, r in zip(lon[: len(refs_lon)], refs_lon)]
        compare("latitude", [r[0] for r in g], signed_lat, bad)
        compare("longitude", [r[1] for r in g], signed_lon, bad)
        alt = [float(x) for x in e.get("GPSAltitude", [])][: len(g)]
        compare("altitude", [r[2] for r in g], alt, bad)
        compare("DOP", [r[5] for r in g], [float(x) for x in e.get("GPSDOP", e.get("GPSDilutionOfPrecision", []))], bad)
        sp = [float(x) / 3.6 for x in e.get("GPSSpeed", [])]
        compare("speed m/s (ExifTool km/h / 3.6)", [r[3] for r in g], sp, bad)
    sys.exit(1 if bad else 0)


main()

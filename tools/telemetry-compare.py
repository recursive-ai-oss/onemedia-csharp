#!/usr/bin/env python3
"""Compares OneMedia.TelemetryCheck dumps with `exiftool -ee -n -b -j -G3 -a` output of the same GoPro file (-b keeps long sensor lists as text).

usage: telemetry-compare.py <file.ours.tsv> <exiftool.json>

Per channel it prints our sample count, ExifTool's count, the largest absolute difference per component and how many samples differ by more
than 1e-6 (relative to the larger magnitude). ExifTool reports one tag per sample for GPS and one list per payload for the sensors; both
are flattened into file order here.
"""
import re
import sys
from collections import OrderedDict, defaultdict

# our GPMF key -> (ExifTool tag names to try, number of components per sample)
MAP = {
    'ACCL': (['Accelerometer'], 3), 'GYRO': (['Gyroscope'], 3), 'MAGN': (['Magnetometer'], 3), 'GRAV': (['GravityVector'], 3),
    'CORI': (['CameraOrientation'], 4), 'IORI': (['ImageOrientation'], 4), 'SHUT': (['ExposureTimes'], 1),
    'ISOG': (['ImageSensorGain', 'ISOSpeeds'], 1), 'ISOE': (['ISOSpeeds', 'ImageSensorGain'], 1), 'WBAL': (['ColorTemperatures'], 1),
    'WRGB': (['WhiteBalanceRGB'], 3), 'YAVG': (['LumaAverage'], 1), 'UNIF': (['InputUniformity'], 1), 'FCNM': (['FaceNumbers'], 1),
}
GPS = [('GPSLatitude', 0), ('GPSLongitude', 1), ('GPSAltitude', 2), ('GPSSpeed', 3), ('GPSSpeed3D', 4)]


class ExifTool:
    """Per-tag values from `exiftool -ee -n -b -j -G3 -a`, in file order (Doc1, Doc1-1, Doc1-2, ..., Doc2)."""

    def __init__(self, json_path):
        import json
        doc = json.load(open(json_path, encoding='utf8'))[0]
        order = []
        for key, value in doc.items():
            m = re.match(r'Doc(\d+)(?:-(\d+))?:(.+)$', key)
            if m:
                order.append((int(m.group(1)), int(m.group(2) or 0), m.group(3), str(value)))
        order.sort(key=lambda x: (x[0], x[1]))
        self.tags = defaultdict(list)
        for _, _, tag, value in order:
            self.tags[tag].append(value)

    def __contains__(self, tag):
        return tag in self.tags

    def __getitem__(self, tag):
        return self.tags.get(tag, [])


def numbers(chunks):
    out = []
    for c in chunks:
        for tok in re.split(r'[ ,]+', c.strip()):
            try:
                out.append(float(tok))
            except ValueError:
                pass
    return out


def read_ours(path):
    ch = OrderedDict()
    for line in open(path, encoding='utf8'):
        f = line.rstrip('\n').split('\t')
        key = (f[1], f[2])
        if f[5] == 'RAW':
            continue
        ch.setdefault(key, []).append((f[7], [float(x) for x in f[8:]], int(f[6])))
    return ch


def compare(label, ours_vals, et_vals, comps):
    n = min(len(ours_vals), len(et_vals) // comps)
    worst = [0.0] * comps
    bad = 0
    for i in range(n):
        o, e = ours_vals[i], et_vals[i * comps:(i + 1) * comps]
        d = [abs(a - b) for a, b in zip(o, e)]
        for c in range(comps):
            worst[c] = max(worst[c], d[c])
        if any(x > 1e-6 * max(1.0, abs(a), abs(b)) for x, a, b in zip(d, o, e)):
            bad += 1
    print(f'{label}: ours {len(ours_vals)} exiftool {len(et_vals) // comps} compared {n} differing {bad} max|diff| {" ".join(f"{w:.3g}" for w in worst)}')
    return bad == 0 and len(ours_vals) == len(et_vals) // comps


def main():
    ours = read_ours(sys.argv[1])
    et = ExifTool(sys.argv[2])
    ok = True
    for (name, dev), rows in ours.items():
        if name in MAP:
            tagnames, comps = MAP[name]
            tag = next((t for t in tagnames if t in et), None)
            if tag is None:
                print(f'{name}: ours {len(rows)} exiftool has none of {tagnames}')
                continue
            comps = len(rows[0][1])  # the stream's own component count (MAGN has four on GoPro MAX)
            ok &= compare(f'{name}/{tag}', [r[1][:comps] for r in rows], numbers(et[tag]), comps)
        elif name == 'GPS5':
            for tag, idx in GPS:
                if tag in et:
                    # ExifTool reports GPSSpeed and GPSSpeed3D in km/h; the stream carries m/s.
                    k = 3.6 if tag.startswith('GPSSpeed') else 1.0
                    ok &= compare(f'{name}/{tag}' + (' (ours x3.6)' if k != 1.0 else ''), [[r[1][idx] * k] for r in rows], numbers(et[tag]), 1)
            times = {r[0] for r in rows if r[0] != '-'}
            ets = {re.sub(r'^(\d+):(\d+):(\d+) ', r'\1-\2-\3 ', t) for t in et['GPSDateTime']}
            missing = sorted(ets - times)
            print(f'{name}/GPSDateTime: block times exiftool {len(ets)} found among ours {len(ets) - len(missing)}' + (f' missing e.g. {missing[:2]}' if missing else ''))
            fixes = sorted({int(float(x)) for x in et['GPSMeasureMode']})
            print(f'{name}/GPSMeasureMode: exiftool values {fixes} ours {sorted({r[2] for r in rows})}')
            dop = sorted({round(float(x), 2) for x in et['GPSHPositioningError']})
            odop = sorted({round(r[1][5], 2) for r in rows if len(r[1]) > 5 and r[1][5] == r[1][5]})
            print(f'{name}/GPSHPositioningError: distinct exiftool {len(dop)} ours {len(odop)} equal {dop == odop}')
    print('ALL EQUAL' if ok else 'DIFFERENCES')


main()

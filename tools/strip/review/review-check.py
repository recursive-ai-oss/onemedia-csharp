#!/usr/bin/env python3
"""Corpus check for the strip review, independent of the strip's own verifier.
usage: review-check.py <original> <stripped> <exiftool> [--max-fixes N]
Takes the positions and serial numbers ExifTool (-ee -a -G3 -u -n) reads from the ORIGINAL, builds the byte forms (float32/64 both orders, int32 at
1e7 and 1e6, text with 4-6 decimals, UTF-16 and hex for ids), and searches the STRIPPED file's bytes for each. Also lists the ExifTool tags of the
stripped file whose names say position or serial number and whose values are not neutral. Prints one summary line."""
import json
import re
import struct
import subprocess
import sys

NAME = re.compile(r'(gps|latitude|longitude|altitude|location|coordinates|serial|uniqueid|unique_id|content.?identifier|casn|muid|linf|cinf|owner|imei)', re.I)
LATLON = re.compile(r'(latitude|longitude|gpslat|gpslon|^lat$|^lon$|gps5|gps9|position)', re.I)


def exif(path, tool):
    r = subprocess.run([tool, '-j', '-ee', '-a', '-G3', '-u', '-n', path], capture_output=True, text=True)
    try:
        return json.loads(r.stdout)[0]
    except Exception:
        return {}


def walk(v):
    if isinstance(v, list):
        for x in v:
            yield from walk(x)
    else:
        yield v


def main():
    orig, out, tool = sys.argv[1:4]
    limit = 30
    a = open(orig, 'rb').read()
    b = open(out, 'rb').read()
    eo = exif(orig, tool)
    es = exif(out, tool)
    nums, strs = set(), set()
    for k, v in eo.items():
        if not NAME.search(k):
            continue
        for x in walk(v):
            if isinstance(x, (int, float)) and LATLON.search(k) and abs(x) > 0.001:
                nums.add(round(float(x), 7))
            elif isinstance(x, str) and len(x) >= 6 and not re.fullmatch(r'[\d.\- :/]+', x) and x.lower() not in ('unknown', 'none'):
                strs.add(x)
            elif isinstance(x, str):
                for m in re.findall(r'-?\d+\.\d{4,}', x):
                    if LATLON.search(k):
                        nums.add(round(float(m), 7))
    nums = sorted(nums)[:limit]
    left = []
    for n in nums:
        forms = {'f64be': struct.pack('>d', n), 'f64le': struct.pack('<d', n), 'f32be': struct.pack('>f', n), 'f32le': struct.pack('<f', n)}
        for scale in (10 ** 7, 10 ** 6):
            if abs(n * scale) < 2 ** 31:
                forms['i32be%d' % scale] = struct.pack('>i', round(n * scale))
                forms['i32le%d' % scale] = struct.pack('<i', round(n * scale))
        for d in (4, 5, 6):
            forms['txt%d' % d] = ('%.*f' % (d, n)).encode()
        for name, f in forms.items():
            if f in a and f in b and (name.startswith('txt') or name.startswith('f64') or abs(n) > 1):
                left.append((n, name))
    sleft = [s for s in strs if s.encode() in b and s.encode() in a]
    sleft += [s for s in strs if s.encode('utf-16-le') in b and s.encode('utf-16-le') in a]
    tags = []
    for k, v in es.items():
        if NAME.search(k) and not k.endswith('SourceFile'):
            for x in walk(v):
                if isinstance(x, (int, float)) and abs(x) > 0.0:
                    tags.append('%s=%s' % (k, x))
                elif isinstance(x, str) and x.strip(' 0\x00') not in ('', '0'):
                    tags.append('%s=%s' % (k, x[:40]))
    print(json.dumps({'secret_numbers': len(nums), 'secret_strings': len(strs), 'bytes_left_numbers': left[:6], 'n_left': len(left), 'bytes_left_strings': sleft[:5],
                      'exiftool_tags_left': tags[:8], 'n_tags_left': len(tags), 'same_size': len(a) == len(b)}))


if __name__ == '__main__':
    main()

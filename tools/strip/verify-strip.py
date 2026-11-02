#!/usr/bin/env python3
"""Checks one stripped file against its original with the pinned oracles, black box.

  verify-strip.py <original> <stripped> [--exiftool PATH] [--mediainfo PATH] [--ffprobe PATH]

Prints one JSON object:
  exiftool_left     location / serial / identifier tags ExifTool 13.x still shows in the stripped file (-ee -a -G3)
  mediainfo_left    lines MediaInfo still shows that name a location or an identifier
  bytes_left        secrets from the original (strings, ISO 6709 forms, big/little-endian binary forms of the coordinates, hex ids) found in the stripped bytes
  framemd5_equal    with --ffmpeg: ffmpeg's decoded frame hashes of the first video and audio stream are equal (the check for MDPM, where the packet bytes differ by design)
  av_payload_equal  per-stream SHA-256 of ffprobe packet data for audio and video streams, original against stripped
The secrets are read from the original with ExifTool (-n, so numbers are plain): GPS coordinates of every sample, and every tag whose name says serial, unique id or content id.
Nothing is printed that is a secret; counts only.
"""
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import sys

TAG_WORDS = re.compile(r'gps|location|latitude|longitude|serial|uniqueid|mediauid|contentid|content identifier|ownername|cameraowner|imageuniqueid|coordinates|iso6709|position', re.I)
KEEP = re.compile(r'GPSMeasure|^\[(Track|Composite)\]', re.I)


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, errors='replace').stdout


def exif_lines(exiftool, path, numeric):
    args = [exiftool, '-ee', '-a', '-G3', '-s', '-api', 'largefilesupport=1']
    if numeric:
        args.append('-n')
    out = []
    for line in run(args + [path]).splitlines():
        m = re.match(r'\[([^\]]+)\]\s+(\S+)\s+:\s?(.*)$', line)
        if m:
            out.append((m.group(1), m.group(2), m.group(3)))
    return out


def secrets_of(lines):
    strings, floats, hexes, pairs = set(), set(), set(), set()
    lat = {}
    lon = {}
    for grp, name, value in lines:
        if name in ('GPSLatitude', 'GPSLongitude') and re.fullmatch(r'-?\d+(\.\d+)?', value.strip()):
            v = float(value)
            if v != 0:
                floats.add(v)
                (lat if name == 'GPSLatitude' else lon).setdefault(grp, []).append(v)
            continue
        if name in ('GPSAltitude', 'GPSSpeed', 'GPSSpeed3D', 'GPSDOP', 'GPSHPositioningError', 'GPSMeasureMode', 'GPSDateTime', 'GPSTrack', 'GPSAltitudeRef', 'GPSLatitudeRef', 'GPSLongitudeRef'):
            continue
        if re.search(r'serial|unique|mediauid|contentid|content identifier|ownername|cameraowner|coordinates|iso6709|location', name, re.I):
            v = value.strip()
            if len(v) >= 6 and not re.fullmatch(r'0+', v):
                if re.fullmatch(r'[0-9a-fA-F]{12,}', v):
                    hexes.add(bytes.fromhex(v[: len(v) // 2 * 2]).rstrip(b'\0')[:16])
                strings.add(v)
    for grp in lat:
        for a, b in zip(lat[grp], lon.get(grp, [])):
            pairs.add((a, b))
    return strings, floats, hexes, pairs


def forms(strings, floats, hexes, pairs):
    out = {}
    for s in strings:
        out['str:' + s[:12]] = s.encode('utf-8', 'replace')
        out['str16:' + s[:12]] = s.encode('utf-16-be', 'replace')
    for h in hexes:
        if len(h) >= 8:
            out['hex:' + h.hex()[:12]] = h
    for v in floats:
        for dec in (4, 5, 6):
            t = f'{v:.{dec}f}'
            out[f'dec{dec}:{t}'] = t.encode()
        out[f'iso:{v:+.4f}'] = f'{v:+.4f}'.encode()
        out[f'f64le:{v}'] = struct.pack('<d', v)
        out[f'f64be:{v}'] = struct.pack('>d', v)
    for a, b in pairs:
        for scale in (1e7, 1e6):
            ia, ib = round(a * scale), round(b * scale)
            if not (-2**31 <= ia < 2**31 and -2**31 <= ib < 2**31):
                continue
            out[f'i32pair-be:{ia},{ib}'] = struct.pack('>ii', ia, ib)
            out[f'i32pair-le:{ia},{ib}'] = struct.pack('<ii', ia, ib)
        out[f'f32pair-be:{a},{b}'] = struct.pack('>ff', a, b)
        out[f'f32pair-le:{a},{b}'] = struct.pack('<ff', a, b)
    return out


def search(path, pats):
    found = {}
    size = os.path.getsize(path)
    chunk = 64 << 20
    overlap = 64
    with open(path, 'rb') as f:
        pos = 0
        prev = b''
        while pos < size:
            data = prev + f.read(chunk)
            for k, p in pats.items():
                if p and p in data:
                    found[k] = True
            prev = data[-overlap:]
            pos += chunk
    return sorted(found)


def av_hashes(ffprobe, path):
    types = {}
    for line in run([ffprobe, '-v', 'error', '-show_entries', 'stream=index,codec_type', '-of', 'csv', path]).splitlines():
        parts = line.split(',')
        if parts[0] == 'stream' and len(parts) >= 3:
            types[parts[1]] = parts[2]
    hashes = {}
    out = run([ffprobe, '-v', 'error', '-show_entries', 'packet=stream_index,data_hash', '-show_data_hash', 'SHA256', '-of', 'csv', path])
    for line in out.splitlines():
        parts = line.split(',')
        if parts[0] == 'packet' and len(parts) >= 3:
            hashes.setdefault(parts[1], hashlib.sha256()).update(parts[2].encode())
    return {k: v.hexdigest() for k, v in hashes.items() if types.get(k) in ('video', 'audio')}


def frame_md5(ffmpeg, path):
    """Decoded frames of the first video and first audio stream as ffmpeg's framemd5 lines (header comments dropped)."""
    lines = []
    for sel in ('0:v:0', '0:a:0'):
        out = run([ffmpeg, '-v', 'error', '-i', path, '-map', sel, '-f', 'framemd5', '-'])
        lines.append([l for l in out.splitlines() if l and not l.startswith('#')])
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('original')
    ap.add_argument('stripped')
    ap.add_argument('--exiftool', default='exiftool')
    ap.add_argument('--mediainfo', default='mediainfo')
    ap.add_argument('--ffprobe', default='ffprobe')
    ap.add_argument('--ffmpeg', default=None, help='also compare decoded frames (framemd5) of the original and the stripped file')
    ap.add_argument('--ids-only', action='store_true', help='only device identifiers were stripped: do not require location to be gone')
    ap.add_argument('--location-only', action='store_true')
    a = ap.parse_args()
    orig = exif_lines(a.exiftool, a.original, True)
    s_strings, s_floats, s_hexes, s_pairs = secrets_of(orig)
    if a.ids_only:
        s_floats, s_pairs = set(), set()
    if a.location_only:
        s_strings, s_hexes = set(), set()
    pats = forms(s_strings, s_floats, s_hexes, s_pairs)
    after = exif_lines(a.exiftool, a.stripped, False)
    NEUTRAL = re.compile(r"^(|0|0 0|0 m|0 km/h|0 m/s|Unknown|No Measurement|0000:00:00 00:00:00|0 deg 0' 0\.00\" [NSEW](, 0 deg 0' 0\.00\" [NSEW])?|[0 ]+)$")
    shown = [(g, n, v.strip()) for g, n, v in after if TAG_WORDS.search(n) and not KEEP.search(f'[{g}]') and not re.search(r'GPSMeasure', n)]
    left = sorted({f'{g} {n}' for g, n, v in shown if not NEUTRAL.match(v)})
    neutral = sorted({f'{g} {n}' for g, n, v in shown if NEUTRAL.match(v)})
    mi = [l.strip() for l in run([a.mediainfo, a.stripped]).splitlines() if TAG_WORDS.search(l.split(':')[0] if ':' in l else l)]
    res = {
        'original': os.path.basename(a.original),
        'secrets': {'strings': len(s_strings), 'coordinates': len(s_floats), 'hex_ids': len(s_hexes), 'forms_searched': len(pats)},
        'bytes_left': search(a.stripped, pats),
        'exiftool_tags_with_real_values': left,
        'exiftool_tags_with_neutral_values': len(neutral),
        'mediainfo_lines_still_shown': [re.sub(r'\s+', ' ', l) for l in mi],
        'size_equal': os.path.getsize(a.original) == os.path.getsize(a.stripped),
    }
    ho, hs = av_hashes(a.ffprobe, a.original), av_hashes(a.ffprobe, a.stripped)
    res['av_payload_equal'] = ho == hs
    res['av_streams'] = len(ho)
    if a.ffmpeg:
        fo, fs = frame_md5(a.ffmpeg, a.original), frame_md5(a.ffmpeg, a.stripped)
        res['framemd5_equal'] = fo == fs
        res['framemd5_frames'] = [len(x) for x in fo]
    json.dump(res, sys.stdout, indent=1)
    print()


main()

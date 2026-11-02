#!/usr/bin/env python3
"""Writes small AVCHD-style transport streams whose H.264 video carries MDPM SEI messages with GPS entries.

The video is ffmpeg's test pattern; the SEI NAL unit (user data unregistered, UUID 17ee8c60-f84d-11d9-8cd6-0800200c9a66, marker
"MDPM") is inserted before every IDR picture. The GPS entry layout (tags 0xb0-0xcc, each a tag byte and four data bytes, rationals as
two big-endian 16-bit halves) is the one the tag documentation lists; ExifTool reading the result back is the check.

usage: gen-mdpm-gps-fixtures.py OUTDIR [FFMPEG]
"""
import os
import struct
import subprocess
import sys

UUID = bytes.fromhex("17ee8c60f84d11d98cd60800200c9a66")


def bcd(n):
    return ((n // 10) << 4) | (n % 10)


def rat(num, den):
    return struct.pack(">HH", num, den)


def entry(tag, data):
    assert len(data) == 4
    return bytes([tag]) + data


def gps_entries(lat, lon, alt, sec, ref_n="N", ref_e="E", with_gps=True):
    e = [
        entry(0x18, bytes([0xFF, 0x20, 0x26, 0x11])),  # flags, century 20, year 26, month 11
        entry(0x19, bytes([bcd(1), bcd(12), bcd(34), bcd(sec)])),  # day 1, 12:34:ss
        entry(0xE0, bytes([0x01, 0x08, 0x00, 0x01])),  # Sony
    ]
    if not with_gps:
        return e
    d, m, s = lat
    d2, m2, s2 = lon
    e += [
        entry(0xB0, bytes([2, 2, 0, 0])),
        entry(0xB1, bytes([ord(ref_n), 0, 0, 0])),
        entry(0xB2, rat(d, 1)),
        entry(0xB3, rat(m, 1)),
        entry(0xB4, rat(s, 100)),
        entry(0xB5, bytes([ord(ref_e), 0, 0, 0])),
        entry(0xB6, rat(d2, 1)),
        entry(0xB7, rat(m2, 1)),
        entry(0xB8, rat(s2, 100)),
        entry(0xB9, bytes([0, 0, 0, 0])),
        entry(0xBA, rat(alt, 10)),
        entry(0xBB, rat(12, 1)),
        entry(0xBC, rat(34, 1)),
        entry(0xBD, rat(sec * 100, 100)),
        entry(0xBE, bytes([ord("A"), 0, 0, 0])),
        entry(0xBF, bytes([ord("3"), 0, 0, 0])),
        entry(0xC0, rat(15, 10)),
        entry(0xC1, bytes([ord("K"), 0, 0, 0])),
        entry(0xC2, rat(123, 10)),
        entry(0xC7, bytes([ord("W"), ord("G"), ord("S"), ord("-")])),
        entry(0xC8, bytes([ord("8"), ord("4"), 0, 0])),
        entry(0xCA, b"2026"),
        entry(0xCB, b"11\x00\x00"),
        entry(0xCC, b"01\x00\x00"),
    ]
    return e


def sei_nal(entries):
    entries = sorted(entries, key=lambda x: x[0])  # cameras write the tags in ascending order
    payload = UUID + b"MDPM" + bytes([len(entries)]) + b"".join(entries)
    rbsp = bytes([5, len(payload)]) + payload + b"\x80"
    out = bytearray()
    zeros = 0
    for b in rbsp:  # emulation prevention (H.264 7.4.1)
        if zeros >= 2 and b <= 3:
            out.append(3)
            zeros = 0
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return b"\x00\x00\x00\x01\x06" + bytes(out)


def nal_units(data):
    starts = []
    i = 0
    while True:
        i = data.find(b"\x00\x00\x01", i)
        if i < 0:
            break
        starts.append(i)
        i += 3
    for k, s in enumerate(starts):
        e = starts[k + 1] if k + 1 < len(starts) else len(data)
        yield data[s + 3 : e].rstrip(b"\x00") if k + 1 < len(starts) else data[s + 3 : e]


def main():
    out = sys.argv[1]
    ff = sys.argv[2] if len(sys.argv) > 2 else "ffmpeg"
    os.makedirs(out, exist_ok=True)
    raw = os.path.join(out, "base.h264")
    subprocess.run([ff, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=25:duration=3", "-c:v", "libx264", "-g", "25", "-bf", "0",
                    "-x264-params", "keyint=25:min-keyint=25:scenecut=0", "-bsf:v", "h264_mp4toannexb", "-f", "h264", raw], check=True)
    data = open(raw, "rb").read()
    variants = {
        "mdpm-gps": dict(with_gps=True),
        "mdpm-nogps": dict(with_gps=False),
        "mdpm-south-west": dict(with_gps=True, ref_n="S", ref_e="W"),
    }
    for name, kw in variants.items():
        buf = bytearray()
        gop = 0
        for nal in nal_units(data):
            t = nal[0] & 0x1F
            if t == 5:
                lat = (52, 30, 1550 + gop)
                lon = (13, 24, 2200 + gop)
                buf += sei_nal(gps_entries(lat, lon, 1234 + gop, gop, **kw))
                gop += 1
            buf += b"\x00\x00\x00\x01" + nal
        h = os.path.join(out, name + ".h264")
        open(h, "wb").write(buf)
        subprocess.run([ff, "-v", "error", "-y", "-r", "25", "-f", "h264", "-i", h, "-c", "copy", "-f", "mpegts", os.path.join(out, name + ".mts")], check=True)
        os.remove(h)
    os.remove(raw)


if __name__ == "__main__":
    main()

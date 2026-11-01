#!/usr/bin/env python3
"""Adds metadata blocks, or an ID3v2 prefix, to a FLAC file written by ffmpeg, so fixtures cover blocks ffmpeg does not write.

  flacpatch.py IN OUT [--id3 BYTES] [--seektable POINTS FFPROBE] [--padding BYTES] [--application] [--cuesheet]

--seektable POINTS FFPROBE   builds a SEEKTABLE of POINTS evenly spaced real points from the packet list of `FFPROBE -show_packets` (positions and
                             sample counts), plus one placeholder point.
The tool only rewrites the metadata area; the frames are copied unchanged.
"""
import struct
import subprocess
import sys


def blocks_of(data, start):
    """Yields (type, body) for the metadata blocks after 'fLaC' at `start`, and finally the offset of the first frame."""
    p = start + 4
    out = []
    while True:
        hdr = data[p]
        last = hdr & 0x80
        typ = hdr & 0x7F
        length = int.from_bytes(data[p + 1:p + 4], "big")
        out.append((typ, data[p + 4:p + 4 + length]))
        p += 4 + length
        if last:
            return out, p


def block(typ, body, last=False):
    return bytes([(0x80 if last else 0) | typ]) + len(body).to_bytes(3, "big") + body


def main():
    args = sys.argv[1:]
    src, dst = args[0], args[1]
    opts = args[2:]
    data = open(src, "rb").read()
    start = 0
    if data[:3] == b"ID3":
        size = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
        start = size
    blocks, audio = blocks_of(data, start)
    frames = data[audio:]
    extra = []
    id3 = b""
    i = 0
    while i < len(opts):
        o = opts[i]
        if o == "--id3":
            n = int(opts[i + 1])
            text = b"\x03Fixture title"
            ts = len(text)
            frame = b"TIT2" + bytes([(ts >> 21) & 0x7F, (ts >> 14) & 0x7F, (ts >> 7) & 0x7F, ts & 0x7F]) + b"\0\0" + text
            size = max(n - 10, len(frame))
            id3 = b"ID3\x04\x00\x00" + bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F, (size >> 7) & 0x7F, size & 0x7F]) + frame + b"\0" * (size - len(frame))
            i += 2
        elif o == "--padding":
            extra.append((1, b"\0" * int(opts[i + 1])))
            i += 2
        elif o == "--application":
            extra.append((2, b"oneM" + bytes(range(20))))
            i += 1
        elif o == "--cuesheet":
            # a cue sheet with one track and the lead-out (RFC 9639 section 8.7)
            cs = b"\0" * 128 + struct.pack(">Q", 0) + b"\0" + b"\0" * 258 + bytes([2])
            cs += struct.pack(">Q", 0) + bytes([1]) + b"\0" * 12 + b"\0" + b"\0" * 13 + bytes([1])
            cs += bytes([0]) * 8 + struct.pack(">QB", 0, 1) + b"\0" * 3
            cs += struct.pack(">Q", 0) + bytes([170]) + b"\0" * 12 + b"\0" + b"\0" * 13 + bytes([0])
            extra.append((5, cs))
            i += 1
        elif o == "--seektable":
            points = int(opts[i + 1])
            probe = subprocess.run([opts[i + 2], "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=pos,pts,duration", "-of", "csv=p=0", src], capture_output=True, text=True, check=True).stdout
            rows = [r.split(",") for r in probe.strip().splitlines()]
            first_pos = int(rows[0][2])
            step = max(1, len(rows) // points)
            table = b""
            for r in rows[::step][:points]:
                table += struct.pack(">QQH", int(r[0]), int(r[2]) - first_pos, int(r[1]))
            table += struct.pack(">QQH", 0xFFFFFFFFFFFFFFFF, 0, 0)
            extra.append((3, table))
            i += 3
        else:
            raise SystemExit("unknown option " + o)
    all_blocks = [blocks[0]] + extra + blocks[1:]
    out = id3 + b"fLaC"
    for n, (typ, body) in enumerate(all_blocks):
        out += block(typ, body, last=(n == len(all_blocks) - 1))
    open(dst, "wb").write(out + frames)


main()

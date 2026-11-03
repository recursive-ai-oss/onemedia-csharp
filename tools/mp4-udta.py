#!/usr/bin/env python3
"""Lists the top-level boxes of an MP4/MOV file and the children of moov/udta and moov/meta (type, size, uuid if any): where a camera keeps its Exif.

  mp4-udta.py FILE
"""
import struct, sys

f = open(sys.argv[1], "rb"); f.seek(0, 2); n = f.tell()

def boxes(start, end):
    pos = start
    while pos + 8 <= end:
        f.seek(pos); size, typ = struct.unpack(">I4s", f.read(8)); hdr = 8
        if size == 1: size = struct.unpack(">Q", f.read(8))[0]; hdr = 16
        elif size == 0: size = end - pos
        if size < hdr: return
        yield typ.decode("latin1"), pos, hdr, min(size, end - pos)
        pos += size

def show(start, end, indent, depth):
    for t, pos, hdr, size in boxes(start, end):
        extra = ""
        if t == "uuid":
            f.seek(pos + hdr); extra = " " + f.read(16).hex()
        print("  " * indent + f"{t!r} {size}{extra}")
        if depth > 0 and t in ("moov", "udta", "meta"):
            show(pos + hdr + (4 if t == "meta" else 0), pos + size, indent + 1, depth - 1)

show(0, n, 0, 2)

#!/usr/bin/env python3
"""Prints the sample-table headers of every track of an MP4/MOV file: stsd entry, stsz, stts, ctts and edit list (first entries only).

  mp4-tables.py FILE
A debugging aid for explaining differential results; it reads only the structure.
"""
import struct, sys

def boxes(f, start, end):
    pos = start
    while pos + 8 <= end:
        f.seek(pos); h = f.read(8)
        size, typ = struct.unpack(">I4s", h)
        hdr = 8
        if size == 1:
            size = struct.unpack(">Q", f.read(8))[0]; hdr = 16
        elif size == 0:
            size = end - pos
        if size < hdr: return
        yield typ.decode("latin1"), pos + hdr, pos + size
        pos += size

def walk(f, start, end, path, out):
    for typ, s, e in boxes(f, start, end):
        p = path + "/" + typ
        if typ in ("moov", "trak", "mdia", "minf", "stbl", "edts"):
            walk(f, s, e, p, out)
        elif typ in ("stsd", "stsz", "stts", "ctts", "elst", "mdhd", "tkhd", "mvhd", "stco", "co64", "stsc", "stss", "hdlr"):
            f.seek(s); out.append((p, typ, f.read(min(e - s, 96))))

def main():
    f = open(sys.argv[1], "rb")
    f.seek(0, 2); n = f.tell()
    out = []
    walk(f, 0, n, "", out)
    for p, typ, d in out:
        ver = d[0]
        if typ == "stsd":
            cnt = struct.unpack(">I", d[4:8])[0]
            fmt = d[12:16].decode("latin1")
            print(p, "entries", cnt, "format", fmt, "head", d[16:16 + 40].hex())
        elif typ == "stsz":
            ss, sc = struct.unpack(">II", d[4:12]); print(p, "sample_size", ss, "count", sc, "first", [struct.unpack(">I", d[12 + 4 * i:16 + 4 * i])[0] for i in range(min(6, max(0, (len(d) - 12) // 4)))] if ss == 0 else "")
        elif typ == "stts":
            c = struct.unpack(">I", d[4:8])[0]; print(p, "entries", c, [struct.unpack(">II", d[8 + 8 * i:16 + 8 * i]) for i in range(min(3, c, (len(d) - 8) // 8))])
        elif typ == "ctts":
            c = struct.unpack(">I", d[4:8])[0]; print(p, "v", ver, "entries", c, [struct.unpack(">Ii", d[8 + 8 * i:16 + 8 * i]) for i in range(min(4, c, (len(d) - 8) // 8))])
        elif typ == "elst":
            c = struct.unpack(">I", d[4:8])[0]
            ents = []
            for i in range(min(c, 4)):
                if ver == 1: ents.append(struct.unpack(">QqHH", d[8 + 20 * i:28 + 20 * i])[:2] + (struct.unpack(">q", d[16 + 20 * i:24 + 20 * i])[0],) if False else struct.unpack(">Qq", d[8 + 20 * i:24 + 20 * i]))
                else: ents.append(struct.unpack(">Ii", d[8 + 12 * i:16 + 12 * i]))
            print(p, "v", ver, "entries", c, "(segment_duration, media_time)", ents)
        elif typ == "mdhd":
            ts = struct.unpack(">I", d[12:16])[0] if ver == 0 else struct.unpack(">I", d[20:24])[0]; print(p, "timescale", ts)
        elif typ == "hdlr":
            print(p, d[8:12].decode("latin1"))
        elif typ == "mvhd":
            ts = struct.unpack(">I", d[12:16])[0] if ver == 0 else struct.unpack(">I", d[20:24])[0]; print(p, "timescale", ts)

main()

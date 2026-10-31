#!/usr/bin/env python3
"""Rewrites a Matroska file so that Tracks come after every Cluster and SeekHead, Cues and Void are dropped.

The result is a valid file with no index at the front: a reader has to walk the Cluster headers to find Tracks. It is used to
measure what that layout costs a probe. Media bytes are copied unchanged.

    make-tracks-last.py <input.mkv> <output.mkv>
"""
import shutil
import sys

SEGMENT = 0x18538067
SEEKHEAD, VOID, CUES, TRACKS = 0x114D9B74, 0xEC, 0x1C53BB6B, 0x1654AE6B


def read_vint(f, keep_marker):
    b = f.read(1)
    if not b:
        return None, 0
    first = b[0]
    length = 1
    mask = 0x80
    while length <= 8 and not first & mask:
        length += 1
        mask >>= 1
    rest = f.read(length - 1)
    raw = bytes([first]) + rest
    value = int.from_bytes(raw, "big")
    if not keep_marker:
        value &= (1 << (7 * length)) - 1
    return value, length


def header(f):
    start = f.tell()
    ident, il = read_vint(f, True)
    if ident is None:
        return None
    size, sl = read_vint(f, False)
    return ident, size, il + sl, start


def main(src, dst):
    with open(src, "rb") as f, open(dst, "wb") as out:
        ebml = header(f)
        f.seek(ebml[3])
        out.write(f.read(ebml[2] + ebml[1]))
        seg = header(f)
        assert seg[0] == SEGMENT
        seg_data_start = f.tell()
        seg_end = seg_data_start + seg[1]
        keep, tracks = [], None
        while f.tell() < seg_end:
            h = header(f)
            if h is None:
                break
            ident, size, hl, start = h
            total = hl + size
            if ident == TRACKS:
                tracks = (start, total)
            elif ident not in (SEEKHEAD, VOID, CUES):
                keep.append((start, total))
            f.seek(start + total)
        assert tracks is not None
        keep.append(tracks)
        new_size = sum(t for _, t in keep)
        out.write(SEGMENT.to_bytes(4, "big") + b"\x01" + new_size.to_bytes(7, "big"))
        for start, total in keep:
            f.seek(start)
            remaining = total
            while remaining:
                chunk = f.read(min(remaining, 1 << 20))
                out.write(chunk)
                remaining -= len(chunk)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])

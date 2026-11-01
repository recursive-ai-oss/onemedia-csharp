#!/usr/bin/env python3
"""Wraps a raw DV stream into an AVI file as DV type 1: one 'iavs' stream whose chunks are whole DV frames,
picture and sound together. Writes an idx1 index. Used to make type-1 test files from DV written by a tool.

  wrap_dv_avi.py in.dv out.avi [--frames N]

The DV frame size is taken from the DSF bit of the first DIF header block (10 sequences of 12000 bytes for 525/60,
12 for 625/50, doubled for 50 Mbit/s when the stream says so through its length being a multiple of the larger size).
"""
import struct
import sys


def chunk(fcc, data):
    pad = b"\0" if len(data) & 1 else b""
    return fcc + struct.pack("<I", len(data)) + data + pad


def lst(kind, payload):
    return b"LIST" + struct.pack("<I", len(payload) + 4) + kind + payload


def main():
    src, dst = sys.argv[1], sys.argv[2]
    limit = int(sys.argv[4]) if len(sys.argv) > 4 and sys.argv[3] == "--frames" else None
    data = open(src, "rb").read()
    pal = bool(data[3] & 0x80)
    seq = 12 if pal else 10
    size = seq * 12000
    # 50 Mbit/s streams hold twice as many DIF sequences per frame; the second channel's blocks have FSC set.
    if len(data) >= size * 2 and data[size] == 0x1F and data[size + 1] & 0x08:
        size *= 2
    frames = len(data) // size
    if limit:
        frames = min(frames, limit)
    rate, scale = (25, 1) if pal else (30000, 1001)
    avih = struct.pack("<14I", 40000 if pal else 33367, size * 25, 0, 0x110, frames, 0, 1, size, 720, 576 if pal else 480, 0, 0, 0, 0)
    strh = b"iavs" + b"dvsd" + struct.pack("<IHHIIIIIIIIhhhh", 0, 0, 0, 0, scale, rate, 0, frames, size, 0xFFFFFFFF, 0, 0, 0, 0, 0)
    dvinfo = struct.pack("<8I", 0, 0, 0, 0, 0, 0, 0, 0)
    strl = lst(b"strl", chunk(b"strh", strh) + chunk(b"strf", dvinfo))
    hdrl = lst(b"hdrl", chunk(b"avih", avih) + strl)
    movi_payload = b""
    index = b""
    off = 4
    for i in range(frames):
        c = chunk(b"00dc", data[i * size:(i + 1) * size])
        index += b"00dc" + struct.pack("<III", 0x10, off, size)
        movi_payload += c
        off += len(c)
    movi = lst(b"movi", movi_payload)
    body = b"AVI " + hdrl + movi + chunk(b"idx1", index)
    open(dst, "wb").write(b"RIFF" + struct.pack("<I", len(body)) + body)


main()

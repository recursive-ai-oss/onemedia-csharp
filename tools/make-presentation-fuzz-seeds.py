#!/usr/bin/env python3
"""Writes seed inputs for the `presentation` fuzz target from the committed presentation fixtures.

    make-presentation-fuzz-seeds.py testdata/streaming testdata/fuzz/presentation

A seed is a flags byte, then the playlist (or manifest), the initialisation resource and the first three media segments, each as a big-endian 16-bit
length and its bytes, in the order a reader asks for them. Resources longer than 65535 bytes are cut.
"""
import os
import re
import sys


def pieces(directory, playlist):
    text = open(os.path.join(directory, playlist), encoding="utf-8", errors="replace").read()
    names = []
    if playlist.endswith(".mpd"):
        names = sorted(n for n in os.listdir(directory) if n.startswith("init"))
        names += sorted(n for n in os.listdir(directory) if n.startswith("chunk") or (n.startswith("media") and n.endswith(".mp4")))[:6]
    else:
        names += re.findall(r'#EXT-X-MAP:URI="([^"]+)"', text)
        names += [l.strip() for l in text.splitlines() if l.strip() and not l.startswith("#")][:3]
    out = [playlist]
    for n in names:
        if n not in out and os.path.exists(os.path.join(directory, n)):
            out.append(n)
    return out


def main():
    src, dst = sys.argv[1], sys.argv[2]
    os.makedirs(dst, exist_ok=True)
    for line in open(os.path.join(src, "MANIFEST.tsv"), encoding="utf-8"):
        name, _kind, playlist = line.rstrip("\n").split("\t")[:3]
        directory = os.path.join(src, name)
        blob = bytearray([2])
        for n in pieces(directory, playlist):
            data = open(os.path.join(directory, n), "rb").read()[:65535]
            blob += len(data).to_bytes(2, "big") + data
        open(os.path.join(dst, name + ".seed"), "wb").write(blob)


main()

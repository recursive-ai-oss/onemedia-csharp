#!/usr/bin/env python3
"""Prints how the chunks of a progressive MP4/MOV are laid out in its media data: contiguity, gaps, order. A debugging aid for the
chunk-offset rebuild in IsoRepair (not used by the sweep).   chunkmap.py <file>"""
import struct
import sys


def boxes(f, start, end):
    pos = start
    while pos + 8 <= end:
        f.seek(pos)
        size, typ = struct.unpack('>I4s', f.read(8))
        hdr = 8
        if size == 1:
            size = struct.unpack('>Q', f.read(8))[0]
            hdr = 16
        elif size == 0:
            size = end - pos
        if size < hdr:
            return
        yield typ, pos, hdr, size
        pos += size


def find(f, start, end, path):
    for typ, pos, hdr, size in boxes(f, start, end):
        if typ == path[0].encode():
            if len(path) == 1:
                yield pos, hdr, size
            else:
                yield from find(f, pos + hdr, pos + size, path[1:])


def main():
    f = open(sys.argv[1], 'rb')
    f.seek(0, 2)
    n = f.tell()
    tracks = []
    for tp, th, ts in find(f, 0, n, ['moov', 'trak']):
        info = {}
        for name in ('stsz', 'stsc', 'stco', 'co64'):
            for p, h, s in find(f, tp + th, tp + ts, ['mdia', 'minf', 'stbl', name]):
                f.seek(p + h + 4)
                info[name] = f.read(s - h - 4)
        sz = info['stsz']
        const, count = struct.unpack('>II', sz[:8])
        sizes = [const] * count if const else list(struct.unpack('>%dI' % count, sz[8:8 + 4 * count]))
        sc = info['stsc']
        k = struct.unpack('>I', sc[:4])[0]
        stsc = [struct.unpack('>III', sc[4 + 12 * i:16 + 12 * i]) for i in range(k)]
        co = info.get('stco') or info.get('co64')
        cn = struct.unpack('>I', co[:4])[0]
        offs = list(struct.unpack('>%d%s' % (cn, 'I' if 'stco' in info else 'Q'), co[4:4 + cn * (4 if 'stco' in info else 8)]))
        chunks = []
        s = 0
        e = 0
        for c in range(cn):
            while e + 1 < len(stsc) and c + 1 >= stsc[e + 1][0]:
                e += 1
            per = stsc[e][1]
            chunks.append((offs[c], sum(sizes[s:s + per]), per))
            s += per
        tracks.append(chunks)
    allc = sorted((o, sz, ti, ci) for ti, ch in enumerate(tracks) for ci, (o, sz, _) in enumerate(ch))
    gaps = 0
    overlaps = 0
    for a, b in zip(allc, allc[1:]):
        if b[0] > a[0] + a[1]:
            gaps += 1
            if gaps <= 5:
                print('gap after track', a[2], 'chunk', a[3], 'at', a[0] + a[1], 'size', b[0] - a[0] - a[1])
        elif b[0] < a[0] + a[1]:
            overlaps += 1
    print('tracks', [len(t) for t in tracks], 'chunks', len(allc), 'gaps', gaps, 'overlaps', overlaps, 'first', allc[0][0], 'end', allc[-1][0] + allc[-1][1])
    print('pattern', ''.join(str(c[2]) for c in allc[:60]))


main()

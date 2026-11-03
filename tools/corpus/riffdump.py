#!/usr/bin/env python3
"""Print the RIFF chunk tree of a file down to two levels, with a hex head for the chunks named on the command line."""
import struct, sys

want = set(sys.argv[2:])
f = open(sys.argv[1], 'rb')
f.seek(0, 2)
size = f.tell()


def walk(off, end, depth, maxdepth):
    while off + 8 <= end:
        f.seek(off)
        cid, sz = struct.unpack('<4sI', f.read(8))
        name = cid.decode('latin1')
        if name in ('RIFF', 'LIST'):
            kind = f.read(4).decode('latin1')
            print('  ' * depth + f'{name} {kind} {sz} @{off}')
            if depth < maxdepth and kind != 'movi':
                walk(off + 12, min(end, off + 8 + sz), depth + 1, maxdepth)
        else:
            print('  ' * depth + f'{name} {sz} @{off}')
            if name in want:
                f.seek(off + 8)
                d = f.read(min(sz, 400))
                print('  ' * depth + '   ' + d.hex())
                print('  ' * depth + '   ' + d.decode('latin1').replace('\x00', '.'))
        off += 8 + sz + (sz & 1)


walk(0, size, 0, 2)

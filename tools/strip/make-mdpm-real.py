#!/usr/bin/env python3
"""Makes a decodable test file whose AVCHD camera data (MDPM) carries position-tagged entries, from a real camcorder recording.

  make-mdpm-real.py <real .mts/.m2ts> <output>

Camcorders without a GPS receiver write no position entries, so the strip would have nothing to remove on a real recording. This copies the file and
changes the tag byte of the entries of every MDPM block whose entries lie inside one transport packet to the position tags 0xB1..0xB8, 0xB9, 0xBA
(latitude, longitude and altitude fields). The data bytes stay as the camera wrote them, so the stream still decodes (the entries are not
video), and the strip must overwrite exactly those entries while ffmpeg decodes the same pictures before and after.
Prints how many blocks and entries were changed.
"""
import sys


def main():
    src, dst = sys.argv[1], sys.argv[2]
    data = bytearray(open(src, 'rb').read())
    stride, prefix = (192, 4) if data[4] == 0x47 and data[196] == 0x47 else (188, 0)
    blocks = entries = 0
    pos = 0
    while True:
        pos = data.find(b'MDPM', pos)
        if pos < 0:
            break
        pkt = pos // stride
        start = pos + 5
        count = data[pos + 4]
        end = start + 5 * count
        if count and end <= (pkt + 1) * stride and start - pkt * stride > prefix + 4:
            for i in range(min(count, 10)):
                tag = data[start + 5 * i]
                if tag < 0xB0 and tag not in (0xE0, 0xE4, 0xE5, 0xE6):
                    data[start + 5 * i] = 0xB1 + (entries % 10)
                    entries += 1
            blocks += 1
        pos += 4
    open(dst, 'wb').write(data)
    print('blocks', blocks, 'entries', entries)


main()

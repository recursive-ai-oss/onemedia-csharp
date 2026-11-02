#!/usr/bin/env python3
"""Test-fixture helper: sets pic_struct in every picture timing SEI message of a transport stream whose H.264 was written by
x264 with pic-struct=1 and no HRD (pic_struct is then the first nibble of the message payload). It also turns the SDT packets
into null packets, because ffprobe 7.0.2 crashes on the SDT that ffmpeg writes.
Usage: patch-pic-struct.py IN.ts PIC_STRUCT OUT.ts"""
import sys

data = bytearray(open(sys.argv[1], 'rb').read())
value = int(sys.argv[2])
count = 0
for i in range(0, len(data), 188):
    pid = ((data[i + 1] & 0x1f) << 8) | data[i + 2]
    if pid == 0x11:
        data[i + 1] |= 0x1f
        data[i + 2] = 0xff
i = 0
while True:
    i = data.find(b'\x00\x00\x01\x06\x01', i)  # start code, SEI NAL unit, payloadType 1
    if i < 0:
        break
    pos = i + 6
    data[pos] = (data[pos] & 0x0f) | (value << 4)
    count += 1
    i += 5
open(sys.argv[3], 'wb').write(data)
print(count, 'messages patched')

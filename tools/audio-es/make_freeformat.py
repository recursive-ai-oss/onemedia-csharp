#!/usr/bin/env python3
"""Makes a free-format MP3 from a constant-bit-rate one: sets the bit rate index of every frame header to 0 (ISO/IEC 11172-3 §2.4.2.3), so the
frame length is no longer in the header and a reader must measure it. The frame bytes and lengths are unchanged.

usage: make_freeformat.py IN.mp3 OUT.mp3   (IN has no tags in front)
"""
import sys

BITRATES = {  # (version bits, layer bits) -> kbit/s by index 1..14
    (3, 1): [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    (2, 1): [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    (0, 1): [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
}
RATES = {3: [44100, 48000, 32000], 2: [22050, 24000, 16000], 0: [11025, 12000, 8000]}

def main():
    data = bytearray(open(sys.argv[1], 'rb').read())
    out = bytearray()
    p = 0
    while p + 4 <= len(data):
        b1, b2 = data[p + 1], data[p + 2]
        if data[p] != 0xFF or (b1 & 0xE0) != 0xE0:
            break
        version, layer = (b1 >> 3) & 3, 4 - ((b1 >> 1) & 3)
        index, rate_index, pad = b2 >> 4, (b2 >> 2) & 3, (b2 >> 1) & 1
        assert layer == 3 and 1 <= index <= 14, 'only Layer III CBR input'
        kbps = BITRATES[(version, 1)][index - 1]
        rate = RATES[version][rate_index]
        spf = 1152 if version == 3 else 576
        length = spf // 8 * kbps * 1000 // rate + pad
        frame = bytearray(data[p:p + length])
        frame[2] &= 0x0F
        out += frame
        p += length
    open(sys.argv[2], 'wb').write(out)

main()

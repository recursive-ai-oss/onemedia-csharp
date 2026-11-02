#!/usr/bin/env python3
"""Copies the PAT, the PMTs, the packets of one PID and the clock-reference packets of a 188-byte transport stream into a small file, so
a reference decoder that has to run elsewhere sees only the teletext stream with working timestamps.
Usage: teletext-extract-pid.py in.ts PID out.ts"""
import sys

src, pid, dst = sys.argv[1], int(sys.argv[2]), sys.argv[3]
data = open(src, "rb").read()
pmts = set()
for i in range(0, min(len(data), 4_000_000) - 188, 188):
    pk = data[i:i + 188]
    if pk[0] != 0x47 or (((pk[1] & 0x1F) << 8) | pk[2]) != 0 or not pk[1] & 0x40:
        continue
    p = 5 + pk[4]                 # pointer_field
    length = ((pk[p + 1] & 0x0F) << 8) | pk[p + 2]
    end = p + 3 + length - 4
    q = p + 8
    while q + 4 <= end:
        prog = (pk[q] << 8) | pk[q + 1]
        if prog:
            pmts.add(((pk[q + 2] & 0x1F) << 8) | pk[q + 3])
        q += 4
    break
# ISO/IEC 13818-1 section 2.4.4.8: the PMT names the PCR PID.
pcr = set()
for i in range(0, min(len(data), 8_000_000) - 188, 188):
    pk = data[i:i + 188]
    if pk[0] == 0x47 and (((pk[1] & 0x1F) << 8) | pk[2]) in pmts and pk[1] & 0x40:
        p = 5 + pk[4]
        pcr.add(((pk[p + 8] & 0x1F) << 8) | pk[p + 9])
keep = {0, pid} | pmts
with open(dst, "wb") as out:
    for i in range(0, len(data) - 187, 188):
        pk = data[i:i + 188]
        if pk[0] != 0x47:
            continue
        q = ((pk[1] & 0x1F) << 8) | pk[2]
        has_pcr = q in pcr and (pk[3] >> 4) & 2 and pk[4] > 0 and pk[5] & 0x10
        if q in keep or has_pcr:
            out.write(pk)

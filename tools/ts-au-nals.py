#!/usr/bin/env python3
"""Lists the NAL unit types of the video access unit that starts at a byte offset of a transport stream (a debugging aid).

  ts-au-nals.py FILE OFFSET [STRIDE]
OFFSET is the position ffprobe prints for the packet (the TS packet that starts the PES packet). STRIDE is 188 or 192 (default: detected).
"""
import sys

f = open(sys.argv[1], "rb")
off = int(sys.argv[2])
f.seek(0); head = f.read(1200)
stride = int(sys.argv[3]) if len(sys.argv) > 3 else (192 if head[4] == 0x47 and head[196:197] == b"\x47" else 188)
pre = stride - 188
f.seek(off); data = f.read(stride * 400)
pid = None; payload = bytearray(); started = False
for i in range(0, len(data) - stride + 1, stride):
    p = data[i + pre:i + pre + 188]
    if p[0] != 0x47: print("sync lost at", i); break
    pusi = p[1] & 0x40; ppid = ((p[1] & 0x1f) << 8) | p[2]
    afc = (p[3] >> 4) & 3
    j = 4
    if afc & 2:
        j += 1 + p[4]
    if not (afc & 1): continue
    if pid is None:
        pid = ppid
    if ppid != pid: continue
    if pusi and started: break
    if pusi: started = True
    payload += p[j:]
b = bytes(payload)
if b[:3] == b"\x00\x00\x01":
    hl = 9 + b[8]; b = b[hl:]
types = []; i = 0
while True:
    k = b.find(b"\x00\x00\x01", i)
    if k < 0 or k + 3 >= len(b): break
    t = b[k + 3] & 0x1f; types.append(t)
    if t == 6 and k + 4 < len(b):
        types[-1] = "6(sei%d)" % b[k + 4]
    i = k + 3
print(len(b), "bytes; NAL types:", types[:24])

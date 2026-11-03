#!/usr/bin/env python3
"""Tallies, for the video packets of a transport stream that open with a sequence parameter set, the NAL type signature of the access unit
against ffprobe's key flag (a debugging aid for explaining key-frame differences).

  ts-key-signatures.py FFPROBE FILE...
"""
import subprocess, sys, collections

def nals(path, off, stride, pre):
    f = open(path, "rb"); f.seek(off); data = f.read(stride * 120)
    pid = None; payload = bytearray(); started = False
    for i in range(0, len(data) - stride + 1, stride):
        p = data[i + pre:i + pre + 188]
        if p[0] != 0x47: break
        pusi = p[1] & 0x40; ppid = ((p[1] & 0x1f) << 8) | p[2]; afc = (p[3] >> 4) & 3; j = 4
        if afc & 2: j += 1 + p[4]
        if not (afc & 1): continue
        if pid is None: pid = ppid
        if ppid != pid: continue
        if pusi and started: break
        if pusi: started = True
        payload += p[j:]
    b = bytes(payload)
    if b[:3] == b"\x00\x00\x01": b = b[9 + b[8]:]
    out = []; i = 0
    while True:
        k = b.find(b"\x00\x00\x01", i)
        if k < 0 or k + 3 >= len(b): break
        t = b[k + 3] & 0x1f
        out.append("6/%d" % b[k + 4] if t == 6 and k + 4 < len(b) else str(t))
        i = k + 3
        if t in (1, 5): break
    return " ".join(out)

ff = sys.argv[1]
for path in sys.argv[2:]:
    head = open(path, "rb").read(400)
    stride = 192 if head[4] == 0x47 else 188
    pre = stride - 188
    tally = collections.Counter()
    out = subprocess.run([ff, "-v", "error", "-select_streams", "v:0", "-show_packets", "-of", "compact", path], capture_output=True, text=True).stdout
    n = 0
    for line in out.splitlines():
        d = dict(kv.split("=", 1) for kv in line.split("|")[1:] if "=" in kv)
        if not d.get("pos", "").isdigit(): continue
        n += 1
        if n > 400: break
        sig = nals(path, int(d["pos"]), stride, pre)
        if sig.startswith("9 7") or sig.startswith("7"):
            tally[(sig, "K" in d["flags"])] += 1
    print(path.split("/")[-1])
    for (sig, k), c in sorted(tally.items()):
        print("   ", sig, "key" if k else "notkey", c)

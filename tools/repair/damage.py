#!/usr/bin/env python3
"""Makes damaged copies of a healthy media file, one pattern per copy, for the repair checks.

    damage.py <format> <healthy-file> <out-dir> [--seed N]

Formats: mkv, avi, mp4, fmp4, ts. Every pattern is deterministic (the seed only picks garbage bytes).
Each output is named <stem>.<pattern><ext>. Prints one line per file: name and what was done.
"""
import os
import random
import struct
import sys


def read(path):
    with open(path, "rb") as f:
        return bytearray(f.read())


# ---------------------------------------------------------------- EBML

def ebml_id(b, p):
    n = 1
    m = 0x80
    while n <= 4 and not (b[p] & m):
        n += 1
        m >>= 1
    return int.from_bytes(b[p:p + n], "big"), n


def ebml_size(b, p):
    n = 1
    m = 0x80
    while n <= 8 and not (b[p] & m):
        n += 1
        m >>= 1
    v = b[p] & (m - 1)
    for i in range(1, n):
        v = (v << 8) | b[p + i]
    unknown = v == (1 << (7 * n)) - 1
    return (-1 if unknown else v), n


def mkv_layout(b):
    """Returns (segment_header_pos, segment_data_start, [(id, offset, header_len, size)])."""
    p = 0
    _, il = ebml_id(b, p)
    s, sl = ebml_size(b, p + il)
    p += il + s + sl
    sid, il = ebml_id(b, p)
    assert sid == 0x18538067
    ssize, sl = ebml_size(b, p + il)
    seg = p
    data = p + il + sl
    els = []
    q = data
    while q < len(b):
        i, il = ebml_id(b, q)
        s, sl = ebml_size(b, q + il)
        if s < 0:
            # unknown size cluster: runs to next level-1 element start; find by scanning
            s = len(b) - q - il - sl
        els.append((i, q, il + sl, s))
        q += il + sl + s
    return seg, data, els


def void(n):
    # Void element of total size n (n >= 9 here)
    payload = n - 1 - 8
    return b"\xec\x01" + payload.to_bytes(7, "big") + bytes(payload) if n >= 10 else b"\xec" + bytes([0x80 | (n - 2)]) + bytes(n - 2)


def mkv_patterns(b, rnd):
    seg, data, els = mkv_layout(b)
    out = {}
    size = len(b)
    clusters = [e for e in els if e[0] == 0x1F43B675]
    cues = [e for e in els if e[0] == 0x1C53BB6B]
    seek = [e for e in els if e[0] == 0x114D9B74]
    info = [e for e in els if e[0] == 0x1549A966]

    def cut(frac):
        return b[: int(size * frac)]

    for name, f in (("cut30", 0.3), ("cut50", 0.5), ("cut80", 0.8)):
        out[name] = (cut(f), f"truncated at {int(f * 100)}%")
    # cut inside the last cluster's last block
    if clusters:
        c = clusters[-1]
        out["cutlast"] = (b[: c[1] + c[2] + c[3] - 7], "last cluster cut 7 bytes short")
        out["cutlast-hdr"] = (b[: c[1] + c[2] + 2], "last cluster cut after 2 content bytes")
    if cues:
        c = cues[0]
        nb = bytearray(b)
        nb[c[1]: c[1] + c[2] + c[3]] = void(c[2] + c[3])
        out["cues-void"] = (nb, "Cues replaced by Void")
        out["cues-cut"] = (b[: c[1] + c[2] + c[3] // 2], "file cut in the middle of the Cues")
        nb = bytearray(b)
        for i in range(c[1] + c[2] + 8, c[1] + c[2] + c[3], 5):
            nb[i] ^= 0x5A
        out["cues-garbled"] = (nb, "Cues payload bytes flipped")
    if seek:
        s = seek[0]
        nb = bytearray(b)
        for i in range(s[1] + s[2], s[1] + s[2] + s[3]):
            # damage SeekPosition payload bytes (they follow 0x53 0xAC 0x8x)
            if nb[i] == 0x53 and nb[i + 1] == 0xAC:
                w = nb[i + 2] & 0x7F
                for k in range(w):
                    nb[i + 3 + k] = 0xEE if k == 0 else nb[i + 3 + k]
        out["seek-past-eof"] = (nb, "SeekHead positions point past the end")
        nb = bytearray(b)
        nb[s[1]: s[1] + s[2] + s[3]] = void(s[2] + s[3])
        out["seek-void"] = (nb, "SeekHead replaced by Void")
    nb = bytearray(b)
    il = ebml_id(nb, seg)[1]
    sl = ebml_size(nb, seg + il)[1]
    nb[seg + il] = 0xFF if sl == 1 else (0x01 if sl == 8 else nb[seg + il])
    if sl == 8:
        nb[seg + il: seg + il + 8] = b"\x01\xff\xff\xff\xff\xff\xff\xff"
    out["segment-unknown"] = (nb, "Segment size set to unknown")
    nb = bytearray(b)
    if sl == 8:
        nb[seg + il: seg + il + 8] = b"\x01" + (size * 4).to_bytes(7, "big")
        out["segment-too-big"] = (nb, "Segment size larger than the file")
    # live-style: unknown cluster sizes, no Cues, unknown Segment, zero duration
    live = bytearray()
    live += b[:seg]
    live += b"\x18\x53\x80\x67\x01\xff\xff\xff\xff\xff\xff\xff"
    for e in els:
        if e[0] in (0x1C53BB6B, 0x114D9B74):
            continue
        raw = b[e[1]: e[1] + e[2] + e[3]]
        if e[0] == 0x1F43B675:
            il2 = ebml_id(raw, 0)[1]
            raw = raw[:il2] + b"\xff" + raw[il2 + ebml_size(raw, il2)[1]:]
        live += raw
    out["live"] = (live, "unknown Segment and Cluster sizes, no SeekHead or Cues")
    # duration zero
    if info:
        i = info[0]
        nb = bytearray(b)
        for p in range(i[1] + i[2], i[1] + i[2] + i[3] - 3):
            if nb[p] == 0x44 and nb[p + 1] == 0x89:
                w = nb[p + 2] & 0x7F
                for k in range(w):
                    nb[p + 3 + k] = 0
                break
        out["duration-zero"] = (nb, "Info Duration set to 0")
    # garbage between clusters
    if len(clusters) > 3:
        c = clusters[len(clusters) // 2]
        junk = bytes(rnd.randrange(0, 256) for _ in range(137))
        junk = bytes(x if x not in (0x1F,) else 0x20 for x in junk)
        out["garbage"] = (b[: c[1]] + junk + b[c[1]:], "137 random bytes inserted before a Cluster")
    if clusters:
        c = [c for c in clusters if c[1] > size * 0.6][0]
        out["zero-tail"] = (b[: c[1]] + bytes(4096), "cut at a Cluster boundary near 60% and padded with zeros")
    return out


# ---------------------------------------------------------------- main

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    seed = 1
    if "--seed" in sys.argv:
        seed = int(sys.argv[sys.argv.index("--seed") + 1])
        args = [a for a in args if a != str(seed)]
    fmt, src, outdir = args[:3]
    rnd = random.Random(seed)
    b = read(src)
    mod = {"mkv": mkv_patterns}
    try:
        import damage_avi
        mod["avi"] = damage_avi.patterns
        import damage_iso
        mod["mp4"] = damage_iso.patterns_mp4
        mod["fmp4"] = damage_iso.patterns_fmp4
        import damage_ts
        mod["ts"] = damage_ts.patterns
    except ImportError:
        pass
    pats = mod[fmt](b, rnd)
    os.makedirs(outdir, exist_ok=True)
    stem, ext = os.path.splitext(os.path.basename(src))
    for name, (data, what) in sorted(pats.items()):
        p = os.path.join(outdir, f"{stem}.{name}{ext}")
        with open(p, "wb") as f:
            f.write(data)
        print(os.path.basename(p), "-", what)


if __name__ == "__main__":
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    main()

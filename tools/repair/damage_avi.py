"""Damage patterns for AVI files (see damage.py)."""
import struct


def u32(b, p):
    return struct.unpack_from("<I", b, p)[0]


def layout(b):
    """Returns dict with riff size pos, hdrl, movi (list pos, data start, end), chunks [(pos, id, size)], idx1 (pos, size) or None."""
    assert b[0:4] == b"RIFF"
    p = 12
    res = {"hdrl": None, "movi": None, "idx1": None, "chunks": [], "lists": []}
    while p + 8 <= len(b):
        cid = bytes(b[p:p + 4])
        size = u32(b, p + 4)
        if cid == b"LIST":
            lt = bytes(b[p + 8:p + 12])
            if lt == b"movi":
                res["movi"] = (p, p + 12, p + 8 + size)
                q = p + 12
                end = min(p + 8 + size, len(b))
                while q + 8 <= end:
                    i = bytes(b[q:q + 4])
                    s = u32(b, q + 4)
                    res["chunks"].append((q, i, s))
                    q += 8 + s + (s & 1)
                p += 8 + size
                continue
            if lt == b"hdrl":
                res["hdrl"] = (p, p + 8 + size)
            res["lists"].append((p, lt, size))
        elif cid == b"idx1":
            res["idx1"] = (p, size)
        elif cid == b"RIFF":
            p += 12
            continue
        p += 8 + size + (size & 1)
    return res


def find_chunk(b, tag):
    return b.find(tag)


def patterns(b, rnd):
    L = layout(b)
    out = {}
    n = len(b)
    chunks = L["chunks"]
    movi = L["movi"]
    idx1 = L["idx1"]
    for name, f in (("cut30", 0.3), ("cut50", 0.5), ("cut80", 0.8)):
        out[name] = (b[: int(n * f)], f"truncated at {int(f * 100)}%")
    last = chunks[-1]
    out["cut-mid-chunk"] = (b[: last[0] + 8 + max(1, last[2] // 2)], "cut inside the last data chunk")
    out["cut-chunk-header"] = (b[: last[0] + 5], "cut inside the header of the last chunk")
    if idx1:
        out["idx1-removed"] = (b[: idx1[0]], "file ends where idx1 began")
        nb = bytearray(b)
        nb[idx1[0]: idx1[0] + 4] = b"JUNK"
        out["idx1-junk"] = (nb, "idx1 renamed to JUNK")
        out["idx1-cut"] = (b[: idx1[0] + 8 + (idx1[1] // 2 & ~15)], "idx1 cut in half")
        nb = bytearray(b)
        for i in range(idx1[0] + 8, idx1[0] + 8 + idx1[1], 16):
            struct.pack_into("<I", nb, i + 8, u32(nb, i + 8) + 1000)
        out["idx1-offsets"] = (nb, "every idx1 offset shifted by 1000")
        nb = bytearray(b)
        for i in range(idx1[0] + 8, idx1[0] + 8 + idx1[1], 16 * 7):
            nb[i + 12] ^= 0x55
        out["idx1-sizes"] = (nb, "every 7th idx1 size changed")
    # header counts
    nb = bytearray(b)
    a = b.find(b"avih")
    struct.pack_into("<I", nb, a + 8 + 16, 0)
    p = 12
    while True:
        s = b.find(b"strh", p)
        if s < 0 or s > L["hdrl"][1]:
            break
        struct.pack_into("<I", nb, s + 8 + 32, 0)
        p = s + 4
    out["counts-zero"] = (nb, "avih.dwTotalFrames and strh.dwLength set to 0")
    nb = bytearray(b)
    struct.pack_into("<I", nb, 4, 0)
    out["riff-size-zero"] = (nb, "RIFF size 0")
    nb = bytearray(b)
    struct.pack_into("<I", nb, 4, 0xFFFFFFF0)
    out["riff-size-huge"] = (nb, "RIFF size 0xFFFFFFF0")
    nb = bytearray(b)
    struct.pack_into("<I", nb, movi[0] + 4, 4)
    out["movi-size-small"] = (nb, "movi LIST size 4")
    mid = chunks[len(chunks) // 2]
    junk = bytes((rnd.randrange(0, 256) & 0x7F) | 0x80 for _ in range(61))
    out["garbage"] = (b[: mid[0]] + junk + b[mid[0]:], "61 non-chunk bytes inserted between chunks")
    cut = chunks[int(len(chunks) * 0.6)][0]
    out["zero-tail"] = (b[:cut] + bytes(2048), "cut at a chunk boundary and padded with zeros")
    return out

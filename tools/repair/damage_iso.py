"""Damage patterns for ISO base media files: progressive MP4 and fragmented MP4 (see damage.py)."""
import struct


def be32(b, p):
    return struct.unpack_from(">I", b, p)[0]


def top_boxes(b):
    out = []
    p = 0
    while p + 8 <= len(b):
        size = be32(b, p)
        t = bytes(b[p + 4:p + 8])
        hdr = 8
        if size == 1:
            size = struct.unpack_from(">Q", b, p + 8)[0]
            hdr = 16
        elif size == 0:
            size = len(b) - p
        if size < 8:
            break
        out.append((t, p, hdr, size))
        p += size
    return out


def find_all(b, tag, start=0, end=None):
    end = end or len(b)
    res = []
    p = start
    while True:
        i = b.find(tag, p, end)
        if i < 0:
            return res
        res.append(i - 4)  # box start
        p = i + 4


def patterns_mp4(b, rnd):
    out = {}
    n = len(b)
    boxes = top_boxes(b)
    moov = [x for x in boxes if x[0] == b"moov"][0]
    mdat = [x for x in boxes if x[0] == b"mdat"][0]

    def stco_boxes():
        return [p for p in find_all(b, b"stco", moov[1], moov[1] + moov[3])]

    def edit_stco(fn):
        nb = bytearray(b)
        for p in stco_boxes():
            cnt = be32(nb, p + 12)
            for i in range(cnt):
                o = p + 16 + 4 * i
                struct.pack_into(">I", nb, o, fn(be32(nb, o), i) & 0xFFFFFFFF)
        return nb

    out["stco-zero"] = (edit_stco(lambda v, i: 0), "all chunk offsets set to 0")
    out["stco-shift-plus"] = (edit_stco(lambda v, i: v + 777), "every chunk offset moved by +777")
    out["stco-shift-minus"] = (edit_stco(lambda v, i: v - 48), "every chunk offset moved by -48")
    out["stco-garbage"] = (edit_stco(lambda v, i: rnd.randrange(0, 1 << 30)), "chunk offsets replaced by random numbers")
    out["stco-ascending"] = (edit_stco(lambda v, i: 1000 + i), "chunk offsets 1000, 1001, ...")

    def edit_dur(tag, off_v0, off_v1):
        nb = bytearray(b)
        for p in find_all(nb, tag, moov[1], moov[1] + moov[3]):
            v = nb[p + 8]
            if v == 1:
                struct.pack_into(">Q", nb, p + 8 + off_v1, 0)
            else:
                struct.pack_into(">I", nb, p + 8 + off_v0, 0)
        return nb

    out["mdhd-zero"] = (edit_dur(b"mdhd", 16, 24), "mdhd durations set to 0")
    out["tkhd-zero"] = (edit_dur(b"tkhd", 20, 28), "tkhd durations set to 0")
    out["mvhd-zero"] = (edit_dur(b"mvhd", 16, 24), "mvhd duration set to 0")
    both = bytearray(edit_dur(b"mdhd", 16, 24))
    for p in find_all(both, b"tkhd", moov[1], moov[1] + moov[3]):
        struct.pack_into(">I", both, p + 8 + 20, 0)
    for p in find_all(both, b"mvhd", moov[1], moov[1] + moov[3]):
        struct.pack_into(">I", both, p + 8 + 16, 0)
    out["durations-zero"] = (both, "mdhd, tkhd and mvhd durations all 0")
    out["tail-zeros"] = (b + bytes(4096), "4096 zero bytes appended")
    out["tail-garbage"] = (b + bytes((rnd.randrange(256) for _ in range(300))), "300 random bytes appended")
    if moov[1] < mdat[1]:
        # moov first (faststart): cut inside mdat keeps the movie box
        for name, f in (("cut-mdat-50", 0.5), ("cut-mdat-90", 0.9)):
            cut = mdat[1] + int((n - mdat[1]) * f)
            out[name] = (b[:cut], f"faststart file cut at {int(f * 100)}% of the media data")
    return out


def patterns_fmp4(b, rnd):
    out = {}
    n = len(b)
    boxes = top_boxes(b)
    moofs = [x for x in boxes if x[0] == b"moof"]
    mfra = [x for x in boxes if x[0] == b"mfra"]
    sidx = [x for x in boxes if x[0] == b"sidx"]
    out["cut-mid-fragment"] = (b[: moofs[len(moofs) // 2][1] + 200], "cut 200 bytes into a fragment")
    out["cut-fragment-boundary"] = (b[: moofs[len(moofs) // 2][1]], "cut exactly before a fragment, no mfra")
    out["cut-in-moof-header"] = (b[: moofs[-1][1] + 5], "cut inside the header of the last moof")
    last = moofs[-1]
    out["cut-last-mdat-short"] = (b[: last[1] + last[3] + 8 + 3] if False else b[: n - 12 - (mfra[0][3] if mfra else 0)], "last media data box missing its last 12 bytes")
    out["cut-90"] = (b[: int(n * 0.9)], "cut at 90%")
    if mfra:
        m = mfra[0]
        out["mfra-removed"] = (b[: m[1]], "file ends where mfra began")
        nb = bytearray(b)
        nb[m[1] + 4: m[1] + 8] = b"free"
        out["mfra-free"] = (nb, "mfra renamed free")
        nb = bytearray(b)
        for i in range(m[1] + 16, m[1] + m[3] - 16, 7):
            nb[i] ^= 0xA5
        out["mfra-garbled"] = (nb, "mfra bytes flipped")
        out["mfra-cut"] = (b[: m[1] + m[3] // 2], "mfra cut in half")
    if sidx:
        s = sidx[0]
        nb = bytearray(b)
        nb[s[1] + 4: s[1] + 8] = b"free"
        out["sidx-free"] = (nb, "sidx renamed free")
        nb = bytearray(b)
        for i in range(s[1] + 44, s[1] + s[3], 12):
            struct.pack_into(">I", nb, i, be32(nb, i) // 2)
        out["sidx-sizes"] = (nb, "sidx reference sizes halved")
    out["tail-zeros"] = (b + bytes(2048), "2048 zero bytes appended")
    return out

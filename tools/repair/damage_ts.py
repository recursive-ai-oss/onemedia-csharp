"""Damage patterns for MPEG-TS and M2TS files (see damage.py)."""


def detect(b):
    for size in (188, 192, 204):
        pre = 4 if size == 192 else 0
        for start in range(0, 400):
            if all(start + k * size + pre < len(b) and b[start + k * size + pre] == 0x47 for k in range(6)):
                return size, pre, start
    raise ValueError("not a transport stream")


def patterns(b, rnd):
    size, pre, first = detect(b)
    n = (len(b) - first) // size

    def pkt(i):
        return first + i * size

    def pid(i):
        p = pkt(i) + pre
        return ((b[p + 1] & 0x1F) << 8) | b[p + 2]

    def has_payload(i):
        return (b[pkt(i) + pre + 3] >> 4) & 1

    # the busiest PID is the video stream
    counts = {}
    for i in range(n):
        counts[pid(i)] = counts.get(pid(i), 0) + 1
    video = max(counts, key=counts.get)
    vp = [i for i in range(n) if pid(i) == video and has_payload(i)]
    out = {}
    out["cut-mid-packet"] = (b[: pkt(n // 2) + 100], "cut 100 bytes into a packet")
    out["cut-90"] = (b[: pkt(int(n * 0.9))], "cut at a packet boundary at 90%")
    out["cut-start"] = (b[1234:], "first 1234 bytes removed: starts mid-packet and mid-PES")
    mid = vp[len(vp) // 2]
    # five consecutive video packets removed
    run = vp[len(vp) // 2: len(vp) // 2 + 5]
    nb = bytearray()
    drop = set(run)
    for i in range(n):
        if i not in drop:
            nb += b[pkt(i): pkt(i) + size]
    out["drop-5-video"] = (bytes(first and b[:first] or b"") + bytes(nb), "five consecutive video packets removed")
    drop = set(vp[k] for k in range(20, len(vp), max(1, len(vp) // 10)))
    nb = bytearray(b[:first])
    for i in range(n):
        if i not in drop:
            nb += b[pkt(i): pkt(i) + size]
    out["drop-spread"] = (bytes(nb), "ten single video packets removed here and there")
    junk = bytes((rnd.randrange(256) for _ in range(300)))
    junk = bytes(x if x != 0x47 else 0x48 for x in junk)
    at = pkt(n // 3)
    out["garbage-between"] = (b[:at] + junk + b[at:], "300 random bytes between two packets")
    out["odd-bytes-inside"] = (b[: pkt(n // 3) + 77] + junk[:77] + b[pkt(n // 3) + 77:], "77 bytes inserted inside a packet")
    d = pkt(vp[len(vp) // 3])
    out["duplicate"] = (b[:d + size] + b[d: d + size] + b[d + size:], "one video packet repeated")
    nb = bytearray(b)
    for i in vp[len(vp) // 4: len(vp) // 4 + 3]:
        nb[pkt(i) + pre + 3] = (nb[pkt(i) + pre + 3] & 0xF0) | ((nb[pkt(i) + pre + 3] + 7) & 0xF)
    out["cc-wrong"] = (bytes(nb), "continuity counters of three video packets changed")
    nb = bytearray(b)
    for i in vp[len(vp) // 5: len(vp) // 5 + 2]:
        nb[pkt(i) + pre + 1] |= 0x80
    out["transport-error"] = (bytes(nb), "transport_error_indicator set on two video packets")
    out["tail-zeros"] = (b + bytes(1000), "1000 zero bytes appended")
    # PCR jump: move a PCR by +5 s
    nb = bytearray(b)
    hit = 0
    for i in range(n):
        p = pkt(i) + pre
        if ((nb[p + 3] >> 4) & 2) and nb[p + 4] >= 7 and (nb[p + 5] & 0x10):
            hit += 1
            if hit == 20:
                base = (nb[p + 6] << 25) | (nb[p + 7] << 17) | (nb[p + 8] << 9) | (nb[p + 9] << 1) | (nb[p + 10] >> 7)
                base += 5 * 90000
                nb[p + 6] = (base >> 25) & 0xFF
                nb[p + 7] = (base >> 17) & 0xFF
                nb[p + 8] = (base >> 9) & 0xFF
                nb[p + 9] = (base >> 1) & 0xFF
                nb[p + 10] = ((base & 1) << 7) | (nb[p + 10] & 0x7F)
                break
    out["pcr-jump"] = (bytes(nb), "one PCR moved forward by 5 s (reported, not changed)")
    return out

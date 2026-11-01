#!/usr/bin/env python3
"""Writes a copy of a Matroska audio file with DiscardPadding set on chosen blocks.

ffmpeg only writes DiscardPadding where its own encoder or demuxer produced it (the last Opus block), so it cannot make a file
with padding in the middle of a track, or any padding for Vorbis. This script rewrites the clusters of an ffmpeg-written file:
each chosen block becomes a BlockGroup holding the block and a DiscardPadding element (RFC 9559 section 5.1.3.5.7, signed
nanoseconds), and any padding the block already had is replaced. Packet bytes are copied unchanged. SeekHead and Cues would
point at the old offsets, so both are replaced by Void elements of the same size.

Usage: matroska-discard-padding.py IN.mka OUT.mka INDEX=NANOSECONDS [INDEX=NANOSECONDS ...]
INDEX counts the file's blocks from 0 (the files this is used on have one track); a negative INDEX counts from the end.
"""
import sys

SEGMENT, SEEKHEAD, CUES, CLUSTER = 0x18538067, 0x114D9B74, 0x1C53BB6B, 0x1F43B675
SIMPLEBLOCK, BLOCKGROUP, BLOCK, DISCARD = 0xA3, 0xA0, 0xA1, 0x75A2


def vint_len(first):
    n = 1
    while n <= 8 and not (first & (0x80 >> (n - 1))):
        n += 1
    return n


def children(b, start, end):
    """Yields (id, header_start, payload_start, payload_end) for the elements between start and end."""
    p = start
    while p < end:
        n = vint_len(b[p])
        eid = int.from_bytes(b[p:p + n], "big")
        q = p + n
        m = vint_len(b[q])
        size = b[q] & ((0x80 >> (m - 1)) - 1)
        for i in range(1, m):
            size = (size << 8) | b[q + i]
        if size == (1 << (7 * m)) - 1:
            raise SystemExit("unknown-size element: not supported by this tool")
        yield eid, p, q + m, q + m + size
        p = q + m + size


def enc_id(eid):
    return eid.to_bytes((eid.bit_length() + 7) // 8, "big")


def enc_size(n):
    for length in range(1, 9):
        if n < (1 << (7 * length)) - 1:
            return (((0x80 >> (length - 1)) << (8 * (length - 1))) | n).to_bytes(length, "big")
    raise ValueError(n)


def elem(eid, payload):
    return enc_id(eid) + enc_size(len(payload)) + bytes(payload)


def void_of(total):
    """A Void element (ID 0xEC) occupying exactly `total` bytes, with an 8-byte size field."""
    if total < 10:
        raise SystemExit("cannot fill %d bytes with a Void element" % total)
    pay = total - 9
    return b"\xEC" + ((1 << 56) | pay).to_bytes(8, "big") + bytes(pay)


def signed(v):
    n = 1
    while not -(1 << (8 * n - 1)) <= v < (1 << (8 * n - 1)):
        n += 1
    return v.to_bytes(n, "big", signed=True)


def block_without_keyframe_flag(blk):
    # Block: track number (vint), 16-bit relative timecode, flags. SimpleBlock's 0x80 key-frame flag is not part of a Block.
    out = bytearray(blk)
    out[vint_len(out[0]) + 2] &= 0x7F
    return bytes(out)


def main():
    src, dst, *specs = sys.argv[1:]
    pads = {int(k): int(v) for k, v in (s.split("=") for s in specs)}
    b = open(src, "rb").read()
    segment = next((h, s, e) for eid, h, s, e in children(b, 0, len(b)) if eid == SEGMENT)
    top = list(children(b, segment[1], segment[2]))
    total = sum(1 for cid, _, cs, ce in top if cid == CLUSTER
                for eid, _, _, _ in children(b, cs, ce) if eid in (SIMPLEBLOCK, BLOCKGROUP))
    wanted = {(i if i >= 0 else total + i): v for i, v in pads.items()}
    body = bytearray()
    index = 0
    for cid, h, s, e in top:
        if cid in (SEEKHEAD, CUES):
            body += void_of(e - h)
        elif cid != CLUSTER:
            body += b[h:e]
        else:
            parts = bytearray()
            for eid, eh, es, ee in children(b, s, e):
                if eid not in (SIMPLEBLOCK, BLOCKGROUP):
                    parts += b[eh:ee]
                    continue
                if index in wanted:
                    if eid == SIMPLEBLOCK:
                        group = elem(BLOCK, block_without_keyframe_flag(b[es:ee]))
                    else:
                        group = b"".join(b[gh:ge] for gid, gh, _, ge in children(b, es, ee) if gid != DISCARD)
                    parts += elem(BLOCKGROUP, group + elem(DISCARD, signed(wanted[index])))
                else:
                    parts += b[eh:ee]
                index += 1
            body += elem(CLUSTER, parts)
    # The Segment size is written as 8 bytes so its width does not depend on the content.
    out = b[:segment[0]] + enc_id(SEGMENT) + ((1 << 56) | len(body)).to_bytes(8, "big") + body
    open(dst, "wb").write(out)
    print("blocks:", total, "padded:", sorted(wanted))


if __name__ == "__main__":
    main()

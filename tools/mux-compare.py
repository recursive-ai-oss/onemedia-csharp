#!/usr/bin/env python3
"""Compares the packets ffprobe reads from a muxed file with the ones it reads from the file it was copied from.

usage: mux-compare.py [-v] <ffprobe> <input> <output>
  <output>.map   one line per output track: "<output index> <input index>" (written by OneMedia.MuxCheck)
  <output>.pkts  optional, one line per packet in output order: "<output index> <dts> <pts> <duration> <size> <key> <disposable> <sha256>" as the
                 container reader of the input delivered it (raw track ticks, -1 for an unknown duration)

Per stream the packets of both files are compared in order on pts, dts, duration, size, flags and the SHA-256 of the data. A difference is
"explained" (counted under explained:<class>) when it is one of the documented ways ffprobe reads the *input* differently from the container
(docs/isobmff.md), recognised by the output agreeing with the reader's own view or with the output's own timeline:
  shift      every pts and dts of a stream moved by one constant (ffprobe's handling of negative composition offsets in progressive files)
  dur-na     ffprobe prints N/A for the input's duration (a fragment default)
  dur-reader the output's duration is the one the container reader delivered for that packet
  dur-gap    the output's duration is the distance to the next decode time (the sample table can say nothing else)
  key-reader the output's key flag is the one the container reader delivered
  disc-frag  a discard flag from an edit list that ffprobe applies to the sample tables of a movie box and not to fragment samples, so the two
             files differ whenever one is fragmented and the other not
  in-reader  the packets of the stream differ from ffprobe's reading of the *input*, but ffprobe's reading of the output equals the container
             reader's own view of the input: same count, sizes, SHA-256, key flags, and decode and presentation times moved by constants
             (durations too, except gaps in the timeline and the last packet, unless the output is fragmented, where ffprobe derives them differently; set FRAGMENTED=1). The muxer then
             wrote what the reader delivered, and the difference is between ffprobe and the reader on the input (docs/isobmff.md).
Everything else is counted as a difference. Exit status 1 when any difference remains.
"""
import os
import subprocess
import sys
from collections import defaultdict

FIELDS = "stream_index,pts,dts,duration,size,flags,data_hash"
NAMES = ["pts", "dts", "duration", "size", "flags", "hash"]


def probe(ffprobe, path):
    out = subprocess.run(
        [ffprobe, "-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=" + FIELDS, "-of", "csv=p=0", path],
        capture_output=True, text=True, check=False).stdout
    streams = defaultdict(list)
    for line in out.splitlines():
        f = line.strip().split(",")
        if len(f) < 7:
            continue
        try:
            streams[int(f[0])].append(tuple(f[1:7]))
        except ValueError:
            pass
    return streams


def to_int(v):
    try:
        return int(v)
    except ValueError:
        return None


def load_reader(path):
    tracks = defaultdict(list)
    if not os.path.exists(path):
        return tracks
    with open(path) as fh:
        for line in fh:
            f = line.split()
            tracks[int(f[0])].append(tuple(int(x) for x in f[1:7]) + (f[7] if len(f) > 7 else "",))
    return tracks


def reader_tier(pb, pr):
    """True when ffprobe's packets of the output equal the container reader's view of the input (see in-reader above)."""
    if not pr or len(pr) != len(pb):
        return False
    fragmented = os.environ.get("FRAGMENTED") == "1"
    dts_shift = pts_shift = None
    for n, ((rdts, rpts, rdur, rsize, rkey, _disp, rhash), (pts, dts, dur, size, flags, h)) in enumerate(zip(pr, pb)):
        if to_int(size) != rsize or h != "SHA256:" + rhash or (flags[:1] == "K") != bool(rkey):
            return False
        if to_int(pts) is None or to_int(dts) is None:
            return False
        a, b = to_int(dts) - rdts, to_int(pts) - rpts
        if dts_shift is None:
            dts_shift, pts_shift = a, b
        elif (a, b) != (dts_shift, pts_shift):
            return False
        if not fragmented and rdur >= 0 and dur != "N/A" and to_int(dur) != rdur:
            # The sample table cannot hold a gap in the timeline, so the muxer writes the distance to the next decode time (dur-gap);
            # ffprobe derives the last packet's duration from the edit list and stream duration.
            nxt = pb[n + 1] if n + 1 < len(pb) else None
            if nxt is not None and to_int(nxt[1]) is not None and to_int(dur) == to_int(nxt[1]) - to_int(dts):
                continue
            if nxt is None:
                continue
            return False
    return True


def main():
    verbose = "-v" in sys.argv
    argv = [a for a in sys.argv[1:] if a != "-v"]
    ffprobe, src, dst = argv
    mapping = []
    with open(dst + ".map") as fh:
        for line in fh:
            o, i = line.split()
            mapping.append((int(o), int(i)))
    a = probe(ffprobe, src)
    b = probe(ffprobe, dst)
    reader = load_reader(dst + ".pkts")
    classes = defaultdict(int)
    total = match = diff = 0
    first = []
    for o, i in mapping:
        pa, pb, pr = a.get(i, []), b.get(o, []), reader.get(o, [])
        if len(pa) != len(pb):
            kind = "count(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))
            if pr and len(pr) == len(pb):
                classes["explained:count-reader(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))] += 1
            else:
                classes[kind] += 1
                diff += abs(len(pa) - len(pb))
        if reader_tier(pb, pr):
            n_in = sum(1 for x, y in zip(pa, pb) if x != y) + abs(len(pa) - len(pb))
            if n_in:
                classes["explained:in-reader"] += n_in
                for x, y in zip(pa, pb):
                    total += 1
                    match += 1
                continue
        pairs = list(zip(pa, pb))
        shifts = set()
        for x, y in pairs:
            if x[0] != y[0] or x[1] != y[1]:
                dx, dy = to_int(y[0]), to_int(y[1])
                ox, oy = to_int(x[0]), to_int(x[1])
                shifts.add(None if None in (dx, dy, ox, oy) else (dx - ox, dy - oy))
        uniform = len(shifts) == 1 and None not in shifts and next(iter(shifts))[0] == next(iter(shifts))[1]
        shift = next(iter(shifts))[0] if uniform else 0
        for n, (x, y) in enumerate(pairs):
            total += 1
            bad = []
            for k in range(6):
                if x[k] == y[k]:
                    continue
                cls = None
                if k < 2 and uniform and to_int(x[k]) is not None and to_int(y[k]) is not None and to_int(y[k]) - to_int(x[k]) == shift:
                    cls = "shift"
                elif k == 2:
                    nxt = pb[n + 1] if n + 1 < len(pb) else None
                    if x[2] == "N/A":
                        cls = "dur-na"
                    elif pr and n < len(pr) and to_int(y[2]) == pr[n][2]:
                        cls = "dur-reader"
                    elif nxt and to_int(nxt[1]) is not None and to_int(y[1]) is not None and to_int(y[2]) == to_int(nxt[1]) - to_int(y[1]):
                        cls = "dur-gap"
                elif k == 4:
                    if pr and n < len(pr) and x[4][0] != y[4][0] and (y[4][0] == "K") == bool(pr[n][4]):
                        cls = "key-reader"
                    elif x[4][0] == y[4][0] and x[4][1:2] != y[4][1:2] and {x[4][1:2], y[4][1:2]} == {"D", "_"}:
                        cls = "disc-frag"
                if cls:
                    classes["explained:" + cls] += 1
                else:
                    bad.append(NAMES[k])
            if bad:
                diff += 1
                for k in bad:
                    classes[k] += 1
                if len(first) < 5:
                    first.append("stream %d->%d packet %d: %s vs %s" % (i, o, n, x, y))
            else:
                match += 1
    name = src.rsplit("/", 1)[-1]
    print("%s\tstreams=%d\tpackets=%d\tmatch=%d\tdiff=%d\t%s" % (name, len(mapping), total, match, diff, dict(classes) if classes else ""))
    if verbose or diff:
        for line in first:
            print("\t" + line)
    return 1 if diff else 0


if __name__ == "__main__":
    sys.exit(main())

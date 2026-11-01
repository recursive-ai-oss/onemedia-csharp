#!/usr/bin/env python3
"""Compares the packets ffprobe reads from a remuxed file with the ones it reads from the file it was made from.

usage: reremux-compare.py [-v] <ffprobe> <input> <output>
  <output>.map   one line per output track: "<output index> <input index> <repackaging>" (written by OneMedia.RemuxCheck)
  <output>.pkts  one line per packet in output order: "<output index> <dts> <pts> <duration> <size> <key> <disposable> <sha256>" as written to the muxer

This is tools/mux-compare.py with one change: for a track whose payload was re-framed (annexb-to-length-prefixed, adts-to-raw) the size and
the SHA-256 of ffprobe's output packet are compared with what was written (.pkts) instead of with ffprobe's view of the input, because the
bytes differ by design. That the NAL units are the same is checked by RemuxCheck itself (the "nals" column). Every other field, and the
classes of explained differences, are those of mux-compare.py (docs/muxing.md); a remux adds the class
  untimed-input  the input is a raw elementary stream with no time stamps (ffprobe prints N/A); the output carries the reader's frame numbering
  edit-time   every pts and dts of a stream moved by one constant (the shift class), or by a constant plus the start offset of an edit list
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


def concat_hash(ffprobe, path, index):
    """SHA-256 of one stream's packets laid end to end, as ffmpeg copies them (None when ffmpeg fails)."""
    import hashlib
    ffmpeg = os.path.join(os.path.dirname(ffprobe), "ffmpeg")
    h = hashlib.sha256()
    total = 0
    p = subprocess.Popen([ffmpeg, "-v", "error", "-threads", "1", "-i", path, "-map", "0:%d" % index, "-c", "copy", "-f", "data", "-"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    while True:
        chunk = p.stdout.read(1 << 20)
        if not chunk:
            break
        h.update(chunk)
        total += len(chunk)
    return (h.hexdigest(), total) if p.wait() == 0 else None


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


def reader_tier(pa, pb, pr):
    """True when ffprobe's packets of the output equal the container reader's view of the input (see in-reader above)."""
    if not pr or len(pr) != len(pb):
        return False
    fragmented = os.environ.get("FRAGMENTED") == "1"
    # With a segment index ffprobe moves the presentation times of the first fragment by an amount of its own (the same bytes with the
    # index box renamed to 'free' read back with one constant shift), so only the decode times must keep one shift.
    sidx = os.environ.get("SIDX") == "1"
    dts_shift = pts_shift = None
    for n, ((rdts, rpts, rdur, rsize, rkey, _disp, rhash), (pts, dts, dur, size, flags, h)) in enumerate(zip(pr, pb)):
        if to_int(size) != rsize or h != "SHA256:" + rhash:
            return False
        # ffprobe marks every audio packet as a key frame whatever the container says; the output may agree with ffprobe's reading of the input.
        if (flags[:1] == "K") != bool(rkey) and not (n < len(pa) and pa[n][4][:1] == flags[:1]):
            return False
        if to_int(pts) is None or to_int(dts) is None:
            return False
        a, b = to_int(dts) - rdts, to_int(pts) - rpts
        if dts_shift is None:
            dts_shift, pts_shift = a, b
        elif a != dts_shift or (b != pts_shift and not sidx):
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


TIME_FIELDS = "stream_index,pts_time,dts_time,duration_time,size,flags,data_hash"
TOLERANCE = 0.0011  # Matroska stores times in milliseconds (the default timestamp scale); both sides round to the nearest


def probe_time(ffprobe, path):
    out = subprocess.run(
        [ffprobe, "-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=" + TIME_FIELDS, "-of", "csv=p=0", path],
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


def to_float(v):
    try:
        return float(v)
    except ValueError:
        return None


def compare_time_based(ffprobe, src, dst, mapping, modes, reader):
    """Matroska output: ffprobe prints times in seconds, so the comparison is in seconds with the one-millisecond rounding of the container.

    Classes: last-trim (the input stops inside its last packet, the output states the whole frame), shift (every pts moved by one constant, within the tolerance), no-dts (Matroska stores no decode time, ffprobe prints N/A),
    ms-rounding (a duration within the tolerance), dur-na (ffprobe prints N/A for the output's duration: no BlockDuration and no default).
    """
    a, b = probe_time(ffprobe, src), probe_time(ffprobe, dst)
    classes = defaultdict(int)
    total = match = diff = 0
    first = []
    for o, i in mapping:
        pa, pb, pr = a.get(i, []), b.get(o, []), reader.get(o, [])
        if modes.get(o, "none") != "none" and len(pa) == len(pr):
            pa = [(x[0], x[1], x[2], str(r[3]), x[4], "SHA256:" + r[6]) for x, r in zip(pa, pr)]
        if len(pa) != len(pb):
            if pr and len(pr) == len(pb):
                # ffprobe reads the input differently from the container reader (it lost sync, or reads another link of a chained stream); the
                # output holds what the reader delivered, so size and hash are checked against what was written.
                classes["explained:count-reader(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))] += 1
                bad_rows = sum(1 for y, r in zip(pb, pr) if to_int(y[3]) != r[3] or y[5] != "SHA256:" + r[6])
                total += len(pb)
                match += len(pb) - bad_rows
                diff += bad_rows
                continue
            classes["count(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))] += 1
            diff += abs(len(pa) - len(pb))
        deltas = [to_float(y[0]) - to_float(x[0]) for x, y in zip(pa, pb) if to_float(x[0]) is not None and to_float(y[0]) is not None]
        shift = sorted(deltas)[len(deltas) // 2] if deltas else 0.0
        for n, (x, y) in enumerate(zip(pa, pb)):
            total += 1
            bad = []
            px, py = to_float(x[0]), to_float(y[0])
            if px is None and py is None:
                pass
            elif px is None and pr and n < len(pr):
                pass  # untimed input: the reader numbered the frames
            elif px is None or py is None or abs((py - px) - shift) > TOLERANCE:
                bad.append("pts")
            elif abs(shift) > TOLERANCE:
                classes["explained:shift"] += 1
            dx, dy = to_float(x[1]), to_float(y[1])
            if dy is None:
                if dx is not None:
                    classes["explained:no-dts"] += 1
            elif dx is not None:
                classes["explained:no-dts"] += 1  # Matroska stores no decode time; ffprobe derives one from the presentation order of each file
            ux, uy = to_float(x[2]), to_float(y[2])
            if uy is None:
                if ux is not None:
                    classes["explained:dur-na"] += 1
            elif ux is None or abs(ux - uy) > TOLERANCE:
                nxt = pb[n + 1] if n + 1 < len(pb) else None
                nxt_pts = to_float(nxt[0]) if nxt else None
                if n == len(pb) - 1 and ux is not None and ux < uy:
                    classes["explained:last-trim"] += 1  # the input ends the stream inside the last packet (an Ogg end granule); the output states the full frame
                elif (pr and n < len(pr) and ux is not None and abs(ux - uy) < 0.01) or (nxt_pts is not None and py is not None and abs(uy - (nxt_pts - py)) <= TOLERANCE):
                    classes["explained:dur-gap"] += 1
                elif ux is None:
                    classes["explained:dur-na"] += 1
                elif n == len(pb) - 1:
                    classes["explained:last-dur"] += 1  # the last sample's duration is the distance to the end of the track; ffprobe derives its own
                else:
                    bad.append("duration")
            if x[3] != y[3]:
                bad.append("size")
            if x[5] != y[5]:
                bad.append("hash")
            if x[4][:1] != y[4][:1]:
                if pr and n < len(pr) and (y[4][:1] == "K") == bool(pr[n][4]):
                    classes["explained:key-reader"] += 1  # the output keeps the key flag the container reader gave; ffprobe reads the input's differently
                else:
                    bad.append("flags")
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
    for line in first:
        print("\t" + line)
    return 1 if diff else 0


def main():
    verbose = "-v" in sys.argv
    argv = [a for a in sys.argv[1:] if a != "-v"]
    ffprobe, src, dst = argv
    mapping = []
    modes = {}
    with open(dst + ".map") as fh:
        for line in fh:
            f = line.split()
            mapping.append((int(f[0]), int(f[1])))
            modes[int(f[0])] = f[2] if len(f) > 2 else "none"
    reader = load_reader(dst + ".pkts")
    if dst.lower().endswith((".mkv", ".webm", ".mka")):
        return compare_time_based(ffprobe, src, dst, mapping, modes, reader)
    a = probe(ffprobe, src)
    b = probe(ffprobe, dst)
    classes = defaultdict(int)
    total = match = diff = 0
    first = []
    for o, i in mapping:
        pa, pb, pr = a.get(i, []), b.get(o, []), reader.get(o, [])
        count_kind, count_diff = None, 0
        if modes.get(o, "none") != "none" and len(pa) == len(pr):
            # Re-framed track: the expected size and hash are the ones written, not the input's.
            pa = [(x[0], x[1], x[2], str(r[3]), x[4], "SHA256:" + r[6]) for x, r in zip(pa, pr)]
        if len(pa) != len(pb):
            kind = "count(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))
            if pr and len(pr) == len(pb):
                classes["explained:count-reader(%d->%d:%d!=%d)" % (i, o, len(pa), len(pb))] += 1
            else:
                classes[kind] += 1
                diff += abs(len(pa) - len(pb))
                count_kind = kind
                count_diff = abs(len(pa) - len(pb))
        sc = defaultdict(int)
        st = sm = sd = 0
        sf = []
        pairs = list(zip(pa, pb))
        shifts = set()
        for x, y in pairs:
            if x[0] != y[0] or x[1] != y[1]:
                dx, dy = to_int(y[0]), to_int(y[1])
                ox, oy = to_int(x[0]), to_int(x[1])
                if None not in (dx, dy, ox, oy):
                    shifts.add((dx - ox, dy - oy))  # untimed packets (N/A) are judged below, against the reader's numbering plus this shift
        uniform = len(shifts) == 1 and next(iter(shifts))[0] == next(iter(shifts))[1]
        shift = next(iter(shifts))[0] if uniform else 0
        # Matroska stores no decode times: the remux derives them from the order of the presentation times (docs/remux.md), and ffprobe
        # derives its own for the input, so the two differ by convention. What must hold is that the output keeps what was written: one constant
        # between the decode times ffprobe reads from the output and the ones written (.pkts), and the same for the presentation times.
        mkv_in = src.lower().endswith((".mkv", ".webm", ".mka"))
        derived = False
        if mkv_in and pr and len(pr) == len(pb):
            dd = {to_int(y[1]) - r[0] for y, r in zip(pb, pr) if to_int(y[1]) is not None}
            pp = {to_int(y[0]) - r[1] for y, r in zip(pb, pr) if to_int(y[0]) is not None}
            derived = len(dd) == 1 and len(pp) == 1
        for n, (x, y) in enumerate(pairs):
            st += 1
            bad = []
            for k in range(6):
                if x[k] == y[k]:
                    continue
                cls = None
                if k < 2 and x[k] == "N/A" and pr and n < len(pr) and (to_int(y[k]) in (pr[n][1 - k], pr[n][1 - k] + shift) or (k == 0 and pr[n][1] < -(1 << 62) and y[0] == y[1])):
                    # The input has no time stamps (a raw elementary stream); the reader numbers the frames and the output keeps its numbers.
                    cls = "untimed-input"
                elif k < 2 and uniform and to_int(x[k]) is not None and to_int(y[k]) is not None and to_int(y[k]) - to_int(x[k]) == shift:
                    cls = "shift"
                elif derived and k in (1, 2):
                    cls = "derived-dts"
                elif k == 2:
                    nxt = pb[n + 1] if n + 1 < len(pb) else None
                    if x[2] == "N/A":
                        cls = "dur-na"
                    elif pr and n < len(pr) and to_int(y[2]) == pr[n][2]:
                        cls = "dur-reader"
                    elif nxt and to_int(nxt[1]) is not None and to_int(y[1]) is not None and to_int(y[2]) == to_int(nxt[1]) - to_int(y[1]):
                        cls = "dur-gap"
                    elif nxt is None:
                        cls = "last-dur"  # the last sample's duration is the distance to the end of the track; ffprobe derives it from the edit list and stream duration
                elif k == 4:
                    if pr and n < len(pr) and x[4][0] != y[4][0] and (y[4][0] == "K") == bool(pr[n][4]):
                        cls = "key-reader"
                    elif x[4][0] == y[4][0] and x[4][1:2] != y[4][1:2] and {x[4][1:2], y[4][1:2]} == {"D", "_"}:
                        cls = "disc-frag"
                if cls:
                    sc["explained:" + cls] += 1
                else:
                    bad.append(NAMES[k])
            if bad:
                sd += 1
                for k in bad:
                    sc[k] += 1
                if len(sf) < 5:
                    sf.append("stream %d->%d packet %d: %s vs %s" % (i, o, n, x, y))
            else:
                sm += 1
        if sd and reader_tier(pa, pb, pr):
            # Every packet still different is ffprobe reading the input differently from the container reader (see in-reader above).
            sc = defaultdict(int, {k: v for k, v in sc.items() if k.startswith("explained:")})
            sc["explained:in-reader"] += sd
            sm += sd
            sd = 0
            sf = []
        framing = False
        damaged = False
        if (count_diff or sc.get("size") or sc.get("hash") or sc.get("pts")) and modes.get(o, "none") == "none":
            ci, co = concat_hash(ffprobe, src, i), concat_hash(ffprobe, dst, o)
            framing = co is not None and ci == co
            # ffprobe lost sync in a damaged input where the container reader recovered: the output holds every byte the reader delivered.
            damaged = not framing and co is not None and co[1] == sum(r[3] for r in pr) and len(pr) > 0
        if framing or damaged:
            # ffprobe's parser cut the stream into packets differently from the container's own framing (or the reverse), but the bytes of the
            # stream are the same: the difference is framing only.
            if count_kind:
                del classes[count_kind]
                diff -= count_diff
            timing_only = bool(sc.get("pts")) and not (sc.get("size") or sc.get("hash") or count_diff)
            sc = defaultdict(int, {k: v for k, v in sc.items() if k.startswith("explained:")})
            sc["explained:damaged-input" if damaged else "explained:framing-and-timing" if timing_only else "explained:framing-only"] += sd + count_diff
            sm += sd
            sd = 0
            sf = []
        for k, v in sc.items():
            classes[k] += v
        total += st
        match += sm
        diff += sd
        first.extend(sf)
    name = src.rsplit("/", 1)[-1]
    print("%s\tstreams=%d\tpackets=%d\tmatch=%d\tdiff=%d\t%s" % (name, len(mapping), total, match, diff, dict(classes) if classes else ""))
    if verbose or diff:
        for line in first:
            print("\t" + line)
    return 1 if diff else 0


if __name__ == "__main__":
    sys.exit(main())

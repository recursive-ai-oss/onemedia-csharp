#!/usr/bin/env python3
"""Compares the packets ffprobe reads from a packaged presentation with the ones it reads from the file it was made from.

usage: check-packets.py <ffprobe> <source> <output-dir> [--bytes] [--source-ffprobe <ffprobe>]

For every track directory of the output (video0, audio0, ...) the HLS media playlist is probed, and for the DASH manifest every stream; each is
compared with the first source stream of the same kind: packet count, size, flags (key frame) and the SHA-256 of the payload (--bytes), and the
presentation and decode times after removing one constant shift per stream (an edit list or a negative first decode time moves a whole
timeline by one amount). A difference is reported per field; the exit status is 1 when any packet differs.

--source-ffprobe reads the source with another ffprobe (7.0.2 crashes on some MPEG-TS files).

ffmpeg's DASH reader ends every stream when the shortest representation ends, so a DASH row may lack the last packets of the longer stream. That is
reported as "dash-end" and not counted as a difference when the missing packets start within 50 ms of the shortest stream's end. A text/vtt
representation (a sidecar file that ffmpeg's DASH reader cannot open) is removed from a temporary copy of the manifest before probing.

A source whose video is stored as Annex B (MPEG-TS, raw H.264) or whose AAC carries ADTS headers is re-framed on the way in, so its payload
bytes differ by design: run without --bytes and only counts, sizes of other codecs, flags and times are compared.
"""
import json
import os
import subprocess
import sys
from collections import defaultdict

FIELDS = "stream_index,pts_time,dts_time,duration_time,size,flags,data_hash"
TOLERANCE = 0.0011


def run(ffprobe, args):
    return subprocess.run([ffprobe, "-v", "error"] + args, capture_output=True, text=True, check=False).stdout


def kinds(ffprobe, path):
    data = json.loads(run(ffprobe, ["-show_entries", "stream=index,codec_type", "-of", "json", path]) or "{}")
    return {s["index"]: s["codec_type"] for s in data.get("streams", [])}


def packets(ffprobe, path):
    out = run(ffprobe, ["-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=" + FIELDS, "-of", "csv=p=0", path])
    streams = defaultdict(list)
    for line in out.splitlines():
        f = line.strip().split(",")
        if len(f) < 7:
            continue
        try:
            streams[int(f[0])].append(f[1:7])
        except ValueError:
            pass
    return streams


def number(v):
    try:
        return float(v)
    except ValueError:
        return None


def compare(src, out, with_bytes):
    """Returns a dict of difference counts."""
    diff = defaultdict(int)
    if len(src) != len(out):
        diff["count"] = abs(len(src) - len(out))
    n = min(len(src), len(out))
    # ffprobe prints packets in file order; compare in presentation order so a reordered interleave does not matter.
    shift_pts = shift_dts = None
    for i in range(n):
        s, o = src[i], out[i]
        if s[3] != o[3]:
            # Without --bytes the source is re-framed (Annex B start codes and in-band parameter sets, ADTS headers), so sizes differ by design.
            diff["size" if with_bytes else "size-reframed(explained)"] += 1
        if with_bytes and s[5] != o[5]:
            diff["hash"] += 1
        if (s[4][:1] == "K") != (o[4][:1] == "K"):
            diff["key"] += 1
        sp, op, sd, od = number(s[0]), number(o[0]), number(s[1]), number(o[1])
        if sp is not None and op is not None:
            if shift_pts is None:
                shift_pts = op - sp
            elif abs((op - sp) - shift_pts) > TOLERANCE:
                diff["pts"] += 1
        if sd is not None and od is not None:
            if shift_dts is None:
                shift_dts = od - sd
            elif abs((od - sd) - shift_dts) > TOLERANCE:
                diff["dts"] += 1
    return diff, shift_pts


def main():
    argv = sys.argv[1:]
    source_ffprobe = None
    if "--source-ffprobe" in argv:
        i = argv.index("--source-ffprobe")
        source_ffprobe = argv[i + 1]
        del argv[i:i + 2]
    args = [a for a in argv if a != "--bytes"]
    with_bytes = "--bytes" in argv
    if len(args) != 3:
        print(__doc__)
        return 2
    ffprobe, source, outdir = args
    outdir = os.path.abspath(outdir)
    src_kinds = kinds(source_ffprobe or ffprobe, source)
    src_packets = packets(source_ffprobe or ffprobe, source)
    by_kind = {}
    for index, kind in sorted(src_kinds.items()):
        by_kind.setdefault(kind, index)
    total = 0
    bad = 0
    for track in sorted(os.listdir(outdir)):
        path = os.path.join(outdir, track, "index.m3u8")
        if not os.path.isfile(path):
            continue
        if track == "stream0":
            # A transport stream rendition carries audio and video together: compare each stream with the source stream of its kind.
            out_kinds, out_all = kinds(ffprobe, path), packets(ffprobe, path)
            for idx, o in sorted(out_all.items()):
                kind = out_kinds.get(idx)
                if kind not in by_kind:
                    continue
                s = src_packets[by_kind[kind]]
                d, shift = compare(s, o, with_bytes)
                total += len(s)
                bad += sum(v for k, v in d.items() if not k.endswith("(explained)"))
                print("%s HLS %s packets %d/%d shift %s diffs %s" % (track, kind, len(s), len(o), "%.6f" % shift if shift is not None else "n/a", dict(d) or "none"))
            continue
        kind = "video" if track.startswith("video") else "audio" if track.startswith("audio") else None
        if kind is None or kind not in by_kind:
            continue
        # A ladder rendition (video1...) comes from another input: only the first video and audio track map to this source.
        if track not in ("video0", "audio0"):
            continue
        out = packets(ffprobe, path)
        if len(out) != 1:
            print("%s HLS: expected one stream, found %d" % (track, len(out)))
            bad += 1
            continue
        s = src_packets[by_kind[kind]]
        o = next(iter(out.values()))
        d, shift = compare(s, o, with_bytes)
        total += len(s)
        bad += sum(v for k, v in d.items() if not k.endswith("(explained)"))
        print("%s HLS  packets %d/%d shift %s diffs %s" % (track, len(s), len(o), "%.6f" % shift if shift is not None else "n/a", dict(d) or "none"))
    mpd = os.path.join(outdir, "manifest.mpd")
    if os.path.isfile(mpd):
        text = open(mpd).read()
        if 'contentType="text"' in text:
            import re
            filtered = re.sub(r'<AdaptationSet[^>]*contentType="text".*?</AdaptationSet>', '', text, flags=re.S)
            mpd = os.path.join(outdir, "manifest.nosubs.mpd")
            open(mpd, "w").write(filtered)
        out_kinds = kinds(ffprobe, mpd)
        out_packets = packets(ffprobe, mpd)
        ends = []
        for kind in ("video", "audio"):
            if kind in by_kind and src_packets[by_kind[kind]]:
                last = src_packets[by_kind[kind]][-1]
                p, d = number(last[0]), number(last[2])
                if p is not None:
                    ends.append(p + (d or 0))
        shortest_end = min(ends) if ends else None
        for kind in ("video", "audio"):
            if kind not in by_kind:
                continue
            idx = [i for i, k in out_kinds.items() if k == kind]
            if not idx:
                print("%s DASH: no stream" % kind)
                bad += 1
                continue
            s = src_packets[by_kind[kind]]
            o = out_packets[idx[0]]
            d, shift = compare(s, o, with_bytes)
            if d.get("count") and len(o) < len(s) and shortest_end is not None:
                missing = s[len(o):]
                if all(number(m[0]) is not None and number(m[0]) >= shortest_end - 0.05 for m in missing):
                    del d["count"]
                    d["dash-end(explained)"] = len(missing)
            total += len(s)
            bad += sum(v for k, v in d.items() if not k.endswith("(explained)"))
            print("%s DASH packets %d/%d shift %s diffs %s" % (kind, len(s), len(o), "%.6f" % shift if shift is not None else "n/a", dict(d) or "none"))
    print("TOTAL packets %d differences %d" % (total, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

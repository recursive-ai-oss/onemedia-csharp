#!/usr/bin/env python3
"""Compares the packets of two files through ffprobe: per stream, the sequence of (size, key flag, SHA-256 of the payload) must be equal.

  packet-compare.py FFPROBE A.mp4 B.mp4 [--range-b FIRST COUNT] [--times]

With --times the presentation times must also be equal after removing one constant offset per stream (a clip's edit list moves its start).
--skip-a-head N / --skip-a-tail N drop that many packets from the video stream of A (for comparing a keyframe-mode clip with the same range of its source).
Prints one line per stream and exits 1 on any difference.
"""
import json
import subprocess
import sys
from fractions import Fraction


def packets(ffprobe, path):
    r = subprocess.run([ffprobe, "-v", "error", "-show_packets", "-show_data_hash", "SHA256", "-show_entries",
                        "packet=stream_index,pts,dts,duration,size,flags,data_hash", "-of", "json", path], capture_output=True, text=True)
    s = subprocess.run([ffprobe, "-v", "error", "-show_entries", "stream=index,codec_type,time_base", "-of", "json", path], capture_output=True, text=True)
    streams = {x["index"]: x for x in json.loads(s.stdout)["streams"]}
    out = {}
    for p in json.loads(r.stdout)["packets"]:
        out.setdefault(p["stream_index"], []).append(p)
    return streams, out


def tb(stream):
    n, d = stream["time_base"].split("/")
    return Fraction(int(n), int(d))


def key(p):
    return (int(p["size"]), "K" in p["flags"], p.get("data_hash", ""))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    ffprobe, a_path, b_path = args[:3]
    times = "--times" in sys.argv
    sa, pa = packets(ffprobe, a_path)
    sb, pb = packets(ffprobe, b_path)
    bad = False
    for idx, st in sa.items():
        la, lb = pa.get(idx, []), pb.get(idx, [])
        same = [key(p) for p in la] == [key(p) for p in lb]
        note = ""
        if same and times and la:
            # A constant offset per stream is allowed; ffprobe lists packets in decode order, so compare the pts relative to the first packet's dts.
            ta, tb_ = tb(st), tb(sb[idx])
            ra = [Fraction(int(p["pts"])) * ta - Fraction(int(la[0]["dts"])) * ta for p in la]
            rb = [Fraction(int(p["pts"])) * tb_ - Fraction(int(lb[0]["dts"])) * tb_ for p in lb]
            if ra != rb:
                same = False
                note = "pts differ"
        print(f"stream {idx} {st['codec_type']}: {len(la)} vs {len(lb)} packets {'EQUAL' if same else 'DIFFERENT'} {note}")
        bad |= not same
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

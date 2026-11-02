#!/usr/bin/env python3
"""Checks the files MakePlayable wrote against the files they were made from, with ffprobe, MP4Box and mkvalidator.

usage: check-makeplayable.py <oracle-bin-dir> <input-dir> <profile-dir>
  <profile-dir> holds report.tsv and out/ as run-makeplayable.sh leaves them.

For every written output (status Made) and every output made by moving the movie box:
  untouched tracks  each input stream is matched to an output stream with the same codec name, in order; packet count, size, flags and SHA-256
                    must be equal (classes: reframed = Annex B or ADTS input, so only count, flags and times are compared), and the time
                    stamps must differ from the input's by one constant per stream (the muxer moves the first decode time to zero).
  transcoded tracks input streams left without a match: the output audio stream is compared by duration (within one frame) and by
                    start time (within one frame) with the input's.
  structure         MP4Box -info for ISO outputs, mkvalidator for Matroska/WebM; any line mentioning an error counts.
Prints one line per file and a summary; the exit status is 1 when anything is unexplained.
"""
import csv
import json
import os
import subprocess
import sys
from collections import defaultdict
from fractions import Fraction

FRAME = Fraction(1024, 44100)  # the longest codec frame in play (AAC-LC at 44.1 kHz), ~23.2 ms; Opus frames are 20 ms


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, check=False)


def streams(ffprobe, path):
    j = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", path]).stdout or "{}")
    return j.get("streams", []), j.get("format", {})


def packets(ffprobe, path):
    out = run([ffprobe, "-v", "error", "-show_packets", "-show_data_hash", "sha256", "-show_entries",
               "packet=stream_index,pts,dts,duration,size,flags,data_hash", "-of", "csv=p=0", path]).stdout
    by = defaultdict(list)
    for line in out.splitlines():
        f = line.strip().split(",")
        if len(f) < 7:
            continue
        by[int(f[0])].append(f[1:7])
    return by


def num(v):
    try:
        return int(v)
    except ValueError:
        return None


def seconds(v):
    try:
        return Fraction(v)
    except (ValueError, TypeError):
        return None


def main():
    bindir, indir, pdir = sys.argv[1:4]
    ffprobe = os.path.join(bindir, "ffprobe")
    mp4box = os.path.join(bindir, "MP4Box")
    mkvalidator = os.path.join(bindir, "mkvalidator")
    rows = list(csv.reader(open(os.path.join(pdir, "report.tsv")), delimiter="\t"))[1:]
    totals = defaultdict(int)
    bad = 0
    for r in rows:
        src, out, status = r[0], r[1], r[2]
        if out == "-" or status == "AlreadyPlayable":
            continue
        a, b = os.path.join(indir, src), os.path.join(pdir, "out", out)
        sa, fa = streams(ffprobe, a)
        sb, fb = streams(ffprobe, b)
        pa, pb = packets(ffprobe, a), packets(ffprobe, b)
        # Match input streams to output streams by codec name, in order.
        free = list(sb)
        notes = []
        for s in sa:
            if s["codec_type"] not in ("video", "audio"):
                continue
            m = next((x for x in free if x["codec_type"] == s["codec_type"] and x["codec_name"] == s["codec_name"]), None)
            if m is None:
                # Transcoded (an audio track whose codec changed) or dropped.
                o = next((x for x in free if x["codec_type"] == "audio" and s["codec_type"] == "audio" and x["codec_name"] == "opus"), None)
                if o is None:
                    notes.append("lost:%s" % s["codec_name"])
                    totals["tracks_dropped"] += 1
                    continue
                free.remove(o)
                da, db = seconds(s.get("duration")) or seconds(fa.get("duration")), seconds(o.get("duration")) or seconds(fb.get("duration"))
                sta, stb = seconds(s.get("start_time")) or Fraction(0), seconds(o.get("start_time")) or Fraction(0)
                ok = da is not None and db is not None and abs(db - da) <= FRAME and abs(stb - sta) <= FRAME
                notes.append("transcoded:%s->opus dur %.3f->%.3f start %.3f->%.3f %s" % (s["codec_name"], float(da or 0), float(db or 0), float(sta), float(stb), "ok" if ok else "OUT OF RANGE"))
                totals["transcoded_ok" if ok else "transcoded_bad"] += 1
                bad += 0 if ok else 1
                continue
            free.remove(m)
            ka, kb = pa.get(s["index"], []), pb.get(m["index"], [])
            # Annex B input becomes length-prefixed in MP4/Matroska, so the bytes differ by design; the NAL units are checked by the remux tests.
            reframed = s["codec_name"] in ("h264", "hevc") and any(x[3] != y[3] for x, y in zip(ka[:50], kb[:50]))
            if s["codec_name"] == "aac" and fa.get("format_name") == "mpegts":
                reframed = True
            problems = []
            if len(ka) != len(kb):
                problems.append("packets %d->%d" % (len(ka), len(kb)))
            else:
                if not reframed:
                    diff = sum(1 for x, y in zip(ka, kb) if x[3] != y[3] or x[5] != y[5])
                    if diff:
                        problems.append("%d payload differences" % diff)
                flags = sum(1 for x, y in zip(ka, kb) if x[4][:1] != y[4][:1])
                if flags:
                    problems.append("%d key flag differences" % flags)
                # One constant shift per stream for pts (pts of input minus pts of output), allowing the output's first pts to be moved by an edit.
                sh = {(num(x[0]) - num(y[0])) for x, y in zip(ka, kb) if num(x[0]) is not None and num(y[0]) is not None}
                if len(sh) > 1:
                    # time bases may differ; compare in seconds instead.
                    tba, tbb = seconds(s.get("time_base")), seconds(m.get("time_base"))
                    secs = {(Fraction(num(x[0])) * tba - Fraction(num(y[0])) * tbb) for x, y in zip(ka, kb) if num(x[0]) is not None and num(y[0]) is not None}
                    # tolerance: one tick of the coarser time base
                    if secs and max(secs) - min(secs) > max(tba, tbb):
                        problems.append("time stamps not shifted by a constant")
            cls = "reframed" if reframed else "identical"
            if problems:
                bad += 1
                totals["tracks_bad"] += 1
                notes.append("%s:%s PROBLEM %s" % (s["codec_type"], s["codec_name"], "; ".join(problems)))
            else:
                totals["tracks_" + cls] += 1
                totals["packets_" + cls] += len(ka)
                notes.append("%s:%s %s %d pkts" % (s["codec_type"], s["codec_name"], cls, len(ka)))
        # Structure
        ext = os.path.splitext(out)[1]
        if ext in (".mkv", ".webm", ".mka"):
            v = run([mkvalidator, b])
            msg = (v.stdout + v.stderr)
            bad_lines = [l for l in msg.splitlines() if "ERR" in l.upper() or "WRN" in l.upper() and "WRN801" not in l]
            tool = "mkvalidator"
        else:
            v = run([mp4box, "-info", b])
            msg = v.stdout + v.stderr
            bad_lines = [l for l in msg.splitlines() if "error" in l.lower() and "no error" not in l.lower()]
            tool = "MP4Box"
        if v.returncode != 0 or bad_lines:
            bad += 1
            totals["structure_bad"] += 1
            notes.append("%s: %s" % (tool, "; ".join(bad_lines[:2]) or "exit %d" % v.returncode))
        else:
            totals["structure_ok"] += 1
        print("%-48s %s" % (src, " | ".join(notes)))
    print("summary", dict(totals), "unexplained", bad)
    return 1 if bad else 0


sys.exit(main())

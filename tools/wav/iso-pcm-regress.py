#!/usr/bin/env python3
"""Regression check for PCM carried in MOV/MP4: remuxes ffmpeg-written files to MP4, MOV and Matroska and compares what ffmpeg decodes
from the input and from the output (whole-stream md5 of the samples decoded to 64-bit float, and the sample count). Covers both
byte orders, 8/16/24/32-bit integers, float, A-law and mu-law, and the 'enda' atom that marks little-endian 24/32-bit entries.

usage: iso-pcm-regress.py --work DIR --tool "dotnet /path/to/OneMedia.RemuxCheck.dll" [--ffmpeg PATH] [--inputs DIR]
  --inputs DIR  also use every .mov/.mp4/.m4a file in DIR (generated files are always added).
Writes DIR/results.tsv (input, target, status) and prints a summary; exit code 1 on any mismatch. Targets a muxer refuses are counted as
refused, not as mismatches, and listed.
"""
import argparse
import glob
import hashlib
import os
import shlex
import subprocess
import sys

CODECS = ["pcm_u8", "pcm_s8", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be",
          "pcm_f32le", "pcm_f32be", "pcm_f64le", "pcm_f64be", "pcm_alaw", "pcm_mulaw"]
LAYOUTS = [("mono", 1), ("stereo", 2), ("5.1", 6)]


def run(args, **kw):
    return subprocess.run(args, capture_output=True, text=True, check=False, **kw)


def decode_md5(ffmpeg, path):
    p = subprocess.run([ffmpeg, "-v", "error", "-i", path, "-map", "0:a:0", "-c:a", "pcm_f64le", "-f", "f64le", "-"], capture_output=True)
    if p.returncode != 0 or not p.stdout:
        return None
    return hashlib.md5(p.stdout).hexdigest(), len(p.stdout)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", required=True)
    ap.add_argument("--tool", required=True)
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--inputs")
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    tool = shlex.split(a.tool)
    files = []
    for codec in CODECS:
        for layout, n in LAYOUTS:
            for ext in ["mov", "mp4"]:
                if layout == "5.1" and ext == "mp4":
                    continue
                name = os.path.join(a.work, "in-{}-{}.{}".format(codec[4:], layout.replace(".", ""), ext))
                freqs = [440, 550, 660, 330, 770, 220][:n]
                src = "aevalsrc='{}':c={}:s=44100:d=1".format("|".join("0.5*sin({}*2*PI*t)".format(x) for x in freqs), layout)
                r = run([a.ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", src, "-c:a", codec, "-strict", "-2", name])
                if r.returncode == 0 and os.path.getsize(name) > 0:
                    files.append(name)
                else:
                    print("skip (ffmpeg cannot write)", name)
    if a.inputs:
        for pat in ("*.mov", "*.mp4", "*.m4a"):
            files += sorted(glob.glob(os.path.join(a.inputs, pat)))
    rows, bad = [], 0
    for f in files:
        ref = decode_md5(a.ffmpeg, f)
        for fmt, ext in [("mp4", "mp4"), ("mov", "mov"), ("mkv", "mkv")]:
            out = os.path.join(a.work, "out-{}.{}".format(os.path.basename(f), ext))
            if os.path.exists(out):
                os.remove(out)
            r = run(tool + ["remux", f, out, "--format", fmt])
            if not os.path.exists(out) or os.path.getsize(out) == 0:
                rows.append((os.path.basename(f), fmt, "REFUSED " + (r.stdout.strip().splitlines() or [""])[0][:100]))
                continue
            got = decode_md5(a.ffmpeg, out)
            if ref is None:
                status = "INPUT-UNDECODABLE"
            elif got is None:
                status = "MISMATCH output undecodable"
                bad += 1
            elif got == ref:
                status = "OK"
            else:
                status = "MISMATCH {} vs {}".format(ref, got)
                bad += 1
            rows.append((os.path.basename(f), fmt, status))
            os.remove(out)
            for sfx in (".map", ".pkts"):
                if os.path.exists(out + sfx):
                    os.remove(out + sfx)
    with open(os.path.join(a.work, "results.tsv"), "w") as fh:
        for r in rows:
            fh.write("\t".join(r) + "\n")
    ok = sum(1 for r in rows if r[2] == "OK")
    ref_ = sum(1 for r in rows if r[2].startswith("REFUSED"))
    print("inputs", len(files), "rows", len(rows), "ok", ok, "refused", ref_, "mismatch", bad)
    for r in rows:
        if r[2].startswith(("MISMATCH", "REFUSED")):
            print("  ", *r)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

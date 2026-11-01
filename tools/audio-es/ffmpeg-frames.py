#!/usr/bin/env python3
"""Compares the frames the adapter decodes with ffmpeg's -f f32le output for the first audio stream of each file.

Usage: ffmpeg-frames.py <oracles.dll> <workdir> <file>...   (ffmpeg on PATH, dotnet on PATH)
One line per file: name, our frames, ffmpeg frames, difference, and the largest sample difference over the common prefix after
aligned at the start and at the end (a count difference with a small head-aligned figure is a difference at the end). ffmpeg runs with -threads 1.
"""
import array, os, subprocess, sys

dll, work, files = sys.argv[1], sys.argv[2], sys.argv[3:]
os.makedirs(work, exist_ok=True)
for f in files:
    name = os.path.basename(f)
    ours, ref = os.path.join(work, "ours.f32"), os.path.join(work, "ref.f32")
    r = subprocess.run(["dotnet", "exec", dll, "audiopcm", f, ours], capture_output=True, text=True)
    if r.returncode != 0:
        print(f"{name}\tERROR\t{(r.stdout + r.stderr).strip().splitlines()[-1:]}")
        continue
    hdr = r.stdout.strip().splitlines()[-1].split()  # "<frames> frames, <rate> Hz, <ch> ch"
    n, ch, rate = int(hdr[0]), int(hdr[4]), int(hdr[2])
    if os.path.exists(ref):
        os.remove(ref)  # a failed run must not leave the previous file's output behind
    e = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-threads", "1", "-i", f, "-map", "0:a:0", "-ac", str(ch), "-ar", str(rate), "-f", "f32le", ref], check=False).returncode
    if e != 0 or not os.path.exists(ref):
        print(f"{name}\tffmpeg failed ({e})")
        continue
    m = os.path.getsize(ref) // (4 * ch)
    a, b = array.array("f"), array.array("f")
    a.frombytes(open(ours, "rb").read()); b.frombytes(open(ref, "rb").read())
    d = n - m
    k = min(len(a), len(b))
    head = max((abs(p - q) for p, q in zip(a[:k], b[:k])), default=0.0)
    # The same with the longer side's leading frames dropped, to tell a difference at the start from one at the end.
    x, y = (a[d * ch:], b) if d > 0 else (a, b[-d * ch:])
    k = min(len(x), len(y))
    tail = max((abs(p - q) for p, q in zip(x[:k], y[:k])), default=0.0)
    print(f"{name}\t{ch}ch {rate}Hz\tours {n}\tffmpeg {m}\tdiff {d}\thead-aligned {head:.2e}\ttail-aligned {tail:.2e}")

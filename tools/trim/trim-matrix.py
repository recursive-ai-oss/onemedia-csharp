#!/usr/bin/env python3
"""Checks frame-accurate trims against a black-box decode.

For every generated source (codec x audio x frame rate x GOP) it cuts a few random ranges with the onemedia tool and compares, through ffmpeg:
  * video: the MD5 of every decoded frame of the clip equals the MD5s of the same frames of the source (framemd5), the clip starts at time 0
    and keeps the source's frame spacing;
  * audio: the clip's decoded PCM has the expected length (within one sample) and lines up with the source's PCM at the expected sample.
The expected frames are computed here from the source's own decode (not by the library).

  trim-matrix.py --oracles DIR --tool "dotnet path/to/onemedia.dll" --work DIR [--seed 1] [--per-file 3] [--only h264,hevc] [--quick]

Prints one TSV line per case and a summary; exit status 1 when a case fails.
"""
import argparse
import itertools
import os
import random
import re
import subprocess
import sys
from fractions import Fraction

import numpy as np

SAMPLE_RATE = 48000


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=kw.pop("text", True), **kw)


def parse_rate(s):
    return {"23.976": "24000/1001", "25": "25", "29.97": "30000/1001", "60": "60"}[s]


VIDEO = {
    "h264": lambda g, b: ["-c:v", "libx264", "-preset", "veryfast", "-g", str(g), "-keyint_min", str(g), "-bf", str(b), "-sc_threshold", "0"],
    "hevc": lambda g, b: ["-c:v", "libx265", "-preset", "veryfast", "-x265-params", f"keyint={g}:min-keyint={g}:bframes={b}:no-open-gop=1:scenecut=0:log-level=error", "-tag:v", "hvc1"],
    "av1": lambda g, b: ["-c:v", "libaom-av1", "-cpu-used", "8", "-g", str(g), "-lag-in-frames", "0", "-row-mt", "1"],
    "vp9": lambda g, b: ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", str(g), "-lag-in-frames", "0"],
}
AUDIO = {
    "aac": ["-c:a", "aac", "-b:a", "64k"],
    "opus": ["-c:a", "libopus", "-b:a", "48k"],
}


def generate(ff, path, vcodec, acodec, fps, gop, bf, seconds=12):
    cmd = [ff, "-v", "error", "-y", "-f", "lavfi", "-i", f"testsrc2=size=320x180:rate={parse_rate(fps)}",
           "-f", "lavfi", "-i", f"aevalsrc=0.5*sin(2*PI*(200+150*t)*t):s={SAMPLE_RATE}:c=mono",
           "-t", str(seconds), "-pix_fmt", "yuv420p", *VIDEO[vcodec](gop, bf), *AUDIO[acodec], "-movflags", "+faststart", path]
    r = run(cmd)
    if r.returncode != 0:
        raise RuntimeError(f"generate failed: {r.stderr[-400:]}")


def framemd5(ff, path):
    """[(time, md5)] of the decoded video frames, in presentation order, as the demuxer presents them."""
    r = run([ff, "-v", "error", "-i", path, "-map", "0:v:0", "-f", "framemd5", "-"])
    tb = None
    frames = []
    for line in r.stdout.splitlines():
        if line.startswith("#tb 0:"):
            n, d = line.split()[-1].split("/")
            tb = Fraction(int(n), int(d))
        elif line and not line.startswith("#"):
            f = [x.strip() for x in line.split(",")]
            frames.append((Fraction(int(f[2])) * tb, int(f[3]) * tb, f[5]))
    return frames


def keyframe_times(fp, path):
    r = run([fp, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts,flags", "-of", "csv=p=0", path])
    r2 = run([fp, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=time_base", "-of", "csv=p=0", path])
    n, d = r2.stdout.strip().split("/")
    tb = Fraction(int(n), int(d))
    keys = []
    for line in r.stdout.splitlines():
        pts, flags = line.split(",")[:2]
        if "K" in flags and pts != "N/A":
            keys.append(Fraction(int(pts)) * tb)
    return sorted(keys)


def pcm(ff, path):
    r = run([ff, "-v", "error", "-i", path, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "f32le", "-"], text=False)
    return np.frombuffer(r.stdout, dtype="<f4")


def floor_100ns(x: Fraction) -> Fraction:
    return Fraction(int(x * 10_000_000), 10_000_000) if x >= 0 else Fraction(0)


def check_case(a, src, idx, kind, frames, keys, src_pcm, from_t, to_t, keyframe):
    out = os.path.join(a.work, f"clip-{idx}.mp4")
    cmd = a.tool.split() + ["trim", src, out, "--from", f"{float(from_t):.7f}", "--to", f"{float(to_t):.7f}"] + (["--keyframe"] if keyframe else [])
    r = run(cmd)
    if r.returncode != 0:
        return "REFUSED", r.stderr.strip().splitlines()[-1] if r.stderr else r.stdout.strip()
    # Expected video: frames with from <= t < to; keyframe mode starts at the last keyframe at or before from.
    start_t = from_t
    if keyframe:
        start_t = max([k for k in keys if k <= from_t] or [keys[0]])
    expected = [f for f in frames if f[0] >= start_t and f[0] < to_t]
    got = framemd5(a.ffmpeg, out)
    problems = []
    if [e[2] for e in expected] != [g[2] for g in got]:
        n = min(len(expected), len(got))
        first_bad = next((i for i in range(n) if expected[i][2] != got[i][2]), n)
        problems.append(f"video frames differ (expected {len(expected)}, got {len(got)}, first mismatch at {first_bad})")
    if got and got[0][0] != 0:
        problems.append(f"first frame at {float(got[0][0]):.6f}")
    if len(expected) > 1 and len(got) == len(expected):
        spacing_exp = [expected[i + 1][0] - expected[i][0] for i in range(len(expected) - 1)]
        spacing_got = [got[i + 1][0] - got[i][0] for i in range(len(got) - 1)]
        if spacing_exp != spacing_got:
            problems.append("frame spacing differs")
    # Audio: the expected span is [S, E) in the source's timeline, S = first shown frame, E = the first frame not shown (or the end).
    audio = ""
    if expected:
        S = expected[0][0]
        nxt = [f for f in frames if f[0] >= expected[-1][0] + Fraction(1, 10**9) and f[0] >= to_t]
        E = nxt[0][0] if nxt else expected[-1][0] + expected[-1][1]
        s_samp = int(round(S * SAMPLE_RATE))
        want_len = int(round((E - S) * SAMPLE_RATE))
        clip = pcm(a.ffmpeg, out)
        d_len = len(clip) - min(want_len, max(0, len(src_pcm) - s_samp))
        # Align the clip against the source around the expected sample (skip the first 2048 samples: the lossy codec's start-up).
        probe = clip[2048:2048 + 4096]
        best, best_err = None, None
        for off in range(-64, 65):
            seg = src_pcm[s_samp + 2048 + off:s_samp + 2048 + off + len(probe)]
            if len(seg) != len(probe):
                continue
            err = float(np.mean((seg - probe) ** 2))
            if best_err is None or err < best_err:
                best, best_err = off, err
        audio = f"alen{d_len:+d} aoff{best:+d}" if best is not None else f"alen{d_len:+d} aoff?"
        # The edit list ends the clip at the sample, but ffmpeg decodes the last packet whole: up to one codec frame of overshoot is expected.
        frame = 1024 if "aac" in kind else 960
        if d_len < -1 or d_len > frame:
            problems.append(f"audio length off by {d_len} samples")
        if best is None or best != 0:
            problems.append(f"audio offset {best} samples")
    else:
        problems.append("no frames expected")
    ev = run([a.ffmpeg, "-v", "error", "-i", out, "-f", "null", "-"])
    if ev.stderr.strip():
        problems.append("ffmpeg: " + ev.stderr.strip().splitlines()[0][:100])
    if not a.keep:
        os.remove(out)
    return ("OK" if not problems else "FAIL"), f"{len(got)}f {audio} " + "; ".join(problems)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--oracles", required=True)
    p.add_argument("--tool", required=True)
    p.add_argument("--work", required=True)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--per-file", type=int, default=3)
    p.add_argument("--only", default="")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--keep", action="store_true")
    a = p.parse_args()
    a.ffmpeg = os.path.join(a.oracles, "ffmpeg")
    a.ffprobe = os.path.join(a.oracles, "ffprobe")
    os.makedirs(a.work, exist_ok=True)
    rng = random.Random(a.seed)
    vcodecs = [c for c in VIDEO if not a.only or c in a.only.split(",")]
    fpss = ["25"] if a.quick else ["23.976", "25", "29.97", "60"]
    gops = [(25, 2)] if a.quick else [(12, 0), (48, 2), (120, 3)]
    total = ok = 0
    n = 0
    for vc, ac, fps, (gop, bf) in itertools.product(vcodecs, AUDIO, fpss, gops):
        if a.quick and ac == "opus" and vc != "h264":
            continue
        name = f"{vc}-{ac}-{fps}-g{gop}b{bf}"
        src = os.path.join(a.work, name + ".mp4")
        if not os.path.exists(src):
            generate(a.ffmpeg, src, vc, ac, fps, gop, bf)
        frames = framemd5(a.ffmpeg, src)
        keys = keyframe_times(a.ffprobe, src)
        src_pcm = pcm(a.ffmpeg, src)
        for c in range(a.per_file):
            n += 1
            fa = rng.randrange(1, len(frames) - 40)
            length = rng.randrange(15, min(150, len(frames) - fa))
            from_t = frames[fa][0]
            to_t = frames[fa + length][0]
            if c == 1:  # between two frames: the next frame is the first one shown
                from_t = from_t - (frames[fa][0] - frames[fa - 1][0]) * Fraction(2, 5)
            from_t, to_t = floor_100ns(from_t), floor_100ns(to_t)
            keyframe = c == 2 and a.per_file > 2 and rng.random() < 0.5
            status, detail = check_case(a, src, n, name, frames, keys, src_pcm, from_t, to_t, keyframe)
            total += 1
            ok += status == "OK"
            print(f"{status}\t{name}\t{'key' if keyframe else 'acc'}\t{float(from_t):.4f}\t{float(to_t):.4f}\t{detail}", flush=True)
    print(f"SUMMARY\t{ok}/{total} cases match")
    return 0 if ok == total else 1


if __name__ == "__main__":
    sys.exit(main())

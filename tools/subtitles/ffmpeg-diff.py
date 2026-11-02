#!/usr/bin/env python3
"""Black-box differential of the subtitle readers and writers against ffmpeg.

Usage: ffmpeg-diff.py <workdir> <ffmpeg> <onemedia-dll> [cues=300] [seed=7]

For each of SRT, WebVTT, ASS and LRC (the formats ffmpeg reads and writes):
  A. ffmpeg writes the format from a generated SRT; our reader reads it; cues are compared with our reading of the SRT.
  B. our writer writes the format; ffmpeg converts it back to SRT; our reader reads that; compared with our reading of the SRT.
Compared per cue: start and end in milliseconds, and the plain text. Prints counts per pair and the first differences.
"""
import random, subprocess, sys, os

work, ffmpeg, dll = sys.argv[1:4]
N = int(sys.argv[4]) if len(sys.argv) > 4 else 300
random.seed(int(sys.argv[5]) if len(sys.argv) > 5 else 7)
os.makedirs(work, exist_ok=True)
WORDS = ["alpha", "beta", "gamma", "delta", "caf\u00e9", "na\u00efve", "\u65e5\u672c\u8a9e", "x", "hello,", "world!", "it's", "a&b", "1,2,3"]


def ts(ms):
    return "%02d:%02d:%02d,%03d" % (ms // 3600000, ms // 60000 % 60, ms // 1000 % 60, ms % 1000)


def make(path, line_choices, markup):
  t = 500
  out = []
  for i in range(N):
      t += random.randint(100, 3000)
      d = random.randint(300, 4000)
      lines = []
      for _ in range(random.choice(line_choices)):
          w = " ".join(random.choice(WORDS) for _ in range(random.randint(1, 6)))
          r = random.random() if markup else 1.0
          if r < 0.15:
              w = "<i>" + w + "</i>"
          elif r < 0.25:
              w = "<b>" + w + "</b>"
          elif r < 0.30:
              w = '<font color="#00ff00">' + w + "</font>"
          lines.append(w)
      out.append("%d\n%s --> %s\n%s\n" % (i + 1, ts(t), ts(t + d), "\n".join(lines)))
      t += d
  open(path, "w", encoding="utf8", newline="\n").write("\n".join(out) + "\n")


srt = os.path.join(work, "gen.srt")
srt1 = os.path.join(work, "gen1.srt")
random.seed(7)
make(srt, [1, 1, 1, 2, 3], True)
make(srt1, [1], False)


def run(args):
    return subprocess.run(args, capture_output=True, text=True, encoding="utf8")


def cues(path):
    r = run(["dotnet", dll, "subs", "info", path, "--cues"])
    if r.returncode != 0:
        raise SystemExit("onemedia failed on %s: %s" % (path, r.stderr))
    rows = [l.split("\t", 4) for l in r.stdout.splitlines() if l.startswith("cue\t")]
    return [(int(a[2]), int(a[3]), a[4] if len(a) > 4 else "") for a in rows]


base_by = {srt: cues(srt), srt1: cues(srt1)}
assert len(base_by[srt]) == N
print("generated %d cues" % N)
OUT = {"srt": "srt", "vtt": "webvtt", "ass": "ass", "lrc": "lrc"}
for ext, fmt in OUT.items():
    if ext == "srt":
        continue
    srt_in = srt1 if ext == "lrc" else srt
    base = base_by[srt_in]
    for direction in ("A ffmpeg-writes", "B ffmpeg-reads"):
        f = os.path.join(work, "%s.%s.%s" % ("A" if direction[0] == "A" else "B", "gen", ext))
        if os.path.exists(f):
            os.remove(f)
        if direction[0] == "A":
            r = run([ffmpeg, "-v", "error", "-y", "-i", srt_in, "-f", fmt if ext != "vtt" else "webvtt", f])
            if r.returncode:
                print(ext, direction, "ffmpeg failed:", r.stderr[:200])
                continue
            got = cues(f)
        else:
            r = run(["dotnet", dll, "subs", srt_in, "--to", f, "--overwrite"])
            if r.returncode:
                print(ext, direction, "onemedia failed:", r.stderr[:200])
                continue
            back = f + ".back.srt"
            if os.path.exists(back):
                os.remove(back)
            r = run([ffmpeg, "-v", "error", "-y", "-i", f, "-f", "srt", back])
            if r.returncode:
                print(ext, direction, "ffmpeg failed:", r.stderr[:200])
                continue
            got = cues(back)
        if len(got) != len(base):
            print(ext, direction, "cue count differs: %d vs %d" % (len(got), len(base)))
        exact = rounding = other_t = text_diff = 0
        examples = []
        for (bs, be, bt), (gs, ge, gt) in zip(base, got):
            ds, de = abs(gs - bs), abs(ge - be)
            if ext == "lrc":
                de = 0  # LRC has no end times
            if ds == 0 and de == 0:
                exact += 1
            elif ds <= 10 and de <= 10:
                rounding += 1
            else:
                other_t += 1
                examples.append(("time", bs, be, gs, ge))
            if ext == "lrc":
                continue
            if bt != gt:
                text_diff += 1
                if len(examples) < 6:
                    examples.append(("text", bt, gt))
        print("%-4s %-16s cues=%d exact-time=%d within-10ms=%d other-time=%d text-different=%d" % (ext, direction, len(base), exact, rounding, other_t, text_diff))
        for e in examples[:4]:
            print("     ", e)

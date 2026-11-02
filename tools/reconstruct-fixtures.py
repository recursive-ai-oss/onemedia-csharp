#!/usr/bin/env python3
"""Fixtures for the truncated-recording reconstruction checks.

  reconstruct-fixtures.py gen OUTDIR        healthy MP4/MOV/3GP files written by ffmpeg from lavfi input
  reconstruct-fixtures.py cut FILE OUTDIR   cut-off copies of one healthy file

The healthy files use ffmpeg (FFMPEG env var, default "ffmpeg") on generated test video and a sine tone, so they need no input
media. `cut` makes, for 11 offsets between 25 % and 95 % of the media data: a copy truncated at the offset (the movie box of an
ordinary file sits after the media data, so it is lost with the tail); every third one also with the media data box size field set
to zero, as a recorder leaves it before it patches the size. For a file whose movie box comes first (faststart) it also writes
copies with the movie box zeroed.
"""
import os
import struct
import subprocess
import sys

FFMPEG = os.environ.get("FFMPEG", "ffmpeg")

VIDEO_SRC = ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25"]
AUDIO_SRC = ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]
SECONDS = ["-t", "12"]
X264 = ["-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-pix_fmt", "yuv420p"]
X264_NOB = X264 + ["-bf", "0"]
X265 = ["-c:v", "libx265", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p", "-x265-params", "log-level=none"]

# name, extension, input args, codec args, extra output args
CASES = [
    ("h264b-aac", "mp4", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "aac", "-b:a", "96k"], []),
    ("h264-aac", "mp4", VIDEO_SRC + AUDIO_SRC, X264_NOB + ["-c:a", "aac", "-b:a", "96k"], []),
    ("h264b-aac-fs", "mp4", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "aac", "-b:a", "96k"], ["-movflags", "+faststart"]),
    ("hevc-aac", "mp4", VIDEO_SRC + AUDIO_SRC, X265 + ["-tag:v", "hvc1", "-c:a", "aac", "-b:a", "96k"], []),
    ("h264-aac", "mov", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "aac", "-b:a", "96k"], []),
    ("h264-pcm", "mov", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "pcm_s16le"], []),
    ("h264-mp3", "mp4", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "libmp3lame", "-b:a", "128k"], []),
    ("h264-ac3", "mp4", VIDEO_SRC + AUDIO_SRC, X264 + ["-c:a", "ac3", "-b:a", "192k"], []),
    ("h264-amr", "3gp", ["-f", "lavfi", "-i", "testsrc2=size=176x144:rate=15", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000"],
     X264_NOB + ["-profile:v", "baseline", "-c:a", "libopencore_amrnb", "-ar", "8000", "-ac", "1", "-b:a", "12.2k"], []),
    ("av1-aac", "mp4", VIDEO_SRC + AUDIO_SRC, ["-c:v", "libsvtav1", "-preset", "10", "-g", "50", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k"], []),
    ("h264-only", "mp4", VIDEO_SRC, X264, []),
    ("aac-only", "m4a", AUDIO_SRC, ["-c:a", "aac", "-b:a", "96k"], []),
]


def gen(outdir):
    os.makedirs(outdir, exist_ok=True)
    for name, ext, inputs, codec, extra in CASES:
        path = os.path.join(outdir, f"{name}.{ext}")
        cmd = [FFMPEG, "-v", "error", "-y"] + inputs + SECONDS + codec + extra + [path]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            print(f"skip {name}.{ext}: {r.stderr.strip().splitlines()[-1] if r.stderr.strip() else 'failed'}")
            if os.path.exists(path):
                os.remove(path)
        else:
            print(f"wrote {path} {os.path.getsize(path)}")


def boxes(data):
    pos = 0
    while pos + 8 <= len(data):
        size, typ = struct.unpack(">I4s", data[pos:pos + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", data[pos + 8:pos + 16])[0]
            header = 16
        elif size == 0:
            size = len(data) - pos
        yield pos, size, typ.decode("latin1"), header
        pos += size


def cut(path, outdir):
    os.makedirs(outdir, exist_ok=True)
    data = open(path, "rb").read()
    base = os.path.splitext(os.path.basename(path))[0]
    ext = os.path.splitext(path)[1]
    layout = list(boxes(data))
    mdat = next(b for b in layout if b[2] == "mdat")
    moov = next(b for b in layout if b[2] == "moov")
    start = mdat[0] + mdat[3]
    length = mdat[1] - mdat[3]
    moov_first = moov[0] < mdat[0]
    for i in range(11):
        frac = 0.25 + 0.07 * i
        # A jitter so the cut rarely lands on a sample boundary.
        at = start + int(length * frac) + (i * 37) % 11
        name = f"{base}.cut{int(frac * 100):02d}{ext}"
        out = bytearray(data[:at])
        open(os.path.join(outdir, name), "wb").write(out)
        if i % 3 == 0:
            z = bytearray(out)
            if mdat[3] == 8:
                z[mdat[0]:mdat[0] + 4] = b"\0\0\0\0"
            open(os.path.join(outdir, f"{base}.cut{int(frac * 100):02d}.size0{ext}"), "wb").write(z)
        if moov_first:
            z = bytearray(out)
            z[moov[0]:moov[0] + moov[1]] = b"\0" * moov[1]
            open(os.path.join(outdir, f"{base}.cut{int(frac * 100):02d}.zeroed{ext}"), "wb").write(z)


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "gen":
        gen(sys.argv[2])
    elif len(sys.argv) >= 4 and sys.argv[1] == "cut":
        cut(sys.argv[2], sys.argv[3])
    else:
        print(__doc__)
        sys.exit(2)

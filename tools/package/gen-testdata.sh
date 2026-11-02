#!/usr/bin/env bash
# Writes the small input of the packager tests: six seconds of H.264 (B-frames, a keyframe every second) and AAC, made by ffmpeg
# from generated test patterns (testsrc2, sine), so the file carries no third-party media.
#   usage: gen-testdata.sh [ffmpeg] [out-dir]
set -euo pipefail
FFMPEG="${1:-ffmpeg}"
OUT="${2:-$(cd "$(dirname "$0")/../.." && pwd)/testdata/package}"
mkdir -p "$OUT"
"$FFMPEG" -v error -y -f lavfi -i testsrc2=size=128x72:rate=25:duration=6 -f lavfi -i sine=frequency=440:sample_rate=22050:duration=6 \
  -c:v libx264 -preset veryfast -profile:v main -g 25 -keyint_min 25 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -b:v 60k \
  -c:a aac -b:a 24k -ac 1 -shortest -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -movflags +faststart "$OUT/gop1s-bframes-aac.mp4"
"$FFMPEG" -version | head -1 > "$OUT/VERSIONS.txt"

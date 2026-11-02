#!/usr/bin/env bash
# Writes the small healthy files the repair tests damage in memory (testdata/repair). Needs ffmpeg and mkvmerge-free: ffmpeg only.
# Usage: tools/gen-repair-testdata.sh [out-dir]
set -eu
OUT=${1:-testdata/repair}
mkdir -p "$OUT"
V='-v error -y -f lavfi -i testsrc2=size=160x120:rate=15 -f lavfi -i sine=frequency=440:sample_rate=32000 -t 2'
C='-c:v libx264 -g 15 -crf 40 -preset ultrafast -bf 0 -c:a aac -b:a 24k -bitexact -fflags +bitexact -flags:v +bitexact'
ffmpeg $V $C -cluster_time_limit 1000 "$OUT/tiny.mkv"
ffmpeg $V -c:v mpeg4 -g 15 -qscale:v 24 -c:a mp3 -b:a 32k -fflags +bitexact "$OUT/tiny.avi"
ffmpeg $V $C -movflags +faststart "$OUT/tiny.mp4"
ffmpeg $V $C -movflags frag_keyframe+empty_moov+default_base_moof "$OUT/tiny-frag.mp4"
ffmpeg $V $C -f mpegts "$OUT/tiny.ts"

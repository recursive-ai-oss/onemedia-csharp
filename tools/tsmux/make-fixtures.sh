#!/usr/bin/env bash
# Generates the small synthetic inputs for the transport stream muxer checks with ffmpeg (black box) from lavfi sources.
# Usage: make-fixtures.sh <ffmpeg> <outdir>
set -euo pipefail
FF=${1:?ffmpeg}; OUT=${2:?outdir}
mkdir -p "$OUT"; cd "$OUT"
V() { echo -f lavfi -i "testsrc2=size=${2:-320x240}:rate=$1"; }
A() { echo -f lavfi -i "sine=frequency=${1:-440}:sample_rate=${2:-44100}"; }
q() { "$FF" -v error -y "$@"; }

# H.264: reordered frames, rates that exercise the PCR gap fill and 29.97, open GOP, intra only, large frames
q $(V 25) $(A) -t 5 -c:v libx264 -bf 2 -g 50 -c:a aac -shortest h264-bf-25.mp4
q $(V 30000/1001) $(A 1000 48000) -t 5 -c:v libx264 -bf 3 -g 60 -c:a aac -shortest h264-bf-2997.mp4
q $(V 60) -t 4 -c:v libx264 -bf 2 -g 120 h264-60.mkv
q $(V 12) $(A) -t 5 -c:v libx264 -g 24 -c:a aac -shortest h264-12fps.mp4
q $(V 3) $(A) -t 6 -c:v libx264 -g 6 -c:a aac -shortest h264-3fps.mp4
q $(V 25) -t 4 -c:v libx264 -bf 2 -x264-params open-gop=1:keyint=25 h264-opengop.mp4
q $(V 25 1280x720) -t 3 -c:v libx264 -g 1 -crf 10 h264-intra-720p.mp4
q $(V 25 640x360) -t 4 -c:v libx264 -bf 2 -g 50 -c:a aac -shortest -movflags +frag_keyframe+empty_moov h264-frag.mp4
q $(V 25 640x360) $(A) -t 4 -c:v libx264 -bf 2 -g 50 -c:a aac -shortest -f matroska h264-aac.mkv
q $(V 25) $(A) -t 4 -c:v libx264 -bf 0 -g 25 -c:a aac -b:a 32k -ac 1 -ar 22050 -shortest h264-aac-mono22k.mp4
q $(V 25) $(A) -t 4 -c:v libx264 -bf 2 -g 50 -c:a aac -ac 6 -channel_layout 5.1 -shortest h264-aac-51.mp4 2>/dev/null || true
q $(V 25) $(A 440 44100) $(A 880 48000) -t 4 -map 0:v -map 1:a -map 2:a -c:v libx264 -bf 2 -g 50 -c:a aac h264-2aac.mp4
q $(V 25) -f lavfi -i "sine=frequency=300:sample_rate=48000,aformat=channel_layouts=stereo" -t 4 -c:v libx264 -bf 2 -g 50 -c:a aac -b:a 256k -shortest h264-aac-256k.mp4
# HEVC
q $(V 25) $(A) -t 4 -c:v libx265 -x265-params log-level=error:bframes=3:keyint=50 -tag:v hvc1 -c:a aac -shortest hevc-hvc1.mp4
q $(V 25) $(A) -t 4 -c:v libx265 -x265-params log-level=error:bframes=2:keyint=50 -c:a aac -shortest hevc.mkv
q $(V 30) -t 4 -c:v libx265 -x265-params log-level=error:keyint=30:open-gop=1 -tag:v hev1 hevc-hev1.mp4
# MPEG-2 video and MPEG audio, AC-3, E-AC-3
q $(V 25) $(A) -t 4 -c:v mpeg2video -bf 2 -g 12 -c:a mp2 -shortest mpeg2-mp2.mkv
q $(V 25) $(A) -t 4 -c:v libx264 -g 50 -c:a mp3 -shortest h264-mp3.mkv
q $(V 25) $(A 440 24000) -t 4 -c:v libx264 -g 50 -c:a mp3 -ar 24000 -shortest h264-mp3-24k.mkv
q $(V 25) $(A 440 48000) -t 4 -c:v libx264 -g 50 -c:a ac3 -shortest h264-ac3.mp4
q $(V 25) $(A 440 48000) -t 4 -c:v libx264 -g 50 -c:a eac3 -shortest h264-eac3.mp4
q $(V 25) $(A 440 48000) -t 4 -c:v libx264 -g 50 -c:a ac3 -shortest h264-ac3.mkv
# audio only
q $(A 440 44100) -t 6 -c:a aac aac.m4a
q $(A 440 48000) -t 6 -c:a aac -b:a 320k aac-48k.mp4
q $(A 440 44100) -t 6 -c:a mp3 mp3.mkv
q $(A 440 48000) -t 6 -c:a ac3 ac3.mkv
# video only, small and odd sizes
q $(V 25 160x120) -t 3 -c:v libx264 -bf 2 -g 25 small.mp4
q $(V 25 1920x1080) -t 2 -c:v libx264 -bf 2 -g 50 -b:v 8M hd.mp4
# an MPEG-TS source (ADTS and Annex B passed through) and an M2TS source
q $(V 25) $(A) -t 4 -c:v libx264 -bf 2 -g 50 -c:a aac -shortest -f mpegts h264-aac-src.ts
q $(V 25) $(A) -t 4 -c:v libx264 -bf 2 -g 50 -c:a ac3 -shortest -f mpegts h264-ac3-src.ts
ls | wc -l

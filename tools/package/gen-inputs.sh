#!/usr/bin/env bash
# Generates the inputs of the packaging sweep with ffmpeg from test patterns (testsrc2, sine): ten seconds each, 25 fps, a keyframe every two seconds
# (scene-cut detection off, so renditions of one picture cut at the same frames), small sizes. Nothing here carries third-party media.
#   usage: gen-inputs.sh <ffmpeg> <out-dir>
set -euo pipefail
FFMPEG="$1"; OUT="$2"
mkdir -p "$OUT"; cd "$OUT"
DUR=10
V() { echo "-f lavfi -i testsrc2=size=${1:-320x180}:rate=${2:-25}:duration=$DUR"; }
A() { echo "-f lavfi -i sine=frequency=${1:-440}:sample_rate=${2:-48000}:duration=$DUR"; }
run() { local out="$1"; shift; [ -s "$out" ] || "$FFMPEG" -v error -y -threads 2 "$@" "$out" || { echo "FAILED $out" >&2; rm -f "$out"; }; }
H264="-c:v libx264 -preset veryfast -g 50 -keyint_min 50 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -b:v 300k"
H265="-c:v libx265 -preset ultrafast -x265-params keyint=50:min-keyint=50:scenecut=0:bframes=2:log-level=error -pix_fmt yuv420p -b:v 300k -tag:v hvc1"
VP9="-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -g 50 -keyint_min 50 -b:v 300k -pix_fmt yuv420p"
AV1="-c:v libaom-av1 -cpu-used 8 -g 50 -keyint_min 50 -b:v 300k -pix_fmt yuv420p -row-mt 1"
AAC="-c:a aac -b:a 64k"
OPUS="-c:a libopus -b:a 48k"
AC3="-c:a ac3 -b:a 96k"
EAC3="-c:a eac3 -b:a 96k"
run h264-aac.mp4     $(V) $(A) $H264 $AAC -shortest
run h264-aac.mkv     $(V) $(A) $H264 $AAC -shortest
run h264-aac.ts      $(V) $(A) $H264 $AAC -shortest
run h264-opus.mkv    $(V) $(A) $H264 $OPUS -shortest
run h264-ac3.mp4     $(V) $(A) $H264 $AC3 -shortest
run h264-eac3.mp4    $(V) $(A) $H264 $EAC3 -shortest
run hevc-aac.mp4     $(V) $(A) $H265 $AAC -shortest
run hevc-ac3.mkv     $(V) $(A) $H265 $AC3 -shortest
run av1-opus.webm    $(V) $(A) $AV1 $OPUS -shortest
run av1-aac.mp4      $(V) $(A) $AV1 $AAC -shortest
run vp9-opus.webm    $(V) $(A) $VP9 $OPUS -shortest
run vp9-opus.mp4     $(V) $(A) $VP9 $OPUS -shortest
run vp9-aac.mkv      $(V) $(A) $VP9 $AAC -shortest
run h264-only.mp4    $(V) $H264
run aac-only.m4a     $(A 330) -vn $AAC
run opus-only.mka    $(A 550) -vn $OPUS
run ac3-only.mka     $(A 660) -vn $AC3
run h264-aac-2audio.mp4 $(V) $(A 440) $(A 880) -map 0:v -map 1:a -map 2:a $H264 $AAC -metadata:s:a:0 language=eng -metadata:s:a:1 language=spa -shortest
run h264-aac-bf0.mp4 $(V) $(A) -c:v libx264 -preset veryfast -g 50 -keyint_min 50 -sc_threshold 0 -bf 0 -pix_fmt yuv420p -b:v 300k $AAC -shortest
run h264-aac-ntsc.mp4 $(V 320x180 30000/1001) $(A) -c:v libx264 -preset veryfast -g 60 -keyint_min 60 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -b:v 300k $AAC -shortest
run h264-aac-longgop.mp4 $(V) $(A) -c:v libx264 -preset veryfast -g 200 -keyint_min 200 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -b:v 300k $AAC -shortest
run h264-aac-vfr.mkv $(V) $(A) $H264 $AAC -shortest
# a subtitle track in the container, and sidecar subtitles
printf '1\n00:00:01,000 --> 00:00:03,000\nFirst line\n\n2\n00:00:04,500 --> 00:00:06,000\nSecond line\nwith two rows\n\n3\n00:00:08,000 --> 00:00:09,800\n<i>Third</i>\n' > subs.srt
printf 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nEnglish one\n\n00:00:05.000 --> 00:00:09.000 line:80%%\nEnglish two\n' > subs-en.vtt
run h264-aac-subs.mkv $(V) $(A) -f srt -i subs.srt -map 0:v -map 1:a -map 2:s $H264 $AAC -c:s srt -shortest
# a bit-rate ladder: one picture at three sizes, the same keyframes
run ladder-180.mp4 $(V 320x180) $(A) $H264 $AAC -shortest
run ladder-360.mp4 $(V 640x360) -an $H264 -b:v 600k
run ladder-720.mp4 $(V 1280x720) -an $H264 -b:v 1200k
ls "$OUT" | wc -l

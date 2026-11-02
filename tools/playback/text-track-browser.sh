#!/usr/bin/env bash
# Builds VP9 files that carry text tracks written by OneMedia (MP4 with tx3g and with wvtt, WebM and Matroska) and shows what each Playwright browser exposes.
# Needs ffmpeg on PATH, the OneMedia tool built in Release, and the Playwright venv. Usage: text-track-browser.sh WORKDIR ONEMEDIA_DLL PYTHON
set -eu
W=$1; TOOL=$2; PY=$3
mkdir -p "$W"; cd "$W"
R=$(cd "$(dirname "$0")/../.." && pwd)
ffmpeg -v error -y -f lavfi -i testsrc2=s=320x240:r=10:d=8 -i "$R/testdata/text/cues.srt" -map 0 -map 1 -c:v libvpx-vp9 -b:v 200k -c:s srt in-srt.mkv
ffmpeg -v error -y -f lavfi -i testsrc2=s=320x240:r=10:d=8 -i "$R/testdata/text/cues.vtt" -map 0 -map 1 -c:v libvpx-vp9 -b:v 200k -c:s webvtt in-vtt.mkv
dotnet "$TOOL" remux in-srt.mkv tx3g.mp4 --overwrite >/dev/null
dotnet "$TOOL" remux in-vtt.mkv wvtt.mp4 --overwrite >/dev/null
dotnet "$TOOL" remux in-vtt.mkv text.webm --format webm --overwrite >/dev/null
dotnet "$TOOL" remux in-srt.mkv srt.mkv --overwrite >/dev/null
ffprobe -v error -show_entries stream=codec_name -of csv=p=0 tx3g.mp4 wvtt.mp4 text.webm | tr '\n' ' '; echo
"$PY" "$R/tools/playback/text-track-browser.py" "$W" tx3g.mp4 wvtt.mp4 text.webm srt.mkv

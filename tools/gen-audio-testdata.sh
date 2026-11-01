#!/usr/bin/env bash
# Regenerates the tiny audio fixtures in testdata/audio. The input is a synthetic sine; every file is written by ffmpeg, whose version is
# recorded in VERSIONS.txt. The .ogg/.flac/.m4a "twin" of each Matroska file is the same stream copied unchanged into the container
# OneAudio reads itself, so the tests can compare the samples decoded through the adapter with the samples decoded natively.
# Usage: tools/gen-audio-testdata.sh [ffmpeg]
set -eu
FF=${1:-ffmpeg}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/audio"
mkdir -p "$OUT"; cd "$OUT"
Q="-hide_banner -loglevel error -y"
A="-f lavfi -i sine=frequency=440:sample_rate=16000:duration=0.4"
$FF $Q $A -c:a libopus -b:a 16k opus.mka
$FF $Q $A -c:a libopus -b:a 16k opus.webm
$FF $Q $A -c:a libvorbis -q:a 0 vorbis.mka
$FF $Q $A -c:a flac flac.mka
$FF $Q $A -c:a aac -b:a 24k aac.mka
$FF $Q $A -c:a aac -b:a 24k aac.m4a
$FF $Q $A -c:a libmp3lame -b:a 32k mp3.mka
$FF $Q -i opus.mka -c copy opus.ogg
$FF $Q -i vorbis.mka -c copy vorbis.ogg
$FF $Q -i flac.mka -c copy flac.flac
$FF $Q -i aac.mka -c copy aac-twin.m4a
# DiscardPadding on blocks in the middle of a track, which ffmpeg does not write itself (nanoseconds; 62500 ns is one frame at 16 kHz,
# 20833.3 ns one at 48 kHz). The last block's value is kept or set so the end is trimmed too.
PY=${PYTHON:-python3}
$PY "$HERE/matroska-discard-padding.py" opus.mka opus-midpad.mka 5=10000000 12=5000000 -1=13500000
$PY "$HERE/matroska-discard-padding.py" vorbis.mka vorbis-midpad.mka 3=1000000 7=2500000 -1=3125000
$PY "$HERE/matroska-discard-padding.py" aac.mka aac-midpad.mka 3=4000000 -1=1000000
# Two audio tracks of different pitch, for reading one track and then the other from the same open file.
B="-f lavfi -i sine=frequency=660:sample_rate=16000:duration=0.4"
for ext in mp4 mkv; do
  $FF $Q $A $B -map 0:a -map 1:a -c:a aac -b:a 24k two-audio.$ext
done
{ "$FF" -version | head -1; } > VERSIONS.txt
ls -l

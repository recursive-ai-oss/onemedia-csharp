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
{ "$FF" -version | head -1; } > VERSIONS.txt
ls -l

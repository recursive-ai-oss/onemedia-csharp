#!/usr/bin/env bash
# Writes the small files the capture-time tests start from, with ffmpeg as a black-box writer from generated input. Each carries an old date in the
# fields its format keeps, so a capture-time write has something to replace. Usage: tools/gen-datewrite-fixtures.sh [outdir] (default testdata/datewrite)
set -eu
OUT=${1:-$(dirname "$0")/../testdata/datewrite}
mkdir -p "$OUT"
V=(-f lavfi -i testsrc2=size=64x48:rate=5:duration=1 -f lavfi -i sine=frequency=440:duration=1)
A=(-f lavfi -i sine=frequency=440:duration=0.3)
F=(-v error -y -fflags +bitexact)
ffmpeg "${F[@]}" "${V[@]}" -c:v libx264 -preset ultrafast -c:a aac -b:a 24k -metadata creation_time=2020-01-02T03:04:05Z "$OUT/plain.mp4"
ffmpeg "${F[@]}" "${V[@]}" -c:v libx264 -preset ultrafast -c:a aac -b:a 24k -movflags +use_metadata_tags -metadata creation_time=2020-01-02T03:04:05Z \
  -metadata com.apple.quicktime.creationdate=2020-01-02T04:04:05+0100 -metadata date=2020-01-02 "$OUT/keys.mov"
ffmpeg "${F[@]}" "${V[@]}" -c:v libx264 -preset ultrafast -c:a aac -b:a 24k -metadata creation_time=2020-01-02T03:04:05Z -metadata DATE_RECORDED=2020-01-02T03:04:05Z "$OUT/tagged.mkv"
ffmpeg "${F[@]}" "${V[@]}" -c:v libx264 -preset ultrafast -c:a aac -b:a 24k "$OUT/undated.mkv"
ffmpeg "${F[@]}" "${A[@]}" -write_bext 1 -metadata origination_date=2020-01-02 -metadata origination_time=03:04:05 -metadata date=2020 "$OUT/bwf.wav"
ffmpeg "${F[@]}" "${A[@]}" "$OUT/undated.wav"
ffmpeg "${F[@]}" "${A[@]}" -metadata date=2020-01-02 "$OUT/dated.mp3"
ffmpeg "${F[@]}" "${A[@]}" "$OUT/undated.mp3"
ffmpeg "${F[@]}" "${A[@]}" -metadata date=2020 "$OUT/dated.flac"
ffmpeg "${F[@]}" "${A[@]}" -c:a libvorbis -metadata date=2020 "$OUT/dated.ogg"
ffmpeg "${F[@]}" "${V[@]}" -c:v wmv2 -c:a wmav2 "$OUT/plain.wmv"
ls -l "$OUT"

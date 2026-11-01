#!/usr/bin/env bash
# Writes the small committed fixtures in testdata/xiph-flv with ffmpeg from synthetic input and records the ffmpeg version.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../../testdata/xiph-flv"
FF=${FFMPEG:-ffmpeg}
mkdir -p "$OUT"
G=(-hide_banner -loglevel error -y)
O=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1)
SINE=(-f lavfi -i "sine=frequency=440:sample_rate=44100:duration=1")
VID=(-f lavfi -i "testsrc2=size=160x120:rate=15:duration=1")
TAGS=(-metadata title="Tagged Title" -metadata artist="Some Artist" -metadata album="An Album" -metadata date=2021-05-06 -metadata track=3/12 -metadata genre=Test -metadata comment="a comment" -metadata REPLAYGAIN_TRACK_GAIN="-6.50 dB")
"$FF" "${G[@]}" "${VID[@]}" "${SINE[@]}" -c:v libx264 -preset ultrafast -g 15 -bf 2 -pix_fmt yuv420p -c:a aac -b:a 32k "${O[@]}" "$OUT/h264-aac.flv"
"$FF" "${G[@]}" "${VID[@]}" "${SINE[@]}" -c:v flv -g 15 -c:a libmp3lame -b:a 32k "${O[@]}" "$OUT/flv1-mp3.flv"
"$FF" "${G[@]}" "${SINE[@]}" -c:a libvorbis -q:a 1 "${TAGS[@]}" -bitexact -fflags +bitexact -flags:a +bitexact "$OUT/tags.ogg"
"$FF" "${G[@]}" "${SINE[@]}" -c:a flac -compression_level 0 "${TAGS[@]}" -bitexact -fflags +bitexact -flags:a +bitexact "$OUT/tags.flac"
"$FF" "${G[@]}" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=1" -c:a libopus -b:a 24k "${TAGS[@]}" -bitexact -fflags +bitexact "$OUT/tags.opus"
"$FF" "${G[@]}" -f lavfi -i "anoisesrc=seed=7:amplitude=0.3:sample_rate=48000:duration=0.4" -ac 6 -c:a libvorbis -q:a 3 -bitexact -fflags +bitexact -flags:a +bitexact "$OUT/vorbis-6ch.ogg"
"$FF" "${G[@]}" -f lavfi -i "testsrc2=size=64x48:rate=30:duration=0.5" -c:v libtheora -q:v 2 -g 8 -bitexact -fflags +bitexact -flags:v +bitexact "$OUT/theora.ogv"
"$FF" -version | head -1 > "$OUT/VERSIONS.txt"
ls -l "$OUT"

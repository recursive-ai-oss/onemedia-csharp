#!/usr/bin/env bash
# Regenerates the tiny ASF fixtures in testdata/asf with ffmpeg's ASF muxer from synthetic input (lavfi sources).
# The fixtures seed the ASF fuzz target and the tag tests. The ffmpeg version is recorded in testdata/asf/VERSION.
#
#   tools/gen-asf-testdata.sh [ffmpeg]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FF="${1:-ffmpeg}"
out="$here/testdata/asf"
mkdir -p "$out"
G=(-hide_banner -loglevel error -y)
O=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1 -strict -2)
V=(-f lavfi -i "testsrc2=size=96x72:rate=10:duration=1")
A=(-f lavfi -i "sine=frequency=440:sample_rate=22050:duration=1")

"$FF" "${G[@]}" "${V[@]}" "${A[@]}" -t 1 -c:v wmv2 -b:v 100k -g 5 -c:a wmav2 -b:a 32k "${O[@]}" -f asf "$out/wmv2-wmav2.wmv"
"$FF" "${G[@]}" "${A[@]}" -t 1 -c:a libmp3lame -b:a 32k "${O[@]}" -f asf "$out/mp3.wma"
"$FF" "${G[@]}" "${V[@]}" "${A[@]}" -t 1 -c:v msmpeg4 -b:v 100k -g 5 -c:a pcm_s16le -packetsize 800 "${O[@]}" -f asf "$out/msmpeg4v3-pcm-small-packets.asf"
"$FF" "${G[@]}" "${A[@]}" -t 1 -c:a wmav2 -b:a 32k -metadata title="Tone" -metadata artist="Sine" -metadata album="Tones" \
  -metadata genre="Sound" -metadata track=2 -metadata date=1999 -metadata comment="Sound check" -metadata copyright="None" "${O[@]}" -f asf "$out/wmav2-tagged.wma"
"$FF" -version | head -1 | cut -d' ' -f1-3 > "$out/VERSION"
ls -l "$out"

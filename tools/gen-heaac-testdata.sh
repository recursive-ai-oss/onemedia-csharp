#!/usr/bin/env bash
# Regenerates the HE-AAC signalling fixtures in testdata/heaac. No encoder here writes real SBR, so the coded frames are plain AAC-LC
# frames from a synthetic sine (written by ffmpeg) and MP4Box only rewrites the AudioSpecificConfig to signal SBR, the way HE-AAC files do:
#   sbr-compatible.m4a backward-compatible signalling (extension after the LC config; the sample entry keeps the core rate)
#   sbr-explicit.m4a  explicit hierarchical signalling (object type 5 first)
#   sbrps-explicit.m4a  explicit signalling with SBR and parametric stereo (object type 29)
#   sbr-implied.m4a   plain AAC-LC configuration at the core rate, but a sample entry that declares twice that rate: how files that signal
#                     SBR only in the stream (the oldest HE-AAC convention) look from the container
# The files therefore declare an output rate twice the core rate, while the coded data decodes to the core rate only. That mismatch is
# the point: it is what an SBR decoder would have to resolve.
# Usage: tools/gen-heaac-testdata.sh [ffmpeg] [MP4Box]
set -eu
FF=${1:-ffmpeg}
MP4BOX=${2:-MP4Box}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/heaac"
mkdir -p "$OUT"; cd "$OUT"
Q="-hide_banner -loglevel error -y"
$FF $Q -f lavfi -i sine=frequency=440:sample_rate=22050:duration=0.5 -c:a aac -b:a 24k -f adts core-mono.aac
$FF $Q -f lavfi -i "sine=frequency=440:sample_rate=22050:duration=0.5" -ac 2 -c:a aac -b:a 32k -f adts core-stereo.aac
$MP4BOX -quiet -add core-mono.aac:sbr -new sbr-compatible.m4a
$MP4BOX -quiet -add core-mono.aac:sbrx -new sbr-explicit.m4a
$MP4BOX -quiet -add core-stereo.aac:sbrx:psx -new sbrps-explicit.m4a
$MP4BOX -quiet -add core-mono.aac -new sbr-implied.m4a
# The sample entry's rate is the 16.16 field 28 bytes after the 'mp4a' type: 6 reserved, 2 data reference index, 8 reserved, 2 channels,
# 2 sample size, 2 predefined, 2 reserved. Same size as before, so no offset in the file moves.
OFF=$(grep -abo mp4a sbr-implied.m4a | head -1 | cut -d: -f1)
printf '\254\104\000\000' | dd of=sbr-implied.m4a bs=1 seek=$((OFF + 28)) conv=notrunc status=none
rm -f core-mono.aac core-stereo.aac
{ "$FF" -version | head -1; "$MP4BOX" -version 2>&1 | head -1; } > VERSIONS.txt
ls -l

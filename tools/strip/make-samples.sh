#!/bin/bash
# Writes small sample files with location and device identifiers in the tag systems of several formats, made by ffmpeg from generated input
# (testsrc2 / sine): the coordinates are over open water, the serial numbers are invented.
# usage: make-samples.sh <output dir> [ffmpeg]
set -e
OUT=$1; FF=${2:-ffmpeg}
mkdir -p "$OUT"
V="-f lavfi -i testsrc2=size=160x120:rate=15:duration=2 -f lavfi -i sine=frequency=440:duration=2"
C="-c:v libx264 -preset ultrafast -crf 40 -c:a aac -b:a 32k -shortest"
LOC='+47.6205-122.3493+184.500/'
$FF -v error -y $V $C -movflags use_metadata_tags -metadata "com.apple.quicktime.location.ISO6709=$LOC" -metadata "com.apple.quicktime.content.identifier=AAAA-BBBB-CCCC-0042" \
   -metadata com.apple.quicktime.make=Synthetic -metadata com.apple.quicktime.model=One "$OUT/keys.mov"
$FF -v error -y $V $C -metadata "location=$LOC" -metadata title=Kept "$OUT/udta.mp4"
$FF -v error -y $V $C -metadata "location=$LOC" -metadata title=Kept -f ipod "$OUT/ilst.m4a" 2>/dev/null || true
$FF -v error -y $V -c:v libvpx -deadline realtime -b:v 50k -c:a libopus -metadata "RECORDING_LOCATION=Atlantis 47.6205 -122.3493" -metadata "SERIAL_NUMBER=MKVSERIAL42" -metadata TITLE=Kept "$OUT/tags.mkv" 2>/dev/null || \
   $FF -v error -y $V $C -metadata "RECORDING_LOCATION=Atlantis 47.6205 -122.3493" -metadata "SERIAL_NUMBER=MKVSERIAL42" -metadata TITLE=Kept "$OUT/tags.mkv"
$FF -v error -y -f lavfi -i sine=frequency=440:duration=2 -id3v2_version 3 -metadata "location=Atlantis 47.6205 -122.3493" -metadata "serial_number=MP3SERIAL42" -metadata title=Kept "$OUT/tags.mp3"
$FF -v error -y -f lavfi -i sine=frequency=440:duration=2 -c:a libopus -metadata "LOCATION=Atlantis 47.6205 -122.3493" -metadata "SERIAL_NUMBER=OGGSERIAL42" -metadata title=Kept "$OUT/tags.opus"
$FF -v error -y -f lavfi -i sine=frequency=440:duration=2 -metadata "LOCATION=Atlantis 47.6205 -122.3493" -metadata "SERIAL_NUMBER=FLACSERIAL42" -metadata title=Kept "$OUT/tags.flac"
ls -la "$OUT"

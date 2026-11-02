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
# Text tracks with positions written as text: SRT in Matroska and mov_text in MP4 (the DJI-style lines, an ISO 6709 string and a decimal pair).
cat > "$OUT/.pos.srt" <<'EOS'
1
00:00:00,000 --> 00:00:01,000
[latitude: 47.620512] [longitude: -122.349300] [rel_alt: 1.300 abs_alt: 132.860] [iso : 100]

2
00:00:01,000 --> 00:00:02,000
GPS(47.620612,-122.349400,69.800) filmed at +47.6205-122.3493/
EOS
$FF -v error -y $V -i "$OUT/.pos.srt" -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -crf 40 -c:a aac -b:a 32k -c:s srt -shortest "$OUT/text.mkv"
$FF -v error -y $V -i "$OUT/.pos.srt" -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -crf 40 -c:a aac -b:a 32k -c:s mov_text -shortest "$OUT/text.mp4"
rm -f "$OUT/.pos.srt"
ls -la "$OUT"

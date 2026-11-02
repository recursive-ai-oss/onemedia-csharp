#!/usr/bin/env bash
# Generates the text-track inputs the subtitle track differential runs on, from the cue files in testdata/text and a synthetic video.
# Usage: OUT=<dir> [FFMPEG=ffmpeg] [MP4BOX=MP4Box] [MKVMERGE=mkvmerge] tools/gen-texttrack-fixtures.sh
# Files written by ffmpeg, MP4Box and mkvmerge are black-box oracle output; their versions are recorded in $OUT/VERSIONS.txt.
set -u
OUT=${OUT:?set OUT to the output directory}
FF=${FFMPEG:-ffmpeg}
MP4BOX=${MP4BOX:-}
MKVMERGE=${MKVMERGE:-}
HERE=$(cd "$(dirname "$0")/.." && pwd)/testdata/text
mkdir -p "$OUT"
cd "$OUT"
{ "$FF" -version | head -1; [ -n "$MP4BOX" ] && "$MP4BOX" -version 2>&1 | head -1; [ -n "$MKVMERGE" ] && "$MKVMERGE" --version | head -1; } > VERSIONS.txt
Q="-hide_banner -loglevel error -y"
V="-f lavfi -i testsrc2=size=64x48:rate=5:duration=8"
x264="-c:v libx264 -preset veryfast -g 5 -crf 45 -pix_fmt yuv420p"
cp "$HERE"/cues.srt "$HERE"/cues.vtt "$HERE"/cues.ass .

# A plain video (no text) for the muxers that take their text from a sidecar.
"$FF" $Q $V $x264 video.mp4 || echo "FAILED: video.mp4" >&2
"$FF" $Q $V -c:v libvpx -b:v 20k -deadline realtime -cpu-used 8 video.webm || echo "FAILED: video.webm" >&2

g() { local name=$1; shift; "$FF" $Q "$@" "$name" || echo "FAILED: $name" >&2; }

# ffmpeg: mov_text (tx3g) in MP4 and MOV, WebVTT in MP4, SRT/ASS/WebVTT in Matroska, WebVTT in WebM, two languages and dispositions.
g ff_tx3g.mp4   -i video.mp4 -i cues.srt -map 0 -map 1 -c copy -c:s mov_text -metadata:s:s:0 language=eng
g ff_tx3g.mov   -i video.mp4 -i cues.srt -map 0 -map 1 -c copy -c:s mov_text -metadata:s:s:0 language=fra

g ff_srt.mkv    -i video.mp4 -i cues.srt -map 0 -map 1 -c copy -c:s srt -metadata:s:s:0 language=eng -disposition:s:0 +default+forced
g ff_ass.mkv    -i video.mp4 -i cues.ass -map 0 -map 1 -c copy -c:s ass -metadata:s:s:0 language=jpn
g ff_vtt.mkv    -i video.mp4 -i cues.vtt -map 0 -map 1 -c copy -c:s webvtt -metadata:s:s:0 language=eng
g ff_vtt.webm   -i video.webm -i cues.vtt -map 0 -map 1 -c copy -c:s webvtt -metadata:s:s:0 language=eng
g ff_two.mkv    -i video.mp4 -i cues.srt -i cues.ass -map 0 -map 1 -map 2 -c copy -c:s:0 srt -c:s:1 ass -metadata:s:s:0 language=eng -metadata:s:s:1 language=deu -disposition:s:1 +hearing_impaired
g cues.ttml -i cues.srt -f ttml

if [ -n "$MP4BOX" ]; then
  "$MP4BOX" -quiet -add video.mp4#video -add cues.srt:lang=eng:name=Subtitles -new mb_tx3g.mp4 >/dev/null 2>&1 || echo "FAILED: mb_tx3g.mp4" >&2
  "$MP4BOX" -quiet -add video.mp4#video -add cues.vtt:lang=eng -new mb_wvtt.mp4 >/dev/null 2>&1 || echo "FAILED: mb_wvtt.mp4" >&2
  "$MP4BOX" -quiet -add video.mp4#video -add cues.ttml:lang=eng -new mb_stpp.mp4 >/dev/null 2>&1 || echo "FAILED: mb_stpp.mp4" >&2
  "$MP4BOX" -quiet -add video.mp4#video -add cues.srt:lang=eng -brand 3gp5 -new mb_tx3g.3gp >/dev/null 2>&1 || echo "FAILED: mb_tx3g.3gp" >&2
fi

if [ -n "$MKVMERGE" ]; then
  "$MKVMERGE" -q -o mm_srt.mkv video.mp4 --language 0:eng --default-track-flag 0:yes cues.srt >/dev/null 2>&1 || echo "FAILED: mm_srt.mkv" >&2
  "$MKVMERGE" -q -o mm_ass.mkv video.mp4 --language 0:jpn cues.ass >/dev/null 2>&1 || echo "FAILED: mm_ass.mkv" >&2
  "$MKVMERGE" -q -o mm_vtt.mkv video.mp4 --language 0:eng cues.vtt >/dev/null 2>&1 || echo "FAILED: mm_vtt.mkv" >&2
fi
ls -l

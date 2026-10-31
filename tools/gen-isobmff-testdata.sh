#!/usr/bin/env bash
# Regenerates the tiny MP4-family fixtures in testdata/isobmff and the ffprobe packet listings the tests compare with.
# Usage: tools/gen-isobmff-testdata.sh [ffmpeg] [ffprobe]
# The inputs are synthetic (lavfi test pattern and sine); the files are written by ffmpeg, whose version is recorded in VERSIONS.txt.
set -eu
FF=${1:-ffmpeg}; FP=${2:-ffprobe}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/isobmff"
mkdir -p "$OUT"; cd "$OUT"
Q="-hide_banner -loglevel error -y"
V="-f lavfi -i testsrc2=size=96x64:rate=25:duration=1"
A="-f lavfi -i sine=frequency=440:sample_rate=16000:duration=1"
X="-c:v libx264 -preset veryfast -bf 2 -g 12 -pix_fmt yuv420p -crf 35"
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest bframes_aac.mp4
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest -movflags +faststart faststart.mp4
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest bframes_aac.mov
$FF $Q -itsoffset 0.5 $V $A $X -c:a aac -b:a 24k -shortest empty_edit.mp4
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest -movflags frag_keyframe+empty_moov+default_base_moof frag.mp4
$FF $Q $V $X -movflags frag_keyframe+empty_moov+default_base_moof+negative_cts_offsets frag_negcts.mp4
$FF $Q $V $A -c:v mpeg4 -qscale:v 20 -s 96x64 -c:a aac -b:a 24k -ar 16000 -ac 1 -shortest -f 3gp video.3gp
$FF $Q $A -c:a libmp3lame -b:a 32k mp3.mp4
{ "$FF" -version | head -1; } > VERSIONS.txt
for f in *.mp4 *.mov *.3gp; do
  $FP -v error -show_packets -show_entries packet=stream_index,pts,dts,duration,size,pos,flags -of csv=p=0 "$f" > "$f.packets.csv"
done
ls -l

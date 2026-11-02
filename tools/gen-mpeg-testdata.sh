#!/usr/bin/env bash
# Regenerates the tiny MPEG fixtures in testdata/mpeg: transport streams (188 and 192 byte packets), program streams, raw video
# and IVF files, and the ffprobe packet listings the tests compare with. Inputs are synthetic (lavfi test pattern and sine); the
# files are written by ffmpeg, whose version is recorded in VERSIONS.txt.
# Usage: tools/gen-mpeg-testdata.sh [ffmpeg] [ffprobe]
set -eu
FF=${1:-ffmpeg}; FP=${2:-ffprobe}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/mpeg"
mkdir -p "$OUT"; cd "$OUT"
Q="-hide_banner -loglevel error -y"
B="-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1"
V="-f lavfi -i testsrc2=size=96x64:rate=25:duration=1"
A="-f lavfi -i sine=frequency=440:sample_rate=16000:duration=1"
X="-c:v libx264 -preset veryfast -threads 1 -bf 2 -g 12 -pix_fmt yuv420p -crf 35"
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest -f mpegts $B h264-aac.ts
$FF $Q $V $A $X -c:a ac3 -b:a 64k -shortest -f mpegts $B h264-ac3.ts
$FF $Q $V $A $X -c:a aac -b:a 24k -shortest -f mpegts $B h264-aac.m2ts
$FF $Q $V $A -c:v mpeg2video -g 12 -bf 2 -b:v 300k -c:a mp2 -b:a 64k -shortest -f mpegts $B mpeg2-mp2.ts
$FF $Q $V $A -c:v mpeg2video -g 12 -bf 2 -b:v 300k -c:a mp2 -b:a 64k -shortest -f vob $B mpeg2-mp2.vob
$FF $Q $V $A -c:v mpeg1video -g 12 -b:v 200k -c:a mp2 -b:a 64k -shortest -f mpeg $B mpeg1-mp2.mpg
$FF $Q $V $X -f h264 $B h264.264
$FF $Q $V -c:v libx265 -preset ultrafast -x265-params log-level=error:pools=1:frame-threads=1 -pix_fmt yuv420p -f hevc $B hevc.265
$FF $Q $V -c:v mpeg2video -g 12 -b:v 300k -f mpeg2video $B mpeg2.m2v
$FF $Q $V -c:v libvpx -b:v 200k -deadline realtime -f ivf $B vp8.ivf
# Frame doubling: pictures 7200 ticks apart under a VUI that declares 25 frames per second, pic_struct 7 in every picture timing SEI.
$FF $Q -f lavfi -i testsrc2=size=96x64:rate=12.5:duration=2 -c:v libx264 -preset veryfast -threads 1 -bf 2 -g 12 -pix_fmt yuv420p -crf 35 \
  -x264-params pic-struct=1 -bsf:v h264_metadata=tick_rate=50 -f mpegts $B pic-struct-raw.ts
python3 "$HERE/patch-pic-struct.py" pic-struct-raw.ts 7 h264-pic-struct-doubling.ts; rm pic-struct-raw.ts
{ "$FF" -version | head -1; } > VERSIONS.txt
for f in *.ts *.m2ts *.vob *.mpg *.264 *.265 *.m2v *.ivf; do
  $FP -v error -show_packets -show_entries packet=stream_index,pts,dts,duration,size,flags -of csv=p=0 "$f" > "$f.packets.csv"
done
ls -l

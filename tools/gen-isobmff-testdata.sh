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
# A fragmented AAC file whose movie box still states a duration for only the first few samples (as discont-frag.mp4 does): mdhd/tkhd/mvhd
# durations are patched to 4096 media ticks, a stale figure that is no length for the fragments after it.
$FF $Q -f lavfi -i "sine=frequency=440:sample_rate=16000:duration=2" -c:a aac -b:a 24k -movflags frag_keyframe+empty_moov+default_base_moof frag_stale_duration.m4a
python3 - <<'PY'
import struct
d = bytearray(open("frag_stale_duration.m4a", "rb").read())
def patch(name, off_v0, off_v1, size_v1, value):
    i = d.find(name)
    ver = d[i + 4]
    o = i + (off_v0 if ver == 0 else off_v1)
    d[o:o + (4 if ver == 0 else 8)] = value.to_bytes(4 if ver == 0 else 8, "big")
patch(b"mdhd", 20, 28, 0, 4096)
open("frag_stale_duration.m4a", "wb").write(d)
PY
{ "$FF" -version | head -1; } > VERSIONS.txt
for f in *.mp4 *.mov *.3gp; do
  $FP -v error -show_packets -show_entries packet=stream_index,pts,dts,duration,size,pos,flags -of csv=p=0 "$f" > "$f.packets.csv"
done
ls -l

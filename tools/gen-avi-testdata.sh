#!/usr/bin/env bash
# Regenerates the tiny AVI and DV fixtures in testdata/avi. The input is synthetic (lavfi test pattern and sine); every file is written by
# ffmpeg, whose version is recorded in VERSIONS.txt, and the DV-in-AVI type 1 file is a raw DV frame wrapped by tools/avi/wrap_dv_avi.py.
# Usage: tools/gen-avi-testdata.sh [ffmpeg]
set -eu
FF=${1:-ffmpeg}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/avi"
mkdir -p "$OUT"; cd "$OUT"
Q="-hide_banner -loglevel error -y"
O="-bitexact -fflags +bitexact -map_metadata -1"
V="-f lavfi -i testsrc2=size=64x48:rate=10:duration=0.5"
A="-f lavfi -i sine=frequency=440:sample_rate=8000:duration=0.5"
$FF $Q $V $A $O -c:v mjpeg -c:a pcm_s16le -flags:v +bitexact -flags:a +bitexact -f avi mjpeg-pcm.avi
$FF $Q $V $A $O -c:v mpeg4 -q:v 5 -g 5 -bf 0 -c:a libmp3lame -b:a 32k -flags:v +bitexact -flags:a +bitexact -f avi mpeg4-mp3.avi
# H.264 in AVI: Annex B with the parameter sets in the first key frame, no avcC.
$FF $Q $V $O -c:v libx264 -profile:v high -level 3.0 -preset ultrafast -g 5 -bsf:v h264_mp4toannexb -f avi h264.avi
$FF $Q $V $A $O -c:v mpeg4 -q:v 5 -c:a libmp3lame -b:a 32k -flags:v +bitexact -flags:a +bitexact \
  -metadata title="Test title" -metadata artist="Test artist" -metadata comment="A comment" -metadata date=2021-06-05 \
  -metadata creation_time=2021-06-05T10:11:12Z -f avi tagged.avi
# One PAL frame of DV25 (144000 bytes), with a recording date and time in the subcode and VAUX packs.
$FF $Q -f lavfi -i testsrc2=size=720x576:rate=25:duration=0.04 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=0.04,aformat=channel_layouts=stereo \
  $O -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 48000 -metadata creation_time=2019-03-04T05:06:07Z -f dv one-frame-pal.dv
python3 "$HERE/avi/wrap_dv_avi.py" one-frame-pal.dv one-frame-pal-type1.avi
{ "$FF" -version | head -1; } > VERSIONS.txt
ls -l

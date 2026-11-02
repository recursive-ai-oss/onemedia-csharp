#!/usr/bin/env bash
# Write-back, SCC and pass-through checks for embedded captions. Run on the worker:
#   run-caption-writeback.sh <workdir>
# Generates H.264 and HEVC test videos with ffmpeg, writes captions into them through CaptionSeiFeed, and compares what ccextractor and ffmpeg
# read back (black-box) with what was written; checks decoded frames are identical (framemd5) and that remux to Matroska keeps the captions.
set -uo pipefail
export PATH=~/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
W=$1; mkdir -p "$W"
HERE=$(cd "$(dirname "$0")" && pwd)
dotnet build "$HERE/OneMedia.CaptionCheck/OneMedia.CaptionCheck.csproj" -c Release -v quiet -clp:NoSummary -o "$W/_bin" >"$W/build.log" 2>&1 || { tail "$W/build.log"; exit 1; }
CC="dotnet $W/_bin/OneMedia.CaptionCheck.dll"
cd "$W"
$CC mkscc sample.scc; head -c 600 sample.scc | head -12
$CC scc2srt sample.scc scc-ours.srt
# SCC read by the black-box tools
ccextractor sample.scc -out=srt -o scc-ccx.srt >/dev/null 2>&1
ffmpeg -v error -y -i sample.scc scc-ffm.srt 2>/dev/null
for t in ccx ffm; do echo "SCC read vs $t: $(python3 "$HERE/caption-compare.py" scc-ours.srt scc-$t.srt | head -3)"; done
# canonical round trip is covered by the unit tests; here: our SCC written from what ccextractor can decode (SCC output of ccextractor is not used).
for v in h264 hevc; do
  enc=libx264; [ $v = hevc ] && enc=libx265
  ffmpeg -v error -y -f lavfi -i testsrc2=size=320x240:rate=30000/1001:duration=11 -c:v $enc -preset veryfast -bf 2 -g 30 -pix_fmt yuv420p -an base-$v.mp4
  $CC writeback base-$v.mp4 sample.scc cc-$v.mp4
  # decoded frames identical
  ffmpeg -v error -i base-$v.mp4 -f framemd5 base-$v.md5; ffmpeg -v error -i cc-$v.mp4 -f framemd5 cc-$v.md5
  echo "$v framemd5 identical: $(diff <(grep -v '^#' base-$v.md5 | cut -d, -f1,2,3,4,6) <(grep -v '^#' cc-$v.md5 | cut -d, -f1,2,3,4,6) >/dev/null && echo yes || echo NO)"
  # what the oracles read back
  ccextractor cc-$v.mp4 -in=mp4 -out=srt -o cc-$v-ccx.srt >/dev/null 2>&1
  ffmpeg -v error -y -f lavfi -i "movie=cc-$v.mp4[out0+subcc]" -map 0:s:0 cc-$v-ffm.srt 2>/dev/null
  $CC srt cc-$v.mp4 ours-$v --rebase >/dev/null 2>&1
  echo "$v ours vs written: $(python3 "$HERE/caption-compare.py" ours-$v/cc1.srt scc-ours.srt | head -1)"
  echo "$v ccextractor vs written: $(python3 "$HERE/caption-compare.py" cc-$v-ccx.srt scc-ours.srt | head -3)"
  echo "$v ffmpeg vs written: $(python3 "$HERE/caption-compare.py" cc-$v-ffm.srt scc-ours.srt | head -3)"
  # pass-through: remux to Matroska and back to MP4
  echo "$v written:  $($CC triplets cc-$v.mp4)"
  $CC remux cc-$v.mp4 pt-$v.mp4 >/dev/null; $CC remux cc-$v.mp4 pt-$v.mkv >/dev/null
  echo "$v mp4->mp4:  $($CC triplets pt-$v.mp4)"
  echo "$v mp4->mkv:  $($CC triplets pt-$v.mkv)"
  ffmpeg -v error -y -f lavfi -i "movie=pt-$v.mkv[out0+subcc]" -map 0:s:0 pt-$v-mkv-ffm.srt 2>/dev/null
  echo "$v mkv ffmpeg vs written: $(python3 "$HERE/caption-compare.py" pt-$v-mkv-ffm.srt scc-ours.srt | head -1)"
  ccextractor pt-$v.mp4 -in=mp4 -out=srt -o pt-$v-ccx.srt >/dev/null 2>&1
  echo "$v remuxed-mp4 ccextractor == written-mp4 ccextractor: $(cmp -s pt-$v-ccx.srt cc-$v-ccx.srt && echo yes || echo NO)"
done

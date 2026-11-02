#!/usr/bin/env bash
# Embedded captions through the transport stream muxer: the caption triplets of the output equal the input's, and ccextractor (black box)
# reads the same cues from both.
# Usage: caption-check.sh <workdir> [caption-sample.mpg ...]   (light work on the worker; needs the oracles and .NET 10)
set -uo pipefail
export PATH=~/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=4
W=$1; shift
mkdir -p "$W"
HERE=$(cd "$(dirname "$0")/.." && pwd)
[ -f ~/WORKER-BENCH.lock ] && { echo "bench lock present"; exit 3; }
nice dotnet build "$HERE/OneMedia.CaptionCheck/OneMedia.CaptionCheck.csproj" -c Release -v quiet -clp:NoSummary -o "$W/_bin" -p:UseArtifactsOutput=false -p:NuGetAudit=false -m:4 >"$W/build.log" 2>&1 || { tail "$W/build.log"; exit 1; }
CC="dotnet $W/_bin/OneMedia.CaptionCheck.dll"
cd "$W"
$CC mkscc sample.scc >/dev/null
$CC scc2srt sample.scc scc-ours.srt
check() { # name source
  local n=$1 src=$2 fmt
  for fmt in ts m2ts; do
    out=$n.$fmt
    $CC remux "$src" "$out" >"$n.$fmt.log" 2>&1 || { echo "$n -> $fmt: remux failed: $(tail -1 "$n.$fmt.log")"; continue; }
    a=$($CC triplets "$src" | grep -o 'sha256=.*'); b=$($CC triplets "$out" | grep -o 'sha256=.*')
    echo "$n -> $fmt: triplets equal: $([ "$a" = "$b" ] && echo yes || echo "NO ($a vs $b)") [$($CC triplets "$out" | head -1 | cut -c1-100)]"
    ccextractor "$src" -out=srt -o "$n.src.srt" >/dev/null 2>&1
    ccextractor "$out" -out=srt -o "$n.$fmt.srt" >/dev/null 2>&1
    echo "  ccextractor output vs input: $(python3 "$HERE/caption-compare.py" "$n.$fmt.srt" "$n.src.srt" 1000 | head -1)"
    ffmpeg -v error -y -f lavfi -i "movie=$out[out0+subcc]" -map 0:s:0 "$n.$fmt.ffm.srt" 2>/dev/null
    ffmpeg -v error -y -f lavfi -i "movie=$src[out0+subcc]" -map 0:s:0 "$n.src.ffm.srt" 2>/dev/null
    echo "  ffmpeg subcc output vs input: $(python3 "$HERE/caption-compare.py" "$n.$fmt.ffm.srt" "$n.src.ffm.srt" 1000 | head -1)"
  done
}
for v in h264 hevc; do
  enc=libx264; [ $v = hevc ] && enc=libx265
  ffmpeg -v error -y -f lavfi -i testsrc2=size=320x240:rate=30000/1001:duration=11 -c:v $enc -preset veryfast -bf 2 -g 30 -pix_fmt yuv420p -an base-$v.mp4
  $CC writeback base-$v.mp4 sample.scc cc-$v.mp4 >/dev/null
  check cc-$v cc-$v.mp4
done
for f in "$@"; do b=$(basename "$f"); check "${b%.*}" "$f"; done

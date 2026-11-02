#!/usr/bin/env bash
# Indicative timings, information only: packaging one 10-minute H.264/AAC file as CMAF with HLS and DASH (6 s segments) with `onemedia package`,
# Shaka Packager and MP4Box -dash. Run it under a heavy queue job, never as a published row. Each tool runs REPS times; the median wall time is printed.
#   usage: native-compare.sh [reps]        env: SHAKA (packager binary), FFMPEG, MP4BOX
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"; S="$WORK/native"
REPS="${1:-5}"
SHAKA="${SHAKA:-$HOME_DIR/oracles/shaka/packager}"; FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"; MP4BOX="${MP4BOX:-$HOME_DIR/oracles/bin/MP4Box}"
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
rm -rf "$S"; mkdir -p "$S"
dotnet build "$ROOT/src/OneMedia.Tool/OneMedia.Tool.csproj" -c Release -v quiet -clp:NoSummary -m:4 -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
"$FFMPEG" -v error -y -f lavfi -i "testsrc2=size=640x360:rate=25" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 600 \
  -c:v libx264 -preset veryfast -crf 28 -g 150 -keyint_min 150 -sc_threshold 0 -pix_fmt yuv420p -c:a aac -b:a 64k -movflags +faststart "$S/in.mp4"
ls -l "$S/in.mp4" | awk '{print "input bytes", $5}'
median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }
time_it() { local t0 t1; t0=$(date +%s.%N); "$@" > /dev/null 2>&1; local rc=$?; t1=$(date +%s.%N); awk -v a="$t0" -v b="$t1" -v r="$rc" 'BEGIN {printf "%.3f %d\n", b - a, r}'; }
run_tool() {
  local name="$1"; shift; local times="" rc=0
  for i in $(seq "$REPS"); do
    rm -rf "$S/out"; mkdir -p "$S/out/v" "$S/out/a"
    read -r t rc < <(time_it "$@")
    times="$times $t"
  done
  echo "$name: median $(echo $times | tr ' ' '\n' | median) s over $REPS runs (last exit $rc); output $(du -sk "$S/out" | cut -f1) KiB, $(find "$S/out" -type f | wc -l) files"
}
run_tool "onemedia package --hls --dash" dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll" package "$S/in.mp4" "$S/out" --segment 6 --overwrite
cd "$S" || exit 1
run_tool "Shaka Packager (CMAF, HLS + DASH)" "$SHAKA" "in=$S/in.mp4,stream=video,init_segment=$S/out/v/init.mp4,segment_template=$S/out/v/\$Number\$.m4s" "in=$S/in.mp4,stream=audio,init_segment=$S/out/a/init.mp4,segment_template=$S/out/a/\$Number\$.m4s" --segment_duration 6 --mpd_output "$S/out/manifest.mpd" --hls_master_playlist_output "$S/out/master.m3u8"
run_tool "MP4Box -dash 6000 (DASH only)" "$MP4BOX" -dash 6000 -frag 6000 -rap -profile live -segment-name 'seg_$RepresentationID$_' -out "$S/out/manifest.mpd" "$S/in.mp4#video" "$S/in.mp4#audio"
rm -rf "$S"

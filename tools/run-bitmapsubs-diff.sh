#!/bin/bash
# Bitmap subtitles: decode crafted streams, and streams other tools write from them, with OneMedia and compare the pictures with the RGBA
# frames ffmpeg renders for the same file. Run on the worker: tools/run-bitmapsubs-diff.sh [workdir]
set -u
[ -d ~/dotnet10 ] && export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10
export DOTNET_CLI_TELEMETRY_OPTOUT=1
ORA=${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}
W=${1:-$HOME/onemedia/u53-bitmapsubs/work}
B=${ONEMEDIA_SCRATCH:-$HOME/onemedia/u53-bitmapsubs/build}
mkdir -p "$W"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
ONEMEDIA_SCRATCH=$B nice dotnet build -c Release -m:4 -p:NuGetAudit=false -v quiet -clp:NoSummary "$ROOT/tools/OneMedia.BitmapSubsCheck" 2>&1 | grep -E 'error' | head
TOOL="dotnet $B/artifacts/bin/OneMedia.BitmapSubsCheck/release/OneMedia.BitmapSubsCheck.dll"
$TOOL gen "$W" || exit 1

# render <input> <w> <h> <name> [track]: ffmpeg paints each subtitle event on a transparent canvas; the tool compares
render() {
  local in=$1 w=$2 h=$3 name=$4 trk=${5:-}
  # showinfo logs the time of every frame the subtitle events produce
  "$ORA/ffmpeg" -v info -y -canvas_size ${w}x${h} -i "$in" -filter_complex "[0:s:0]copy,showinfo[o]" -map "[o]" -f rawvideo -pix_fmt rgba -fps_mode passthrough "$W/$name.raw" 2> "$W/$name.showinfo"
  $TOOL compare "$in" $w $h "$W/$name.raw" "$W/$name.showinfo" $trk
  rm -f "$W/$name.raw"
}

for n in pgs-sd:720:480 pgs-hd:1920:1080; do
  IFS=: read -r name w h <<< "$n"
  render "$W/$name.sup" $w $h $name
  "$ORA/mkvmerge" -q -o "$W/$name.mkv" "$W/$name.sup" >/dev/null 2>&1 && render "$W/$name.mkv" $w $h "$name-mkv"
  "$ORA/ffmpeg" -v error -y -i "$W/$name.sup" -c:s copy "$W/$name-ff.mkv" && render "$W/$name-ff.mkv" $w $h "$name-ffmkv"
  # Plain MPEG-TS from ffmpeg carries PGS as an untyped private stream (stream type 6); only M2TS packets use stream type 0x90.
  "$ORA/ffmpeg" -v error -y -i "$W/$name.sup" -c:s copy -f mpegts -mpegts_m2ts_mode 1 "$W/$name-pgs.m2ts" && render "$W/$name-pgs.m2ts" $w $h "$name-pgsm2ts"
  "$ORA/ffmpeg" -v error -y -canvas_size ${w}x${h} -i "$W/$name.sup" -c:s dvbsub -f mpegts "$W/$name-dvb.ts" && render "$W/$name-dvb.ts" $w $h "$name-dvb"
  "$ORA/ffmpeg" -v error -y -canvas_size ${w}x${h} -i "$W/$name.sup" -c:s dvdsub -f matroska "$W/$name-dvd.mkv" && render "$W/$name-dvd.mkv" $w $h "$name-dvd"
  # a DVD program stream carries no palette (the IFO file does): only geometry and opacity are comparable
  "$ORA/ffmpeg" -v error -y -canvas_size ${w}x${h} -i "$W/$name.sup" -c:s dvdsub -f vob "$W/$name-dvd.vob" && COMPARE_ALPHA_ONLY=1 render "$W/$name-dvd.vob" $w $h "$name-vob"
done
render "$W/vobsub.idx" 720 480 vobsub
"$ORA/mkvmerge" -q -o "$W/vobsub.mkv" "$W/vobsub.idx" >/dev/null 2>&1 && render "$W/vobsub.mkv" 720 480 vobsub-mkv

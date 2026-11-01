#!/usr/bin/env bash
# Trims a 4.5 GB file (structure real, media data a sparse hole) in the middle and across most of it, and prints the peak resident memory of each run,
# the output size, and ffprobe's view of the result. Constant memory means the peak does not grow with the file.
#
#   ORACLES=<dir with ffprobe> WORK=<scratch dir> tools/trim/run-trim-large.sh
set -eu
ORACLES=${ORACLES:?directory holding ffprobe}
WORK=${WORK:?scratch directory}
export ONEMEDIA_SCRATCH="$WORK"
mkdir -p "$WORK/big"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
dotnet build tools/OneMedia.IsoBmffDiff -c Release -v quiet -clp:NoSummary
TOOL="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
BIG="$WORK/big/co64_4g.mp4"
[ -e "$BIG" ] || dotnet "$WORK/artifacts/bin/OneMedia.IsoBmffDiff/release/OneMedia.IsoBmffDiff.dll" --make-big "$BIG" 4.5 moovlast
ls -ls "$BIG"
$TOOL info "$BIG" | head -6
DUR=$($TOOL info "$BIG" | awk '/^duration:/ {split($2,a,":"); print a[1]*3600+a[2]*60+a[3]; exit}')
echo "duration ${DUR}s"
run() {
  local name=$1 from=$2 to=$3; shift 3
  /usr/bin/time -v -o "$WORK/big/$name.time" $TOOL trim "$BIG" "$WORK/big/$name.mp4" --from "$from" --to "$to" "$@" | head -3
  echo "$name: peak RSS $(awk '/Maximum resident/ {print $6/1024 " MB"}' "$WORK/big/$name.time"), wall $(awk '/Elapsed/ {print $8}' "$WORK/big/$name.time"), output $(stat -c %s "$WORK/big/$name.mp4") bytes"
  "$ORACLES/ffprobe" -v error -show_entries format=duration:stream=codec_name,nb_frames -of compact "$WORK/big/$name.mp4"
}
run middle "$(python3 -c "print($DUR*0.4)")" "$(python3 -c "print($DUR*0.5)")"
run most "$(python3 -c "print($DUR*0.05)")" "$(python3 -c "print($DUR*0.95)")"
rm -f "$WORK/big/most.mp4" "$WORK/big/middle.mp4"

#!/usr/bin/env bash
# Repairs every damaged file in a directory and checks the result.
#   run-repair-case.sh <healthy> <damaged-dir> <out-dir> [ffprobe] [validator]
# For each damaged file: onemedia repair, packet differential against the healthy original (the repaired file must hold the original's
# first N packets per track), ffprobe error count, and seekability (ffprobe -read_intervals at three points).
# Needs RC (the OneMedia.RepairCheck dll) in the environment.
set -u
H=$1; D=$2; O=$3; FFPROBE=${4:-ffprobe}
mkdir -p "$O"
ext="${H##*.}"
for f in "$D"/*."$ext"; do
  n=$(basename "$f"); r="$O/$n"; rm -f "$r"
  res=$(dotnet exec "$RC" repair "$f" "$r" 2>&1 | head -1)
  if [ ! -f "$r" ]; then echo "$n: NOOUT $res"; continue; fi
  d=$(dotnet exec "$RC" ${MODE:-diff} "$H" "$r" 2>&1 | grep RESULT)
  e=$($FFPROBE -v error -show_entries format=duration -of csv=p=0 "$r" 2>&1 | grep -vc '^[0-9.]*$')
  dur=$($FFPROBE -v error -show_entries format=duration -of csv=p=0 "$r" 2>/dev/null | head -1)
  dd=$($FFPROBE -v error -show_entries format=duration -of csv=p=0 "$f" 2>&1 | grep -vc '^[0-9.]*$')
  sk=0
  for t in 1 3 5; do
    c=$($FFPROBE -v error -read_intervals "$t%+#5" -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 "$r" 2>&1 | grep -c .)
    [ "$c" -ge 1 ] || sk=1
  done
  echo "$n: ${res%% in=*} | $d | ffprobe-errors in=$dd out=$e dur=$dur seek-fail=$sk"
done

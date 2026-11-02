#!/usr/bin/env bash
# Repairs every damaged file in a directory and checks the result.
#   run-repair-case.sh <healthy> <damaged-dir> <out-dir> [ffprobe]
# Environment: RC (the OneMedia.RepairCheck dll), MODE (diff: the repaired file holds the original's first N packets per track;
# subseq: every repaired packet is one of the original's, in order), VALIDATE (mkvalidator or mp4box: the oracle run on input and output).
# For each damaged file prints: status, packet comparison, ffprobe error lines on input and output, the repaired duration, whether
# ffprobe -read_intervals finds packets at three seek points, and the validator verdict for input and output.
set -u
H=$1; D=$2; O=$3; FFPROBE=${4:-ffprobe}
mkdir -p "$O"
ext="${H##*.}"
validate() {
  case "${VALIDATE:-}" in
    mkvalidator) mkvalidator "$1" >/dev/null 2>&1 && echo ok || echo FAIL ;;
    mp4box) out=$(MP4Box -info "$1" 2>&1); if echo "$out" | grep -qiE 'error|invalid|corrupt'; then echo FAIL; else echo ok; fi ;;
    *) echo - ;;
  esac
}
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
  echo "$n: ${res%% in=*} | $d | ffprobe-errors in=$dd out=$e dur=$dur seek-fail=$sk | validator in=$(validate "$f") out=$(validate "$r")"
done

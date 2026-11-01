#!/usr/bin/env bash
# Copy-muxes every file in a list into Matroska (WebM for WebM input) with MatroskaMuxer and checks each output:
# packets against ffprobe, then mkvalidator, mkvinfo and ffmpeg. One line per file; exit status is non-zero when any step fails.
#
#   tools/matroska/run-mux-checks.sh <work-dir> <file-list> [--live] [--crc]
#
# <file-list> holds one input path per line. Environment: ORACLES (default ~/onemedia/oracles/bin).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="${1:?work directory}"; list="${2:?file list}"; shift 2
extra=("$@")
ORACLES="${ORACLES:-$HOME/onemedia/oracles/bin}"
export ONEMEDIA_SCRATCH="$work"
mkdir -p "$work/out" "$work/logs"
dotnet build "$here/tools/OneMedia.MatroskaMuxCheck" -c Release -v quiet -clp:NoSummary || exit 2
dll="$work/artifacts/bin/OneMedia.MatroskaMuxCheck/release/OneMedia.MatroskaMuxCheck.dll"
"$ORACLES/ffprobe" -version | head -1; "$ORACLES/mkvalidator" --version 2>&1 | head -1; "$ORACLES/mkvinfo" --version | head -1
bad=0; n=0; skipped=0
while IFS= read -r f <&3; do
  [ -f "$f" ] || continue
  b="$(basename "$f")"; ext=mkv; opt=()
  case "$b" in *.webm) ext=webm; opt=(--webm);; esac
  out="$work/out/$b.$ext"
  log="$work/logs/mux-$b.log"
  dotnet exec "$dll" check "$f" "$out" --ffprobe "$ORACLES/ffprobe" "${opt[@]}" "${extra[@]}" > "$log" 2>&1
  rc=$?
  if grep -q '^SKIP' "$log"; then skipped=$((skipped+1)); echo "SKIP $b: $(head -1 "$log" | cut -c1-150)"; continue; fi
  n=$((n+1))
  v=ok; vi=ok; fm=ok
  # mkvalidator: no errors in the output (warnings the input already has are not counted against it)
  "$ORACLES/mkvalidator" "$out" > "$work/logs/val-$b.log" 2>&1
  "$ORACLES/mkvalidator" "$f" > "$work/logs/valin-$b.log" 2>&1
  # a kind of error (ERRxxx) the output has and the input does not
  new_err=$(comm -23 <(grep -o '^ERR[0-9A-F]*' "$work/logs/val-$b.log" | sort -u) <(grep -o '^ERR[0-9A-F]*' "$work/logs/valin-$b.log" | sort -u))
  [ -z "$new_err" ] || v=FAIL
  "$ORACLES/mkvinfo" "$out" > "$work/logs/info-$b.log" 2>&1 || vi=FAIL
  "$ORACLES/ffmpeg" -nostdin -threads 1 -v error -i "$out" -f null - > "$work/logs/ffm-$b.log" 2>&1; "$ORACLES/ffmpeg" -nostdin -threads 1 -v error -i "$f" -f null - > "$work/logs/ffmin-$b.log" 2>&1
  if [ "$(wc -l < "$work/logs/ffm-$b.log")" -gt "$(wc -l < "$work/logs/ffmin-$b.log")" ]; then
    # ffmpeg derives decode times for streams with B-frames; where ffprobe states no presentation time for the input's packets it warns about the output's
    if grep -q 'not-stated-by-ffprobe' "$log"; then fm=explained
    else
      # the same warnings appear when ffmpeg reads a file it wrote itself from the input (its decode-time guess for B-frames at millisecond resolution)
      "$ORACLES/ffmpeg" -nostdin -threads 1 -v error -i "$f" -c copy -f matroska "$work/out/ref-$b.mkv" > /dev/null 2>&1
      "$ORACLES/ffmpeg" -nostdin -threads 1 -v error -i "$work/out/ref-$b.mkv" -f null - > "$work/logs/ffmref-$b.log" 2>&1; rm -f "$work/out/ref-$b.mkv"
      if [ "$(wc -l < "$work/logs/ffm-$b.log")" -le "$(wc -l < "$work/logs/ffmref-$b.log")" ]; then fm=explained; else fm=NOISY; fi
    fi
  fi
  vw=$(grep -c 'WRN' "$work/logs/val-$b.log"); viw=$(grep -c 'WRN' "$work/logs/valin-$b.log")
  status=OK; if [ $rc -ne 0 ] || [ $v != ok ] || [ $vi != ok ] || [ $fm = NOISY ]; then status=BAD; bad=$((bad+1)); fi
  echo "$status $b mux=$rc mkvalidator=$v(wrn $vw, input $viw) mkvinfo=$vi ffmpeg=$fm | $(grep -h 'compared' "$log" | cut -c1-200)"
  [ $status = OK ] && rm -f "$out"
done 3< "$list"
echo "files=$n skipped=$skipped bad=$bad"
[ $bad -eq 0 ]

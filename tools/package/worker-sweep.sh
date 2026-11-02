#!/usr/bin/env bash
# Packages every input of the sweep (HLS and DASH, multi-file and single-file) and checks the packets ffprobe reads back against the input's.
# Runs on the worker; state under $ONEMEDIA_HOME (default ~/onemedia). Output: $WORK/summary.tsv and one log per input.
#   usage: worker-sweep.sh [segment-seconds]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"; FFPROBE="${FFPROBE:-$HOME_DIR/oracles/bin/ffprobe}"
SEG="${1:-4}"
mkdir -p "$WORK/logs"
"$HERE/gen-inputs.sh" "$FFMPEG" "$WORK/inputs" > "$WORK/logs/gen.log" 2>&1
dotnet build "$ROOT/src/OneMedia.Tool/OneMedia.Tool.csproj" -c Release -v quiet -clp:NoSummary -m:4 -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
TOOL=(dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll")
rm -rf "$WORK/out"; mkdir -p "$WORK/out"
printf 'input\tmode\tstatus\tpackets\tdiffs\tnotes\n' > "$WORK/summary.tsv"
for f in "$WORK"/inputs/*; do
  name="$(basename "$f")"; case "$name" in subs.*|subs-*|ladder-*) continue;; esac
  bytes=""; case "$name" in *.mp4|*.m4a|*.mkv|*.mka|*.webm) bytes="--bytes";; esac
  for mode in multi single; do
    out="$WORK/out/$name.$mode"; extra=""; [ "$mode" = single ] && extra="--single-file"
    sub=""; [ "$name" = h264-aac.mp4 ] && sub="--subs $WORK/inputs/subs-en.vtt:en:English"
    if ! "${TOOL[@]}" package "$f" "$out" --segment "$SEG" $extra $sub > "$WORK/logs/$name.$mode.package.log" 2>&1; then
      printf '%s\t%s\tPACKAGE-FAILED\t-\t-\t%s\n' "$name" "$mode" "$(tail -1 "$WORK/logs/$name.$mode.package.log")" >> "$WORK/summary.tsv"; continue; fi
    srcprobe=""; case "$name" in *.ts|*.m2ts) srcprobe="--source-ffprobe ${FFPROBE_TS:-/usr/bin/ffprobe}";; esac
    python3 "$HERE/check-packets.py" "$FFPROBE" "$f" "$out" $bytes $srcprobe > "$WORK/logs/$name.$mode.packets.log" 2>&1; rc=$?
    total="$(grep '^TOTAL' "$WORK/logs/$name.$mode.packets.log" | awk '{print $3"/"$5}')"
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$mode" "$([ $rc = 0 ] && echo OK || echo DIFF)" "${total%%/*}" "${total##*/}" "$(grep -c note: "$WORK/logs/$name.$mode.package.log")" >> "$WORK/summary.tsv"
  done
done
# the ladder
out="$WORK/out/ladder"
"${TOOL[@]}" package "$WORK/inputs/ladder-180.mp4" "$WORK/inputs/ladder-360.mp4" "$WORK/inputs/ladder-720.mp4" "$out" --segment "$SEG" > "$WORK/logs/ladder.package.log" 2>&1 \
  && printf 'ladder\tmulti\tPACKAGED\t-\t-\t%s\n' "$(grep -c note: "$WORK/logs/ladder.package.log")" >> "$WORK/summary.tsv"
column -t -s $'\t' "$WORK/summary.tsv"

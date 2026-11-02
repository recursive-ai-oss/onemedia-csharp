#!/usr/bin/env bash
# Real-time live packaging run: a synthetic stream is generated at its own speed (ffmpeg -re) and piped into `onemedia package - <dir> --live`,
# while OneMedia.LiveCheck reads every playlist and MPD update with the library's parsers and checks it. Resident memory is sampled every 5 s.
#   usage: live-paced.sh [seconds] [segment-seconds]      output: $WORK/paced/{rss.tsv,check.log}
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"; S="$WORK/paced"
SECS="${1:-600}"; SEG="${2:-4}"; WINDOW=6
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"
rm -rf "$S"; mkdir -p "$S" "$WORK/logs"
for p in src/OneMedia.Tool/OneMedia.Tool.csproj tools/OneMedia.LiveCheck/OneMedia.LiveCheck.csproj; do
  dotnet build "$ROOT/$p" -c Release -v quiet -clp:NoSummary -m:4 -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
done
mkfifo "$S/pipe"
"$FFMPEG" -v error -y -re -f lavfi -i "testsrc2=size=320x180:rate=10" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t "$SECS" \
  -c:v libx264 -preset ultrafast -g $((SEG * 10)) -keyint_min $((SEG * 10)) -sc_threshold 0 -b:v 150k -pix_fmt yuv420p -c:a aac -b:a 32k -f matroska "$S/pipe" &
FF=$!
dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll" package - "$S/out" --live --segment "$SEG" --window $WINDOW < "$S/pipe" > "$S/package.log" 2>&1 &
PK=$!
dotnet "$WORK/artifacts/bin/OneMedia.LiveCheck/release/OneMedia.LiveCheck.dll" "$S/out" $WINDOW $((SECS + 60)) > "$S/check.log" 2>&1 &
CK=$!
printf 'elapsed_s\trss_kib\tsegment_files\n' > "$S/rss.tsv"
T0=$SECONDS
while kill -0 "$PK" 2>/dev/null; do
  rss="$(awk '/VmRSS/ {print $2}' /proc/$PK/status 2>/dev/null || echo 0)"
  printf "%s\t%s\t%s\n" $((SECONDS - T0)) "$rss" "$(ls "$S/out"/video0/*.m4s 2>/dev/null | wc -l)" >> "$S/rss.tsv"
  sleep 5
done
wait "$PK"; echo "packager exit $?" >> "$S/check.log"; wait "$FF"; echo "ffmpeg exit $?" >> "$S/check.log"
wait "$CK"; echo "livecheck exit $?" >> "$S/check.log"
awk 'NR>1 && $2>0 {if (min==""||$2<min) min=$2; if ($2>max) max=$2; n++} END {print "rss KiB min", min, "max", max, "samples", n}' "$S/rss.tsv" >> "$S/check.log"
cat "$S/check.log"
rm -rf "$S/out" "$S/pipe"

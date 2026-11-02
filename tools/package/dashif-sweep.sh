#!/usr/bin/env bash
# Packages a few inputs for DASH and runs the DASH-IF conformance validator over each; writes $WORK/dashif/<name>.out and prints the failed and warned
# checks of the modules that apply to plain DASH-IF live/on-demand content (the Global, DASH-IF IOP and generic CMAF ones).
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
TOOL=(dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll")
dotnet build "$ROOT/src/OneMedia.Tool/OneMedia.Tool.csproj" -c Release -v quiet -clp:NoSummary -m:4 -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
mkdir -p "$WORK/dashif"; rm -rf "$WORK/dashif-in"; mkdir -p "$WORK/dashif-in"
I="$WORK/inputs"
"${TOOL[@]}" package "$I/h264-aac-longgop.mp4" "$WORK/dashif-in/vod" --dash --segment 4 > /dev/null 2>&1
"${TOOL[@]}" package "$I/h264-aac-longgop.mp4" "$WORK/dashif-in/single" --dash --segment 4 --single-file > /dev/null 2>&1
"${TOOL[@]}" package "$I/h264-aac.mp4" "$WORK/dashif-in/subs" --dash --segment 4 --subs "$I/subs-en.vtt:en:English" > /dev/null 2>&1
"${TOOL[@]}" package "$I/h264-aac.mkv" "$WORK/dashif-in/mkv" --dash --segment 4 > /dev/null 2>&1
"${TOOL[@]}" package "$I/hevc-aac.mp4" "$WORK/dashif-in/hevc" --dash --segment 4 > /dev/null 2>&1
"${TOOL[@]}" package "$I/ladder-180.mp4" "$I/ladder-360.mp4" "$I/ladder-720.mp4" "$WORK/dashif-in/ladder" --dash --segment 4 > /dev/null 2>&1
for d in "$WORK"/dashif-in/*; do
  n="$(basename "$d")"
  timeout 600 "$HERE/run-dashif.sh" "$d" > "$WORK/dashif/$n.out" 2>&1; echo "== $n rc=$?"
  python3 "$HERE/dashif-summary.py" "$WORK/dashif/$n.out" | grep -v '^SKIP\|WAVE\|CTA-5005\|Low Latency\|DVB\|HbbTV' | cut -c1-360
done

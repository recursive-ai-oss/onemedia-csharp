#!/usr/bin/env bash
# Live packaging soak: a synthetic stream of <hours> of media (default 2) is generated as fast as ffmpeg can encode it and piped into
# `onemedia package - <dir> --live`. The packager's resident memory, the number of segment files on disk and the playlist window are sampled every
# few seconds; a few playlist snapshots are probed with ffprobe. Output: $WORK/soak/rss.tsv and soak.log.
#   usage: live-soak.sh [hours] [segment-seconds]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"; S="$WORK/soak"
HOURS="${1:-2}"; SEG="${2:-4}"
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"; FFPROBE="${FFPROBE:-$HOME_DIR/oracles/bin/ffprobe}"
rm -rf "$S"; mkdir -p "$S/snap" "$WORK/logs"
dotnet build "$ROOT/src/OneMedia.Tool/OneMedia.Tool.csproj" -c Release -v quiet -clp:NoSummary -m:4 -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
SECONDS_TOTAL="${SOAK_SECONDS:-$((HOURS * 3600))}"
mkfifo "$S/pipe"
"$FFMPEG" -v error -f lavfi -i "testsrc2=size=320x180:rate=10" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t "$SECONDS_TOTAL" \
  -c:v libx264 -preset ultrafast -g 20 -keyint_min 20 -sc_threshold 0 -b:v 150k -pix_fmt yuv420p -c:a aac -b:a 32k -f matroska "$S/pipe" &
FF=$!
dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll" package - "$S/out" --live --segment "$SEG" --window 6 < "$S/pipe" > "$S/package.log" 2>&1 &
PK=$!
printf 'elapsed_s\trss_kib\tsegment_files\twindow_segments\tmedia_sequence\n' > "$S/rss.tsv"
T0=$SECONDS; n=0
while kill -0 "$PK" 2>/dev/null; do
  sleep 5; n=$((n + 1))
  rss="$(awk '/VmRSS/ {print $2}' /proc/$PK/status 2>/dev/null || echo 0)"
  files="$(ls "$S/out"/video0/*.m4s 2>/dev/null | wc -l)"
  win="$(grep -c '^#EXTINF' "$S/out/video0/index.m3u8" 2>/dev/null || echo 0)"
  seq="$(awk -F: '/^#EXT-X-MEDIA-SEQUENCE/ {print $2}' "$S/out/video0/index.m3u8" 2>/dev/null)"
  printf '%s\t%s\t%s\t%s\t%s\n' $((SECONDS - T0)) "$rss" "$files" "$win" "${seq:-0}" >> "$S/rss.tsv"
  # every ~10 minutes: probe a snapshot of the live presentation as a client would see it
  if [ $((n % 120)) -eq 1 ] && [ -f "$S/out/master.m3u8" ]; then
    rm -rf "$S/snap/cur"; cp -r "$S/out" "$S/snap/cur" 2>/dev/null
    echo "snapshot at $((SECONDS - T0)) s: $("$FFPROBE" -v error -show_entries format=duration -of csv=p=0 "$S/snap/cur/video0/index.m3u8" 2>&1 | head -1)" >> "$S/soak.log"
  fi
done
wait "$PK"; echo "packager exit $?" >> "$S/soak.log"; wait "$FF"; echo "ffmpeg exit $?" >> "$S/soak.log"
tail -3 "$S/package.log" >> "$S/soak.log"
echo "final: $(grep -c '^#EXTINF' "$S/out/video0/index.m3u8") segments listed, endlist: $(grep -c ENDLIST "$S/out/video0/index.m3u8")" >> "$S/soak.log"
awk 'NR>1 {if (min==""||$2<min) min=$2; if ($2>max) max=$2; n++} END {print "rss KiB min", min, "max", max, "samples", n}' "$S/rss.tsv" >> "$S/soak.log"
cat "$S/soak.log"
rm -rf "$S/out" "$S/snap" "$S/pipe"

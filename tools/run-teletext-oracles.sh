#!/usr/bin/env bash
# Runs the reference extractors over every (file, PID, page) that tools/run-teletext-check.sh found, so tools/teletext-compare.py can
# compare cue text and timing. ccextractor is the pinned release binary; ffmpeg 7.0.2 (static build) crashes in its libzvbi teletext
# decoder here, so ffmpeg references are made in two steps: 7.0.2 copies the stream out, a distribution ffmpeg with libzvbi decodes it.
# Usage: tools/run-teletext-oracles.sh [corpus-dir] [ours-dir] [ref-dir]
set -uo pipefail
CORPUS="${1:-$HOME/onemedia/corpus/teletext}"
OURS="${2:-$HOME/onemedia/u52-teletext/ours}"
REF="${3:-$HOME/onemedia/u52-teletext/ref}"
BIN="${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}"
mkdir -p "$REF/cc" "$REF/ts"
for srt in "$OURS"/*.srt; do
  base=$(basename "$srt" .srt)             # <name>.pid<PID>.p<PAGE>
  name=${base%%.pid*}; rest=${base#*.pid}; pid=${rest%%.p*}; page=${rest#*.p}
  [ -f "$CORPUS/$name.ts" ] || continue
  [ -s "$srt" ] || continue
  "$BIN/ccextractor" -teletext -datapid "$pid" -tpage "$page" -out=srt -o "$REF/cc/$base.srt" "$CORPUS/$name.ts" > "$REF/cc/$base.log" 2>&1
  [ -f "$REF/cc/$base.srt" ] || echo "cc: no output for $base"
  if [ ! -f "$REF/ts/$name.pid$pid.ts" ]; then
    idx=$("$BIN/ffprobe" -v error -show_entries stream=index,id -of csv=p=0 "$CORPUS/$name.ts" | awk -F, -v p="$(printf '0x%x' "$pid")" '$2==p{print $1; exit}')
    [ -n "${idx:-}" ] && "$BIN/ffmpeg" -v error -nostdin -y -i "$CORPUS/$name.ts" -map "0:$idx" -c copy -f mpegts "$REF/ts/$name.pid$pid.ts" > /dev/null 2>&1
  fi
done
ls "$REF/cc" | grep -c srt

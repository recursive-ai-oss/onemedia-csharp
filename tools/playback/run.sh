#!/usr/bin/env bash
# Runs the playback matrix: writes the files, describes them, plays each in the named headless browsers and compares our
# CanPlay verdict with what happened.
#
#   usage: run.sh PROFILE:BROWSER [PROFILE:BROWSER ...]       e.g.  run.sh chromium:chromium firefox:firefox webkit:webkit
#   ONEMEDIA_HOME   state root, default ~/onemedia (oracles in $ONEMEDIA_HOME/oracles)
#   WORK            working directory, default $ONEMEDIA_HOME/playback
#   DOTNET_DIR      directory of a .NET 10 SDK (default ~/dotnet10)
#   ONLY            regular expression: probe only matching file names
#   COMPARE_ONLY=1  reuse results.* files already in $WORK/<browser>/ and only compare
# Results land in $WORK/<profile>/results.jsonl and table.tsv; the exit status is 1 when any verdict disagrees.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
WORK="${WORK:-$HOME_DIR/playback}"
DOTNET_DIR="${DOTNET_DIR:-$HOME/dotnet10}"
export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$WORK}"
PY="$HOME_DIR/oracles/playwright/venv/bin/python"
FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"

mkdir -p "$WORK"
if [ ! -d "$WORK/matrix" ] || [ -z "$(ls -A "$WORK/matrix")" ]; then
  FFMPEG="$FFMPEG" "$HERE/gen-matrix.sh" "$WORK/matrix" >/dev/null
fi

dotnet build "$ROOT/tools/OneMedia.PlaybackCheck" -c Release -v quiet -clp:NoSummary -m:4 >/dev/null
CHECK=(dotnet "$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.PlaybackCheck/release/OneMedia.PlaybackCheck.dll")

"${CHECK[@]}" describe "$WORK/matrix" "$WORK/manifest.json"
status=0
for pair in "$@"; do
  profile="${pair%%:*}"
  browser="${pair##*:}"
  dir="$WORK/$profile"
  mkdir -p "$dir"
  if [ "${COMPARE_ONLY:-0}" != 1 ]; then
    "$PY" "$HERE/probe.py" --browser "$browser" --manifest "$WORK/manifest.json" --media "$WORK/matrix" --out "$dir/results.jsonl" --only "${ONLY:-}"
  fi
  "${CHECK[@]}" compare "$WORK/manifest.json" "$dir/results.jsonl" "$profile" "$dir/table.tsv" || status=1
done
exit $status

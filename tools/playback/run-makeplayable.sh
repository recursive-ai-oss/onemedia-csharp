#!/usr/bin/env bash
# Runs MakePlayable over the playback matrix for each profile, then plays every output in the named headless browser and checks that it
# plays (time advances, video frames decode, audio is heard) and kept the tracks it should have.
#
#   usage: run-makeplayable.sh PROFILE:BROWSER [PROFILE:BROWSER ...]       e.g.  run-makeplayable.sh chromium:chromium firefox:firefox webkit-linux:webkit
#   ONEMEDIA_HOME   state root, default ~/onemedia (oracles in $ONEMEDIA_HOME/oracles)
#   WORK            working directory, default $ONEMEDIA_HOME/playback (the matrix is $WORK/matrix; run.sh makes it)
#   MATRIX          input directory, default $WORK/matrix; point it at any directory of media files
#   DOTNET_DIR      directory of a .NET 10 SDK (default ~/dotnet10)
#   ONLY            regular expression: probe only matching output names
# Per profile, $WORK/makeplayable/<profile>/ gets the outputs, report.tsv (what MakePlayable did per file), results.jsonl and table.tsv.
# Exit status 1 when any written output fails to play.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
WORK="${WORK:-$HOME_DIR/playback}"
MATRIX="${MATRIX:-$WORK/matrix}"
DOTNET_DIR="${DOTNET_DIR:-$HOME/dotnet10}"
export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$WORK}"
PY="$HOME_DIR/oracles/playwright/venv/bin/python"

if [ ! -d "$MATRIX" ] || [ -z "$(ls -A "$MATRIX")" ]; then
  echo "no matrix at $MATRIX; run tools/playback/run.sh once to create it" >&2
  exit 2
fi

dotnet build "$ROOT/tools/OneMedia.PlaybackCheck" -c Release -v quiet -clp:NoSummary -m:4 >/dev/null
CHECK=(dotnet "$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.PlaybackCheck/release/OneMedia.PlaybackCheck.dll")

status=0
for pair in "$@"; do
  profile="${pair%%:*}"
  browser="${pair##*:}"
  dir="$WORK/makeplayable/$profile"
  rm -rf "$dir"
  mkdir -p "$dir/out"
  "${CHECK[@]}" makeplayable "$MATRIX" "$dir/out" "$profile" "$dir/report.tsv"
  "${CHECK[@]}" describe "$dir/out" "$dir/manifest.json"
  "$PY" "$HERE/probe.py" --browser "$browser" --manifest "$dir/manifest.json" --media "$dir/out" --out "$dir/results.jsonl" --only "${ONLY:-}"
  "${CHECK[@]}" expect "$dir/report.tsv" "$dir/manifest.json" "$dir/results.jsonl" "$dir/table.tsv" || status=1
done
exit $status

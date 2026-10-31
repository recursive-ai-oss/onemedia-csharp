#!/usr/bin/env bash
# Runs the I/O measurement over the corpus as light work beside queued jobs: it never runs while a benchmark
# holds the machine (it stops when the lock appears and resumes when the lock is gone).
#
#   io.sh [io options]      e.g. --family isobmff; results in $ONEMEDIA_HOME/results/io (override with IO_OUT)
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT=${IO_OUT:-$HOME_DIR/results/io}
LOCK="${ONEMEDIA_BENCH_LOCK:-/nonexistent}"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}"

dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null
# Run from a private copy so a rebuild during a long measurement cannot disturb it.
BIN=$(mktemp -d "$HOME_DIR/.io-bin.XXXXXX"); trap 'rm -rf "$BIN"' EXIT
cp -r "$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/." "$BIN/"
mkdir -p "$OUT"

while true; do
  while [ -e "$LOCK" ]; do sleep 30; done
  dotnet "$BIN/OneMedia.Oracles.dll" io --out "$OUT" --resume "$@" &
  pid=$!
  killed=0
  while kill -0 "$pid" 2>/dev/null; do
    if [ -e "$LOCK" ]; then kill "$pid" 2>/dev/null || true; killed=1; break; fi
    sleep 10
  done
  rc=0; wait "$pid" || rc=$?
  [ "$killed" -eq 1 ] && continue
  exit "$rc"
done

#!/usr/bin/env bash
# Runs the packet differential over the whole corpus for one format family in chunks of at most 30 minutes,
# optionally through a job queue. Safe to interrupt and rerun: finished files are skipped.
#
#   sweep.sh FAMILY [extra diff options]     FAMILY is isobmff, matroska or all
#
#   ONEMEDIA_HOME   state root, default ~/onemedia (corpus in $ONEMEDIA_HOME/corpus)
#   SWEEP_CORES     cores for the job and parallel files, default 8
#   SWEEP_OUT       result directory, default $ONEMEDIA_HOME/results/<family>
#   SWEEP_WRAP      command prefix for every chunk, e.g. a job-queue submit command; default: run directly
set -euo pipefail

FAMILY=${1:?usage: sweep.sh isobmff|matroska|all [diff options]}; shift
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
CORES=${SWEEP_CORES:-8}
OUT=${SWEEP_OUT:-$HOME_DIR/results/$FAMILY}
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}"

dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null
DLL="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
[ -f "$DLL" ] || { echo "build output not found: $DLL" >&2; exit 1; }
mkdir -p "$OUT"

# Each chunk stops starting files after 25 minutes and exits 75 when files remain.
run() {
  local cmd=(dotnet "$DLL" diff --family "$FAMILY" --out "$OUT" --exceptions "$REPO/tools/OneMedia.Oracles/exceptions.tsv" --jobs "$CORES" --budget 25 --resume "$@")
  local wrap=(); read -ra wrap <<< "${SWEEP_WRAP:-}"
  "${wrap[@]}" "${cmd[@]}"
}

while true; do
  set +e; run "$@"; rc=$?; set -e
  [ "$rc" -eq 75 ] && continue
  exit "$rc"
done

#!/usr/bin/env bash
# Worker-side helper: builds the harness once (Release) and runs a harness command on the MPEG corpus.
#   run-mpeg-diff.sh build
#   run-mpeg-diff.sh run COMMAND [ARGS...]      e.g. run diff --out $OUT FILES...
set -uo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
export ONEMEDIA_HOME="${ONEMEDIA_HOME:-$HOME/onemedia}"
export ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME/onemedia/u13-mpeg}"
ART="$ONEMEDIA_SCRATCH/artifacts"
export TMPDIR="${TMPDIR:-$HOME/onemedia/u13-mpeg/tmp}"
mkdir -p "$ART" "$TMPDIR"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
case "${1:-}" in
  build) shift; dotnet build "$here/OneMedia.Oracles/OneMedia.Oracles.csproj" -c Release -v quiet -clp:NoSummary "$@" ;;
  run) shift; exec dotnet "$ART/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll" "$@" ;;
  *) echo "usage: $0 build | run COMMAND ARGS" >&2; exit 2 ;;
esac

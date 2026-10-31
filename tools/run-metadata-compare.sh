#!/usr/bin/env bash
# Builds the comparison tool and compares our tags and basic facts with ExifTool and MediaInfo on the given files.
# Usage: ONEMEDIA_HOME=~/onemedia tools/run-metadata-compare.sh <out-dir> files...
# Result: <out-dir>/metadata-compare.tsv (one row per field: ours, ExifTool, MediaInfo, verdict) and a summary on stdout.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
OUT=$1; shift
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="${ONEMEDIA_HOME:-$HOME/onemedia}" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-${ONEMEDIA_HOME:-$HOME/onemedia}/scratch}" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null
nice dotnet "$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll" meta --out "$OUT" "$@"

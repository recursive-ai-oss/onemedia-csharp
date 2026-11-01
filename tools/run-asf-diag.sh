#!/usr/bin/env bash
# Prints the ASF reader's view of every ASF corpus file (protection, streams, objects, defects, packets per track).
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null || exit 1
DLL="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
dotnet "$DLL" asfinfo $(dotnet "$DLL" list --family asf)

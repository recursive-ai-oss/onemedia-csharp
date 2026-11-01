#!/usr/bin/env bash
# Runs the ASF checks beyond the packet differential on the corpus: track/codec/size/duration agreement with ffprobe (infocheck),
# keyframe seeks against a full read (asfseek), tags and facts against ExifTool and MediaInfo (meta) and the common tag view against
# ATL.NET (atlcompare). Output goes to $ONEMEDIA_HOME/results/asf-checks (override with ASF_OUT); one log file per check.
#
#   tools/run-asf-checks.sh
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT=${ASF_OUT:-$HOME_DIR/results/asf-checks}
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}" DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$OUT"
dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null || exit 1
DLL="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
run() { local name=$1; shift; dotnet "$DLL" "$@" > "$OUT/$name.log" 2>&1; echo "$name exit $?"; tail -n 3 "$OUT/$name.log"; }
run infocheck infocheck --family asf
run asfseek asfseek
run meta meta --family asf --out "$OUT"
run atlcompare atlcompare --family asf --out "$OUT"

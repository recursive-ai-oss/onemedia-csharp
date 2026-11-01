#!/usr/bin/env bash
# Runs infocheck (tracks, codecs, sizes, rates, durations against ffprobe) over every reader family on the corpus, with the accepted
# differences in exceptions.tsv. One log per family in $ONEMEDIA_HOME/results/infocheck (override with INFOCHECK_OUT).
#
#   tools/run-infocheck-all.sh [family...]
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT=${INFOCHECK_OUT:-$HOME_DIR/results/infocheck}
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}" DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$OUT"
dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null || exit 1
DLL="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
families=("$@"); [ ${#families[@]} -gt 0 ] || families=(isobmff matroska asf mpa adts ac3 amr xiph flv pcm avi dv)
for f in "${families[@]}"; do
  dotnet "$DLL" infocheck --family "$f" --exceptions "$REPO/tools/OneMedia.Oracles/exceptions.tsv" > "$OUT/$f.log" 2>&1
  echo "$f exit $?: $(tail -n 1 "$OUT/$f.log")"
done

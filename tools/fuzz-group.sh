#!/usr/bin/env bash
# Builds and instruments the fuzz target once, then runs the named targets side by side for the same time.
#   tools/fuzz-group.sh <home> <seconds> <target>...
# <home> becomes ONEMEDIA_FUZZ_HOME (tools are installed into it if missing). Needs the .NET SDK on PATH.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ONEMEDIA_FUZZ_HOME="$1"; secs="$2"; shift 2
mkdir -p "$ONEMEDIA_FUZZ_HOME"
"$here/tools/fuzz.sh" setup
pb="$ONEMEDIA_FUZZ_HOME/prebuilt"
rm -rf "$pb"; mkdir -p "$pb"
dotnet publish "$here/fuzz/OneMedia.Fuzz/OneMedia.Fuzz.csproj" -c Release -o "$pb/bin-plain" -v quiet -clp:NoSummary >/dev/null
cp -r "$pb/bin-plain" "$pb/bin"
"$ONEMEDIA_FUZZ_HOME/tools/sharpfuzz" "$pb/bin/RecursiveAI.OneMedia.dll" >/dev/null
exec "$here/tools/fuzz-campaign.sh" "$secs" "$@"

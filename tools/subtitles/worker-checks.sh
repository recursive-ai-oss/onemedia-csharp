#!/usr/bin/env bash
# Worker-side: the subtitle unit tests, the NativeAOT and WebAssembly smoke apps, and the indicative subtitle bench.
#   tools/subtitles/worker-checks.sh [WORKDIR]
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PATH=~/dotnet10:~/tools/node/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
W="${1:-$HOME/onemedia/u48-cues}"
export ONEMEDIA_SCRATCH="$W" TMPDIR="$W/tmp"; mkdir -p "$TMPDIR"
cd "$here"
echo "=== tests"
dotnet build tests/OneMedia.Tests/OneMedia.Tests.csproj -c Release -m:4 -v quiet -clp:NoSummary -p:NuGetAudit=false 2>&1 | grep -E "error|warning" | sort -u | head -20
T=$(find "$W/artifacts" -name RecursiveAI.OneMedia.Tests.dll -path '*/bin/*release*' | head -1)
dotnet exec "$T" -class '*SubtitleFormatTests*' 2>&1 | grep -v '^\s*at ' | tail -8
echo "=== aot"
rm -rf "$W/aot"; dotnet publish smoke/OneMedia.AotSmoke -c Release -r linux-x64 -o "$W/aot" -warnaserror -p:NuGetAudit=false 2>&1 | grep -E "warning|error|Warn" | sort -u | head -10
"$W/aot/OneMedia.AotSmoke" | grep -cE "^ok"; "$W/aot/OneMedia.AotSmoke" | grep -E "FAIL|subtitle"
echo "=== wasm"
dotnet build smoke/OneMedia.WasmSmoke -c Release -p:NuGetAudit=false 2>&1 | grep -E "error|Warn|warning" | sort -u | head -5
( cd "$W/artifacts/bin/OneMedia.WasmSmoke/release_browser-wasm/AppBundle" && node main.mjs 2>&1 | grep -cE "^ok"; node main.mjs 2>&1 | grep -E "FAIL|subtitle" )
echo "=== bench build"
dotnet build bench/OneMedia.Bench -c Release -m:4 -v quiet -clp:NoSummary -p:NuGetAudit=false 2>&1 | grep -E "error|warning" | sort -u | head -20
B=$(find "$W/artifacts" -name OneMedia.Bench.dll -path '*/bin/*release*' | head -1)
echo "=== bench"
dotnet "$B" subsbench 20000 2>&1 | tail -40
rm -rf "$W/aot" "$W/artifacts/bin/OneMedia.WasmSmoke"

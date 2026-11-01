#!/usr/bin/env bash
# Worker-side: the unit test suite, the NativeAOT smoke app (warnings are errors) and the browser-WebAssembly smoke app under node.
#   tools/run-mpeg-checks.sh [WORKDIR]
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
W="${1:-$HOME/onemedia/u13-mpeg}"
export ONEMEDIA_SCRATCH="$W" TMPDIR="$W/tmp"; mkdir -p "$TMPDIR"
cd "$here"
echo "=== tests"
dotnet test --project tests/OneMedia.Tests/OneMedia.Tests.csproj -c Release 2>&1 | grep -E "total:|failed|succeeded|skipped|error" | head -20
echo "=== aot"
rm -rf "$W/aot"; dotnet publish smoke/OneMedia.AotSmoke -c Release -r linux-x64 -o "$W/aot" -warnaserror 2>&1 | grep -E "warning|error|Warn" | head -10
"$W/aot/OneMedia.AotSmoke" | grep -cE "^ok" ; "$W/aot/OneMedia.AotSmoke" | grep -E "FAIL|mpeg"
echo "=== wasm"
dotnet workload list 2>&1 | tail -4
dotnet build smoke/OneMedia.WasmSmoke -c Release 2>&1 | grep -E "error|Warn|warning" | head -5
( cd "$W/artifacts/bin/OneMedia.WasmSmoke/release_browser-wasm/AppBundle" && node main.mjs 2>&1 | grep -cE "^ok"; node main.mjs 2>&1 | grep -E "FAIL|mpeg" )

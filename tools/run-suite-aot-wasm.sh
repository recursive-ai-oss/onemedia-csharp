#!/bin/bash
# Full test suite, NativeAOT publish and browser-WASM build of the smoke apps, on a build machine.
#   tools/run-suite-aot-wasm.sh WORKDIR
set -uo pipefail
W=${1:?work dir}
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH=$W/scratch
cd "$(dirname "$0")/.."
mkdir -p "$W"
echo "=== suite"
dotnet test --project tests/OneMedia.Tests/OneMedia.Tests.csproj -c Release -p:NuGetAudit=false 2>&1 | grep -E "total:|failed|succeeded|skipped|error" | head -30
echo "=== aot"
rm -rf "$W/aot"; dotnet publish smoke/OneMedia.AotSmoke -c Release -r linux-x64 -o "$W/aot" -warnaserror -p:NuGetAudit=false 2>&1 | grep -E "warning|error|Warn" | head -10
"$W/aot/OneMedia.AotSmoke" | grep -cE "^ok"; "$W/aot/OneMedia.AotSmoke" | grep -E "FAIL|reconstruct"
echo "=== wasm"
dotnet build smoke/OneMedia.WasmSmoke -c Release -p:NuGetAudit=false 2>&1 | grep -E "error|Warn|warning" | head -5
BUNDLE=$(find "$W/scratch/artifacts" -type d -name AppBundle | head -1)
echo "bundle: $BUNDLE"
( cd "$BUNDLE" && node main.mjs > "$W/wasm.log" 2>&1; grep -cE "^ok" "$W/wasm.log"; grep -E "FAIL|reconstruct" "$W/wasm.log"; tail -3 "$W/wasm.log" )
rm -rf "$W/aot" "$W/scratch"

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
( cd "$W/scratch/artifacts/bin/OneMedia.WasmSmoke/release_browser-wasm/AppBundle" && node main.mjs 2>&1 | grep -cE "^ok"; node main.mjs 2>&1 | grep -E "FAIL|reconstruct" )
rm -rf "$W/aot" "$W/scratch"

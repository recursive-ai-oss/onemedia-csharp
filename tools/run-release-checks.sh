#!/usr/bin/env bash
# Worker-side release checks: the whole solution's tests in Release, the NativeAOT smoke app (warnings are errors), the browser-WebAssembly
# smoke app under node, and the three packages packed with package validation against the last published version.
#   tools/run-release-checks.sh [WORKDIR]
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PATH=~/dotnet10:~/tools/node/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
W="${1:-$HOME/onemedia/release-checks}"
export ONEMEDIA_SCRATCH="$W" TMPDIR="$W/tmp"; mkdir -p "$TMPDIR"
cd "$here"
rc=0
echo "=== build + tests"
dotnet build OneMedia.slnx -c Release -v quiet -clp:NoSummary 2>&1 | grep -E "warning|error|Warn" | head -10
dotnet test --solution OneMedia.slnx -c Release --no-build 2>&1 | grep -E "total:|failed|succeeded|skipped|error" | head -20
echo "=== aot"
rm -rf "$W/aot"; dotnet publish smoke/OneMedia.AotSmoke -c Release -r linux-x64 -o "$W/aot" -warnaserror 2>&1 | grep -E "warning|error|Warn" | head -10
"$W/aot/OneMedia.AotSmoke" | grep -cE "^ok"; "$W/aot/OneMedia.AotSmoke" | grep -E "FAIL"
echo "=== wasm"
dotnet build smoke/OneMedia.WasmSmoke -c Release 2>&1 | grep -E "error|Warn|warning" | head -5
( cd "$W/artifacts/bin/OneMedia.WasmSmoke/release_browser-wasm/AppBundle" && node main.mjs 2>&1 | grep -cE "^ok"; node main.mjs 2>&1 | grep -E "FAIL" )
echo "=== pack"
rm -rf "$W/packages"; dotnet pack OneMedia.slnx -c Release --no-build -o "$W/packages" 2>&1 | grep -E "warning|error|Warn|Successfully" | head -20
ls -l "$W/packages"

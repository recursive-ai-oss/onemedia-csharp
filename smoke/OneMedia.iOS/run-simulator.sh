#!/usr/bin/env bash
# Builds the iOS smoke app (Release, trimmed, warnings as errors), installs it on an iOS simulator, runs it and waits for
# the one result line the app writes. Exits 0 only on "mobile smoke: ok".
#
# Needs: macOS on Apple silicon with Xcode and an iOS simulator runtime, and a .NET 10 SDK with the `ios` workload
# (dotnet workload install ios; keep it updated so it matches the installed Xcode). IOS_VALIDATE_XCODE=false builds with
# an Xcode the workload does not list; the app still has to pass its own checks.
#
#   smoke/OneMedia.iOS/run-simulator.sh                 build, boot a simulator, install, run, delete the simulator
#   BUILD_ONLY=1 smoke/OneMedia.iOS/run-simulator.sh    build and report the app size only
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mobile="${ONEMEDIA_MOBILE:-$HOME/onemedia/mobile}"
art="${ONEMEDIA_ARTIFACTS:-$mobile/artifacts}"
export ONEMEDIA_ARTIFACTS="$art" ONEMEDIA_SCRATCH="$(dirname "$art")"
out="$art/ios-run"
timeout_s="${SMOKE_TIMEOUT:-300}"
bundle_id=ai.recursive.onemedia.smoke
mkdir -p "$out"

command -v xcrun >/dev/null || { echo "xcrun not found: Xcode is required" >&2; exit 3; }
xcrun simctl list runtimes | grep -q 'iOS' || { echo "no iOS simulator runtime installed" >&2; exit 3; }

echo "building (Release, trimmed)"
dotnet build "$here/OneMedia.iOS.csproj" -c Release -p:ValidateXcodeVersion="${IOS_VALIDATE_XCODE:-true}" -v quiet -clp:NoSummary 2>&1 \
  | tee "$out/build.log" | tail -n 20
app="$(find "$art" -maxdepth 6 -name '*.app' -path '*/iOS/bin/*' 2>/dev/null | head -n 1)"
[ -n "$app" ] || { echo "no .app produced" >&2; exit 2; }
echo "app: $app ($(du -sk "$app" | cut -f1) KiB)"
if grep -qE ': warning' "$out/build.log"; then
  echo "build produced warnings" >&2; exit 2
fi
[ "${BUILD_ONLY:-0}" = 1 ] && exit 0

# macOS has no timeout(1): run a step in the background and give up on it after the given seconds.
within() {
  local secs="$1"; shift
  "$@" &
  local pid=$!
  for _ in $(seq 1 "$secs"); do kill -0 "$pid" 2>/dev/null || { wait "$pid"; return; }; sleep 1; done
  kill "$pid" 2>/dev/null || true
  echo "timed out after ${secs}s: $*" >&2
  return 124
}

# Use the newest installed iOS runtime and the newest iPhone type that runtime supports; the simulator is recreated each run.
runtime="$(xcrun simctl list runtimes -j | python3 -c 'import json,sys; r=[x for x in json.load(sys.stdin)["runtimes"] if x["platform"]=="iOS" and x["isAvailable"]]; print(r[-1]["identifier"])')"
devtype="$(xcrun simctl list runtimes -j | python3 -c 'import json,sys; rt=[x for x in json.load(sys.stdin)["runtimes"] if x["platform"]=="iOS" and x["isAvailable"]][-1]; print([t["identifier"] for t in rt["supportedDeviceTypes"] if "iPhone" in t["name"]][-1])')"
for old in $(xcrun simctl list devices -j | python3 -c 'import json,sys; [print(x["udid"]) for v in json.load(sys.stdin)["devices"].values() for x in v if x["name"]=="onemedia-smoke"]'); do
  xcrun simctl delete "$old" || true
done
udid="$(xcrun simctl create onemedia-smoke "$devtype" "$runtime")"
cleanup() { xcrun simctl shutdown "$udid" >/dev/null 2>&1 || true; xcrun simctl delete "$udid" >/dev/null 2>&1 || true; }
trap cleanup EXIT
xcrun simctl boot "$udid" 2>/dev/null || true
within 600 xcrun simctl bootstatus "$udid" -b >/dev/null
echo "simulator: $runtime"
within 300 xcrun simctl install "$udid" "$app"

# The app writes its console output to Documents/smoke.txt in its container and exits after the result line. (The simulator's
# own console capture does not see .NET console output.)
xcrun simctl launch --terminate-running-process "$udid" "$bundle_id" >/dev/null
result="$(xcrun simctl get_app_container "$udid" "$bundle_id" data)/Documents/smoke.txt"
for _ in $(seq 1 "$timeout_s"); do
  grep -qE 'mobile smoke: (ok|FAILED)' "$result" 2>/dev/null && break
  sleep 1
done
cp "$result" "$out/console.txt" 2>/dev/null || : >"$out/console.txt"
cat "$out/console.txt"
if grep -q 'mobile smoke: ok' "$out/console.txt"; then
  echo "PASS"
  exit 0
fi
echo "FAIL (no pass line)" >&2
exit 1

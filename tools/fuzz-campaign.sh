#!/usr/bin/env bash
# Runs several fuzz targets side by side for the same time, one process each, from one prebuilt instrumented build.
#   tools/fuzz-campaign.sh <seconds> <target>...
# Needs ONEMEDIA_FUZZ_HOME (tools installed, prebuilt/bin and prebuilt/bin-plain present). Prints one line per target:
# target, last libFuzzer progress line, crash files.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
secs="$1"; shift
export FUZZ_PREBUILT="$ONEMEDIA_FUZZ_HOME/prebuilt"
for t in "$@"; do
  nice "$here/tools/fuzz.sh" run "$t" "$secs" > "$ONEMEDIA_FUZZ_HOME/campaign-$t.log" 2>&1 &
done
wait
for t in "$@"; do
  w="$ONEMEDIA_FUZZ_HOME/work/$t"
  echo "== $t"; grep -E '^#[0-9]+' "$w/last-run.log" | tail -n 1; grep -E '^Done' "$w/last-run.log" | tail -n 1
  echo "crashes: $(ls -1 "$w/crashes" | tr '\n' ' ')"
done

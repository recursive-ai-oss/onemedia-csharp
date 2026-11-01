#!/usr/bin/env bash
# Runs the short checks for the AVI and DV readers against ffprobe, ExifTool and MediaInfo: track comparison, tag comparison and
# audio decoding, then the probe I/O measurement. Light work: it stops when the benchmark lock appears.
#   ONEMEDIA_HOME   state root (default ~/onemedia)     AVI_OUT   result directory (default $ONEMEDIA_HOME/avi-checks)
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT="${AVI_OUT:-$HOME_DIR/avi-checks}"
LOCK="${ONEMEDIA_BENCH_LOCK:-$HOME/WORKER-BENCH.lock}"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}"
mkdir -p "$OUT"
[ -e "$LOCK" ] && { echo "benchmark lock present" >&2; exit 3; }
# Kill our own process group if the lock appears.
( while sleep 30; do [ -e "$LOCK" ] && kill -- -$$ 2>/dev/null; done ) &
nice dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -m:4 -v quiet -clp:NoSummary >/dev/null || exit 1
D="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
run() { local name=$1; shift; nice dotnet "$D" "$@" > "$OUT/$name.log" 2>&1; echo "$name: exit $?"; }
run infocheck-avi infocheck --family avi
run infocheck-dv infocheck --family dv
run audiodecode-avi audiodecode --family avi --out "$OUT/audiodecode"
run meta-avi meta --family avi --out "$OUT/meta-avi"
run meta-dv meta --family dv --out "$OUT/meta-dv"
run io-avi io --family avi --metadata --out "$OUT/io"
run io-dv io --family dv --metadata --out "$OUT/io"
kill %1 2>/dev/null

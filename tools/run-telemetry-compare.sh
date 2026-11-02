#!/bin/sh
# Decodes the GoPro samples in $CORPUS (default ~/onemedia/corpus/gopro) with OneMedia and compares each file with ExifTool's -ee output.
# Needs the oracles installed by tools/oracles/install-linux.sh and a release build of tools/OneMedia.TelemetryCheck in $BIN.
# usage: tools/run-telemetry-compare.sh [out-dir]
set -eu
CORPUS=${CORPUS:-$HOME/onemedia/corpus/gopro}
EXIFTOOL=${EXIFTOOL:-$HOME/onemedia/oracles/bin/exiftool}
BIN=${BIN:?set BIN to the TelemetryCheck release dll}
OUT=${1:-$HOME/onemedia/u42-telemetry/ref}
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
for f in "$CORPUS"/*.mp4; do
  n=$(basename "$f" .mp4)
  dotnet "$BIN" dump "$f" "$OUT/$n.ours.tsv" 2> "$OUT/$n.channels.txt"
  nice "$EXIFTOOL" -ee -n -b -j -G3 -a "$f" > "$OUT/$n.et.json" 2>/dev/null
  echo "=== $n"
  python3 "$HERE/telemetry-compare.py" "$OUT/$n.ours.tsv" "$OUT/$n.et.json"
done

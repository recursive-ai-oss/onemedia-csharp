#!/bin/sh
# Writes the synthetic DJI and AVCHD fixtures, decodes them with OneMedia and compares the result with ExifTool's -ee output.
# Needs the oracles installed by tools/oracles/install-linux.sh (ffmpeg for the transport streams) and a release build of tools/OneMedia.TelemetryCheck in $BIN.
# Extra files (for example published AVCHD samples) can be added as arguments; they are compared as MDPM streams.
# usage: BIN=<TelemetryCheck dll> tools/run-dji-mdpm-compare.sh [extra.mts ...]
set -u
BIN=${BIN:?set BIN to the TelemetryCheck release dll}
ORACLES=${ORACLES:-$HOME/onemedia/oracles/bin}
OUT=${OUT:-$HOME/onemedia/djimdpm/compare}
HERE=$(cd "$(dirname "$0")" && pwd)
PATH=$ORACLES:$PATH
mkdir -p "$OUT"
python3 "$HERE/gen-dji-fixtures.py" "$OUT/dji" >/dev/null
python3 "$HERE/gen-mdpm-gps-fixtures.py" "$OUT/mdpm" ffmpeg >/dev/null
status=0
run() { # mode file
  n=$(basename "$2")
  dotnet "$BIN" dump "$2" "$OUT/$n.ours.tsv" 2>/dev/null
  exiftool -ee -n -j -G3 -a "$2" > "$OUT/$n.et.json" 2>/dev/null
  echo "=== $1 $n"
  python3 "$HERE/dji-mdpm-compare.py" "$1" "$OUT/$n.ours.tsv" "$OUT/$n.et.json" || status=1
}
for f in djmd-real djmd-osmo; do run djmd "$OUT/dji/$f.mp4"; done
for f in mdpm-gps mdpm-south-west "$@"; do
  case "$f" in */*) p=$f;; *) p=$OUT/mdpm/$f.mts;; esac
  run mdpm "$p"
done
exit $status

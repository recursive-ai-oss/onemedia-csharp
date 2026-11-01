#!/usr/bin/env bash
# Probe + metadata I/O over every reader family, one output directory per family under $ONEMEDIA_HOME/results/io-sweep, and one merged
# table (io-results.txt) in the same place. Light work: it stops while a benchmark holds the machine (see corpus/io.sh) and resumes.
#
#   tools/run-io-sweep.sh [family...]     families: isobmff matroska asf mpa adts ac3 amr xiph flv pcm avi dv mpeg
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT=${IO_SWEEP_OUT:-$HOME_DIR/results/io-sweep}
C="$HOME_DIR/corpus"; G="$C/generated/mpeg"; S="$C/samples"
export ONEMEDIA_BENCH_LOCK="${ONEMEDIA_BENCH_LOCK:-$HOME/WORKER-BENCH.lock}"
families=("$@"); [ ${#families[@]} -gt 0 ] || families=(isobmff matroska asf mpa adts ac3 amr xiph flv pcm avi dv mpeg)
mkdir -p "$OUT"
for f in "${families[@]}"; do
  if [ "$f" = mpeg ]; then
    # The pinned ffprobe cannot read the SDT ffmpeg writes (docs/mpeg.md), so transport streams are the SDT-stripped copies.
    FILES=$(ls "$G"/tsx/* "$S"/mpeg/*.ts "$S"/avchd/*.[mM][tT][sS] "$G"/ps/* "$S"/mpeg/*.mpg "$S"/mpeg/*.mpeg "$S"/mpeg/*.VOB "$G"/es/* "$S"/mpeg/*.264 "$S"/mpeg/*.h264 "$S"/mpeg/*.m2v "$G"/large/* 2>/dev/null)
    IO_OUT="$OUT/$f" bash "$HERE/corpus/io.sh" --metadata --root "$C" $FILES > "$OUT/$f.log" 2>&1
  else
    IO_OUT="$OUT/$f" bash "$HERE/corpus/io.sh" --family "$f" --metadata > "$OUT/$f.log" 2>&1
  fi
  echo "$f exit $?"
done
{ for f in "${families[@]}"; do [ -f "$OUT/$f/io-table.txt" ] && { echo "## $f"; cat "$OUT/$f/io-table.txt"; echo; }; done; } > "$OUT/io-results.txt"
wc -l "$OUT/io-results.txt"

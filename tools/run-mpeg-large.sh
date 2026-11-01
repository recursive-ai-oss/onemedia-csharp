#!/usr/bin/env bash
# Worker-side large-file check for the transport stream reader: writes a ~2.1 GB stream (ffmpeg, from the generated seed), then measures
# probe and metadata I/O next to ffprobe, MediaInfo and ExifTool, and a full pull with allocation and peak memory.
#   tools/run-mpeg-large.sh [OUT]
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
O="${1:-$HOME/onemedia/u13-mpeg/out-large}"; mkdir -p "$O"
G="${ONEMEDIA_HOME:-$HOME/onemedia}/corpus/generated/mpeg"
LARGE=1 bash "$here/corpus/generate-mpeg.sh" large 2>&1 | tail -3
ls -l "$G"/large/
bash "$here/run-mpeg-diff.sh" build 2>&1 | tail -3
f="$G/large/ts-2gb.ts"
bash "$here/run-mpeg-diff.sh" run io --metadata --out "$O" "$f" > "$O/io.log" 2>&1; tail -15 "$O/io.log"
bash "$here/run-mpeg-diff.sh" run drain "$f" 2>&1 | tail -3

#!/usr/bin/env bash
# Runs the cause classifier (device-triage) over the linked device corpus: tools/run-device-triage.sh OUT
# Needs OUT/links from run-device-corpus.sh. Writes OUT/triage-iso.tsv and OUT/triage-ts.tsv (columns: file, track, codec, cause, detail).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
O=${1:?output dir}
TSPROBE="${ONEMEDIA_FFPROBE_TS:-$HOME/onemedia/oracles/ffmpeg-8.1/bin/ffprobe}"
bash "$here/run-mpeg-diff.sh" build -m:4 2>&1 | grep -E "error" | head -3
nice bash "$here/run-mpeg-diff.sh" run device-triage $(ls "$O"/links/* | grep -iv '\.m2ts$\|\.mts$') > "$O/triage-iso.tsv" 2>&1
ONEMEDIA_FFPROBE="$TSPROBE" nice bash "$here/run-mpeg-diff.sh" run device-triage $(ls "$O"/links/* | grep -i '\.m2ts$\|\.mts$') > "$O/triage-ts.tsv" 2>&1
for f in iso ts; do echo "== $f"; cut -f4 "$O/triage-$f.tsv" | sort | uniq -c | sort -rn; done

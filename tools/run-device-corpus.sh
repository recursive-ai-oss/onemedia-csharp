#!/usr/bin/env bash
# Runs the packet, info and metadata comparisons on the device corpus (tools/corpus/devices-fetch.sh fetches it).
#   tools/run-device-corpus.sh OUT
# Writes OUT/diff-*, OUT/infocheck-*.log and OUT/metadata-compare.tsv. MPEG transport streams go against a newer ffprobe
# (ONEMEDIA_FFPROBE_TS, default ~/onemedia/oracles/ffmpeg-8.1/bin/ffprobe) because the pinned one crashes on some of them.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
O=${1:?output dir}; mkdir -p "$O"
D="$O/links"   # symlinks named <id>.<ext> from the manifest: file names with spaces or personal text never reach the results
M="$here/corpus/devices-manifest.tsv"; R="${ONEMEDIA_HOME:-$HOME/onemedia}/corpus/devices"
rm -rf "$D"; mkdir -p "$D"
tail -n +2 "$M" | while IFS=$'\t' read -r id path rest; do ln -s "$R/$path" "$D/$id.${path##*.}"; done
TSPROBE="${ONEMEDIA_FFPROBE_TS:-$HOME/onemedia/oracles/ffmpeg-8.1/bin/ffprobe}"
bash "$here/run-mpeg-diff.sh" build 2>&1 | tail -3
run() { bash "$here/run-mpeg-diff.sh" run "$@"; }
OTHER=$(ls "$D"/* | grep -iv '\.m2ts$\|\.mts$')
TS=$(ls "$D"/* | grep -i '\.m2ts$\|\.mts$')
run diff --jobs 4 --out "$O/diff-iso" --exceptions "$here/OneMedia.Oracles/exceptions.tsv" $OTHER > "$O/diff-iso.log" 2>&1; tail -3 "$O/diff-iso.log"
ONEMEDIA_FFPROBE="$TSPROBE" run diff --jobs 4 --out "$O/diff-ts" --exceptions "$here/OneMedia.Oracles/exceptions.tsv" $TS > "$O/diff-ts.log" 2>&1; tail -3 "$O/diff-ts.log"
run infocheck --exceptions "$here/OneMedia.Oracles/exceptions.tsv" $OTHER > "$O/infocheck-iso.log" 2>&1; tail -3 "$O/infocheck-iso.log"
ONEMEDIA_FFPROBE="$TSPROBE" run infocheck --exceptions "$here/OneMedia.Oracles/exceptions.tsv" $TS > "$O/infocheck-ts.log" 2>&1; tail -3 "$O/infocheck-ts.log"
run meta --out "$O" $OTHER $TS > "$O/meta.log" 2>&1; tail -3 "$O/meta.log"
ls "$O"

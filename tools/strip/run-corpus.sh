#!/bin/bash
# Strips every file in a folder with the built onemedia tool and checks each result with verify-strip.py.
# usage: run-corpus.sh <tool dll> <input dir> <work dir> [strip options...]   (oracles: ONEMEDIA_ORACLES, default ~/onemedia/oracles/bin)
set -u
DLL=$1; IN=$2; OUT=$3; shift 3
O=${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}
HERE=$(dirname "$0")
mkdir -p "$OUT"
for f in "$IN"/*; do
  b=$(basename "$f")
  rm -f "$OUT/$b"
  dotnet exec "$DLL" strip "$f" "$OUT/$b" "$@" > "$OUT/$b.report.txt" 2>&1
  code=$?
  echo "== $b exit=$code entries=$(grep -vc '^note\|NOT REMOVED' "$OUT/$b.report.txt") notremoved=$(grep -c 'NOT REMOVED' "$OUT/$b.report.txt")"
  python3 "$HERE/verify-strip.py" "$f" "$OUT/$b" --exiftool "$O/exiftool" --mediainfo "$O/mediainfo" --ffprobe "$O/ffprobe" > "$OUT/$b.verify.json" 2>&1
  python3 - "$OUT/$b.verify.json" <<'EOF'
import json, sys
try:
    r = json.load(open(sys.argv[1]))
except Exception as e:
    print('  verify failed:', open(sys.argv[1]).read()[:300]); sys.exit()
print('  secrets', r['secrets'], '| bytes_left', len(r['bytes_left']), r['bytes_left'][:4], '| exiftool real-valued', r['exiftool_tags_with_real_values'][:6], '| neutral', r['exiftool_tags_with_neutral_values'], '| mediainfo', len(r['mediainfo_lines_still_shown']), r['mediainfo_lines_still_shown'][:3], '| size_equal', r['size_equal'], '| av_equal', r['av_payload_equal'], r['av_streams'])
EOF
done

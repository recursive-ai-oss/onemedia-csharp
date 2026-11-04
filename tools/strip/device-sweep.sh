#!/bin/bash
# Strips the device-corpus files that carry a position or a device id, and every AVI, and checks each result.
#
# usage: device-sweep.sh <tool dll> <corpus raw dir> <work dir>   (oracles: ONEMEDIA_ORACLES, default ~/onemedia/oracles/bin)
#
# Selection: a file is picked when ExifTool (-ee -a -G3 -s) lists a tag whose name says GPS, serial, unique id, owner or content id, or when it is an .avi.
# Per file it writes <name>.select.txt (the flagged tag names), <name>.report.txt (the tool's report) and <name>.verify.json (verify-strip.py with framemd5),
# deletes the stripped copy afterwards, and prints one summary line: report entries / not-removed count, ExifTool tags that still show a real value, whether
# the secrets were found in the bytes, and whether audio and video packets are unchanged.
set -u
DLL=$1; IN=$2; WORK=$3
O=${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}
HERE=$(dirname "$0")
mkdir -p "$WORK"
WORDS='GPS|Serial|UniqueID|UniqueId|OwnerName|CameraOwner|ContentIdentifier|ContentID|MediaUID|Location|Coordinates'
for f in "$IN"/*; do
  b=$(basename "$f")
  case "$b" in *.json|*.txt|*.md) continue;; esac
  tags=$("$O/exiftool" -ee -a -G3 -s -api largefilesupport=1 "$f" 2>/dev/null | sed -E 's/^\[[^]]*\] *//; s/ *:.*$//' | grep -E "$WORDS" | sort -u | tr '\n' ' ')
  case "$b" in *.avi|*.AVI) is_avi=1;; *) is_avi=0;; esac
  [ -z "$tags" ] && [ $is_avi = 0 ] && continue
  echo "$tags" > "$WORK/$b.select.txt"
  out="$WORK/$b.stripped"
  rm -f "$out"
  dotnet exec "$DLL" strip "$f" "$out" > "$WORK/$b.report.txt" 2>&1
  code=$?
  python3 "$HERE/verify-strip.py" "$f" "$out" --exiftool "$O/exiftool" --mediainfo "$O/mediainfo" --ffprobe "$O/ffprobe" --ffmpeg "$O/ffmpeg" > "$WORK/$b.verify.json" 2>&1
  python3 - "$b" "$code" "$WORK" <<'EOF'
import json, sys
b, code, work = sys.argv[1], sys.argv[2], sys.argv[3]
rep = open(f'{work}/{b}.report.txt', errors='replace').read().splitlines()
entries = sum(1 for l in rep if l and not l.startswith(('note:', 'not examined:', 'NOT REMOVED')))
left = [l for l in rep if l.startswith('NOT REMOVED')]
try:
    r = json.load(open(f'{work}/{b}.verify.json'))
except Exception:
    print(f'{b}: exit={code} VERIFY FAILED'); sys.exit()
print(f"{b}: exit={code} entries={entries} not_removed={len(left)} exif_real={r['exiftool_tags_with_real_values'][:8]} bytes_left={len(r['bytes_left'])} av_equal={r['av_payload_equal']} framemd5={r.get('framemd5_equal')}")
EOF
  rm -f "$out"
done

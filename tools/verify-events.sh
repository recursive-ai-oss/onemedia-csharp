#!/usr/bin/env bash
# Runs the timed-event checks against the oracles. Meant for the worker; state under $ONEMEDIA_HOME (default ~/onemedia).
#   tools/verify-events.sh [workdir]
# Needs: the oracles from tools/oracles/install-linux.sh (ffprobe, MP4Box, TSDuck), a built tools/OneMedia.EventsCheck, python3.
set -uo pipefail
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
W="${1:-$HOME_DIR/u57-events/verify}"
O="$HOME_DIR/oracles"
export ONEMEDIA_ORACLES="$O"
FFMPEG="$O/bin/ffmpeg"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$W/tc"
fail=0

# 1. timecode: ffmpeg-written files, drop-frame and non-drop, several rates, MOV and MP4
n=0
while IFS='|' read -r rate tc tag; do
  for ext in mov mp4; do
    f="$W/tc/tc-$tag.$ext"
    if "$FFMPEG" -v error -y -f lavfi -i "testsrc2=s=64x48:r=$rate:d=2" -c:v libx264 -preset ultrafast -crf 40 -timecode "$tc" "$f" 2>/dev/null; then n=$((n+1)); fi
  done
done <<'EOF'
24|00:59:59:23|24
24000/1001|01:00:00:00|23976
25|23:59:59:24|25
30000/1001|00:00:59;29|2997df-a
30000/1001|00:10:00;00|2997df-b
30000/1001|01:02:03;04|2997df-c
30000/1001|01:02:03:04|2997ndf
30|12:00:00:00|30
50|00:00:01:49|50
60000/1001|00:09:59;59|5994df
60000/1001|01:02:03:04|5994ndf
60|10:10:10:10|60
EOF
echo "timecode files written: $n"
python3 "$HERE/events-compare.py" tmcd "$W"/tc/*.mov "$W"/tc/*.mp4 | tee "$W/tmcd.txt" | grep -c '^ok' | sed 's/^/timecode ok: /'
grep -v '^ok' "$W/tmcd.txt" | head -20
[ -z "$(grep -v '^ok' "$W/tmcd.txt")" ] || fail=1

python3 "$HERE/events-compare.py" tmcd-remux "$W"/tc/*.mov "$W"/tc/*.mp4 | tee "$W/tmcd-remux.txt" | grep -c '^ok' | sed 's/^/timecode remux ok: /'
grep -v '^ok' "$W/tmcd-remux.txt" | head -20
[ -z "$(grep -v '^ok' "$W/tmcd-remux.txt")" ] || fail=1

# 2. SCTE 35 and ID3 on the fixture transport stream
FX="$HERE/../testdata/events"
python3 "$HERE/events-compare.py" scte "$FX/scte35-id3.ts" --pid 0x102 || fail=1
python3 "$HERE/events-compare.py" id3 "$FX/scte35-id3.ts" || fail=1
# 3. emsg
python3 "$HERE/events-compare.py" emsg "$FX/emsg.mp4" || fail=1
for extra in "${@:2}"; do :; done
exit $fail

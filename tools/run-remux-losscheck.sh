#!/usr/bin/env bash
# Spot-checks loss reports on a sample of corpus files (see tools/check-remux-losses.py).
#   ORACLES=... CORPUS=... WORK=... [N=40] tools/run-remux-losscheck.sh
set -u
ORACLES=${ORACLES:?}; CORPUS=${CORPUS:?}; WORK=${WORK:?}; N=${N:-40}
export ONEMEDIA_SCRATCH="$WORK"
dotnet build tools/OneMedia.RemuxCheck -c Release -v quiet -clp:NoSummary || exit 1
R="$WORK/artifacts/bin/OneMedia.RemuxCheck/release/OneMedia.RemuxCheck.dll"
# A fixed sample across families: every Nth file of the sorted list of readable files under 100 MB.
LIST=$(find "$CORPUS" -type f \( -iname '*.mp4' -o -iname '*.mov' -o -iname '*.m4a' -o -iname '*.3gp' -o -iname '*.mkv' -o -iname '*.mka' -o -iname '*.webm' -o -iname '*.ts' -o -iname '*.m2ts' -o -iname '*.flv' -o -iname '*.avi' -o -iname '*.ogg' -o -iname '*.opus' -o -iname '*.flac' -o -iname '*.mp3' -o -iname '*.aac' -o -iname '*.wav' -o -iname '*.aif*' -o -iname '*.caf' -o -iname '*.asf' -o -iname '*.wma' -o -iname '*.wmv' \) -size -102400k | sort)
TOTAL=$(echo "$LIST" | wc -l); STEP=$(( TOTAL / N )); [ "$STEP" -lt 1 ] && STEP=1
SAMPLE=$(echo "$LIST" | awk -v s="$STEP" 'NR % s == 0')
echo "$SAMPLE" | tr '\n' '\0' | xargs -0 python3 tools/check-remux-losses.py "$R" "$ORACLES/exiftool" "$ORACLES/mediainfo" "$WORK/losscheck"

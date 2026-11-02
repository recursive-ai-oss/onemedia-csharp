#!/usr/bin/env bash
# Runs the transport stream muxer differential on the worker: builds the tool, generates fixtures, picks corpus files, compares.
# Usage (inside a remote_run job, light work): tools/tsmux/run-diff.sh [layout=ts|m2ts] [maxMB=60]
set -euo pipefail
LAYOUT=${1:-ts}; MAXMB=${2:-60}
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=4
W=~/onemedia/u61-tsmux; O=~/onemedia/oracles/bin
mkdir -p $W/fix $W/res
[ -f ~/WORKER-BENCH.lock ] && { echo "bench lock present"; exit 3; }
if [ ! -f $W/bin/onemedia.dll ] || [ "${REBUILD:-0}" = 1 ]; then
  nice dotnet publish src/OneMedia.Tool -c Release -o $W/bin -v quiet -clp:NoSummary -p:UseArtifactsOutput=false -p:NuGetAudit=false -m:4 2>&1 | tail -5
fi
[ -f $W/fix/.done ] || { bash tools/tsmux/make-fixtures.sh $O/ffmpeg $W/fix && touch $W/fix/.done; }
find $W/fix -type f ! -name .done > $W/res/list.txt
find ~/onemedia/corpus \( -name '*.mp4' -o -name '*.mkv' -o -name '*.mov' -o -name '*.m4v' -o -name '*.webm' -o -name '*.m4a' -o -name '*.3gp' -o -name '*.ts' -o -name '*.mts' \) -size -${MAXMB}M \
  ! -name multitrack.mov ! -path '*/_dl/*' ! -path '*teletext*' ! -path '*bitmapsubs*' >> $W/res/list.txt
sort -u $W/res/list.txt -o $W/res/list.txt
wc -l $W/res/list.txt
nice python3 tools/tsmux/ts_diff.py --tool $W/bin/onemedia.dll --ffprobe $O/ffprobe --ffprobe8 ~/onemedia/oracles/ffmpeg-8.1/bin/ffprobe --keep-bad --ffmpeg $O/ffmpeg --tsp $O/tsp --work $W/res/$LAYOUT --jobs 4 --layout $LAYOUT $(cat $W/res/list.txt) > $W/res/$LAYOUT.tsv || true
grep -c OK $W/res/$LAYOUT.tsv; grep -v '	OK	' $W/res/$LAYOUT.tsv | cut -c1-330 | head -60

# Our reader on both sides (damaged and exotic inputs where ffprobe and this library's reader differ on the input itself).
if [ "${CHECK:-1}" = 1 ]; then
  nice dotnet publish tools/OneMedia.TsMuxCheck -c Release -o $W/chk -v quiet -clp:NoSummary -p:UseArtifactsOutput=false -p:NuGetAudit=false -m:4 2>&1 | tail -3
  mkdir -p $W/res/chk
  cat $W/res/list.txt | nice xargs -P 4 -I{} sh -c 'dotnet '$W'/chk/OneMedia.TsMuxCheck.dll "{}" '$W'/res/chk/$(basename "{}").'$LAYOUT' '$( [ "$LAYOUT" = m2ts ] && echo --m2ts )' 2>&1 | tail -1' > $W/res/chk-$LAYOUT.tsv
  rm -f $W/res/chk/*
  echo "chk: $(grep -c '	OK	' $W/res/chk-$LAYOUT.tsv) ok, $(grep -c '	DIFF	' $W/res/chk-$LAYOUT.tsv) diff, $(grep -c '	REFUSED	' $W/res/chk-$LAYOUT.tsv) refused"
  grep -v '	OK	' $W/res/chk-$LAYOUT.tsv | grep -v 'No track of the input can be written' | cut -c1-260 | head -40
fi

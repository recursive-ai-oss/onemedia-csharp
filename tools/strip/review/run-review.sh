#!/usr/bin/env bash
# The strip review's worker run. usage: run-review.sh <work dir>   (expects ~/dotnet10, oracles in ~/onemedia/oracles/bin, corpus in ~/onemedia/corpus)
# Light job: a few cores for a few minutes.
set -u
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
W=$1; mkdir -p "$W"; cd "$(dirname "$0")/../../.." || exit 1
R=$PWD
here=$R/tools/strip/review
export ONEMEDIA_SCRATCH=$W/build
[ -e ~/WORKER-BENCH.lock ] && { echo "bench lock present"; exit 9; }
nice dotnet build src/OneMedia.Tool -c Release -m:4 -v quiet -clp:NoSummary -p:NuGetAudit=false 2>&1 | tail -3
DLL=$W/build/artifacts/bin/OneMedia.Tool/release/onemedia.dll
export ONEMEDIA_DLL=$DLL
python3 $here/gen-adversarial.py $W/adv > /dev/null
for mode in both location device; do
  case $mode in both) flags="";; location) flags="--location";; device) flags="--device-ids";; esac
  bash $here/run-adversarial.sh $W/adv $W/out-$mode $flags > $W/run-$mode.txt 2>&1
done
echo "== adversarial done"
# Claim 3: validators on input and output (adversarial set, both mode)
for f in $W/adv/*.mp4; do n=$(basename $f); o=$W/out-both/$n; [ -f $o ] || continue
  a=$(ffprobe -v error -show_entries format=duration -of csv=p=0 $f 2>&1 | head -c 100 | tr '\n' ' '); b=$(ffprobe -v error -show_entries format=duration -of csv=p=0 $o 2>&1 | head -c 100 | tr '\n' ' ')
  ma=$(MP4Box -info $f 2>&1 | grep -ci 'error\|invalid\|warning'); mb=$(MP4Box -info $o 2>&1 | grep -ci 'error\|invalid\|warning')
  echo "validate $n ffprobe[$a|$b] mp4box-problem-lines[$ma|$mb]"; done > $W/validate-mp4.txt
for f in $W/adv/*.mkv; do n=$(basename $f); o=$W/out-both/$n; [ -f $o ] || continue
  echo "validate $n mkvalidator[$(mkvalidator $f 2>&1 | grep -ci 'error\|warn')|$(mkvalidator $o 2>&1 | grep -ci 'error\|warn')]"; done >> $W/validate-mp4.txt
# ExifTool and MediaInfo over the adversarial outputs
for o in $W/out-both/*; do case $o in *.report) continue;; esac; n=$(basename $o)
  exiftool -ee -a -G3 -u $o > $W/out-both/$n.exif.txt 2>&1; mediainfo --Full $o > $W/out-both/$n.mi.txt 2>&1; done
echo "== validators + exiftool + mediainfo done"
# GoPro corpus
G=$HOME/onemedia/corpus/gopro
mkdir -p $W/gopro
for f in $G/*.mp4; do n=$(basename $f); o=$W/gopro/$n; rm -f $o
  dotnet exec $DLL strip $f $o > $o.report 2>&1; echo "exit=$?" >> $o.report
  echo "$n $(python3 $here/review-check.py $f $o $(which exiftool))"
  # claim 2: payload hash per av stream
  for s in v a; do
    h1=$(ffprobe -v error -select_streams $s -show_entries packet=size,dts,pts,flags,data_hash -of csv=p=0 -show_data_hash SHA256 $f 2>/dev/null | sha256sum | cut -c1-16)
    h2=$(ffprobe -v error -select_streams $s -show_entries packet=size,dts,pts,flags,data_hash -of csv=p=0 -show_data_hash SHA256 $o 2>/dev/null | sha256sum | cut -c1-16)
    echo "   payload $s $h1 $h2 $([ $h1 = $h2 ] && echo same || echo DIFFERENT)"; done
  echo "   validate ffprobe-err=$(ffprobe -v error $o 2>&1 | wc -l) in=$(ffprobe -v error $f 2>&1 | wc -l) mp4box-err=$(MP4Box -info $o 2>&1 | grep -ci 'error\|invalid') in=$(MP4Box -info $f 2>&1 | grep -ci 'error\|invalid')"
  # claim 4: location-only and device-only
  dotnet exec $DLL strip $f $o.loc --location > /dev/null 2>&1; dotnet exec $DLL strip $f $o.dev --device-ids > /dev/null 2>&1
  echo "   loc-only: $(python3 $here/review-check.py $f $o.loc $(which exiftool))"
  echo "   dev-only: $(python3 $here/review-check.py $f $o.dev $(which exiftool))"
  rm -f $o.loc $o.dev
done > $W/gopro-summary.txt 2>&1
echo "== gopro done"

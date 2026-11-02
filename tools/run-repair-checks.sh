#!/usr/bin/env bash
# Worker sweep for index repair: builds the check tool, writes healthy files with the pinned oracles from lavfi input, damages them (and a set of
# corpus files) with tools/repair/damage.py, repairs, compares packets and runs ffprobe, mkvalidator and MP4Box on the results.
#   tools/run-repair-checks.sh <work-dir> [quick]
# Needs ~/onemedia/oracles/bin, ~/dotnet10 and the corpus under ~/onemedia/corpus. Deletes everything big it makes.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
W=$1; QUICK=${2:-}
ORA=$HOME/onemedia/oracles/bin; COR=$HOME/onemedia/corpus
export PATH=$HOME/dotnet10:$ORA:$PATH DOTNET_ROOT=$HOME/dotnet10 ONEMEDIA_SCRATCH=$W DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
mkdir -p "$W/logs" "$W/fx"
RES=$W/logs/results.txt; : > "$RES"
( cd "$here" && dotnet build tools/OneMedia.RepairCheck -v quiet -clp:NoSummary -p:NuGetAudit=false 2>&1 | grep -E 'error' | head -3 )
export RC=$(find "$W/artifacts/bin/OneMedia.RepairCheck" -name OneMedia.RepairCheck.dll | head -1)
case_run() { # label format healthy validate mode
  local label=$1 fmt=$2 h=$3
  export VALIDATE=$4 MODE=$5
  rm -rf "$W/dm" "$W/out"; mkdir -p "$W/dm"
  python3 "$here/tools/repair/damage.py" "$fmt" "$h" "$W/dm" > /dev/null || { echo "$label: DAMAGE-FAILED" >> "$RES"; return; }
  echo "== $label ($(basename "$h"))" >> "$RES"
  # the healthy file itself: nothing to repair, identical copy
  local s; s=$(dotnet exec "$RC" repair "$h" "$W/healthy.copy" 2>&1 | head -1); cmp -s "$h" "$W/healthy.copy" && s="$s identical" || s="$s DIFFERENT"; rm -f "$W/healthy.copy"
  echo "healthy: ${s%% in=*}${s##*dropped=?* }" >> "$RES"
  "$here/tools/run-repair-case.sh" "$h" "$W/dm" "$W/out" ffprobe >> "$RES" 2>&1
  rm -rf "$W/dm" "$W/out"
}
F=$W/fx
V='-v error -y -f lavfi -i testsrc2=size=320x240:rate=25 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 12'
X='-c:v libx264 -g 25 -preset ultrafast -c:a aac -b:a 64k'
ffmpeg $V $X -cluster_time_limit 1000 $F/ffmpeg.mkv
ffmpeg $V -c:v libvpx -g 25 -b:v 300k -c:a libvorbis -f webm $F/ffmpeg.webm
ffmpeg $V -c:v mpeg4 -g 25 -c:a mp3 -b:a 64k $F/ffmpeg.avi
ffmpeg $V $X -movflags +faststart $F/ffmpeg-fast.mp4
ffmpeg $V $X $F/ffmpeg-last.mp4
ffmpeg $V $X -movflags frag_keyframe+empty_moov+default_base_moof $F/ffmpeg-frag.mp4
ffmpeg $V $X -f mpegts $F/ffmpeg.ts
ffmpeg -v error -y -i $F/ffmpeg.ts -c copy -f mpegts -mpegts_m2ts_mode 1 $F/ffmpeg.m2ts
mkvmerge -q -o $F/mkvmerge.mkv $F/ffmpeg.mkv
MP4Box -quiet -add $F/ffmpeg-last.mp4 -new $F/gpac.mp4
MP4Box -quiet -frag 2000 -out $F/gpac-frag.mp4 $F/ffmpeg-fast.mp4
MP4Box -quiet -dash 2000 -frag 2000 -out $F/gpac-dash.mpd $F/ffmpeg-fast.mp4 2>/dev/null; ls $F | tr '\n' ' ' >> "$RES"; echo >> "$RES"
case_run mkv-ffmpeg mkv $F/ffmpeg.mkv mkvalidator diff
case_run mkv-mkvmerge mkv $F/mkvmerge.mkv mkvalidator diff
case_run webm-ffmpeg mkv $F/ffmpeg.webm mkvalidator diff
case_run avi-ffmpeg avi $F/ffmpeg.avi "" diff
case_run mp4-ffmpeg-faststart mp4 $F/ffmpeg-fast.mp4 mp4box diff
case_run mp4-ffmpeg-moovlast mp4 $F/ffmpeg-last.mp4 mp4box diff
case_run mp4-gpac mp4 $F/gpac.mp4 mp4box diff
case_run fmp4-ffmpeg fmp4 $F/ffmpeg-frag.mp4 mp4box diff
case_run fmp4-gpac fmp4 $F/gpac-frag.mp4 mp4box diff
case_run ts-ffmpeg ts $F/ffmpeg.ts "" subseq
case_run m2ts-ffmpeg ts $F/ffmpeg.m2ts "" subseq
rm -rf "$F"
if [ -z "$QUICK" ]; then
  for spec in mkv:blender/../matroska-test-files/test5.mkv mkv:samples/Matroska/H264+EAC3.mkv mkv:samples/Matroska/size_change/azumi.mkv avi:samples/avi/ODML/Nanami01.avi avi:samples/avi/GoneNutty.avi avi:samples/avi/TRA3106.avi mp4:blender/elephantsdream_teaser.mp4 mp4:blender/BigBuckBunny_320x180.mp4 mp4:blender/elephantsdream-480-h264-st-aac.mov ts:samples/mpeg/HD-h264.ts; do
    f=$COR/${spec#*:}; [ -f "$f" ] || { echo "missing $f" >> "$RES"; continue; }
    v=""; case ${spec%%:*} in mkv) v=mkvalidator;; mp4) v=mp4box;; esac
    m=diff; [ ${spec%%:*} = ts ] && m=subseq
    case_run "corpus-$(basename "$f")" ${spec%%:*} "$f" "$v" $m
  done
fi
echo DONE >> "$RES"

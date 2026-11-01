#!/usr/bin/env bash
# Builds a file of more than 4 GiB of real (incompressible) media by joining copies of a generated clip with `onemedia concat`, then trims it in the
# middle and across most of it, splits it, and prints the peak resident memory of every run, the output sizes and ffprobe's view of the results.
# Constant memory means the peaks do not grow with the file.
#
#   ORACLES=<dir with ffmpeg ffprobe> WORK=<scratch dir> [GIB=4.5] tools/trim/run-trim-large.sh
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg and ffprobe}
WORK=${WORK:?scratch directory}
GIB=${GIB:-4.5}
export ONEMEDIA_SCRATCH="$WORK"
mkdir -p "$WORK/big"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
TOOL="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
B="$WORK/big"
rss() { awk '/Maximum resident/ {printf "%.1f MB", $6/1024}' "$1"; }
if [ ! -e "$B/clip.mp4" ]; then
  "$ORACLES/ffmpeg" -v error -y -f lavfi -i "nullsrc=s=1280x720:r=25,geq=random(1)*255:128:128" -f lavfi -i "sine=frequency=440:sample_rate=48000" \
    -t 24 -c:v libx264 -preset ultrafast -crf 14 -g 50 -bf 2 -pix_fmt yuv420p -c:a aac -b:a 96k -movflags +faststart "$B/clip.mp4"
fi
CLIP=$(stat -c %s "$B/clip.mp4")
N=$(python3 -c "import math; print(math.ceil($GIB*1024**3/$CLIP))")
echo "clip $CLIP bytes, joining $N copies"
if [ ! -e "$B/big.mp4" ]; then
  # shellcheck disable=SC2046
  /usr/bin/time -v -o "$B/concat.time" $TOOL concat "$B/big.mp4" $(for _ in $(seq "$N"); do printf '%s ' "$B/clip.mp4"; done) | head -2
  echo "concat of $N copies: peak RSS $(rss "$B/concat.time")"
fi
ls -l "$B/big.mp4"
DUR=$("$ORACLES/ffprobe" -v error -show_entries format=duration -of csv=p=0 "$B/big.mp4")
echo "big.mp4 duration ${DUR}s"
run() {
  local name=$1 from=$2 to=$3; shift 3
  /usr/bin/time -v -o "$B/$name.time" $TOOL trim "$B/big.mp4" "$B/$name.mp4" --from "$from" --to "$to" "$@" | head -2
  echo "$name: peak RSS $(rss "$B/$name.time"), wall $(awk '/Elapsed/ {print $8}' "$B/$name.time"), output $(stat -c %s "$B/$name.mp4") bytes"
  "$ORACLES/ffprobe" -v error -show_entries format=duration:stream=codec_name,nb_frames -of compact "$B/$name.mp4"
}
run middle "$(python3 -c "print($DUR*0.4+0.013)")" "$(python3 -c "print($DUR*0.5)")"
"$ORACLES/ffmpeg" -v error -i "$B/middle.mp4" -f null - 2>&1 | head -3 | sed 's/^/ffmpeg decode of middle: /'
run most "$(python3 -c "print($DUR*0.05+0.013)")" "$(python3 -c "print($DUR*0.95)")"
rm -f "$B/most.mp4" "$B/middle.mp4"
/usr/bin/time -v -o "$B/split.time" $TOOL split "$B/big.mp4" "$B/part-{0:00}.mp4" --size 1073741824 | cut -c1-120
echo "split by 1 GiB: peak RSS $(rss "$B/split.time")"
rm -f "$B"/part-*.mp4

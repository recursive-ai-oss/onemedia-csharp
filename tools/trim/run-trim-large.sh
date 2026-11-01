#!/usr/bin/env bash
# Builds a file of more than 4 GiB of real (incompressible) media by looping a generated clip with a stream copy, then trims it in the
# middle and across most of it, splits it, and prints the peak resident memory of every run, the output sizes and ffprobe's view of the results.
# Constant memory means the peaks do not grow with the file.
#
#   ORACLES=<dir with ffmpeg ffprobe> WORK=<scratch dir> [GIB=4.5] [EXT=mp4|mkv] tools/trim/run-trim-large.sh
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg and ffprobe}
WORK=${WORK:?scratch directory}
GIB=${GIB:-4.5}
EXT=${EXT:-mp4}
FLAGS="-movflags +faststart"; KEYARG=""
# H.264 in Matroska cannot start between keyframes (docs/trim.md), so the Matroska run cuts in keyframe mode.
[ "$EXT" = mkv ] && { FLAGS=""; KEYARG="--keyframe"; }
export ONEMEDIA_SCRATCH="$WORK"
mkdir -p "$WORK/big"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
TOOL="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
B="$WORK/big"
rss() { awk '/Maximum resident/ {printf "%.1f MB", $6/1024}' "$1"; }
if [ ! -e "$B/clip.$EXT" ]; then
  "$ORACLES/ffmpeg" -v error -y -f lavfi -i "nullsrc=s=1280x720:r=25,geq=random(1)*255:128:128" -f lavfi -i "sine=frequency=440:sample_rate=48000" \
    -t 24 -c:v libx264 -preset ultrafast -crf 14 -g 50 -bf 2 -pix_fmt yuv420p -c:a aac -b:a 96k $FLAGS "$B/clip.$EXT"
fi
CLIP=$(stat -c %s "$B/clip.$EXT")
N=$(python3 -c "import math; print(math.ceil($GIB*1024**3/$CLIP))")
echo "clip $CLIP bytes, joining $N copies"
if [ ! -e "$B/big.$EXT" ]; then
  # Looped stream copy gives one continuous timeline with a single edit segment per track (a concat of clips keeps one segment per clip, which trim refuses).
  /usr/bin/time -v -o "$B/loop.time" "$ORACLES/ffmpeg" -v error -y -stream_loop $((N - 1)) -i "$B/clip.$EXT" -c copy "$B/big.$EXT"
fi
ls -l "$B/big.$EXT"
DUR=$("$ORACLES/ffprobe" -v error -show_entries format=duration -of csv=p=0 "$B/big.$EXT")
echo "big.$EXT duration ${DUR}s"
run() {
  local name=$1 from=$2 to=$3; shift 3
  /usr/bin/time -v -o "$B/$name.time" $TOOL trim "$B/big.$EXT" "$B/$name.$EXT" --from "$from" --to "$to" "$@" | head -2
  echo "$name: peak RSS $(rss "$B/$name.time"), wall $(awk '/Elapsed/ {print $8}' "$B/$name.time"), output $(stat -c %s "$B/$name.$EXT") bytes"
  "$ORACLES/ffprobe" -v error -show_entries format=duration:stream=codec_name,nb_frames -of compact "$B/$name.$EXT"
}
run middle "$(python3 -c "print($DUR*0.4+0.013)")" "$(python3 -c "print($DUR*0.5)")" $KEYARG
"$ORACLES/ffmpeg" -v error -i "$B/middle.$EXT" -f null - 2>&1 | head -3 | sed 's/^/ffmpeg decode of middle: /'
run most "$(python3 -c "print($DUR*0.05+0.013)")" "$(python3 -c "print($DUR*0.95)")" $KEYARG
rm -f "$B/most.$EXT" "$B/middle.$EXT"
/usr/bin/time -v -o "$B/split.time" $TOOL split "$B/big.$EXT" "$B/part-{0:00}.$EXT" --size 1073741824 | cut -c1-120
echo "split by 1 GiB: peak RSS $(rss "$B/split.time")"
rm -f "$B"/part-*.$EXT

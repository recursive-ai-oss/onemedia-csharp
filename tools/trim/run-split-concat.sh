#!/usr/bin/env bash
# Splits generated files at keyframes, joins the parts again, and compares the result with the source: packets through ffprobe
# (tools/trim/packet-compare.py: size, key flag, payload hash, times up to one offset), then MP4Box -info and ffmpeg's null muxer on the
# parts and the join. Also checks that parts with different codec configurations are refused.
#
#   ORACLES=<dir with ffmpeg ffprobe MP4Box> WORK=<scratch dir> [PYTHON=python3] tools/trim/run-split-concat.sh
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg, ffprobe and MP4Box}
WORK=${WORK:?scratch directory}
PYTHON=${PYTHON:-python3}
export ONEMEDIA_SCRATCH="$WORK" PATH="$ORACLES:$PATH"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
T="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
mkdir -p "$WORK/sc" && cd "$WORK/sc" && rm -f ./*.mp4
ffmpeg -version | head -1; MP4Box -version 2>&1 | head -1
status=0
noise() { grep -ciE 'error|warning|invalid|fail' || true; }
for codec in "libx264 -bf 2 -g 50" "libx265 -x265-params bframes=2:keyint=50:min-keyint=50:no-open-gop=1:log-level=error" "libvpx-vp9 -deadline realtime -cpu-used 8 -g 50" "libaom-av1 -cpu-used 8 -g 50 -lag-in-frames 0"; do
  name=$(echo "$codec" | cut -d' ' -f1)
  for audio in aac libopus; do
    src="$name-$audio.mp4"
    # shellcheck disable=SC2086
    ffmpeg -v error -y -f lavfi -i testsrc2=size=320x180:rate=25 -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 20 -pix_fmt yuv420p -c:v $codec -c:a $audio -movflags +faststart "$src"
    rm -f part-*.mp4 join.mp4
    $T split "$src" 'part-{0:00}.mp4' --at 3.1,8,13.7 | sed "s/^/$src /" | head -5
    $T concat join.mp4 part-*.mp4 | head -1 | sed "s/^/$src join: /"
    $PYTHON "$OLDPWD/tools/trim/packet-compare.py" "$ORACLES/ffprobe" "$src" join.mp4 --times | sed "s/^/$src join vs source: /" || status=1
    for f in part-*.mp4 join.mp4; do
      new=$(MP4Box -info "$f" 2>&1 | noise); fm=$(ffmpeg -v error -i "$f" -f null - 2>&1 | wc -l)
      echo "$src $f: MP4Box messages $new, ffmpeg null-muxer messages $fm"
    done
  done
done
# Parts with different parameter sets must not be joined.
ffmpeg -v error -y -f lavfi -i testsrc2=size=320x180:rate=25 -t 2 -c:v libx264 a.mp4
ffmpeg -v error -y -f lavfi -i testsrc2=size=640x360:rate=25 -t 2 -c:v libx264 b.mp4
if $T concat bad.mp4 a.mp4 b.mp4 2>refusal.txt; then echo "FAIL: mismatched configurations were joined"; status=1; else echo "refused as expected: $(cat refusal.txt)"; fi
exit $status

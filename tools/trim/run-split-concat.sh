#!/usr/bin/env bash
# Splits generated files at keyframes, joins the parts again, and compares the result with the source: packets through ffprobe
# (tools/trim/packet-compare.py: size, key flag, payload hash, times up to one offset), then MP4Box -info and ffmpeg's null muxer on the
# parts and the join. Also checks that parts with different codec configurations are refused.
#
#   ORACLES=<dir with ffmpeg ffprobe MP4Box> WORK=<scratch dir> [PYTHON=python3] [EXT=mp4|mkv] tools/trim/run-split-concat.sh
#   With EXT=mkv the sources are Matroska and the parts are checked with mkvalidator (when it is in ORACLES) instead of MP4Box.
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg, ffprobe and MP4Box}
WORK=${WORK:?scratch directory}
PYTHON=${PYTHON:-python3}
EXT=${EXT:-mp4}
FLAGS="-movflags +faststart"; [ "$EXT" = mkv ] && FLAGS=""
export ONEMEDIA_SCRATCH="$WORK" PATH="$ORACLES:$PATH"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
T="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
mkdir -p "$WORK/sc" && cd "$WORK/sc" && rm -f ./*.mp4 ./*.mkv
ffmpeg -version | head -1; [ "$EXT" = mkv ] || MP4Box -version 2>&1 | head -1
status=0
noise() { grep -ciE 'error|warning|invalid|fail' || true; }
for codec in "libx264 -bf 2 -g 50" "libx265 -x265-params bframes=2:keyint=50:min-keyint=50:no-open-gop=1:log-level=error" "libvpx-vp9 -deadline realtime -cpu-used 8 -g 50" "libaom-av1 -cpu-used 8 -g 50 -lag-in-frames 0"; do
  name=$(echo "$codec" | cut -d' ' -f1)
  for audio in aac libopus; do
    src="$name-$audio.$EXT"
    # shellcheck disable=SC2086
    ffmpeg -v error -y -f lavfi -i testsrc2=size=320x180:rate=25 -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 20 -pix_fmt yuv420p -c:v $codec -c:a $audio $FLAGS "$src"
    rm -f part-*.$EXT join.$EXT
    $T split "$src" "part-{0:00}.$EXT" --at 3.1,8,13.7 | sed "s/^/$src /" | head -5
    $T concat join.$EXT part-*.$EXT | head -1 | sed "s/^/$src join: /"
    $PYTHON "$OLDPWD/tools/trim/packet-compare.py" "$ORACLES/ffprobe" "$src" join.$EXT --times | sed "s/^/$src join vs source: /" || status=1
    for f in part-*.$EXT join.$EXT; do
      if [ "$EXT" = mkv ]; then
        if command -v mkvalidator >/dev/null; then new=$(mkvalidator "$f" 2>&1 | grep -ciE 'error|warning|invalid' || true); else new=n/a; fi
      else
        new=$(MP4Box -info "$f" 2>&1 | noise)
      fi
      fm=$(ffmpeg -v error -i "$f" -f null - 2>&1 | wc -l)
      echo "$src $f: validator messages $new, ffmpeg null-muxer messages $fm"
    done
  done
done
# Parts with different parameter sets must not be joined.
ffmpeg -v error -y -f lavfi -i testsrc2=size=320x180:rate=25 -t 2 -c:v libx264 a.$EXT
ffmpeg -v error -y -f lavfi -i testsrc2=size=640x360:rate=25 -t 2 -c:v libx264 b.$EXT
if $T concat bad.$EXT a.$EXT b.$EXT 2>refusal.txt; then echo "FAIL: mismatched configurations were joined"; status=1; else echo "refused as expected: $(cat refusal.txt)"; fi
exit $status

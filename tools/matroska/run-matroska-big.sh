#!/usr/bin/env bash
# Large-file checks for the Matroska reader: probe I/O (bytes and reads through the observing source), a full demux with allocation
# counts and peak working set, for a ~2.3 GB file with SeekHead and Cues, the same file written live (unknown sizes, no index) and a
# copy with Tracks after all Clusters. Also a small file for the working-set comparison.
#
#   tools/matroska/run-matroska-big.sh <work-dir>
#
# Environment: ORACLES (default ~/onemedia/oracles/bin). Needs the .NET SDK, python3 and about 7 GB free in <work-dir>.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="${1:?work directory}"
ORACLES="${ORACLES:-$HOME/onemedia/oracles/bin}"
export ONEMEDIA_SCRATCH="$work"
mkdir -p "$work/big" "$work/logs"
dotnet build "$here/tools/OneMedia.MatroskaDiff" -c Release -v quiet -clp:NoSummary || exit 2
dll="$work/artifacts/bin/OneMedia.MatroskaDiff/release/OneMedia.MatroskaDiff.dll"
cd "$work/big"

if [ ! -f big.mkv ]; then
  # Raw video (so the file is large and fast to make) plus Opus, 2.3 GB.
  "$ORACLES/ffmpeg" -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=25:duration=135 \
    -f lavfi -i sine=frequency=440:sample_rate=48000:duration=135 -c:v rawvideo -pix_fmt rgb24 -allow_raw_vfw 1 -c:a libopus -f matroska big.mkv
fi
[ -f big-live.mkv ] || "$ORACLES/ffmpeg" -hide_banner -loglevel error -y -i big.mkv -c copy -f matroska -live 1 big-live.mkv
[ -f big-tracks-last.mkv ] || python3 "$here/tools/matroska/make-tracks-last.py" big.mkv big-tracks-last.mkv
[ -f small.mkv ] || "$ORACLES/ffmpeg" -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=160x112:rate=25:duration=3 -f lavfi -i sine=duration=3 -c:v libx264 -c:a libopus small.mkv
ls -l big.mkv big-live.mkv big-tracks-last.mkv small.mkv

for f in big.mkv big-live.mkv big-tracks-last.mkv; do
  echo "== probe-io $f"; dotnet "$dll" probe-io "$work/big/$f"
done
for f in small.mkv big.mkv big-live.mkv big-tracks-last.mkv; do
  echo "== alloc $f"; /usr/bin/time -f "peak RSS %M KiB, wall %e s" dotnet "$dll" alloc "$work/big/$f" 2>&1
done
echo "== ffprobe packets on big.mkv (packets compared)"
dotnet "$dll" diff "$work/big/big.mkv" --ffprobe "$ORACLES/ffprobe" | tail -n 1

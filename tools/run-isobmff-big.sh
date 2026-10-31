#!/usr/bin/env bash
# Large-file checks for the ISO BMFF demuxer: structural read counts, per-packet allocation, memory use and the ffprobe comparison on
# sparse files (only the structure is real, the media data is a hole).
# Usage (repository root): ORACLES=<dir> WORK=<scratch dir> BLENDER=<a larger MP4/MOV> tools/run-isobmff-big.sh
set -eu
ORACLES=${ORACLES:?}; WORK=${WORK:?}; BLENDER=${BLENDER:?}
mkdir -p "$WORK/big"
export ONEMEDIA_SCRATCH="$WORK"
dotnet build tools/OneMedia.IsoBmffDiff -c Release -v quiet -clp:NoSummary
T="dotnet $WORK/artifacts/bin/OneMedia.IsoBmffDiff/release/OneMedia.IsoBmffDiff.dll"
B="$WORK/big"
[ -e "$B/last_2g.mp4" ] || $T --make-big "$B/last_2g.mp4" 2.2 moovlast
[ -e "$B/first_2g.mp4" ] || $T --make-big "$B/first_2g.mp4" 2.2 faststart
[ -e "$B/co64_4g.mp4" ] || $T --make-big "$B/co64_4g.mp4" 4.5 moovlast
[ -e "$B/frag_bbb.mp4" ] || "$ORACLES/ffmpeg" -hide_banner -loglevel error -i "$BLENDER" -c copy -movflags frag_keyframe+empty_moov+default_base_moof "$B/frag_bbb.mp4"
ls -ls "$B"

echo '=== ours: probe (open, tracks, duration) through the observed source'
for f in last_2g first_2g co64_4g frag_bbb; do $T --io "$B/$f.mp4"; done

echo '=== other tools: bytes and read calls on the same files (strace, read/pread64 on the file descriptor)'
cat > "$WORK/count_reads.py" <<'PY'
import re, sys
log, path = sys.argv[1], sys.argv[2]
fd = set(); reads = 0; total = 0
for line in open(log, errors='replace'):
    m = re.search(r'openat\(.*"%s".*\) = (\d+)' % re.escape(path), line)
    if m: fd.add(m.group(1)); continue
    m = re.match(r'(?:\d+\s+)?(?:p?read(?:64)?)\((\d+),.*\) = (\d+)', line)
    if m and m.group(1) in fd:
        reads += 1; total += int(m.group(2))
print(f'reads={reads} bytes={total}')
PY
for f in last_2g first_2g co64_4g frag_bbb; do
  strace -f -e trace=openat,read,pread64 -o "$WORK/st.log" "$ORACLES/ffprobe" -v error -show_format -show_streams "$B/$f.mp4" > /dev/null 2>&1 || true
  echo "ffprobe -show_format -show_streams $f: $(python3 "$WORK/count_reads.py" "$WORK/st.log" "$B/$f.mp4")"
  strace -f -e trace=openat,read,pread64 -o "$WORK/st.log" "$ORACLES/mediainfo" "$B/$f.mp4" > /dev/null 2>&1 || true
  echo "mediainfo $f: $(python3 "$WORK/count_reads.py" "$WORK/st.log" "$B/$f.mp4")"
  strace -f -e trace=openat,read,pread64 -o "$WORK/st.log" "$ORACLES/exiftool" "$B/$f.mp4" > /dev/null 2>&1 || true
  echo "exiftool $f: $(python3 "$WORK/count_reads.py" "$WORK/st.log" "$B/$f.mp4")"
done

echo '=== ours: full demux, allocation per packet and memory (small file first)'
$T --stream "$BLENDER"
for f in last_2g first_2g co64_4g frag_bbb; do $T --stream "$B/$f.mp4"; done

echo '=== ffprobe comparison on the sparse files'
$T --ffprobe "$ORACLES/ffprobe" --mp4box "$ORACLES/MP4Box" --no-audio "$B/last_2g.mp4" "$B/first_2g.mp4" "$B/co64_4g.mp4" "$B/frag_bbb.mp4" || true

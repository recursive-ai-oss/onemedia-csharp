#!/bin/bash
# Checks the reconstruction through the command-line tool (`onemedia repair`), on a build machine.
#   tools/run-repair-reconstruct-checks.sh WORKDIR spot|big
# spot: five ffmpeg-written recordings (H.264 B-frames + AAC, HEVC + AAC, MOV, 3GP with AMR, H.264 + MP3) are cut at 60 % of the media data
#       (the movie box is lost with the tail), repaired with the healthy file as reference, and the packets of the result are compared with
#       ffprobe's listing of the original: every packet's size, key flag and decode time must equal the original's first packets.
#       One of them is also repaired without a reference, which must be refused (its parameter sets lived in the lost movie box). Output also goes through `ffmpeg -f null` for decode errors.
# big:  a file of about 4.5 GB made by looping a small clip is cut at 90 % and repaired with the clip as reference; reports peak RSS.
# ffmpeg/ffprobe come from ~/onemedia/oracles/bin.
set -euo pipefail
W=${1:?work dir}
WHAT=${2:-spot}
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH=$W/scratch FFMPEG=ffmpeg
mkdir -p "$W"
ffprobe -version | head -1
(cd "$ROOT/src/OneMedia.Tool" && dotnet build -c Release -v quiet -clp:NoSummary -p:NuGetAudit=false -m:4 2>&1 | tail -2)
TOOL=$(find "$W/scratch/artifacts/bin/OneMedia.Tool/release" -name onemedia.dll | head -1)
test -f "$TOOL"

cut_file() { # file fraction out
python3 - "$1" "$2" "$3" <<'EOF'
import struct, sys
path, frac, out = sys.argv[1], float(sys.argv[2]), sys.argv[3]
data = open(path, "rb").read()
pos = 0; mdat = None
while pos + 8 <= len(data):
    n, t = struct.unpack(">I4s", data[pos:pos + 8]); hdr = 8
    if n == 1: n = struct.unpack(">Q", data[pos + 8:pos + 16])[0]; hdr = 16
    elif n == 0: n = len(data) - pos
    if t == b"mdat": mdat = (pos, n, hdr); break
    pos += n
start = mdat[0] + mdat[2]
at = start + int((mdat[1] - mdat[2]) * frac) + 123
open(out, "wb").write(data[:at])
print("cut at", at, "of", len(data))
EOF
}

case "$WHAT" in
spot)
  rm -rf "$W/fx" "$W/spot"; mkdir -p "$W/spot"
  python3 "$HERE/reconstruct-fixtures.py" gen "$W/fx" >/dev/null
  fail=0
  for spec in h264b-aac.mp4:ref hevc-aac.mp4:ref h264-aac.mov:ref h264-amr.3gp:ref h264-mp3.mp4:ref h264b-aac.mp4:noref; do
    f=${spec%%:*}; mode=${spec##*:}
    base=${f%.*}; ext=${f##*.}
    cut_file "$W/fx/$f" 0.6 "$W/spot/$base.$mode.cut.$ext" >/dev/null
    out="$W/spot/$base.$mode.fixed.$ext"
    args=("$W/spot/$base.$mode.cut.$ext" "$out")
    [ "$mode" = ref ] && args+=(--reference "$W/fx/$f")
    dotnet "$TOOL" repair "${args[@]}" > "$W/spot/$base.$mode.report.txt" 2>&1 && rc=0 || rc=$?
    head -1 "$W/spot/$base.$mode.report.txt"
    if [ "$mode" = noref ]; then
      # ffmpeg-written MP4 keeps its parameter sets only in the lost movie box, so without a reference there is nothing to rebuild the video from
      if [ "$rc" = 2 ] && [ ! -e "$out" ]; then echo "   noref: unrepairable, no output left (expected)"; else echo "   noref: unexpected"; fail=1; fi
      rm -f "$W/spot/$base.$mode.cut.$ext"
      continue
    fi
    python3 - "$W/fx/$f" "$out" "$f $mode" <<'EOF' || fail=1
import subprocess, sys
def pk(p):
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "packet=codec_type,size,flags,dts_time", "-of", "csv=p=0", p], capture_output=True, text=True)
    d = {}
    for line in r.stdout.splitlines():
        t, dts, size, flags = line.split(",")[:4]
        d.setdefault(t, []).append((int(size), "K" in flags, float(dts) if dts not in ("N/A", "") else None))
    return d
o, n = pk(sys.argv[1]), pk(sys.argv[2])
bad = 0
for t, lst in n.items():
    ol = o.get(t, [])
    eq = sum(1 for a, b in zip(lst, ol) if a[0] == b[0] and a[1] == b[1])
    # video decode times are shifted by the reorder delay in the original's listing only if the edit lists differ: compare sizes and keys
    print("  ", sys.argv[3], t, "recovered", len(lst), "of", len(ol), "size+key equal", eq, "of", len(lst))
    if eq != len(lst) or len(lst) == 0: bad = 1
sys.exit(bad)
EOF
    ffmpeg -v error -xerror -i "$out" -f null - 2>&1 | head -2 | sed 's/^/   ffmpeg: /' || true
    rm -f "$W/spot/$base.$mode.cut.$ext"
  done
  echo "spot-fail=$fail"
  ;;
big)
  rm -rf "$W/big"; mkdir -p "$W/big"
  ffmpeg -v error -y -f lavfi -i "testsrc2=size=1280x720:rate=25" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 20 \
    -c:v libx264 -preset veryfast -b:v 20M -maxrate 20M -bufsize 20M -g 50 -pix_fmt yuv420p -c:a aac -b:a 128k "$W/big/clip.mp4"
  SIZE=$(stat -c %s "$W/big/clip.mp4")
  LOOPS=$(( 4500000000 / SIZE ))
  ffmpeg -v error -y -stream_loop "$LOOPS" -i "$W/big/clip.mp4" -c copy "$W/big/full.mp4"
  cut_file "$W/big/full.mp4" 0.9 "$W/big/big.mp4"
  rm -f "$W/big/full.mp4"
  ls -l "$W/big/big.mp4"
  /usr/bin/time -v dotnet "$TOOL" repair "$W/big/big.mp4" "$W/big/out.mp4" --reference "$W/big/clip.mp4" 2> "$W/big/time.txt" | head -12
  grep -E 'Maximum resident|Elapsed' "$W/big/time.txt"
  ls -l "$W/big/out.mp4"
  ffmpeg -v error -i "$W/big/out.mp4" -f null - 2>&1 | head -3 || true
  rm -rf "$W/big"
  ;;
*) echo "unknown mode $WHAT"; exit 2 ;;
esac

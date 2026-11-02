#!/bin/bash
# Checks for the reconstruction of cut-off MP4/MOV/3GP recordings, on the worker.
#   tools/run-reconstruct-checks.sh WORKDIR synthetic|corpus|big
# synthetic: ffmpeg-written fixtures (lavfi input) cut at 11 offsets, with and without a reference file.
# corpus:    the GoPro samples and Blender clips under ~/onemedia/corpus, the same way (self as reference).
# big:       a file of about 4.5 GB made by looping a small clip, cut at 90 %, reconstructed with the clip as reference; reports peak RSS.
# Oracles (ffmpeg, MP4Box) come from ~/onemedia/oracles/bin. Results are written under WORKDIR; the 4.5 GB files are deleted after use.
set -euo pipefail
W=${1:?work dir}
WHAT=${2:-synthetic}
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH=$W/scratch FFMPEG=ffmpeg MP4BOX=MP4Box
mkdir -p "$W"
ffmpeg -version | head -1
MP4Box -version 2>&1 | head -1

(cd "$ROOT/tools/OneMedia.ReconstructCheck" && dotnet build -c Release -v quiet -clp:NoSummary -p:NuGetAudit=false -m:4 2>&1 | tail -3)
DLL=$W/scratch/artifacts/bin/OneMedia.ReconstructCheck/release/OneMedia.ReconstructCheck.dll
test -f "$DLL"

case "$WHAT" in
synthetic)
  rm -rf "$W/fx" "$W/sw-synthetic"
  python3 "$HERE/reconstruct-fixtures.py" gen "$W/fx"
  python3 "$HERE/reconstruct-sweep.py" "$W/sw-synthetic" "$DLL" --healthy "$W/fx" --jobs 4
  ;;
corpus)
  rm -rf "$W/corpus-links" "$W/sw-corpus"
  mkdir -p "$W/corpus-links"
  for f in "$HOME"/onemedia/corpus/gopro/*.mp4; do ln -s "$f" "$W/corpus-links/gopro-$(basename "$f")"; done
  C=$HOME/onemedia/corpus/blender
  for f in BigBuckBunny_320x180.mp4 elephantsdream-480-h264-st-aac.mov big_buck_bunny_480p_h264.mov; do
    test -f "$C/$f" && ln -s "$C/$f" "$W/corpus-links/blender-$f"
  done
  python3 "$HERE/reconstruct-sweep.py" "$W/sw-corpus" "$DLL" --healthy "$W/corpus-links" --jobs 4
  ;;
big)
  rm -rf "$W/big"; mkdir -p "$W/big"
  ffmpeg -v error -y -f lavfi -i "testsrc2=size=1280x720:rate=25" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 20 \
    -c:v libx264 -preset veryfast -b:v 20M -maxrate 20M -bufsize 20M -g 50 -pix_fmt yuv420p -c:a aac -b:a 128k "$W/big/clip.mp4"
  ls -l "$W/big/clip.mp4"
  # Loop the clip with stream copy until the file is about 4.5 GB.
  SIZE=$(stat -c %s "$W/big/clip.mp4")
  LOOPS=$(( 4500000000 / SIZE ))
  ffmpeg -v error -y -stream_loop "$LOOPS" -i "$W/big/clip.mp4" -c copy "$W/big/big.mp4"
  ls -l "$W/big/big.mp4"
  python3 - "$W/big/big.mp4" <<'EOF'
import struct, sys
path = sys.argv[1]
with open(path, "r+b") as f:
    f.seek(0, 2); size = f.tell(); f.seek(0)
    pos = 0; mdat = None
    while pos + 8 <= size:
        f.seek(pos); h = f.read(16)
        n, t = struct.unpack(">I4s", h[:8]); hdr = 8
        if n == 1: n = struct.unpack(">Q", h[8:16])[0]; hdr = 16
        elif n == 0: n = size - pos
        if t == b"mdat": mdat = (pos, n, hdr); break
        pos += n
    start = mdat[0] + mdat[2]; cut = start + int((mdat[1] - mdat[2]) * 0.9) + 123
    f.truncate(cut)
    print("cut at", cut, "of", size)
EOF
  ls -l "$W/big/big.mp4"
  /usr/bin/time -v dotnet "$DLL" reconstruct "$W/big/big.mp4" "$W/big/out.mp4" --ref "$W/big/clip.mp4" 2> "$W/big/time.txt" | tee "$W/big/report.txt"
  grep -E 'Maximum resident|Elapsed' "$W/big/time.txt"
  ls -l "$W/big/out.mp4"
  ffmpeg -v error -t 60 -i "$W/big/out.mp4" -f null - 2>&1 | head -3 || true
  rm -rf "$W/big"
  ;;
*) echo "unknown mode $WHAT"; exit 2 ;;
esac

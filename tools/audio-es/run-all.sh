#!/usr/bin/env bash
# Builds the comparison tools and runs the audio elementary-stream checks against the reference tools: packet differential and stream facts
# (ffprobe), decoded length (ffmpeg), tag agreement (ExifTool, ATL.NET, TagLib#), probe I/O (ffprobe, MediaInfo, ExifTool; over HTTP ranges and
# with strace) and the pull-path allocation. Run from the repository root on the machine that holds the oracles and corpus.
#   tools/audio-es/run-all.sh [STEP...]     steps: gen diff info decode tags io alloc  (default: all)
# Results go to $OUT (default ~/onemedia/u12-mp3-es/results). A step that finds differences prints them and carries on.
set -uo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
O="${ONEMEDIA_HOME:-$HOME/onemedia}"
export ONEMEDIA_HOME="$O"
OUT="${OUT:-$O/u12-mp3-es/results}"
export ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$O/u12-mp3-es}"
ART="$ONEMEDIA_SCRATCH/artifacts"
G="$O/corpus/generated/audio-es"
export PATH="$O/oracles/bin:$PATH"
mkdir -p "$OUT"
steps=("$@"); [ ${#steps[@]} -eq 0 ] && steps=(gen diff info decode tags io alloc)
want() { local s; for s in "${steps[@]}"; do [ "$s" = "$1" ] && return 0; done; return 1; }

dotnet build tools/OneMedia.Oracles/OneMedia.Oracles.csproj -c Release -v q -clp:NoSummary 2>&1 | tail -3
T="$ART/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
FILES=$(find "$G" -type f \( -name '*.mp3' -o -name '*.mp2' -o -name '*.aac' -o -name '*.ac3' -o -name '*.eac3' -o -name '*.amr' -o -name '*.awb' \) ! -path '*/_*' | sort)

if want gen; then
  # A file left empty by an earlier failed run is made again; then the big files for the I/O and memory rows.
  find "$G" -type f -size 0 -delete
  bash tools/audio-es/generate.sh 2>&1 | tail -3
  mkdir -p "$O/corpus/generated/audio-es-big"
  B="$O/corpus/generated/audio-es-big"
  if [ ! -s "$B/big-noxing.mp3" ]; then
    # 2.2 GB of CBR frames behind an ID3v2 tag with 64 KiB of padding, and the same behind an Info frame.
    python3 - "$G" "$B" <<'PY'
import sys
g, b = sys.argv[1], sys.argv[2]
src = open(g + '/_src/plain.mp3', 'rb').read()
info = open(g + '/_src/info.mp3', 'rb').read()
def synch(n): return bytes([(n >> 21) & 0x7F, (n >> 14) & 0x7F, (n >> 7) & 0x7F, n & 0x7F])
pad = 65536
head = b'ID3\x04\x00\x00' + synch(pad) + bytes(pad)
def strip_id3(d):
    return d[10 + ((d[6] << 21) | (d[7] << 14) | (d[8] << 7) | d[9]):] if d[:3] == b'ID3' else d
body = strip_id3(src)
for name in ('big-noxing.mp3', 'big-xing.mp3'):
    with open(b + '/' + name, 'wb') as f:
        f.write(head)
        if name == 'big-xing.mp3':
            f.write(strip_id3(info))   # the Info frame leads, then more frames
        n = (2_200_000_000 // len(body)) + 1
        for _ in range(n):
            f.write(body)
PY
  fi
fi

if want diff; then
  dotnet "$T" diff --family audio-es --root "$G" --out "$OUT/diff" --exceptions tools/OneMedia.Oracles/exceptions.tsv --jobs 4 | tail -3
  head -60 "$OUT/diff/summary.txt"
fi

if want info; then
  dotnet "$T" infocheck $FILES > "$OUT/infocheck.txt" 2>&1
  tail -1 "$OUT/infocheck.txt"; grep -c '^same' "$OUT/infocheck.txt"; grep '^DIFF' "$OUT/infocheck.txt" | head -40
fi

if want decode; then
  dotnet "$T" audiodecode --out "$OUT/decode" $FILES > "$OUT/audiodecode.txt" 2>&1
  tail -3 "$OUT/audiodecode.txt"
fi

if want tags; then
  dotnet build bench/OneMedia.Bench/OneMedia.Bench.csproj -c Release -v q -clp:NoSummary 2>&1 | tail -3
  BN="$ART/bin/OneMedia.Bench/release/OneMedia.Bench.dll"
  dotnet "$BN" tagcompare "$OUT/tagcompare.tsv" $(find "$G/tagged" "$G/tagged-info" "$G/tagged-ext" "$G/mp3" "$G/aac" -type f \( -name '*.mp3' -o -name '*.aac' \) | sort) | head -80
fi

if want io; then
  # Probe and metadata reads: ours (local and over HTTP ranges) next to ffprobe, MediaInfo and ExifTool.
  dotnet "$T" io --root "$O/corpus/generated" --out "$OUT/io-small" --metadata $(find "$G/tagged" "$G/mp3" "$G/aac" "$G/ac3" "$G/amr" -type f | sort | head -80)
  dotnet "$T" io --root "$O/corpus/generated" --out "$OUT/io-big" --metadata "$O"/corpus/generated/audio-es-big/*.mp3
fi

if want alloc; then
  BN="$ART/bin/OneMedia.Bench/release/OneMedia.Bench.dll"
  [ -f "$BN" ] || dotnet build bench/OneMedia.Bench/OneMedia.Bench.csproj -c Release -v q -clp:NoSummary 2>&1 | tail -3
  dotnet "$BN" pullalloc generated/audio-es/mp3/long-60s.mp3 generated/audio-es/mp3/cbr128-44k-info.mp3 generated/audio-es/aac/lc-48k-stereo.aac generated/audio-es/ac3/ac3-48k-stereo-192k.ac3 generated/audio-es/amr/nb-12200.amr generated/audio-es-big/big-noxing.mp3 generated/audio-es-big/big-xing.mp3 | tee "$OUT/pullalloc.txt"
  /usr/bin/time -v dotnet "$BN" pullalloc generated/audio-es-big/big-xing.mp3 2>&1 | grep -E 'Maximum resident|packets' | tee -a "$OUT/pullalloc.txt"
fi

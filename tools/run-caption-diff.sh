#!/usr/bin/env bash
# Extracts embedded captions with OneMedia, ccextractor and ffmpeg (both black-box) and leaves SRT files side by side.
#   run-caption-diff.sh <outdir> <file>...
# Needs the oracles (tools/oracles/install-linux.sh) and .NET 10 on the worker.
set -uo pipefail
export PATH=~/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
OUT=$1; shift
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
dotnet build "$HERE/OneMedia.CaptionCheck/OneMedia.CaptionCheck.csproj" -c Release -v quiet -clp:NoSummary -o "$OUT/_bin" >"$OUT/build.log" 2>&1 || { tail "$OUT/build.log"; exit 1; }
for f in "$@"; do
  b=$(basename "$f"); b=${b%.*}; d="$OUT/$b"; mkdir -p "$d"
  echo "== $b"
  dotnet "$OUT/_bin/OneMedia.CaptionCheck.dll" triplets "$f"
  dotnet "$OUT/_bin/OneMedia.CaptionCheck.dll" srt "$f" "$d/ours" --rebase > "$d/ours.log" 2>&1; sed 's/^/  ours: /' "$d/ours.log"
  # ccextractor: CC1..CC4 of field 1/2, and DTVCC services 1-6.
  mkdir -p "$d/ccx"
  ccextractor "$f" -out=srt -o "$d/ccx/cc1.srt" >/dev/null 2>&1
  ccextractor "$f" -out=srt -cc2 -o "$d/ccx/cc2.srt" >/dev/null 2>&1
  ccextractor "$f" -out=srt -12 -o "$d/ccx/both.srt" >/dev/null 2>&1
  for s in 1 2; do ccextractor "$f" -out=srt -svc $s -o "$d/ccx/svc$s.srt" >/dev/null 2>&1; done
  # ffmpeg: the subcc virtual stream carries the 608 pairs decoded as eia_608 by the SRT-converting path.
  mkdir -p "$d/ffm"
  ffmpeg -v error -y -f lavfi -i "movie=$f[out0+subcc]" -map 0:s:0 "$d/ffm/subcc.srt" 2>"$d/ffm/err.log"
  for o in "$d"/ours/*.srt; do
    [ -e "$o" ] || continue
    n=$(basename "$o" .srt); case $n in cc1) c=cc1;; cc2) c=cc2;; svc*) c=$n;; *) c=;; esac
    [ -n "$c" ] && ls "$d"/ccx/$c*.srt >/dev/null 2>&1 && echo "  $n vs ccextractor: $(python3 "$HERE/caption-compare.py" "$o" "$(ls "$d"/ccx/$c*.srt | head -1)" | head -1)"
  done
  # pass-through: remux to Matroska (the ISO muxer does not take MPEG-2 video); the caption triplets must equal the source's, byte for byte.
  src=$(dotnet "$OUT/_bin/OneMedia.CaptionCheck.dll" triplets "$f" | grep -o 'sha256=.*')
  dotnet "$OUT/_bin/OneMedia.CaptionCheck.dll" remux "$f" "$d/pt.mkv" 2>&1 | sed 's/^/  /' | head -3
  dst=$(dotnet "$OUT/_bin/OneMedia.CaptionCheck.dll" triplets "$d/pt.mkv" | grep -o 'sha256=.*')
  echo "  remux->mkv triplets equal: $([ "$src" = "$dst" ] && echo yes || echo "NO ($src vs $dst)")"
  ffmpeg -v error -y -f lavfi -i "movie=$f[out0+subcc]" -map 0:s:0 "$d/src-ffm.srt" 2>/dev/null
  ffmpeg -v error -y -f lavfi -i "movie=$d/pt.mkv[out0+subcc]" -map 0:s:0 "$d/pt-ffm.srt" 2>/dev/null
  echo "  ffmpeg(remuxed mkv) vs ffmpeg(source): $(python3 "$HERE/caption-compare.py" "$d/pt-ffm.srt" "$d/src-ffm.srt" 1000 | head -1)"
done

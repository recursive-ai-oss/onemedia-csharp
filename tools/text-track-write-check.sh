#!/usr/bin/env bash
# Remuxes the oracle-written text-track files to MP4 and Matroska with OneMedia, then checks the results the way a player pipeline would:
# ffprobe errors against a no-text baseline, MP4Box/mkvalidator verdicts, and the cues ffmpeg, MP4Box and mkvextract read back.
# Needs the pinned oracles on PATH (ffmpeg, ffprobe, MP4Box, mkvmerge, mkvextract, mkvalidator) and a .NET 10 SDK. Run from the repository root.
set -u
W=${1:-$HOME/onemedia/u49-texttracks/write}
mkdir -p "$W"
dotnet build src/OneMedia.Tool -c Release -o "$W/bin/tool" -v quiet -clp:NoSummary || exit 1
dotnet build tools/OneMedia.TextTrackCheck -c Release -o "$W/bin/check" -v quiet -clp:NoSummary || exit 1
TOOL="$W/bin/tool/onemedia.dll"
CHECK="$W/bin/check/OneMedia.TextTrackCheck"
O=testdata/text/oracle
for f in "$O"/*.mp4 "$O"/*.mkv "$O"/*.webm "$O"/*.mov "$O"/*.3gp; do
  b=$(basename "$f")
  for t in mkv mp4; do
    [ "$t" = mp4 ] && [[ "$b" == *.webm ]] && :
    out="$W/${b%.*}.to-$t.$t"
    rm -f "$out"
    dotnet "$TOOL" remux "$f" "$out" > "$W/${b%.*}.to-$t.log" 2>&1 || echo "REMUX-FAIL $b -> $t: $(tail -1 "$W/${b%.*}.to-$t.log")"
  done
done
echo '--- ffprobe errors (stderr lines) per output; video.* are the no-text baseline'
for o in "$W"/*.to-*.*; do
  case "$o" in *.log) continue;; esac
  echo "$(ffprobe -v error "$o" 2>&1 | wc -l) $(basename "$o")"
done
echo '--- validators'
for o in "$W"/*.to-mkv.mkv; do echo "mkvalidator $(basename "$o"): $(mkvalidator "$o" 2>&1 | tail -1)"; done
for o in "$W"/*.to-mp4.mp4; do echo "MP4Box -info $(basename "$o"): rc=$(MP4Box -quiet -info "$o" >/dev/null 2>"$W/mp4box.err"; echo $?) $(head -c 120 "$W/mp4box.err" | tr '\n' ' ')"; done
echo '--- ffmpeg read-back'
python3 tools/text-track-compare.py --tool "$CHECK" "$W"/*.to-*.mkv "$W"/*.to-*.mp4
echo '--- MP4Box / mkvextract read-back'
python3 tools/text-track-extract-check.py --tool "$CHECK" "$W"/*.to-*.mkv "$W"/*.to-*.mp4

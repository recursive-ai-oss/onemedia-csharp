#!/usr/bin/env bash
# Decodes the first audio stream of every corpus file whose codec is AAC through the adapter and prints "file<TAB>frames<TAB>rate<TAB>channels".
# Run it on two builds and diff the outputs to find changes in decoded length.
# Usage: aac-sweep.sh <OneMedia.Oracles.dll> <corpus root> > out.tsv   (dotnet and ffprobe on PATH)
set -u
DLL=$1; ROOT=$2
find "$ROOT" -type f \( -iname '*.mp4' -o -iname '*.mov' -o -iname '*.m4a' -o -iname '*.m4v' -o -iname '*.3gp' -o -iname '*.3g2' -o -iname '*.mkv' -o -iname '*.mka' -o -iname '*.webm' -o -iname '*.ts' -o -iname '*.mts' -o -iname '*.m2ts' -o -iname '*.aac' \) | sort | while read -r f; do
  c=$(ffprobe -v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 "$f" 2>/dev/null | head -1)
  [ "$c" = aac ] || continue
  r=$(timeout 120 dotnet exec "$DLL" audiopcm "$f" /dev/null 2>&1 | tail -1)
  printf '%s\t%s\n' "${f#$ROOT/}" "$r"
done

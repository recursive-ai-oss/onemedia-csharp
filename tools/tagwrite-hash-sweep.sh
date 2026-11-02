#!/usr/bin/env bash
# Edits every Matroska/WebM file of a corpus with three fixed edit sets and prints "sha256  variant  file" lines, so two builds of the
# tag writer can be compared with diff. Output files are deleted as they are hashed.
#
#   tools/tagwrite-hash-sweep.sh CHECK-BIN-DIR [CORPUS-DIR] > hashes.txt
#
# CHECK-BIN-DIR is the output directory of `dotnet build tools/OneMedia.TagWriteCheck -o DIR`. Variants: "a" the default edit set (title,
# artist, comment, cover, one field removed); "b" no removal and a long title (the file grows); "c" no removal and a one-letter title.
set -u
BIN=${1:?check bin dir}
CORPUS=${2:-$HOME/onemedia/corpus}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORK=${WORK:-$HOME/onemedia/u77-mkv-edit/sweep-work}
mkdir -p "$WORK"
LONG=$(printf 'A long edited title %.0s' $(seq 1 40))
find "$CORPUS" -type f \( -iname '*.mkv' -o -iname '*.mka' -o -iname '*.webm' \) -size -1000000k -not -path '*/_dl/*' | sort | while read -r f; do
  for v in a b c; do
    out="$WORK/out.${f##*.}"
    rm -f "$out"
    case $v in
      a) r=$(dotnet "$BIN/OneMedia.TagWriteCheck.dll" edit "$f" "$out" 2>&1 | head -c 300 | tr '\t\n' '  ') ;;
      b) r=$(TAGCHECK_NOREMOVE=1 TAGCHECK_TITLE="$LONG" dotnet "$BIN/OneMedia.TagWriteCheck.dll" edit "$f" "$out" 2>&1 | head -c 300 | tr '\t\n' '  ') ;;
      c) r=$(TAGCHECK_NOREMOVE=1 TAGCHECK_TITLE="T" dotnet "$BIN/OneMedia.TagWriteCheck.dll" edit "$f" "$out" 2>&1 | head -c 300 | tr '\t\n' '  ') ;;
    esac
    if [ -f "$out" ]; then h=$(sha256sum "$out" | cut -c1-64); else h="none"; fi
    echo "$h  $v  ${f#$CORPUS/}  ${r:0:40}"
    rm -f "$out"
  done
done

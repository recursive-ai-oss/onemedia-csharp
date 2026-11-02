#!/usr/bin/env bash
# Picks a small cross-section of the fetched corpus for the MakePlayable check: per container family, up to N files under a size cap,
# linked (not copied) into one directory that run-makeplayable.sh takes as MATRIX.
#
#   usage: pick-corpus-sample.sh OUTDIR [N per family, default 4] [MAX_MB, default 40]
#   CORPUS   corpus root, default ~/onemedia/corpus
set -euo pipefail

OUT="${1:?usage: pick-corpus-sample.sh OUTDIR [N] [MAX_MB]}"
N="${2:-4}"
MAX_MB="${3:-40}"
CORPUS="${CORPUS:-$HOME/onemedia/corpus}"

rm -rf "$OUT"
mkdir -p "$OUT"
pick() { # EXT...: the N smallest-first files of those extensions under the size cap, name prefixed with the extension family
  local fam=$1; shift
  local pattern=()
  for e in "$@"; do pattern+=(-o -iname "*.$e"); done
  find "$CORPUS" -type f \( -false "${pattern[@]}" \) -size -"${MAX_MB}"M -size +20k -printf '%s %p\n' 2>/dev/null \
    | sort -n | awk 'NR % 3 == 1' | head -n "$N" | while read -r _ f; do
      ln -s "$f" "$OUT/${fam}-$(basename "$f")"
    done
}

pick mkv mkv mka
pick webm webm
pick ts ts mts m2ts
pick avi avi
pick flv flv
pick mov mov
pick 3gp 3gp 3g2
pick mp4 mp4 m4v m4a
pick ogg ogg oga opus
pick flac flac
pick wmv wmv asf
pick mpg mpg vob
ls "$OUT" | wc -l

#!/usr/bin/env bash
# Runs the tag-writing checks on the corpus: the fixed edit set per family, verified against ffprobe, ExifTool, MediaInfo and the validators,
# and in-place saves within padding.
#
#   tools/run-tagwrite-checks.sh build
#   tools/run-tagwrite-checks.sh edit    <family> [corpus-dir]     family: iso mkv mp3 flac ogg wav aiff asf
#   tools/run-tagwrite-checks.sh inplace <family> [corpus-dir]
#   tools/run-tagwrite-checks.sh large   <file>...                 one big file each: edit with peak memory
#
# Environment: ONEMEDIA_ORACLES (default ~/onemedia/oracles/bin), OUT (default ~/onemedia/u30-tagwrite/out), JOBS (default 4),
# MAXSIZE (bytes, default 1 GiB).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
export PATH=$HOME/dotnet10:${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
OUT=${OUT:-$HOME/onemedia/u30-tagwrite/out}
WORK=${WORK:-$HOME/onemedia/u30-tagwrite/work}
BIN=$HOME/onemedia/u30-tagwrite/bin
JOBS=${JOBS:-4}
MAXSIZE=${MAXSIZE:-1073741824}
CORPUS=${3:-$HOME/onemedia/corpus}
mkdir -p "$OUT" "$WORK" "$BIN"
TOOL="dotnet $BIN/OneMedia.TagWriteCheck.dll"

exts() {
  case "$1" in
    iso) echo "mp4 m4a m4v mov 3gp 3g2" ;;
    mkv) echo "mkv mka webm" ;;
    mp3) echo "mp3" ;;
    flac) echo "flac" ;;
    ogg) echo "ogg opus oga" ;;
    wav) echo "wav" ;;
    aiff) echo "aif aiff aifc" ;;
    asf) echo "wma wmv asf" ;;
  esac
}
list_files() {
  local args=() first=1
  for e in $(exts "$1"); do
    [ $first = 1 ] || args+=(-o)
    args+=(-iname "*.$e"); first=0
  done
  find "$CORPUS" -type f \( "${args[@]}" \) -size -"$((MAXSIZE / 1024))"k -not -path '*/_dl/*' | sort
}

case "${1:-}" in
build)
  cd "$HERE" && dotnet build tools/OneMedia.TagWriteCheck -c Release -v quiet -clp:NoSummary -m:4 -o "$BIN" 2>&1 | tail -5
  ;;
edit)
  fam=${2:?family}
  list_files "$fam" > "$OUT/$fam.list"
  echo "$fam: $(wc -l < "$OUT/$fam.list") files"
  nice python3 "$HERE/tools/tagwrite-verify.py" --tool "$TOOL" --work "$WORK/$fam" --jobs "$JOBS" --detail "$OUT/$fam.detail.jsonl" "@$OUT/$fam.list" > "$OUT/$fam.tsv" 2> "$OUT/$fam.summary"
  cat "$OUT/$fam.summary"
  ;;
inplace)
  fam=${2:?family}
  list_files "$fam" > "$OUT/$fam.list"
  : > "$OUT/$fam.inplace.tsv"
  while read -r f; do nice $TOOL inplace "$f" "$WORK/inplace" >> "$OUT/$fam.inplace.tsv" 2>&1; done < "$OUT/$fam.list"
  echo "$fam in-place:"; cut -f3,4 "$OUT/$fam.inplace.tsv" | sed 's/(need [0-9]*, have [0-9]*)//;s/([0-9]*B)//' | sort | uniq -c | sort -rn | head
  ;;
large)
  shift
  for f in "$@"; do
    /usr/bin/time -v $TOOL edit "$f" "$WORK/large.out" 2>&1 | grep -E '^EDIT|^ERROR|Maximum resident' | cut -c1-300
    rm -f "$WORK/large.out"
  done
  ;;
*) echo "usage: $0 build|edit|inplace|large" >&2; exit 2 ;;
esac

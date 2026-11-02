#!/usr/bin/env bash
# Runs the capture-time checks: the time is set in each file, then ExifTool, MediaInfo and ffprobe are read on the result (see datewrite-verify.py).
#
#   tools/run-datewrite-checks.sh build
#   tools/run-datewrite-checks.sh run <family> [corpus-dir]     family: iso mkv mp3 flac ogg wav asf fixtures
#
# Environment: ONEMEDIA_ORACLES (default ~/onemedia/oracles/bin), OUT (default ~/onemedia/u45-datewrite/out), JOBS (default 4), LIMIT (files per family,
# default 60), MAXSIZE (bytes, default 512 MiB), WHEN (default 2024-05-01T12:30:00+02:00), HEADER_TIMES (utc or local, default unset).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
export PATH=$HOME/dotnet10:${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
OUT=${OUT:-$HOME/onemedia/u45-datewrite/out}
WORK=${WORK:-$HOME/onemedia/u45-datewrite/work}
BIN=${BIN:-$HOME/onemedia/u45-datewrite/bin}
JOBS=${JOBS:-4}
LIMIT=${LIMIT:-60}
MAXSIZE=${MAXSIZE:-536870912}
WHEN=${WHEN:-2024-05-01T12:30:00+02:00}
CORPUS=${3:-$HOME/onemedia/corpus}
mkdir -p "$OUT" "$WORK" "$BIN"
TOOL="dotnet $BIN/OneMedia.DateWriteCheck.dll"

exts() {
  case "$1" in
    iso) echo "mp4 m4a m4v mov 3gp 3g2" ;;
    mkv) echo "mkv mka webm" ;;
    mp3) echo "mp3" ;;
    flac) echo "flac" ;;
    ogg) echo "ogg opus oga" ;;
    wav) echo "wav" ;;
    asf) echo "wma wmv asf" ;;
  esac
}
list_files() {
  if [ "$1" = fixtures ]; then ls "$HERE"/testdata/datewrite/* "$HERE"/testdata/metadata/*.mp4 "$HERE"/testdata/metadata/*.mov "$HERE"/testdata/metadata/*.3gp; return; fi
  local args=() first=1
  for e in $(exts "$1"); do
    [ $first = 1 ] || args+=(-o)
    args+=(-iname "*.$e"); first=0
  done
  find "$CORPUS" -type f \( "${args[@]}" \) -size -"$((MAXSIZE / 1024))"k -not -path '*/_dl/*' | sort | awk -v n="$LIMIT" 'BEGIN{srand(7)} {a[NR]=$0} END{step=(NR>n)?NR/n:1; for(i=1;i<=NR && c<n;i+=step){print a[int(i)]; c++}}'
}

case "${1:-}" in
build)
  cd "$HERE" && dotnet build tools/OneMedia.DateWriteCheck -c Release -p:NuGetAudit=false -v quiet -clp:NoSummary -m:4 -o "$BIN" 2>&1 | tail -5
  ;;
run)
  fam=${2:?family}
  list_files "$fam" > "$OUT/$fam.list"
  echo "$fam: $(wc -l < "$OUT/$fam.list") files"
  nice python3 "$HERE/tools/datewrite-verify.py" --tool "$TOOL" --work "$WORK/$fam" --when "$WHEN" ${HEADER_TIMES:+--header-times "$HEADER_TIMES"} --jobs "$JOBS" --detail "$OUT/$fam.detail.jsonl" "@$OUT/$fam.list" > "$OUT/$fam.tsv" 2> "$OUT/$fam.summary"
  cat "$OUT/$fam.summary"
  ;;
*)
  echo "usage: $0 build | run <family> [corpus-dir]" >&2
  exit 2
  ;;
esac

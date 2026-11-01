#!/usr/bin/env bash
# Runs the structure-writer checks on a corpus: untouched round trips, minimal metadata edits verified against ffprobe, MP4Box and
# mkvalidator, and in-place edits within padding. Results are tab-separated files under $OUT.
#
#   tools/run-writer-checks.sh build
#   tools/run-writer-checks.sh roundtrip   [corpus-dir]
#   tools/run-writer-checks.sh edit        [corpus-dir]
#   tools/run-writer-checks.sh inplace     [corpus-dir]
#   tools/run-writer-checks.sh large       <file>...       one big file each: edit with peak memory
#   tools/run-writer-checks.sh kill        <file> [rounds] kill -9 an atomic Save at several points
#
# Environment: ONEMEDIA_ORACLES (default ~/onemedia/oracles/bin), OUT (default ~/onemedia/u25-writer/out), JOBS (default 6),
# MAXSIZE (bytes, default 1 GiB: larger files are left to the "large" stage).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
export PATH=$HOME/dotnet10:${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
OUT=${OUT:-$HOME/onemedia/u25-writer/out}
WORK=${WORK:-$HOME/onemedia/u25-writer/work}
JOBS=${JOBS:-6}
MAXSIZE=${MAXSIZE:-1073741824}
CORPUS=${2:-$HOME/onemedia/corpus}
BIN=$HOME/onemedia/u25-writer/bin
mkdir -p "$OUT" "$WORK" "$BIN"
TOOL="dotnet $BIN/OneMedia.WriterCheck.dll"

list_files() { # family files up to MAXSIZE; the corpus's own large/ directories are handled by the large stage
  find "$CORPUS" -type f \( -iname '*.mp4' -o -iname '*.m4a' -o -iname '*.m4v' -o -iname '*.mov' -o -iname '*.3gp' -o -iname '*.3g2' \
    -o -iname '*.mkv' -o -iname '*.mka' -o -iname '*.webm' -o -iname '*.wav' -o -iname '*.aif' -o -iname '*.aiff' -o -iname '*.aifc' \
    -o -iname '*.w64' -o -iname '*.rf64' -o -iname '*.avi' \) -size -"$((MAXSIZE / 1024))"k -not -path '*/_dl/*' | sort
}

case "${1:-}" in
build)
  cd "$HERE" && dotnet build tools/OneMedia.WriterCheck -c Release -v quiet -clp:NoSummary -m:4 -o "$BIN" 2>&1 | tail -5
  ;;
roundtrip)
  list_files > "$OUT/files.list"
  wc -l < "$OUT/files.list"
  $TOOL roundtrip @"$OUT/files.list" > "$OUT/roundtrip.tsv" 2> "$OUT/roundtrip.err"
  awk -F'\t' '{k=$1" "$2; n[k]++} END {for (k in n) print n[k], k}' "$OUT/roundtrip.tsv" | sort -k2
  ;;
edit)
  list_files > "$OUT/files.list"
  one() {
    f=$1
    case "$f" in *.avi) exit 0 ;; esac # AVI indexes hold absolute offsets: not an edit target in this unit
    b=$(echo "$f" | md5sum | cut -c1-8)_$(basename "$f")
    o="$WORK/$b"
    r=$($TOOL edit "$f" "$o" 2>/dev/null | head -1)
    status=${r%%$'\t'*}
    if [ "$status" != OK ]; then printf 'EDITFAIL\t%s\t%s\n' "$f" "$r"; rm -f "$o"; exit 0; fi
    # 1. our per-track payload hash, in and out
    hi=$($TOOL hash "$f" 2>/dev/null | md5sum | cut -c1-12); ho=$($TOOL hash "$o" 2>/dev/null | md5sum | cut -c1-12)
    # 2. ffprobe packets (size, times, flags, data hash), in and out
    fp() { ffprobe -v error -show_packets -show_data_hash CRC32 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash -of csv=p=0 "$1" 2>&1 | sed -E "s#$1##g; s/@ 0x[0-9a-f]+//" | md5sum | cut -c1-12; }
    pi=$(fp "$f"); po=$(fp "$o")
    # 3. the format's own validator
    v="-"
    case "${f,,}" in
      *.mp4|*.m4a|*.m4v|*.mov|*.3gp|*.3g2)
        norm() { MP4Box -dts "$1" 2>&1 | sed -E "s#$1##g; s/Offset [0-9]+//; s/start [0-9]+//" | md5sum | cut -c1-12; }
        mi=$(norm "$f"); mo=$(norm "$o")
        MP4Box -info "$f" > /dev/null 2>&1; rci=$?; MP4Box -info "$o" > /dev/null 2>&1; rc=$?
        v="mp4box dts $([ "$mi" = "$mo" ] && echo equal || echo DIFF) info-rc in=$rci out=$rc"; [ $rc -le $rci ] || v="$v DIFF" ;;
      *.mkv|*.mka|*.webm)
        mkvalidator "$f" > /dev/null 2>&1; ri=$?; mkvalidator "$o" > "$WORK/$b.val" 2>&1; ro=$?
        v="mkvalidator in=$ri out=$ro $([ $ro -le $ri ] && echo ok || echo WORSE)"; rm -f "$WORK/$b.val" ;;
    esac
    ok=OK; [ "$hi" = "$ho" ] || ok=HASHDIFF; [ "$pi" = "$po" ] || ok=FFPROBEDIFF
    case "$v" in *DIFF*|*WORSE*) ok=VALIDATORDIFF ;; esac
    # Files that already carry structural errors (cut-off downloads, crafted damage) are edited best-effort; their differences are classed apart.
    if [ "$ok" != OK ] && [[ "$r" =~ inputErrors=([1-9][0-9]*) ]]; then ok="DAMAGED-INPUT:$ok"; fi
    printf '%s\t%s\tour-hash=%s\tffprobe=%s\t%s\t%s\n' "$ok" "$f" "$([ "$hi" = "$ho" ] && echo equal || echo DIFF)" "$([ "$pi" = "$po" ] && echo equal || echo DIFF)" "$v" "$r" | cut -c1-600
    rm -f "$o"
  }
  export -f one; export TOOL WORK
  xargs -a "$OUT/files.list" -d '\n' -P "$JOBS" -I{} bash -c 'one "$@"' _ {} > "$OUT/edit.tsv" 2> "$OUT/edit.err"
  awk -F'\t' '{print $1}' "$OUT/edit.tsv" | sort | uniq -c
  ;;
inplace)
  list_files > "$OUT/files.list"
  grep -vi '\.avi$' "$OUT/files.list" > "$OUT/files-noavi.list"
  one() { d="$WORK/ip-$(echo "$1" | md5sum | cut -c1-8)"; r=$($TOOL inplace "$1" "$d" 2>/dev/null | head -1 | cut -c1-500 | tr -d '\000-\010'); printf '%s\t%s\n' "$r" "$1"; rm -rf "$d"; }
  export -f one; export TOOL WORK
  xargs -a "$OUT/files-noavi.list" -d '\n' -P "$JOBS" -I{} bash -c 'one "$@"' _ {} > "$OUT/inplace.tsv" 2> "$OUT/inplace.err"
  awk -F'\t' '{print $1" "$2}' "$OUT/inplace.tsv" | sort | uniq -c
  ;;
large)
  shift
  for f in "$@"; do
    o="$WORK/large-$(basename "$f")"
    /usr/bin/time -v $TOOL edit "$f" "$o" > "$OUT/large-$(basename "$f").out" 2> "$OUT/large-$(basename "$f").time"
    cat "$OUT/large-$(basename "$f").out" | cut -c1-400
    grep -E 'Maximum resident|Elapsed' "$OUT/large-$(basename "$f").time"
    rm -f "$o"
  done
  ;;
kill)
  f=$2; rounds=${3:-12}
  want=$(sha256sum "$f" | cut -d' ' -f1)
  d="$WORK/kill"; rm -rf "$d"; mkdir -p "$d"
  # What a Save that is allowed to finish produces (the result is deterministic).
  cp "$f" "$d/target.${f##*.}"; $TOOL save "$d/target.${f##*.}" > /dev/null 2>&1; finished=$(sha256sum "$d/target.${f##*.}" | cut -d' ' -f1); rm -rf "$d"; mkdir -p "$d"
  for i in $(seq 1 "$rounds"); do
    cp "$f" "$d/target.bin"; mv "$d/target.bin" "$d/target.${f##*.}"; t="$d/target.${f##*.}"
    $TOOL save "$t" > /dev/null 2>&1 &
    pid=$!
    sleep "0.$(printf '%02d' $((RANDOM % 90 + 5)))"
    kill -9 $pid 2>/dev/null; wait $pid 2>/dev/null
    now=$(sha256sum "$t" | cut -d' ' -f1)
    stray=$(find "$d" -name '.*.tmp' | wc -l); others=$(find "$d" -type f -not -name 'target.*' -not -name '.*.tmp' | wc -l)
    state=CORRUPT; [ "$now" = "$want" ] && state=original-intact; [ "$now" = "$finished" ] && state=save-completed
    echo -e "round $i\t$state\tstray-temp-files=$stray\tother-files=$others"
    rm -rf "$d"; mkdir -p "$d"
  done
  ;;
*)
  sed -n '2,12p' "$0"; exit 2 ;;
esac

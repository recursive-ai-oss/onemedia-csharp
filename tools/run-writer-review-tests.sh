#!/usr/bin/env bash
# Runs the writer review tests on a machine with the .NET 10 SDK and the pinned oracles, then compares every kept input/output pair.
#
#   tools/run-writer-review-tests.sh tests   build and run WriterReviewTests; every input/output pair is kept in $W/pairs
#   tools/run-writer-review-tests.sh pairs   per pair: ffprobe packet hashes (equal?), ffprobe/MP4Box/mkvalidator errors, ExifTool warnings
#
# Environment: ONEMEDIA_ORACLES (default ~/onemedia/oracles/bin), W (work dir, default ~/onemedia/writer-review/w).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
export PATH=$HOME/dotnet10:${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
W=${W:-$HOME/onemedia/writer-review/w}
mkdir -p "$W"

case "${1:-}" in
tests)
  export TMPDIR=$W/tmp ONEMEDIA_REVIEW_OUT=$W/pairs
  mkdir -p "$TMPDIR"; rm -rf "$ONEMEDIA_REVIEW_OUT"
  cd "$HERE" && dotnet build tests/OneMedia.Tests -c Release -v quiet -clp:NoSummary -m:4 -o "$W/tbin" 2>&1 | grep -E 'error|warn' | sort -u | head -20
  dotnet exec "$W/tbin/RecursiveAI.OneMedia.Tests.dll" -class '*WriterReviewTests*' ${METHOD:+-method "$METHOD"} > "$W/review.log" 2>&1
  tail -3 "$W/review.log"
  echo "refusals: $(grep -c '^REFUSED' "$W/review.log")  pairs: $(ls "$W/pairs" | grep -c '\.out\.')"
  ;;
pairs)
  out=$W/pairs.tsv; : > "$out"
  one() {
    o=$1; i=${o/.out./.in.}; n=$(basename "$o")
    pk() { ffprobe -v error -show_packets -show_data_hash CRC32 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash -of csv=p=0 "$1" 2>/dev/null | md5sum | cut -c1-10; }
    ie=$(ffprobe -v error -show_format "$i" 2>&1 >/dev/null | wc -l); oe=$(ffprobe -v error -show_format "$o" 2>&1 >/dev/null | wc -l)
    pi=$(pk "$i"); po=$(pk "$o")
    case "$o" in
      *.mp4|*.mov|*.m4a|*.3gp) vi=$(MP4Box -info "$i" 2>&1 | grep -ciE 'error|invalid|corrupt'); vo=$(MP4Box -info "$o" 2>&1 | grep -ciE 'error|invalid|corrupt') ;;
      *.mkv|*.mka|*.webm) mkvalidator "$i" >/dev/null 2>&1; vi=$?; mkvalidator "$o" >/dev/null 2>&1; vo=$? ;;
      *) vi=0; vo=0 ;;
    esac
    wi=$(exiftool -a -Warning -Error "$i" 2>&1 | wc -l); wo=$(exiftool -a -Warning -Error "$o" 2>&1 | wc -l)
    if [ "$pi" = "$po" ]; then eq=EQUAL; else eq="DIFFER"; fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$n" "$ie" "$oe" "$eq" "$vi" "$vo" "$wi" "$wo" "$pi"
  }
  export -f one
  ls "$W"/pairs/*.out.* | xargs -P 4 -I{} bash -c 'one {}' >> "$out"
  echo "pairs: $(wc -l < "$out")"
  echo "packets differ: $(awk -F'\t' '$4=="DIFFER"' "$out" | wc -l)"
  echo "ffprobe errors worse: $(awk -F'\t' '$3>$2' "$out" | wc -l)"
  echo "validator worse: $(awk -F'\t' '$6>$5' "$out" | wc -l)"
  echo "exiftool warnings worse: $(awk -F'\t' '$8>$7' "$out" | wc -l)"
  ;;
*) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

#!/usr/bin/env bash
# Moves the movie box to the front of every progressive ISO file of a corpus and checks the result:
#   - the output has the same length, and moov precedes mdat;
#   - the per-track payload hash (OneMedia.WriterCheck hash) is equal for input and output;
#   - ffprobe lists the same packets (pts/dts/duration/size/flags/data hash) for input and output.
#
#   ORACLES=<dir with ffprobe> CORPUS=<dir> WORK=<scratch dir> [MAXKB=...] tools/run-faststart-check.sh [files...]
# One TSV line per file in $WORK/faststart/summary.tsv: file, faststart result, sizes, payload hash and layout, ffprobe packet listing.
set -u
ORACLES=${ORACLES:?}; CORPUS=${CORPUS:?}; WORK=${WORK:?}; MAXKB=${MAXKB:-8388608}
OUT="$WORK/faststart"; mkdir -p "$OUT"
export ONEMEDIA_SCRATCH="$WORK" PATH="$ORACLES:$PATH"
dotnet build tools/OneMedia.RemuxCheck -c Release -v quiet -clp:NoSummary || exit 1
dotnet build tools/OneMedia.WriterCheck -c Release -v quiet -clp:NoSummary || exit 1
R="$WORK/artifacts/bin/OneMedia.RemuxCheck/release/OneMedia.RemuxCheck.dll"
H="$WORK/artifacts/bin/OneMedia.WriterCheck/release/OneMedia.WriterCheck.dll"
if [ $# -gt 0 ]; then LIST=$(printf '%s\n' "$@"); else
  LIST=$(find "$CORPUS" -type f \( -iname '*.mp4' -o -iname '*.mov' -o -iname '*.m4a' -o -iname '*.m4v' -o -iname '*.3gp' -o -iname '*.3g2' \) -size -"${MAXKB}"k | sort)
fi
: > "$OUT/summary.tsv"
echo "$LIST" | while IFS= read -r f; do
  o="$OUT/out.mp4"; rm -f "$o"
  line=$(dotnet "$R" faststart "$f" "$o" 2>&1 | head -1 | cut -f2-)
  case "$line" in
    ok*relocated=True*)
      a=$(dotnet "$H" hash "$f" 2>/dev/null | grep -v peak | sha256sum | cut -c1-16)
      b=$(dotnet "$H" hash "$o" 2>/dev/null | grep -v peak | sha256sum | cut -c1-16)
      same=$([ "$a" = "$b" ] && echo hash-equal || echo HASH-DIFFERENT)
      sa=$(stat -c %s "$f"); sb=$(stat -c %s "$o")
      order=$(python3 - "$o" <<'P'
import struct,sys
f=open(sys.argv[1],'rb');f.seek(0,2);n=f.tell();p=0;seen=[]
while p<n:
    f.seek(p);h=f.read(16)
    if len(h)<8:break
    s,t=struct.unpack('>I4s',h[:8])
    if s==1:s=struct.unpack('>Q',h[8:16])[0]
    if s==0:s=n-p
    seen.append(t.decode('latin1'))
    if s<8:break
    p+=s
print('moov-first' if 'moov' in seen and 'mdat' in seen and seen.index('moov')<seen.index('mdat') else 'moov-NOT-first')
P
)
      # Only the movie box moved, so ffprobe's whole packet listing (stream, pts, dts, duration, size, flags, data hash) must be identical.
      pa=$(ffprobe -v error -show_packets -show_data_hash sha256 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash -of csv=p=0 "$f" 2>&1 | sha256sum | cut -c1-16)
      pb=$(ffprobe -v error -show_packets -show_data_hash sha256 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash -of csv=p=0 "$o" 2>&1 | sha256sum | cut -c1-16)
      cmp=$([ "$pa" = "$pb" ] && echo ffprobe-packets-identical || echo FFPROBE-PACKETS-DIFFER)
      printf '%s\t%s\tsize=%s/%s\t%s\t%s\n' "${f#"$CORPUS"/}" "$line" "$sa" "$sb" "$same $order" "$cmp" >> "$OUT/summary.tsv" ;;
    *) printf '%s\t%s\n' "${f#"$CORPUS"/}" "$line" >> "$OUT/summary.tsv" ;;
  esac
done
rm -f "$OUT/out.mp4"
echo "files: $(wc -l < "$OUT/summary.tsv")"

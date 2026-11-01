#!/usr/bin/env bash
# Copies every ISO file of a corpus through the ISO muxer and compares each copy with its input: packets through ffprobe
# (tools/mux-compare.py), structure through MP4Box -info, and a black-box decode through ffmpeg.
#
#   ORACLES=<dir with ffmpeg ffprobe MP4Box> CORPUS=<dir> WORK=<scratch dir> [LAYOUT=first|last|frag] [OPTS="--cmaf --sidx --mfra"] \
#     [MAXKB=1048576] [JOBS=6] tools/run-mux-diff.sh [files...]
#
# Prints one TSV line per file under $WORK/<tag>/summary.tsv:
#   file, copy status, ffprobe packets compared / matched / different, MP4Box new messages, ffmpeg new messages
# The copy of every file is deleted once compared unless KEEP=1.
set -u
ORACLES=${ORACLES:?directory holding ffmpeg, ffprobe and MP4Box}
CORPUS=${CORPUS:?corpus directory}
WORK=${WORK:?scratch directory}
LAYOUT=${LAYOUT:-first}
OPTS=${OPTS:-}
MAXKB=${MAXKB:-1048576}
JOBS=${JOBS:-6}
TAG=${TAG:-$LAYOUT$(echo "$OPTS" | tr -d ' -')}
OUT="$WORK/$TAG"
mkdir -p "$OUT"
export ONEMEDIA_SCRATCH="$WORK"
export PATH="$ORACLES:$PATH"
dotnet build tools/OneMedia.MuxCheck -c Release -v quiet -clp:NoSummary || exit 1
TOOL="$WORK/artifacts/bin/OneMedia.MuxCheck/release/OneMedia.MuxCheck.dll"
ffprobe -version | head -1
MP4Box -version 2>&1 | head -1

one() {
  f=$1
  base=$(echo "${f#"$CORPUS"/}" | tr '/' '_')
  o="$OUT/$base.mp4"
  copy=$(dotnet "$TOOL" copy "$f" "$o" --layout "$LAYOUT" $OPTS 2>&1 | head -1 | cut -f2- | tr '\t' ' ')
  if [ -f "$o.map" ] && [ -s "$o" ]; then
    cmp=$(FRAGMENTED=$([ "$LAYOUT" = frag ] && echo 1 || echo 0) python3 tools/mux-compare.py "$ORACLES/ffprobe" "$f" "$o" | head -1 | cut -f2-)
    # Messages MP4Box prints for the output that it does not print for the input (file names removed).
    MP4Box -info "$f" 2>&1 | grep -iE 'error|warning|invalid|fail' | sed "s|$f||g" | sort -u > "$o.in.mp4box"
    MP4Box -info "$o" 2>&1 | grep -iE 'error|warning|invalid|fail' | sed "s|$o||g" | sort -u > "$o.out.mp4box"
    newbox=$(comm -13 "$o.in.mp4box" "$o.out.mp4box" | wc -l)
    ffmpeg -threads 1 -v error -i "$f" -f null - 2>&1 | sort -u > "$o.in.ffmpeg"
    ffmpeg -threads 1 -v error -i "$o" -f null - 2>&1 | sort -u > "$o.out.ffmpeg"
    newff=$(comm -13 "$o.in.ffmpeg" "$o.out.ffmpeg" | wc -l)
    printf '%s\t%s\t%s\tmp4box_new=%s\tffmpeg_new=%s\n' "${f#"$CORPUS"/}" "$copy" "$cmp" "$newbox" "$newff"
  else
    printf '%s\t%s\tNOT-WRITTEN\n' "${f#"$CORPUS"/}" "$copy"
  fi
  [ "${KEEP:-0}" = 1 ] || rm -f "$o" "$o.map" "$o.pkts"
}
export -f one
export CORPUS OUT LAYOUT OPTS TOOL ORACLES

if [ $# -gt 0 ]; then LIST=$(printf '%s\n' "$@"); else
  LIST=$(find "$CORPUS" -type f \( -iname '*.mp4' -o -iname '*.mov' -o -iname '*.m4a' -o -iname '*.m4v' -o -iname '*.3gp' -o -iname '*.3g2' -o -iname '*.m4s' -o -iname '*.f4v' \) -size -"${MAXKB}"k | sort)
fi
echo "$LIST" | xargs -P "$JOBS" -I{} bash -c 'one "$@"' _ {} | sort | tee "$OUT/summary.tsv"
echo "files: $(wc -l < "$OUT/summary.tsv")"

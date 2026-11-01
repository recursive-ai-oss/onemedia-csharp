#!/usr/bin/env bash
# Remuxes every file of a corpus into MP4 (or another target) with OneMedia.RemuxCheck and compares each result with its input: packets through
# ffprobe (tools/remux-compare.py), structure through MP4Box -info (MKV: mkvalidator), and a black-box decode through ffmpeg.
#
#   ORACLES=<dir with ffmpeg ffprobe MP4Box [mkvalidator]> CORPUS=<dir> WORK=<scratch dir> [FORMAT=mp4|m4a|mov|3gp|fmp4|mkv|webm] [OPTS="--no-faststart"] \
#     [MAXKB=1048576] [REMUXJOBS=6] [RESUME=1] [KEEP=1] tools/run-remux-diff.sh [files...]
#
# One TSV line per file in $WORK/<tag>/summary.tsv: file, remux status line, ffprobe packets compared / matched / different, structure-check
# messages new in the output, ffmpeg messages new in the output. The result file is deleted once compared unless KEEP=1.
set -u
ORACLES=${ORACLES:?directory holding ffmpeg, ffprobe and MP4Box}
CORPUS=${CORPUS:?corpus directory}
WORK=${WORK:?scratch directory}
FORMAT=${FORMAT:-mp4}
OPTS=${OPTS:-}
MAXKB=${MAXKB:-1048576}
JOBS=${REMUXJOBS:-6}  # not JOBS: the work queue sets that
TAG=${TAG:-$FORMAT$(echo "$OPTS" | tr -d ' -')}
OUT="$WORK/$TAG"
mkdir -p "$OUT"
export ONEMEDIA_SCRATCH="$WORK"
export PATH="$ORACLES:$PATH"
dotnet build tools/OneMedia.RemuxCheck -c Release -v quiet -clp:NoSummary || exit 1
TOOL="$WORK/artifacts/bin/OneMedia.RemuxCheck/release/OneMedia.RemuxCheck.dll"
ffprobe -version | head -1
MP4Box -version 2>&1 | head -1
EXT=$FORMAT; [ "$FORMAT" = fmp4 ] && EXT=mp4; [ "$FORMAT" = webm ] || [ "$FORMAT" = mkv ] || true

messages() {  # structure-check messages of a file, one per line, names removed
  case "$FORMAT" in
    mkv|webm) mkvalidator --no-warn "$1" 2>&1 | grep -iE 'error|warning|invalid' | sed "s|$1||g" | sort -u ;;
    *) MP4Box -info "$1" 2>&1 | grep -iE 'error|warning|invalid|fail' | sed "s|$1||g" | sort -u ;;
  esac
}

one() {
  f=$1
  base=$(echo "${f#"$CORPUS"/}" | tr '/' '_')
  o="$OUT/$base.$EXT"
  copy=$(dotnet "$TOOL" remux "$f" "$o" --format "$FORMAT" $OPTS 2>&1 | head -1 | cut -f2- | tr '\t' ' ')
  if [ -f "$o.map" ] && [ -s "$o" ]; then
    cmp=$(FRAGMENTED=$([ "$FORMAT" = fmp4 ] && echo 1 || echo 0) python3 tools/remux-compare.py "$ORACLES/ffprobe" "$f" "$o" | head -1 | cut -f2-)
    ic="${CACHE:-$o}"
    if [ -n "${CACHE:-}" ]; then mkdir -p "$CACHE"; ic="$CACHE/$base"; fi
    if [ ! -f "$ic.in.done" ]; then
      messages "$f" > "$ic.in.struct"
      ffmpeg -threads 1 -v error -i "$f" -f null - 2>&1 | sed "s/0x[0-9a-f]*/0xADDR/g" | sort -u > "$ic.in.ffmpeg"
      touch "$ic.in.done"
    fi
    messages "$o" > "$o.out.struct"
    newstruct=$(comm -13 "$ic.in.struct" "$o.out.struct" | wc -l)
    ffmpeg -threads 1 -v error -i "$o" -f null - 2>&1 | sed "s/0x[0-9a-f]*/0xADDR/g" | sort -u > "$o.out.ffmpeg"
    newff=$(comm -13 "$ic.in.ffmpeg" "$o.out.ffmpeg" | wc -l)
    printf '%s\t%s\t%s\tstruct_new=%s\tffmpeg_new=%s\n' "${f#"$CORPUS"/}" "$copy" "$cmp" "$newstruct" "$newff"
  else
    printf '%s\t%s\tNOT-WRITTEN\n' "${f#"$CORPUS"/}" "$copy"
  fi
  [ "${KEEP:-0}" = 1 ] || rm -f "$o" "$o.map" "$o.pkts"
}
export -f one messages
export CORPUS OUT FORMAT OPTS TOOL ORACLES CACHE EXT

if [ $# -gt 0 ]; then LIST=$(printf '%s\n' "$@"); else
  LIST=$(find "$CORPUS" -type f \( -iname '*.mp4' -o -iname '*.mov' -o -iname '*.m4a' -o -iname '*.m4v' -o -iname '*.3gp' -o -iname '*.3g2' -o -iname '*.mkv' -o -iname '*.mka' -o -iname '*.webm' \
    -o -iname '*.ts' -o -iname '*.m2ts' -o -iname '*.mts' -o -iname '*.mpg' -o -iname '*.vob' -o -iname '*.flv' -o -iname '*.avi' -o -iname '*.ogg' -o -iname '*.oga' -o -iname '*.opus' -o -iname '*.264' -o -iname '*.265' -o -iname '*.m2v' -o -iname '*.m1v' \
    -o -iname '*.ivf' -o -iname '*.aac' -o -iname '*.ac3' -o -iname '*.eac3' -o -iname '*.mp3' -o -iname '*.mp2' -o -iname '*.amr' -o -iname '*.awb' -o -iname '*.wav' -o -iname '*.aif' -o -iname '*.aiff' -o -iname '*.aifc' -o -iname '*.caf' -o -iname '*.flac' \
    -o -iname '*.asf' -o -iname '*.wmv' -o -iname '*.wma' -o -iname '*.w64' -o -iname '*.dv' -o -iname '*.obu' \) -size -"${MAXKB}"k | sort)
fi
touch "$OUT/summary.part"
[ "${RESUME:-0}" = 1 ] || : > "$OUT/summary.part"
if [ "${RESUME:-0}" = 1 ]; then
  LIST=$(echo "$LIST" | while read -r f; do grep -qF "${f#"$CORPUS"/}	" "$OUT/summary.part" || echo "$f"; done)
fi
echo "$LIST" | xargs -P "${JOBS}" -I{} bash -c 'one "$@"' _ {} >> "$OUT/summary.part"
sort "$OUT/summary.part" > "$OUT/summary.tsv"
echo "files: $(wc -l < "$OUT/summary.tsv")"

#!/usr/bin/env bash
# Generates AVI and DV test files with the pinned ffmpeg from synthetic input (lavfi test sources), plus damaged variants cut
# from them, into $ONEMEDIA_HOME/corpus/generated/avi and appends to generated/MANIFEST.tsv like tools/corpus/generate.sh.
#
#   generate.sh [FILTER]   FILTER is a grep pattern on the output path (default: everything)
#   LARGE=0 generate.sh    skip the multi-gigabyte OpenDML files
set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
O="${ONEMEDIA_HOME:-$HOME/onemedia}"
G="$O/corpus/generated"
FF="${FFMPEG:-$O/oracles/bin/ffmpeg}"
FILTER="${1:-.}"
mkdir -p "$G/avi" "$G/dv"
MAN="$G/MANIFEST.tsv"
[ -s "$MAN" ] || printf 'path\ttool\tsha256\tsize\tcommand\n' > "$MAN"
ffv=$("$FF" -version | head -1 | cut -d' ' -f1-3)
ok=0; failed=0; skipped=0
FFG=(-hide_banner -loglevel error -y)
FFO=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1)
V320=(-f lavfi -i "testsrc2=size=320x240:rate=30:duration=3")
V320B=(-f lavfi -i "testsrc2=size=320x240:rate=25:duration=3")
V_NTSC=(-f lavfi -i "testsrc2=size=720x480:rate=30000/1001:duration=3")
V_PAL=(-f lavfi -i "testsrc2=size=720x576:rate=25:duration=3")
S1=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=3")
S2=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=3,aformat=channel_layouts=stereo")
S441=(-f lavfi -i "sine=frequency=330:sample_rate=44100:duration=3")

record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$(sha256sum "$G/$1" | cut -d' ' -f1)" "$(stat -c %s "$G/$1")" "$3" >> "$MAN"; }
want() { echo "$1" | grep -qE "$FILTER"; }

# ff REL ARGS...: runs ffmpeg writing G/REL (the output path is appended).
ff() {
  local rel=$1; shift
  want "$rel" || return 0
  local out="$G/$rel"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  mkdir -p "$(dirname "$out")"
  if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "benchmark lock present; stopping" >&2; exit 3; fi
  local part="${out%.*}.part.${out##*.}"
  if "$FF" "${FFG[@]}" "$@" "$part" 2>"$G/.last.log" && [ -s "$part" ]; then
    mv "$part" "$out"; record "$rel" "$ffv" "ffmpeg $*"; ok=$((ok+1))
  else
    rm -f "$part"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2
  fi
}

# derive REL DESCRIPTION COMMAND...: writes G/REL by running COMMAND with @OUT@ replaced by the path.
derive() {
  local rel=$1 what=$2; shift 2
  want "$rel" || return 0
  local out="$G/$rel"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  mkdir -p "$(dirname "$out")"
  local args=() a; for a in "$@"; do args+=("${a//@OUT@/$out.part}"); done
  if "${args[@]}" 2>"$G/.last.log" && [ -s "$out.part" ]; then mv "$out.part" "$out"; record "$rel" derived "$what"; ok=$((ok+1))
  else rm -f "$out.part"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2; fi
}

cut_bytes() { head -c "$2" "$1" > "$3"; }  # cut_bytes IN BYTES OUT
idx1_offset() { python3 - "$1" <<'PY'
import sys
d = open(sys.argv[1], "rb").read()
print(d.rindex(b"idx1"))
PY
}

# --- AVI written by ffmpeg ---
ff avi/mpeg4-mp3.avi "${V320[@]}" "${S1[@]}" -c:v mpeg4 -q:v 5 -g 30 -bf 0 -c:a libmp3lame -b:a 96k "${FFO[@]}" -f avi
ff avi/mpeg4-bframes-mp3.avi "${V320[@]}" "${S1[@]}" -c:v mpeg4 -q:v 5 -g 30 -bf 2 -c:a libmp3lame -b:a 96k "${FFO[@]}" -f avi
ff avi/mpeg4-vbr-mp3.avi "${V320[@]}" "${S441[@]}" -c:v mpeg4 -q:v 5 -g 25 -c:a libmp3lame -q:a 4 "${FFO[@]}" -f avi
ff avi/mjpeg-pcm.avi "${V320[@]}" "${S1[@]}" -c:v mjpeg -c:a pcm_s16le "${FFO[@]}" -f avi
ff avi/h264-aac.avi "${V320[@]}" "${S1[@]}" -c:v libx264 -preset veryfast -threads 1 -g 30 -bf 0 -pix_fmt yuv420p -c:a aac -b:a 96k "${FFO[@]}" -f avi
ff avi/h264-bframes-ac3.avi "${V320[@]}" "${S2[@]}" -c:v libx264 -preset veryfast -threads 1 -g 30 -bf 2 -pix_fmt yuv420p -c:a ac3 -b:a 128k "${FFO[@]}" -f avi
ff avi/msmpeg4v2-adpcm.avi "${V320[@]}" "${S1[@]}" -c:v msmpeg4v2 -q:v 5 -c:a adpcm_ms "${FFO[@]}" -f avi
ff avi/rawvideo-pcm24.avi "${V320B[@]}" "${S2[@]}" -c:v rawvideo -pix_fmt bgr24 -c:a pcm_s24le "${FFO[@]}" -f avi
ff avi/huffyuv-pcm8.avi "${V320B[@]}" "${S1[@]}" -c:v huffyuv -c:a pcm_u8 "${FFO[@]}" -f avi
ff avi/ffv1-flac.avi "${V320B[@]}" "${S1[@]}" -c:v ffv1 -c:a flac "${FFO[@]}" -f avi
ff avi/mpeg4-two-audio.avi "${V320[@]}" "${S1[@]}" "${S441[@]}" -map 0:v -map 1:a -map 2:a -c:v mpeg4 -q:v 6 -c:a:0 libmp3lame -b:a:0 64k -c:a:1 pcm_s16le "${FFO[@]}" -f avi
ff avi/video-only-mpeg4.avi "${V320[@]}" -c:v mpeg4 -q:v 5 "${FFO[@]}" -f avi
ff avi/audio-only-pcm.avi "${S2[@]}" -c:a pcm_s16le "${FFO[@]}" -f avi
ff avi/tagged-mpeg4-mp3.avi "${V320[@]}" "${S1[@]}" -c:v mpeg4 -q:v 5 -c:a libmp3lame -b:a 96k -bitexact -fflags +bitexact -map_metadata -1 \
   -metadata title="Test title" -metadata artist="Test artist" -metadata comment="A comment" -metadata date=2021-06-05 -metadata genre=Test -metadata copyright="(c) test" -metadata creation_time=2021-06-05T10:11:12Z -f avi
ff avi/dv-ntsc-type2.avi "${V_NTSC[@]}" "${S2[@]}" -pix_fmt yuv411p -c:v dvvideo -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f avi
ff avi/dv-pal-type2.avi "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f avi
ff avi/dv-pal-type2-mono.avi "${V_PAL[@]}" "${S1[@]}" -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le "${FFO[@]}" -f avi

# --- raw DV written by ffmpeg ---
ff dv/dv25-ntsc.dv "${V_NTSC[@]}" "${S2[@]}" -pix_fmt yuv411p -c:v dvvideo -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv
ff dv/dv25-pal.dv "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv
ff dv/dv25-pal-44k.dv "${V_PAL[@]}" "${S441[@]}" -af aformat=channel_layouts=stereo -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 44100 "${FFO[@]}" -f dv
ff dv/dv25-pal-32k.dv "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 32000 "${FFO[@]}" -f dv
ff dv/dv25-pal-169.dv "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv420p -aspect 16:9 -c:v dvvideo -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv
ff dv/dv50-ntsc.dv "${V_NTSC[@]}" "${S2[@]}" -pix_fmt yuv422p -c:v dvvideo -b:v 50M -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv
ff dv/dv50-pal.dv "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv422p -c:v dvvideo -b:v 50M -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv
ff dv/dv25-pal-dated.dv "${V_PAL[@]}" "${S2[@]}" -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 48000 -bitexact -fflags +bitexact -map_metadata -1 -metadata creation_time=2019-03-04T05:06:07Z -timecode 01:02:03:04 -f dv
ff dv/dvcprohd-1080i50.dv -f lavfi -i "testsrc2=size=1440x1080:rate=25:duration=2" "${S2[@]}" -pix_fmt yuv422p -c:v dvvideo -b:v 100M -vf "setfield=tff" -flags +ildct -c:a pcm_s16le -ar 48000 "${FFO[@]}" -f dv

# --- DV type 1 (wrapped by wrap_dv_avi.py) and damaged variants ---
derive avi/dv-pal-type1.avi "wrap_dv_avi.py dv/dv25-pal.dv" python3 "$HERE/wrap_dv_avi.py" "$G/dv/dv25-pal.dv" @OUT@
derive avi/dv-ntsc-type1.avi "wrap_dv_avi.py dv/dv25-ntsc.dv" python3 "$HERE/wrap_dv_avi.py" "$G/dv/dv25-ntsc.dv" @OUT@
derive avi/dv50-pal-type1.avi "wrap_dv_avi.py dv/dv50-pal.dv" python3 "$HERE/wrap_dv_avi.py" "$G/dv/dv50-pal.dv" @OUT@
derive avi/damaged/mpeg4-mp3-cut-mid-chunk.avi "first 61% of avi/mpeg4-mp3.avi" bash -c 'f="$1"; head -c $(( $(stat -c %s "$f") * 61 / 100 )) "$f" > "$2"' _ "$G/avi/mpeg4-mp3.avi" @OUT@
derive avi/damaged/mpeg4-mp3-no-index.avi "avi/mpeg4-mp3.avi cut before idx1" bash -c 'head -c "$(python3 -c "import sys;d=open(sys.argv[1],\"rb\").read();print(d.rindex(b\"idx1\"))" "$1")" "$1" > "$2"' _ "$G/avi/mpeg4-mp3.avi" @OUT@
derive avi/damaged/h264-bframes-no-index.avi "avi/h264-bframes-ac3.avi cut before idx1" bash -c 'head -c "$(python3 -c "import sys;d=open(sys.argv[1],\"rb\").read();print(d.rindex(b\"idx1\"))" "$1")" "$1" > "$2"' _ "$G/avi/h264-bframes-ac3.avi" @OUT@
derive avi/damaged/mjpeg-pcm-cut-at-header.avi "first 3000 bytes of avi/mjpeg-pcm.avi" bash -c 'head -c 3000 "$1" > "$2"' _ "$G/avi/mjpeg-pcm.avi" @OUT@
derive avi/damaged/dv-pal-type1-cut.avi "first 1.5 MB of avi/dv-pal-type1.avi" bash -c 'head -c 1500000 "$1" > "$2"' _ "$G/avi/dv-pal-type1.avi" @OUT@
derive dv/damaged/dv25-pal-cut-mid-frame.dv "first 700000 bytes of dv/dv25-pal.dv" bash -c 'head -c 700000 "$1" > "$2"' _ "$G/dv/dv25-pal.dv" @OUT@
derive dv/damaged/dv25-ntsc-offset-start.dv "dv/dv25-ntsc.dv without its first 5000 bytes" bash -c 'tail -c +5001 "$1" > "$2"' _ "$G/dv/dv25-ntsc.dv" @OUT@

# --- OpenDML: ffmpeg starts a new RIFF AVIX chunk every GiB. Raw video makes big files fast. ---
if [ "${LARGE:-1}" = 1 ]; then
  ff avi/large/odml-1p3gb.avi -f lavfi -i "testsrc2=size=640x480:rate=25:duration=110" "${S2[@]/duration=3/duration=110}" -c:v rawvideo -pix_fmt yuv420p -c:a pcm_s16le -f avi
  ff avi/large/odml-4p6gb.avi -f lavfi -i "testsrc2=size=640x480:rate=25:duration=400" -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=400" -c:v rawvideo -pix_fmt yuv420p -c:a pcm_s16le -f avi
fi

echo "generated $ok, kept $skipped, failed $failed (manifest: $MAN)"
[ "$failed" -eq 0 ]

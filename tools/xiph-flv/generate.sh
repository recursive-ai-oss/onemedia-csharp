#!/usr/bin/env bash
# Generates Ogg, FLAC and FLV test files with ffmpeg from synthetic input (lavfi test sources), so the files are ours to keep.
# Writes into $ONEMEDIA_HOME/corpus/generated/xiph-flv with a MANIFEST.tsv (SHA-256, size, tool, command).
#
#   generate.sh [FILTER]   FILTER is a grep pattern on the output path (default: everything)
#   LARGE=1 generate.sh    also writes the multi-gigabyte files
#
# A codec the pinned ffmpeg cannot encode is reported as FAILED and the run continues. Re-running keeps existing files.
set -uo pipefail

O="${ONEMEDIA_HOME:-$HOME/onemedia}"
G="$O/corpus/generated/xiph-flv"
FF="${FFMPEG:-$O/oracles/bin/ffmpeg}"
FFPROBE="${FFPROBE:-$O/oracles/bin/ffprobe}"
HERE="$(cd "$(dirname "$0")" && pwd)"
FILTER="${1:-.}"
mkdir -p "$G/_src"
MAN="$G/MANIFEST.tsv"
[ -s "$MAN" ] || printf 'path\ttool\tsha256\tsize\tcommand\n' > "$MAN"
ffv=$("$FF" -version | head -1 | cut -d' ' -f1-3)
ok=0; failed=0; skipped=0

FFG=(-hide_banner -loglevel error -y)
FFO=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1)
SINE=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=4")
SINE44=(-f lavfi -i "sine=frequency=440:sample_rate=44100:duration=4")
NOISE=(-f lavfi -i "anoisesrc=seed=7:amplitude=0.3:sample_rate=48000:duration=4")
VID=(-f lavfi -i "testsrc2=size=320x240:rate=30:duration=4")

record() { local f="$G/$1"; printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$(sha256sum "$f" | cut -d' ' -f1)" "$(stat -c %s "$f")" "$3" >> "$MAN"; }

# ffo REL ARGS...: runs ffmpeg with ARGS before the output options and writes REL.
ffo() {
  local rel=$1; shift
  echo "$rel" | grep -qE "$FILTER" || return 0
  local out="$G/$rel" part="$G/${rel%.*}.part.${rel##*.}"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  mkdir -p "$(dirname "$out")"
  if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "benchmark lock present; stopping" >&2; exit 3; fi
  if "$FF" "${FFG[@]}" "$@" "${FFO[@]}" "$part" >"$G/.last.log" 2>&1 && [ -s "$part" ]; then
    mv "$part" "$out"; record "$rel" "$ffv" "ffmpeg $*"; ok=$((ok+1))
  else
    rm -f "$part"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2
  fi
}

# patch REL FROM ARGS...: copies FROM through flacpatch.py.
patch() {
  local rel=$1 from=$2; shift 2
  echo "$rel" | grep -qE "$FILTER" || return 0
  local out="$G/$rel"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  if python3 "$HERE/flacpatch.py" "$G/$from" "$out" "$@" >"$G/.last.log" 2>&1; then
    record "$rel" "flacpatch.py" "flacpatch.py $from $*"; ok=$((ok+1))
  else
    rm -f "$out"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2
  fi
}

# --- tag and picture sources ------------------------------------------------------------------------
if [ ! -s "$G/_src/cover.jpg" ]; then
  "$FF" "${FFG[@]}" -f lavfi -i "testsrc2=size=200x200:rate=1:duration=1" -frames:v 1 -q:v 3 "$G/_src/cover.jpg" 2>/dev/null
fi
TAGS=(-metadata title="Fixture Title" -metadata artist="Fixture Artist" -metadata album="Fixture Album" -metadata track=3/12 -metadata date=2020-05-17
      -metadata genre=Test -metadata comment="A comment" -metadata disc=1/2 -metadata REPLAYGAIN_TRACK_GAIN="-6.50 dB" -metadata REPLAYGAIN_TRACK_PEAK=0.987)

A_VORBIS=(-c:a libvorbis -q:a 3)
A_OPUS=(-c:a libopus -b:a 64k)
A_FLAC=(-c:a flac)

# --- Ogg ------------------------------------------------------------------------------------------------
ffo ogg/vorbis-mono-q3.ogg "${SINE44[@]}" -ac 1 "${A_VORBIS[@]}"
ffo ogg/vorbis-stereo-q5.ogg "${SINE44[@]}" -ac 2 -c:a libvorbis -q:a 5
ffo ogg/vorbis-q0-22k.ogg "${SINE44[@]}" -ar 22050 -c:a libvorbis -q:a 0
ffo ogg/vorbis-q10-48k.ogg "${NOISE[@]}" -ac 2 -c:a libvorbis -q:a 10
ffo ogg/vorbis-6ch.ogg "${NOISE[@]}" -ac 6 -c:a libvorbis -q:a 3
ffo ogg/opus-stereo-64k.opus "${SINE[@]}" -ac 2 "${A_OPUS[@]}"
ffo ogg/opus-mono-voip.opus "${SINE[@]}" -ar 16000 -ac 1 -c:a libopus -application voip -b:a 24k
ffo ogg/opus-6ch.opus "${NOISE[@]}" -ac 6 -c:a libopus -b:a 192k -mapping_family 1
ffo ogg/opus-60ms-frames.opus "${SINE[@]}" -ac 2 -c:a libopus -b:a 64k -frame_duration 60
ffo ogg/opus-trimmed.opus "${SINE[@]}" -t 3.01 "${A_OPUS[@]}"
ffo ogg/flac-16.oga "${SINE[@]}" "${A_FLAC[@]}"
ffo ogg/flac-24-stereo.oga "${NOISE[@]}" -ac 2 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24 -frame_size 4096
ffo ogg/theora.ogv "${VID[@]}" -c:v libtheora -q:v 5 -g 30
ffo ogg/theora-vorbis.ogv "${VID[@]}" "${SINE44[@]}" -c:v libtheora -q:v 5 -g 30 "${A_VORBIS[@]}"
ffo ogg/theora-opus.ogv "${VID[@]}" "${SINE[@]}" -c:v libtheora -q:v 5 -g 12 "${A_OPUS[@]}"
ffo ogg/speex-16k.spx "${SINE[@]}" -ar 16000 -ac 1 -c:a libspeex
ffo ogg/speex-8k.ogg "${SINE[@]}" -ar 8000 -ac 1 -c:a libspeex
ffo ogg/vorbis-opus-multiplex.ogg "${SINE44[@]}" "${SINE[@]}" -map 0:a -map 1:a -c:a:0 libvorbis -q:a:0 3 -c:a:1 libopus -b:a:1 64k
ffo ogg/vorbis-tags.ogg "${SINE44[@]}" "${A_VORBIS[@]}" "${TAGS[@]}"
ffo ogg/opus-tags.opus "${SINE[@]}" "${A_OPUS[@]}" "${TAGS[@]}"
ffo ogg/flac-tags.oga "${SINE[@]}" "${A_FLAC[@]}" "${TAGS[@]}"
ffo ogg/theora-tags.ogv "${VID[@]}" -c:v libtheora -q:v 5 "${TAGS[@]}"
# Chained files are made by joining finished files: the pages are untouched and the second link has its own serial numbers.
chain() {
  local rel=$1; shift
  echo "$rel" | grep -qE "$FILTER" || return 0
  [ -s "$G/$rel" ] && { skipped=$((skipped+1)); return 0; }
  for f in "$@"; do [ -s "$G/$f" ] || { echo "FAILED $rel: missing $f" >&2; failed=$((failed+1)); return 0; }; done
  (cd "$G" && cat "$@" > "$rel") && record "$rel" "cat" "cat $*" && ok=$((ok+1))
}
ffo _src/vorbis-b.ogg "${SINE[@]}" -ac 2 -t 2 -c:a libvorbis -q:a 4 -serial_offset 5000
ffo _src/opus-b.opus "${SINE[@]}" -t 2 "${A_OPUS[@]}" -serial_offset 9000
chain ogg/chained-vorbis-vorbis.ogg ogg/vorbis-mono-q3.ogg _src/vorbis-b.ogg
chain ogg/chained-vorbis-opus.ogg ogg/vorbis-mono-q3.ogg _src/opus-b.opus

# --- FLAC -----------------------------------------------------------------------------------------------
ffo flac/sine-16-44k.flac "${SINE44[@]}" "${A_FLAC[@]}"
ffo flac/bs256.flac "${SINE44[@]}" "${A_FLAC[@]}" -frame_size 256
ffo flac/bs1152.flac "${SINE44[@]}" "${A_FLAC[@]}" -frame_size 1152
ffo flac/bs4096.flac "${NOISE[@]}" "${A_FLAC[@]}" -frame_size 4096
ffo flac/bs8192.flac "${SINE44[@]}" "${A_FLAC[@]}" -frame_size 8192
ffo flac/bs16384.flac "${SINE44[@]}" "${A_FLAC[@]}" -frame_size 16384
ffo flac/24bit.flac "${SINE44[@]}" -ac 2 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
ffo flac/mono-8k.flac "${SINE[@]}" -ar 8000 -ac 1 "${A_FLAC[@]}"
ffo flac/mono-22k.flac "${SINE44[@]}" -ar 22050 -ac 1 "${A_FLAC[@]}"
ffo flac/stereo-48k.flac "${NOISE[@]}" -ac 2 "${A_FLAC[@]}"
ffo flac/96k-24bit.flac "${SINE[@]}" -ar 96000 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
ffo flac/192k-24bit.flac "${SINE[@]}" -ar 192000 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
ffo flac/3ch.flac "${NOISE[@]}" -ac 3 "${A_FLAC[@]}"
ffo flac/6ch.flac "${NOISE[@]}" -ac 6 "${A_FLAC[@]}"
ffo flac/8ch.flac "${NOISE[@]}" -ac 8 "${A_FLAC[@]}"
ffo flac/noise-verbatim.flac "${NOISE[@]}" -ac 1 -c:a flac -compression_level 0
ffo flac/tags.flac "${SINE44[@]}" "${A_FLAC[@]}" "${TAGS[@]}"
ffo flac/cover.flac "${SINE44[@]}" -i "$G/_src/cover.jpg" -map 0:a -map 1:v -c:a flac -c:v copy -disposition:v attached_pic "${TAGS[@]}"
patch flac/id3-prefix.flac flac/tags.flac --id3 2048
patch flac/seektable.flac flac/sine-16-44k.flac --seektable 8 "$FFPROBE"
patch flac/padding-app-cuesheet.flac flac/tags.flac --padding 4096 --application --cuesheet
patch flac/all-blocks.flac flac/cover.flac --id3 600 --seektable 5 "$FFPROBE" --padding 1000 --application
# A file written to a pipe cannot have its STREAMINFO updated: the sample count stays 0.
if echo flac/unknown-length.flac | grep -qE "$FILTER" && [ ! -s "$G/flac/unknown-length.flac" ]; then
  "$FF" "${FFG[@]}" "${SINE44[@]}" "${A_FLAC[@]}" "${FFO[@]}" -f flac pipe:1 > "$G/flac/unknown-length.flac" 2>"$G/.last.log" \
    && { record flac/unknown-length.flac "$ffv" "ffmpeg sine -c:a flac -f flac pipe:1"; ok=$((ok+1)); } || { failed=$((failed+1)); echo "FAILED flac/unknown-length.flac" >&2; }
fi

# --- FLV ------------------------------------------------------------------------------------------------
V_H264=(-c:v libx264 -preset veryfast -threads 1 -g 30 -bf 2 -pix_fmt yuv420p)
V_FLV1=(-c:v flv -g 30 -b:v 300k)
A_AAC=(-c:a aac -b:a 96k)
A_MP3=(-c:a libmp3lame -b:a 128k)
ffo flv/h264-aac.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}"
ffo flv/h264-mp3.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_MP3[@]}"
ffo flv/h264-only.flv "${VID[@]}" "${V_H264[@]}"
ffo flv/h264-nob.flv "${VID[@]}" "${SINE44[@]}" -c:v libx264 -preset veryfast -threads 1 -g 30 -bf 0 -pix_fmt yuv420p "${A_AAC[@]}"
ffo flv/aac-only.flv "${SINE44[@]}" "${A_AAC[@]}"
ffo flv/aac-mono-22k.flv "${SINE44[@]}" -ar 22050 -ac 1 "${A_AAC[@]}"
ffo flv/mp3-44k.flv "${SINE44[@]}" "${A_MP3[@]}"
ffo flv/mp3-22k-mono.flv "${SINE44[@]}" -ar 22050 -ac 1 "${A_MP3[@]}"
ffo flv/mp3-11k-mono.flv "${SINE44[@]}" -ar 11025 -ac 1 "${A_MP3[@]}"
ffo flv/flv1-mp3.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" "${A_MP3[@]}"
ffo flv/flv1-nellymoser.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a nellymoser -ar 16000 -ac 1
ffo flv/flv1-nellymoser-8k.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a nellymoser -ar 8000 -ac 1
ffo flv/flv1-adpcm.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a adpcm_swf -ar 22050
ffo flv/flv1-pcm16.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a pcm_s16le
ffo flv/flv1-pcm-mono.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a pcm_s16le -ac 1 -ar 11025
ffo flv/flv1-speex.flv "${VID[@]}" "${SINE44[@]}" "${V_FLV1[@]}" -c:a libspeex -ar 16000 -ac 1
ffo flv/flv1-custom-size.flv -f lavfi -i "testsrc2=size=700x500:rate=25:duration=2" "${V_FLV1[@]}"
ffo flv/flv1-small.flv -f lavfi -i "testsrc2=size=176x144:rate=15:duration=3" "${SINE44[@]}" "${V_FLV1[@]}" "${A_MP3[@]}"
ffo flv/screen.flv "${VID[@]}" -c:v flashsv
ffo flv/h264-keyframe-index.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}" -flvflags add_keyframe_index
ffo flv/h264-no-metadata.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}" -flvflags no_metadata
ffo flv/h264-no-duration.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}" -flvflags no_duration_filesize
ffo flv/h264-640x360-25.flv -f lavfi -i "testsrc2=size=640x360:rate=25:duration=3" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}"
ffo flv/hevc-enhanced.flv "${VID[@]}" "${SINE44[@]}" -c:v libx265 -preset ultrafast -x265-params log-level=error:pools=1:frame-threads=1 -g 30 -bf 2 -pix_fmt yuv420p "${A_AAC[@]}" -strict -2
ffo flv/av1-enhanced.flv "${VID[@]}" -c:v libaom-av1 -cpu-used 8 -crf 40 -b:v 0 -g 30 -threads 1 -pix_fmt yuv420p -strict -2
ffo flv/vp9-enhanced.flv "${VID[@]}" -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -g 30 -threads 1 -pix_fmt yuv420p -strict -2
ffo flv/tags.flv "${VID[@]}" "${SINE44[@]}" "${V_H264[@]}" "${A_AAC[@]}" "${TAGS[@]}"

if [ "${LARGE:-0}" = 1 ]; then
  # About 2.2 GB of incompressible FLAC audio: noise in a 24-bit stereo stream.
  ffo large/noise-2gb.flac -f lavfi -i "anoisesrc=seed=3:amplitude=0.5:sample_rate=96000:duration=2700" -ac 2 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
  if [ -s "$G/large/noise-2gb.flac" ]; then
    ffo large/noise-2gb.oga -i "$G/large/noise-2gb.flac" -c copy -f ogg
  fi
fi

echo "generated $ok, kept $skipped, failed $failed (manifest: $MAN)"
[ "$failed" -eq 0 ]

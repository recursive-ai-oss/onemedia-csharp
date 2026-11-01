#!/usr/bin/env bash
# Generates small test files with ffmpeg, MP4Box and mkvmerge from synthetic input (lavfi test sources), so the
# files are ours to keep and commit when small. Writes into $ONEMEDIA_HOME/corpus/generated and a manifest
# generated/MANIFEST.tsv that records each file's SHA-256, size, tool and command.
#
#   generate.sh [FILTER]   FILTER is a grep pattern on the output path (default: everything)
#   LARGE=0 generate.sh    skip the two multi-gigabyte files
#
# A codec the pinned ffmpeg cannot encode is reported as FAILED and the run continues. Re-running keeps
# existing files. Output is bit-exact where the tools allow it.
set -uo pipefail

HERE_GEN="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
O="${ONEMEDIA_HOME:-$HOME/onemedia}"
G="$O/corpus/generated"
FF="${FFMPEG:-$O/oracles/bin/ffmpeg}"
MP4BOX="${MP4BOX:-$O/oracles/bin/MP4Box}"
MKVMERGE="${MKVMERGE:-$O/oracles/bin/mkvmerge}"
FILTER="${1:-.}"
mkdir -p "$G"
MAN="$G/MANIFEST.tsv"
[ -s "$MAN" ] || printf 'path\ttool\tsha256\tsize\tcommand\n' > "$MAN"
ffv=$("$FF" -version | head -1 | cut -d' ' -f1-3); mbv=$("$MP4BOX" -version 2>&1 | head -1 | sed 's/.*version //'); mkv=$("$MKVMERGE" --version | head -1)
ok=0; failed=0; skipped=0

FFG=(-hide_banner -loglevel error -y)
# Output options: placed after the inputs, before the output file.
FFO=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1 -strict -2)
# Synthetic input: a moving test pattern and a tone, 4 s each.
VID_320=(-f lavfi -i "testsrc2=size=320x240:rate=30:duration=4")
VID_QCIF=(-f lavfi -i "testsrc2=size=176x144:rate=15:duration=4")
SINE=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=4")
NOISE=(-f lavfi -i "anoisesrc=seed=7:amplitude=0.3:sample_rate=48000:duration=4")

# Video and audio encoder settings (single-threaded where that keeps output repeatable).
V_H264=(-c:v libx264 -preset veryfast -threads 1 -g 30 -bf 2 -pix_fmt yuv420p)
V_H264_BASE=(-c:v libx264 -preset veryfast -threads 1 -g 30 -profile:v baseline -bf 0 -pix_fmt yuv420p)
V_HEVC=(-c:v libx265 -preset ultrafast -x265-params log-level=error:pools=1:frame-threads=1 -g 30 -bf 2 -pix_fmt yuv420p)
V_AV1=(-c:v libaom-av1 -cpu-used 8 -crf 40 -b:v 0 -g 30 -threads 1 -pix_fmt yuv420p)
V_VP9=(-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -g 30 -threads 1 -pix_fmt yuv420p)
V_VP8=(-c:v libvpx -deadline realtime -cpu-used 8 -b:v 300k -g 30 -threads 1 -pix_fmt yuv420p)
V_MPEG2=(-c:v mpeg2video -b:v 1M -g 15 -bf 2 -pix_fmt yuv420p)
V_MPEG4=(-c:v mpeg4 -q:v 5 -g 30 -bf 2 -pix_fmt yuv420p)
V_H263=(-c:v h263 -b:v 200k -g 15 -pix_fmt yuv420p)
V_THEORA=(-c:v libtheora -q:v 5 -g 30 -pix_fmt yuv420p)
A_AAC=(-c:a aac -b:a 96k)
A_MP3=(-c:a libmp3lame -b:a 128k)
A_OPUS=(-c:a libopus -b:a 64k)
A_VORBIS=(-c:a libvorbis -q:a 3)
A_FLAC=(-c:a flac)
A_AC3=(-c:a ac3 -b:a 128k)
A_EAC3=(-c:a eac3 -b:a 128k)
A_ALAC=(-c:a alac)
A_PCM16=(-c:a pcm_s16le)
A_PCM24=(-c:a pcm_s24le)
A_PCMF32=(-c:a pcm_f32le)
A_AMRNB=(-ar 8000 -ac 1 -c:a libopencore_amrnb -b:a 12.2k)
A_AMRWB=(-ar 16000 -ac 1 -c:a libvo_amrwbenc -b:a 23.05k)

record() { # record REL TOOL CMD
  local f="$G/$1"
  printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$(sha256sum "$f" | cut -d' ' -f1)" "$(stat -c %s "$f")" "$3" >> "$MAN"
}

# gen REL TOOLLABEL RUNNER ARGS...: runs RUNNER with ARGS where @OUT@ is the output path.
gen() {
  local rel=$1 label=$2 runner=$3; shift 3
  echo "$rel" | grep -qE "$FILTER" || return 0
  local out="$G/$rel"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  mkdir -p "$(dirname "$out")"
  local args=() a
  for a in "$@"; do args+=("${a//@OUT@/$out}"); done
  local part="${out%.*}.part.${out##*.}"
  for i in "${!args[@]}"; do [ "${args[$i]}" = "$out" ] && args[$i]="$part"; done
  if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "benchmark lock present; stopping" >&2; exit 3; fi
  if "$runner" "${args[@]}" >"$G/.last.log" 2>&1 && [ -s "$part" ]; then
    mv "$part" "$out"; record "$rel" "$label" "$runner $*"; ok=$((ok+1))
  else
    rm -f "$part"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2
  fi
}
ff() { local rel=$1; shift; gen "$rel" "$ffv" "$FF" "${FFG[@]}" "$@" "${FFO[@]}" @OUT@; }
mp4box() { local rel=$1; shift; gen "$rel" "MP4Box $mbv" "$MP4BOX" "$@"; }
mkvm() { local rel=$1; shift; gen "$rel" "$mkv" "$MKVMERGE" -q -o @OUT@ "$@"; }

# --- subtitle sources --------------------------------------------------------------------------
mkdir -p "$G/_src"
cat > "$G/_src/sub.srt" <<'EOF'
1
00:00:00,500 --> 00:00:01,500
First line

2
00:00:01,800 --> 00:00:03,200
Second line
with a break
EOF
cat > "$G/_src/sub.vtt" <<'EOF'
WEBVTT

00:00.500 --> 00:01.500
First line

00:01.800 --> 00:03.200 line:90%
Second <b>line</b>
EOF
cat > "$G/_src/sub.ass" <<'EOF'
[Script Info]
ScriptType: v4.00+
PlayResX: 320
PlayResY: 240

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,First line
Dialogue: 0,0:00:01.80,0:00:03.20,Default,,0,0,0,,{\b1}Second{\b0} line
EOF

# --- ISO base media: video codecs with AAC --------------------------------------------------------
for v in h264 h264_base hevc av1 vp9 mpeg4 mpeg2; do
  case $v in
    h264) vo=("${V_H264[@]}");; h264_base) vo=("${V_H264_BASE[@]}");; hevc) vo=("${V_HEVC[@]}" -tag:v hvc1);;
    av1) vo=("${V_AV1[@]}");; vp9) vo=("${V_VP9[@]}");; mpeg4) vo=("${V_MPEG4[@]}");; mpeg2) vo=("${V_MPEG2[@]}");;
  esac
  ff "mp4/$v-aac.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${vo[@]}" "${A_AAC[@]}" -f mp4
done
ff "mp4/hevc-hev1-aac.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_HEVC[@]}" -tag:v hev1 "${A_AAC[@]}" -f mp4
ff "mp4/h264-avc3-aac.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" -tag:v avc3 "${A_AAC[@]}" -f mp4
ff "mp4/h264-two-audio.mp4" "${VID_320[@]}" "${SINE[@]}" "${NOISE[@]}" -t 4 -map 0:v -map 1:a -map 2:a "${V_H264[@]}" "${A_AAC[@]}" -f mp4
ff "mp4/video-only-h264.mp4" "${VID_320[@]}" -t 4 "${V_H264[@]}" -f mp4
# Audio codecs in MP4 (audio-only and with video).
for a in aac mp3 opus flac alac ac3 eac3; do
  case $a in aac) ao=("${A_AAC[@]}");; mp3) ao=("${A_MP3[@]}");; opus) ao=("${A_OPUS[@]}");; flac) ao=("${A_FLAC[@]}");;
    alac) ao=("${A_ALAC[@]}");; ac3) ao=("${A_AC3[@]}");; eac3) ao=("${A_EAC3[@]}");; esac
  ff "mp4/audio-$a.mp4" "${SINE[@]}" -t 4 "${ao[@]}" -f mp4
  ff "mp4/h264-$a.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${ao[@]}" -f mp4
done
ff "mp4/audio-aac-stereo-noise.mp4" "${NOISE[@]}" -ac 2 -t 4 "${A_AAC[@]}" -f mp4
ff "mp4/audio-aac-51.mp4" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=4" -af "pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0" -t 4 "${A_AAC[@]}" -f mp4
# M4A through the iPod muxer, MOV with PCM, 3GP/3G2.
ff "m4a/audio-aac.m4a" "${SINE[@]}" -t 4 "${A_AAC[@]}" -f ipod
ff "m4a/audio-alac.m4a" "${SINE[@]}" -t 4 "${A_ALAC[@]}" -f ipod
ff "m4v/h264-aac.m4v" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f ipod
ff "mov/h264-pcm16.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_PCM16[@]}" -f mov
ff "mov/h264-pcm24.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_PCM24[@]}" -f mov
ff "mov/h264-pcmf32.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_PCMF32[@]}" -f mov
ff "mov/h264-aac.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f mov
ff "mov/mpeg4-aac.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_MPEG4[@]}" "${A_AAC[@]}" -f mov
ff "3gp/h263-amrnb.3gp" "${VID_QCIF[@]}" "${SINE[@]}" -t 4 "${V_H263[@]}" "${A_AMRNB[@]}" -f 3gp
ff "3gp/h264-aac.3gp" "${VID_QCIF[@]}" "${SINE[@]}" -t 4 "${V_H264_BASE[@]}" "${A_AAC[@]}" -f 3gp
ff "3gp/audio-amrnb.3gp" "${SINE[@]}" -t 4 "${A_AMRNB[@]}" -f 3gp
ff "3gp/audio-amrwb.3gp" "${SINE[@]}" -t 4 "${A_AMRWB[@]}" -f 3gp
ff "3gp/mpeg4-amrnb.3g2" "${VID_QCIF[@]}" "${SINE[@]}" -t 4 "${V_MPEG4[@]}" "${A_AMRNB[@]}" -f 3g2
# Sample-table layout.
ff "mp4/h264-aac-faststart.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags +faststart -f mp4
ff "mp4/h264-aac-moov-at-end.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f mp4
ff "mp4/h264-aac-negative-cts.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags +negative_cts_offsets -f mp4
ff "mp4/h264-aac-no-editlist.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -use_editlist 0 -f mp4
ff "mp4/h264-aac-rotated90.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -metadata:s:v rotate=90 -f mp4
ff "mp4/h264-aac-tags.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -metadata title="Title" -metadata artist="Artist" -metadata comment="Comment" -metadata:s:a:0 language=eng -f mp4

# --- edit lists: copy cuts, priming, delayed tracks ---------------------------------------------
B="$G/mp4/h264-aac.mp4"
ff "editlist/cut-input-ss-copy.mp4" -ss 1.3 -i "$B" -c copy -f mp4
ff "editlist/cut-output-ss-copy.mp4" -i "$B" -ss 1.3 -c copy -f mp4
ff "editlist/cut-ss-to-copy.mp4" -ss 0.5 -to 2.9 -i "$B" -c copy -f mp4
ff "editlist/audio-delayed-1s.mp4" "${VID_320[@]}" -itsoffset 1 "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f mp4
ff "editlist/video-delayed-500ms.mp4" -itsoffset 0.5 "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f mp4
ff "editlist/audio-aac-priming.m4a" "${SINE[@]}" -t 4 "${A_AAC[@]}" -f ipod
ff "editlist/audio-mp3-priming.mp4" "${SINE[@]}" -t 4 "${A_MP3[@]}" -f mp4
ff "editlist/audio-opus-preskip.mp4" "${SINE[@]}" -t 4 "${A_OPUS[@]}" -f mp4
ff "editlist/audio-alac-cut.m4a" -ss 1.1 -i "$G/m4a/audio-alac.m4a" -c copy -f ipod
ff "editlist/cut-mov-ss-copy.mov" -ss 1.3 -i "$G/mov/h264-aac.mov" -c copy -f mov

# --- fragmented MP4, CMAF --------------------------------------------------------------------------
ff "fmp4/keyframe-empty-moov.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov -f mp4
ff "fmp4/keyframe-empty-moov-default-base-moof.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov+default_base_moof -f mp4
ff "fmp4/dash-flags.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov+dash+default_base_moof -f mp4
ff "fmp4/cmaf-flag.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags cmaf -f mp4
ff "fmp4/every-frame.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_every_frame+empty_moov+default_base_moof -f mp4
ff "fmp4/frag-duration-500ms.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -frag_duration 500000 -movflags empty_moov -f mp4
ff "fmp4/separate-moof.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov+separate_moof+default_base_moof -f mp4
ff "fmp4/negative-cts.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov+negative_cts_offsets+default_base_moof -f mp4
ff "fmp4/hevc-aac.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_HEVC[@]}" -tag:v hvc1 "${A_AAC[@]}" -movflags frag_keyframe+empty_moov+default_base_moof -f mp4
ff "fmp4/av1-opus.mp4" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_AV1[@]}" "${A_OPUS[@]}" -movflags frag_keyframe+empty_moov+default_base_moof -f mp4
ff "fmp4/audio-aac.mp4" "${SINE[@]}" -t 4 "${A_AAC[@]}" -movflags frag_keyframe+empty_moov -f mp4
ff "fmp4/mov-fragmented.mov" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -movflags frag_keyframe+empty_moov -f mov
ff "fmp4/ismv.ismv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f ismv
# ffmpeg's DASH and HLS fMP4 writers (a directory each: manifest, init segments, media segments).
ff "dash-ffmpeg/manifest.mpd" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f dash -seg_duration 1 -use_template 1 -use_timeline 1
ff "hls-ffmpeg/index.m3u8" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -f hls -hls_time 1 -hls_segment_type fmp4 -hls_playlist_type vod -hls_fmp4_init_filename init.mp4 -hls_segment_filename "$G/hls-ffmpeg/seg%d.m4s"

# --- MP4Box as a writer -----------------------------------------------------------------------------
if [ -s "$G/mp4/h264-aac.mp4" ]; then
  mp4box "mp4box/frag-1000.mp4" -frag 1000 "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/frag-500-single-traf.mp4" -frag 500 -single-traf "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/dash-live/manifest.mpd" -dash 1000 -profile live -segment-name 's_$RepresentationID$_$Number$' "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/dash-onDemand/manifest.mpd" -dash 1000 -profile onDemand "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/interleaved-500.mp4" -inter 500 "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/flat-no-interleave.mp4" -flat "$G/mp4/h264-aac.mp4" -out @OUT@
  mp4box "mp4box/new-from-tracks.mp4" -new @OUT@ -add "$G/mp4/h264-aac.mp4#video" -add "$G/mp4/h264-aac.mp4#audio"
  mp4box "mp4box/hevc-new.mp4" -new @OUT@ -add "$G/mp4/hevc-aac.mp4#video"
  mp4box "mp4box/av1-new.mp4" -new @OUT@ -add "$G/mp4/av1-aac.mp4#video"
  mp4box "mp4box/audio-only-m4a.m4a" -new @OUT@ -add "$G/mp4/audio-aac.mp4#audio"
  mp4box "mp4box/moov-first.mp4" -inter 0 -out @OUT@ "$G/mp4/h264-aac-moov-at-end.mp4"
  mp4box "mp4box/cat-two-parts.mp4" -add "$G/mp4/h264-aac.mp4" -cat "$G/mp4/h264-aac.mp4" -new @OUT@
  mp4box "mp4box/brand-iso6.mp4" -brand iso6:1 -out @OUT@ "$G/mp4/h264-aac.mp4"
  mp4box "mp4box/tx3g-sub.mp4" -add "$G/mp4/h264-aac.mp4" -add "$G/_src/sub.srt:lang=eng" -new @OUT@
  mp4box "mp4box/webvtt-sub.mp4" -add "$G/mp4/h264-aac.mp4" -add "$G/_src/sub.vtt:lang=eng" -new @OUT@
fi

# --- subtitle tracks from ffmpeg ---------------------------------------------------------------------
ff "subs/h264-movtext.mp4" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.srt" -t 4 -map 0:v -map 1:a -map 2:s "${V_H264[@]}" "${A_AAC[@]}" -c:s mov_text -f mp4
ff "subs/h264-movtext.mov" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.srt" -t 4 -map 0:v -map 1:a -map 2:s "${V_H264[@]}" "${A_AAC[@]}" -c:s mov_text -f mov
ff "subs/h264-srt.mkv" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.srt" -t 4 -map 0:v -map 1:a -map 2:s "${V_H264[@]}" "${A_AAC[@]}" -c:s srt -f matroska
ff "subs/h264-ass.mkv" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.ass" -t 4 -map 0:v -map 1:a -map 2:s "${V_H264[@]}" "${A_AAC[@]}" -c:s ass -f matroska
ff "subs/h264-webvtt.mkv" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.vtt" -t 4 -map 0:v -map 1:a -map 2:s "${V_H264[@]}" "${A_AAC[@]}" -c:s webvtt -f matroska
ff "subs/vp9-webvtt.webm" "${VID_320[@]}" "${SINE[@]}" -i "$G/_src/sub.vtt" -t 4 -map 0:v -map 1:a -map 2:s "${V_VP9[@]}" "${A_OPUS[@]}" -c:s webvtt -f webm

# --- Matroska and WebM -------------------------------------------------------------------------------
for v in h264 hevc av1 vp8 vp9 mpeg2 mpeg4 theora; do
  case $v in h264) vo=("${V_H264[@]}");; hevc) vo=("${V_HEVC[@]}");; av1) vo=("${V_AV1[@]}");; vp8) vo=("${V_VP8[@]}");;
    vp9) vo=("${V_VP9[@]}");; mpeg2) vo=("${V_MPEG2[@]}");; mpeg4) vo=("${V_MPEG4[@]}");; theora) vo=("${V_THEORA[@]}");; esac
  ff "mkv/$v-vorbis.mkv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${vo[@]}" "${A_VORBIS[@]}" -f matroska
done
for a in aac mp3 opus flac ac3 eac3 pcm16 pcm24; do
  case $a in aac) ao=("${A_AAC[@]}");; mp3) ao=("${A_MP3[@]}");; opus) ao=("${A_OPUS[@]}");; flac) ao=("${A_FLAC[@]}");;
    ac3) ao=("${A_AC3[@]}");; eac3) ao=("${A_EAC3[@]}");; pcm16) ao=("${A_PCM16[@]}");; pcm24) ao=("${A_PCM24[@]}");; esac
  ff "mkv/h264-$a.mkv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${ao[@]}" -f matroska
  ff "mka/audio-$a.mka" "${SINE[@]}" -t 4 "${ao[@]}" -f matroska
done
ff "mka/audio-vorbis.mka" "${SINE[@]}" -t 4 "${A_VORBIS[@]}" -f matroska
# Ogg twins of the Matroska audio files: the same codec packets in another container, for the Ogg comparison.
ff "ogg/audio-opus.opus" -i "$G/mka/audio-opus.mka" -c copy -f ogg
ff "ogg/audio-vorbis.ogg" -i "$G/mka/audio-vorbis.mka" -c copy -f ogg
ff "ogg/audio-flac.oga" -i "$G/mka/audio-flac.mka" -c copy -f ogg
ff "mkv/h264-aac-crc32.mkv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -write_crc32 1 -f matroska
ff "mkv/h264-aac-reserve-index.mkv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -reserve_index_space 4096 -f matroska
ff "mkv/h264-aac-no-cues.mkv" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}" -cues_to_front 0 -f matroska
ff "mkv/h264-two-audio-tags.mkv" "${VID_320[@]}" "${SINE[@]}" "${NOISE[@]}" -t 4 -map 0:v -map 1:a -map 2:a "${V_H264[@]}" "${A_AAC[@]}" -metadata title="Title" -metadata:s:a:0 language=eng -metadata:s:a:1 language=fra -disposition:a:1 default -f matroska
ff "mkv/h264-aac-bframes-cutcopy.mkv" -ss 1.3 -i "$G/mkv/h264-aac.mkv" -c copy -f matroska
ff "webm/vp8-vorbis.webm" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_VP8[@]}" "${A_VORBIS[@]}" -f webm
ff "webm/vp9-opus.webm" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_VP9[@]}" "${A_OPUS[@]}" -f webm
ff "webm/av1-opus.webm" "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_AV1[@]}" "${A_OPUS[@]}" -f webm
ff "webm/audio-opus.webm" "${SINE[@]}" -t 4 "${A_OPUS[@]}" -f webm
# Live-style Matroska: unknown-size segment and clusters, written to a pipe and caught in a file.
live() { local out=$1; shift; "$FF" "${FFG[@]}" "$@" "${FFO[@]}" -live 1 -f matroska pipe:1 > "$out"; }
gen "mkv-live/h264-aac-unknown-size.mkv" "$ffv" live @OUT@ "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_H264[@]}" "${A_AAC[@]}"
live_webm() { local out=$1; shift; "$FF" "${FFG[@]}" "$@" "${FFO[@]}" -live 1 -f webm pipe:1 > "$out"; }
gen "mkv-live/vp9-opus-unknown-size.webm" "$ffv" live_webm @OUT@ "${VID_320[@]}" "${SINE[@]}" -t 4 "${V_VP9[@]}" "${A_OPUS[@]}"
# mkvmerge variants of one source (lacing, cues, headers, compression, clusters).
S="$G/mkv/h264-aac.mkv"
if [ -s "$S" ]; then
  mkvm "mkvmerge/default.mkv" "$S"
  mkvm "mkvmerge/no-cues.mkv" --no-cues "$S"
  mkvm "mkvmerge/no-meta-seek.mkv" --engage no_meta_seek "$S"
  mkvm "mkvmerge/xiph-lacing.mkv" --engage lacing_xiph "$G/mkv/h264-vorbis.mkv"
  mkvm "mkvmerge/ebml-lacing.mkv" --engage lacing_ebml "$G/mkv/h264-vorbis.mkv"
  mkvm "mkvmerge/no-simpleblocks.mkv" --engage no_simpleblocks "$S"
  mkvm "mkvmerge/zlib-compression.mkv" --compression 0:zlib --compression 1:zlib "$S"
  mkvm "mkvmerge/small-clusters.mkv" --cluster-length 200 "$S"
  mkvm "mkvmerge/timestamp-scale-100000.mkv" --timestamp-scale 100000 "$S"
  mkvm "mkvmerge/headers-twice.mkv" --engage write_headers_twice "$S"
  mkvm "mkvmerge/webm.webm" --webm "$G/webm/vp9-opus.webm"
  mkvm "mkvmerge/mp4-source.mkv" "$G/mp4/h264-aac.mp4"
  mkvm "mkvmerge/delay-500ms.mkv" --sync 1:500 "$S"
  mkvm "mkvmerge/chapters-and-tags.mkv" --title "Muxed" --track-name 0:Video --language 1:fre "$S"
  mkvm "mkvmerge/mpeg4-header-removal.mkv" --engage native_mpeg4 "$G/mkv/mpeg4-vorbis.mkv"
fi

# --- uncompressed and companded audio files: WAVE, RF64, Wave64, AIFF, AIFF-C, CAF --------------------------------------------
# One tone per file; the tagged variants keep their metadata (the other files drop it with -map_metadata -1).
TONE=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=3")
TONE44=(-f lavfi -i "sine=frequency=440:sample_rate=44100:duration=3")
TONE8=(-f lavfi -i "sine=frequency=440:sample_rate=8000:duration=3")
TONE96=(-f lavfi -i "sine=frequency=440:sample_rate=96000:duration=2")
META=(-metadata title="Tone test" -metadata artist="Test Artist" -metadata album="Test Album" -metadata comment="A generated tone" -metadata genre=Test -metadata date=2020-05-17 -metadata track=3/12 -metadata copyright="(c) Test")
for c in pcm_s16le pcm_s24le pcm_s32le pcm_f32le pcm_f64le pcm_u8; do
  ff "pcm/wav-$c-stereo48k.wav" "${TONE[@]}" -ac 2 -c:a $c -f wav
  ff "pcm/w64-$c-stereo48k.w64" "${TONE[@]}" -ac 2 -c:a $c -f w64
done
ff "pcm/wav-pcm_s16le-mono44k.wav" "${TONE44[@]}" -ac 1 -c:a pcm_s16le -f wav
ff "pcm/wav-pcm_s16le-mono8k.wav" "${TONE8[@]}" -ac 1 -c:a pcm_s16le -f wav
ff "pcm/wav-pcm_s24le-stereo96k.wav" "${TONE96[@]}" -ac 2 -c:a pcm_s24le -f wav
ff "pcm/wav-pcm_s16le-51-extensible.wav" "${TONE[@]}" -ac 6 -c:a pcm_s16le -f wav
ff "pcm/wav-pcm_s24le-51-extensible.wav" "${TONE[@]}" -ac 6 -c:a pcm_s24le -f wav
ff "pcm/wav-pcm_s24le-71-extensible.wav" "${TONE[@]}" -ac 8 -c:a pcm_s24le -f wav
ff "pcm/w64-pcm_s16le-51.w64" "${TONE[@]}" -ac 6 -c:a pcm_s16le -f w64
ff "pcm/wav-mulaw-8k.wav" "${TONE8[@]}" -ac 1 -c:a pcm_mulaw -f wav
ff "pcm/wav-alaw-8k.wav" "${TONE8[@]}" -ac 1 -c:a pcm_alaw -f wav
ff "pcm/wav-adpcm_ms-stereo.wav" "${TONE44[@]}" -ac 2 -c:a adpcm_ms -f wav
ff "pcm/wav-adpcm_ms-mono-256.wav" "${TONE44[@]}" -ac 1 -c:a adpcm_ms -block_size 256 -f wav
ff "pcm/wav-adpcm_ima_wav-stereo.wav" "${TONE44[@]}" -ac 2 -c:a adpcm_ima_wav -f wav
ff "pcm/wav-adpcm_ima_wav-mono-512.wav" "${TONE44[@]}" -ac 1 -c:a adpcm_ima_wav -block_size 512 -f wav
ff "pcm/wav-mp3.wav" "${TONE44[@]}" -ac 2 "${A_MP3[@]}" -f wav
ff "pcm/wav-ac3.wav" "${TONE[@]}" -ac 2 "${A_AC3[@]}" -f wav
ff "pcm/rf64-pcm_s16le-stereo48k.wav" "${TONE[@]}" -ac 2 -c:a pcm_s16le -rf64 always -f wav
ff "pcm/rf64-pcm_s24le-51.wav" "${TONE[@]}" -ac 6 -c:a pcm_s24le -rf64 always -f wav
for c in pcm_s16be pcm_s24be pcm_s32be pcm_f32be pcm_f64be pcm_s8; do
  ff "pcm/aiff-$c-stereo48k.aiff" "${TONE[@]}" -ac 2 -c:a $c -f aiff
done
ff "pcm/aiff-pcm_s16be-mono44k.aiff" "${TONE44[@]}" -ac 1 -c:a pcm_s16be -f aiff
ff "pcm/aiff-pcm_s24be-51.aiff" "${TONE[@]}" -ac 6 -c:a pcm_s24be -f aiff
ff "pcm/aifc-sowt-stereo48k.aifc" "${TONE[@]}" -ac 2 -c:a pcm_s16le -f aiff
ff "pcm/aifc-mulaw-8k.aifc" "${TONE8[@]}" -ac 1 -c:a pcm_mulaw -f aiff
ff "pcm/aifc-alaw-8k.aifc" "${TONE8[@]}" -ac 1 -c:a pcm_alaw -f aiff
ff "pcm/aifc-ima4-stereo.aifc" "${TONE44[@]}" -ac 2 -c:a adpcm_ima_qt -f aiff
for c in pcm_s16le pcm_s16be pcm_s24le pcm_s24be pcm_s32le pcm_f32le pcm_f32be pcm_f64le pcm_s8; do
  ff "pcm/caf-$c-stereo48k.caf" "${TONE[@]}" -ac 2 -c:a $c -f caf
done
ff "pcm/caf-pcm_s16le-51.caf" "${TONE[@]}" -ac 6 -c:a pcm_s16le -f caf
ff "pcm/caf-mulaw-8k.caf" "${TONE8[@]}" -ac 1 -c:a pcm_mulaw -f caf
ff "pcm/caf-alaw-8k.caf" "${TONE8[@]}" -ac 1 -c:a pcm_alaw -f caf
ff "pcm/caf-alac-stereo48k.caf" "${TONE[@]}" -ac 2 "${A_ALAC[@]}" -f caf
ff "pcm/caf-alac-mono44k.caf" "${TONE44[@]}" -ac 1 "${A_ALAC[@]}" -f caf
ff "pcm/caf-aac-stereo48k.caf" "${TONE[@]}" -ac 2 "${A_AAC[@]}" -f caf
ff "pcm/caf-opus-stereo48k.caf" "${TONE[@]}" -ac 2 "${A_OPUS[@]}" -f caf
ff "pcm/caf-ima4-stereo.caf" "${TONE44[@]}" -ac 2 -c:a adpcm_ima_qt -f caf
# Tagged variants: the metadata options of the muxers (INFO, bext, ID3, AIFF text chunks, the CAF info chunk).
FFT=(-bitexact -fflags +bitexact -flags:a +bitexact -strict -2)
gen "pcm/tagged-wav-info.wav" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a pcm_s16le "${FFT[@]}" "${META[@]}" -f wav @OUT@
gen "pcm/tagged-wav-bext.wav" "$ffv" "$FF" "${FFG[@]}" "${TONE[@]}" -ac 2 -c:a pcm_s24le "${FFT[@]}" -write_bext 1 -metadata description="Scene 12 take 3" -metadata originator="OneMedia test" -metadata originator_reference="REF0001" -metadata origination_date=2020-01-31 -metadata origination_time=14:03:59 -metadata time_reference=172800000 -metadata coding_history="A=PCM,F=48000,W=24,M=stereo" "${META[@]}" -f wav @OUT@
gen "pcm/tagged-wav-id3.wav" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a pcm_s16le "${FFT[@]}" -write_id3v2 1 "${META[@]}" -f wav @OUT@
gen "pcm/tagged-rf64-bext.wav" "$ffv" "$FF" "${FFG[@]}" "${TONE[@]}" -ac 2 -c:a pcm_s16le "${FFT[@]}" -rf64 always -write_bext 1 -metadata description="RF64 take" -metadata origination_date=2021-02-03 -metadata origination_time=04:05:06 "${META[@]}" -f wav @OUT@
gen "pcm/tagged-aiff.aiff" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a pcm_s16be "${FFT[@]}" "${META[@]}" -f aiff @OUT@
gen "pcm/tagged-aiff-id3.aiff" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a pcm_s16be "${FFT[@]}" -write_id3v2 1 "${META[@]}" -f aiff @OUT@
gen "pcm/tagged-caf.caf" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a pcm_s16le "${FFT[@]}" "${META[@]}" -f caf @OUT@
gen "pcm/tagged-caf-alac.caf" "$ffv" "$FF" "${FFG[@]}" "${TONE44[@]}" -ac 2 -c:a alac "${FFT[@]}" "${META[@]}" -f caf @OUT@
# Hand-built chunks ffmpeg cannot write (iXML, axml, cart, smpl, cue/adtl, inst, odd sizes, damage): tools/gen-pcm-fixtures.py.
if [ -f "$HERE_GEN/gen-pcm-fixtures.py" ] && echo "pcm/crafted" | grep -qE "$FILTER|pcm"; then
  mkdir -p "$G/pcm/crafted"; python3 "$HERE_GEN/gen-pcm-fixtures.py" "$G/pcm/crafted" || failed=$((failed+1))
fi

# --- large files by stream-copy concatenation --------------------------------------------------------
# A short high-bitrate clip is repeated until the output passes 4 GiB, so 64-bit offsets (co64) and long
# files are covered without paying for encoding.
if [ "${LARGE:-1}" = 1 ]; then
  BASE="$G/_src/large-base.mkv"
  if [ ! -s "$BASE" ] && echo large | grep -qE "$FILTER|large"; then
    "$FF" "${FFG[@]}" -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=10,noise=alls=60:allf=t" \
      -f lavfi -i "anoisesrc=seed=3:amplitude=0.5:sample_rate=48000:duration=10" -t 10 \
      -c:v libx264 -preset ultrafast -threads 4 -g 30 -b:v 40M -maxrate 40M -bufsize 40M -pix_fmt yuv420p \
      -c:a pcm_s16le "${FFO[@]}" -f matroska "$BASE" 2>"$G/.last.log" || echo "FAILED large base" >&2
  fi
  if [ -s "$BASE" ]; then
    sz=$(stat -c %s "$BASE"); n=$(( 4400000000 / sz + 1 ))
    : > "$G/_src/large-list.txt"; for _ in $(seq "$n"); do echo "file '$BASE'" >> "$G/_src/large-list.txt"; done
    ff "large/over4gb-moov-at-end.mp4" -f concat -safe 0 -i "$G/_src/large-list.txt" -c:v copy -c:a aac -b:a 96k -f mp4
    ff "large/over4gb.mkv" -f concat -safe 0 -i "$G/_src/large-list.txt" -c copy -f matroska
  fi
  # Long tones as uncompressed audio: RF64 and Wave64 past 4 GiB, AIFF past 2 GiB (its sizes are 32-bit), CAF past 4 GiB.
  LONG=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=23000" -ac 2 -c:a pcm_s16le)
  ff "large/over4gb-rf64.wav" "${LONG[@]}" -rf64 auto -f wav
  ff "large/over4gb.w64" "${LONG[@]}" -f w64
  ff "large/over4gb.caf" -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=23000" -ac 2 -c:a pcm_s16le -f caf
  ff "large/over2gb.aiff" -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=12000" -ac 2 -c:a pcm_s16be -f aiff
fi

echo "generated $ok, kept $skipped, failed $failed (manifest: $MAN)"
[ "$failed" -eq 0 ]

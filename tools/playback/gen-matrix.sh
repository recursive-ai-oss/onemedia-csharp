#!/usr/bin/env bash
# Writes the playback verification matrix: small files (2 s, 320x240) covering every container x codec row of the browser
# profiles, encoded by ffmpeg from generated input (lavfi test pattern and sine tone). Nothing here is committed; the output
# goes to the directory given as the first argument.
#
#   usage: gen-matrix.sh OUTDIR          FFMPEG=/path/to/ffmpeg overrides the encoder binary
set -euo pipefail

OUT="${1:?usage: gen-matrix.sh OUTDIR}"
FFMPEG="${FFMPEG:-ffmpeg}"
mkdir -p "$OUT"
cd "$OUT"

V_IN=(-f lavfi -i "testsrc2=size=320x240:rate=25:duration=2")
A_IN=(-f lavfi -i "sine=frequency=440:sample_rate=48000:duration=2")
G=(-g 25 -sc_threshold 0 -bf 2)

# Video encoders (arguments after the input).
X264=(-c:v libx264 -preset veryfast "${G[@]}")
H264_BASE=("${X264[@]}" -profile:v baseline -pix_fmt yuv420p)
H264_MAIN=("${X264[@]}" -profile:v main -pix_fmt yuv420p)
H264_HIGH=("${X264[@]}" -profile:v high -pix_fmt yuv420p)
H264_HIGH10=("${X264[@]}" -profile:v high10 -pix_fmt yuv420p10le)
H264_HIGH422=("${X264[@]}" -profile:v high422 -pix_fmt yuv422p)
H264_HIGH444=("${X264[@]}" -profile:v high444 -pix_fmt yuv444p)
H264_L52=("${X264[@]}" -profile:v high -level:v 5.2 -pix_fmt yuv420p)
X265=(-c:v libx265 -preset ultrafast -x265-params "log-level=error:keyint=25:min-keyint=25:bframes=2")
HEVC_MAIN=("${X265[@]}" -pix_fmt yuv420p)
HEVC_MAIN10=("${X265[@]}" -pix_fmt yuv420p10le)
AV1=(-c:v libaom-av1 -cpu-used 8 -crf 45 -b:v 0 -g 25)
VP9=(-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -g 25)
VP9_P2=(-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -g 25 -profile:v 2 -pix_fmt yuv420p10le)
VP8=(-c:v libvpx -deadline realtime -cpu-used 8 -b:v 300k -g 25)
MPEG4=(-c:v mpeg4 -q:v 5 -g 25)

# Audio encoders.
AAC=(-c:a aac -b:a 96k)
MP3=(-c:a libmp3lame -b:a 96k)
OPUS=(-c:a libopus -b:a 64k)
# Opus starts a little before zero (its pre-skip). Left alone, ffmpeg's WebM muxer writes that first audio block ahead of the first
# video block and Chromium's MediaSource rejects the file ("a block with a timecode before the previous block"); shifting the
# audio by the pre-skip keeps every block in order.
OPUS_WEBM=(-c:a libopus -b:a 64k -af asetpts=PTS+0.0065/TB)
VORBIS=(-c:a libvorbis -q:a 3)
FLAC=(-c:a flac)
AC3=(-c:a ac3 -b:a 192k)
EAC3=(-c:a eac3 -b:a 192k)
ALAC=(-c:a alac)
PCM16=(-c:a pcm_s16le)
PCM24=(-c:a pcm_s24le)
WMA=(-c:a wmav2)

FF=("$FFMPEG" -hide_banner -loglevel error -y)
FMP4=(-movflags +frag_keyframe+empty_moov+default_base_moof)
FAST=(-movflags +faststart)

# av NAME FILE VIDEO-ARGS AUDIO-ARGS [extra output args]: video + audio in one file.
av() { local f=$1; local -n v=$2; local -n a=$3; shift 3; "${FF[@]}" "${V_IN[@]}" "${A_IN[@]}" "${v[@]}" "${a[@]}" -strict -2 -shortest "$@" "$f"; }
# vo FILE VIDEO-ARGS [extra]: video only.
vo() { local f=$1; local -n v=$2; shift 2; "${FF[@]}" "${V_IN[@]}" "${v[@]}" -strict -2 "$@" "$f"; }
# ao FILE AUDIO-ARGS [extra]: audio only.
ao() { local f=$1; local -n a=$2; shift 2; "${FF[@]}" "${A_IN[@]}" "${a[@]}" -strict -2 "$@" "$f"; }

# --- ISO BMFF, H.264 profiles --------------------------------------------------------------------
av mp4-h264-baseline-aac.mp4 H264_BASE AAC "${FAST[@]}"
av mp4-h264-main-aac.mp4 H264_MAIN AAC "${FAST[@]}"
av mp4-h264-high-aac.mp4 H264_HIGH AAC "${FAST[@]}"
av mp4-h264-high10-aac.mp4 H264_HIGH10 AAC "${FAST[@]}"
av mp4-h264-high422-aac.mp4 H264_HIGH422 AAC "${FAST[@]}"
av mp4-h264-high444-aac.mp4 H264_HIGH444 AAC "${FAST[@]}"
av mp4-h264-level52-aac.mp4 H264_L52 AAC "${FAST[@]}"
av mp4-h264-high-aac-moovlast.mp4 H264_HIGH AAC
vo mp4-h264-high-videoonly.mp4 H264_HIGH "${FAST[@]}"
av mov-h264-high-aac.mov H264_HIGH AAC
av 3gp-h264-baseline-aac.3gp H264_BASE AAC -f 3gp
av fmp4-h264-high-aac.mp4 H264_HIGH AAC "${FMP4[@]}" -avoid_negative_ts make_zero
av mp4-h264-high-mp3.mp4 H264_HIGH MP3 "${FAST[@]}"
av mp4-h264-high-opus.mp4 H264_HIGH OPUS "${FAST[@]}"
av mp4-h264-high-flac.mp4 H264_HIGH FLAC "${FAST[@]}"
av mp4-h264-high-ac3.mp4 H264_HIGH AC3 "${FAST[@]}"
av mp4-h264-high-eac3.mp4 H264_HIGH EAC3 "${FAST[@]}"
av mp4-h264-high-alac.mp4 H264_HIGH ALAC "${FAST[@]}"
av mp4-h264-high-vorbis.mp4 H264_HIGH VORBIS "${FAST[@]}"
av mp4-h264-high-pcm.mov H264_HIGH PCM16
av fmp4-h264-high-opus.mp4 H264_HIGH OPUS "${FMP4[@]}" -avoid_negative_ts make_zero

# --- HEVC: hvc1 / hev1 tags ----------------------------------------------------------------------
av mp4-hevc-main-hvc1-aac.mp4 HEVC_MAIN AAC -tag:v hvc1 "${FAST[@]}"
av mp4-hevc-main-hev1-aac.mp4 HEVC_MAIN AAC -tag:v hev1 "${FAST[@]}"
av mp4-hevc-main10-hvc1-aac.mp4 HEVC_MAIN10 AAC -tag:v hvc1 "${FAST[@]}"
av fmp4-hevc-main-hvc1-aac.mp4 HEVC_MAIN AAC -tag:v hvc1 "${FMP4[@]}" -avoid_negative_ts make_zero
av fmp4-hevc-main-hev1-aac.mp4 HEVC_MAIN AAC -tag:v hev1 "${FMP4[@]}" -avoid_negative_ts make_zero
av mkv-hevc-main-aac.mkv HEVC_MAIN AAC -avoid_negative_ts make_zero

# --- AV1, VP9, VP8 -------------------------------------------------------------------------------
av mp4-av1-aac.mp4 AV1 AAC "${FAST[@]}"
av mp4-av1-opus.mp4 AV1 OPUS "${FAST[@]}"
# fragmented from the progressive file so that the AV1 configuration record keeps its sequence header
"${FF[@]}" -i mp4-av1-aac.mp4 -c copy "${FMP4[@]}" fmp4-av1-aac.mp4
av webm-av1-opus.webm AV1 OPUS_WEBM -avoid_negative_ts make_zero
av mkv-av1-opus.mkv AV1 OPUS -avoid_negative_ts make_zero
av mp4-vp9-aac.mp4 VP9 AAC "${FAST[@]}"
av mp4-vp9-opus.mp4 VP9 OPUS "${FAST[@]}"
av fmp4-vp9-opus.mp4 VP9 OPUS "${FMP4[@]}" -avoid_negative_ts make_zero
av webm-vp9-opus.webm VP9 OPUS_WEBM -avoid_negative_ts make_zero
av webm-vp9-vorbis.webm VP9 VORBIS -avoid_negative_ts make_zero
av webm-vp9p2-opus.webm VP9_P2 OPUS_WEBM -avoid_negative_ts make_zero
av webm-vp8-vorbis.webm VP8 VORBIS -avoid_negative_ts make_zero
av webm-vp8-opus.webm VP8 OPUS_WEBM -avoid_negative_ts make_zero
av mkv-vp9-opus.mkv VP9 OPUS -avoid_negative_ts make_zero
av mkv-h264-high-aac.mkv H264_HIGH AAC -avoid_negative_ts make_zero
av mkv-h264-high-opus.mkv H264_HIGH OPUS -avoid_negative_ts make_zero
av mp4-mpeg4-aac.mp4 MPEG4 AAC "${FAST[@]}"

# --- other containers with H.264 + AAC -----------------------------------------------------------
av ts-h264-high-aac.ts H264_HIGH AAC
av avi-h264-high-mp3.avi H264_HIGH MP3
av avi-mpeg4-mp3.avi MPEG4 MP3
av flv-h264-high-aac.flv H264_HIGH AAC
"${FF[@]}" "${V_IN[@]}" "${A_IN[@]}" -strict -2 -c:v wmv2 -c:a wmav2 -shortest asf-wmv2-wma.wmv

# --- audio only ----------------------------------------------------------------------------------
ao m4a-aac.m4a AAC "${FAST[@]}"
ao adts-aac.aac AAC -f adts
ao mp3-mp3.mp3 MP3
ao mp4-mp3.mp4 MP3
ao ogg-vorbis.ogg VORBIS
ao ogg-opus.opus OPUS
ao ogg-flac.ogg FLAC
ao webm-opus.webm OPUS
ao webm-vorbis.webm VORBIS
ao flac-flac.flac FLAC
ao mp4-flac.mp4 FLAC
ao mp4-opus.mp4 OPUS
ao m4a-alac.m4a ALAC
ao mp4-ac3.mp4 AC3
ao mp4-eac3.mp4 EAC3
ao ac3-ac3.ac3 AC3
ao wav-pcm16.wav PCM16
ao wav-pcm24.wav PCM24
ao mka-opus.mka OPUS
ao mkv-aac.mka AAC
ao caf-pcm.caf PCM16
ao aiff-pcm.aiff PCM16
ao ts-aac.ts AAC -f mpegts
ao wma-wma.wma WMA
ao fmp4-aac.m4a AAC "${FMP4[@]}"
ao fmp4-opus.mp4 OPUS "${FMP4[@]}"

ls | wc -l

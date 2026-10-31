#!/usr/bin/env bash
# Generates the synthetic streams the codec-layer differential runs on. Input is ffmpeg's own lavfi test sources, so nothing here is
# third-party media. Usage: make-streams.sh <output-dir> [ffmpeg]   (ffmpeg defaults to the one on PATH)
set -u
OUT=${1:?output directory}
FF=${2:-ffmpeg}
mkdir -p "$OUT"
V="-f lavfi -i testsrc2=size=320x180:rate=25:duration=2"
V64="-f lavfi -i testsrc2=size=64x64:rate=25:duration=1"
A="-f lavfi -i sine=frequency=440:sample_rate=48000:duration=2"  # mono source; -ac sets the layout per case
LOG="$OUT/make.log"
: > "$LOG"

run() { # name, then ffmpeg arguments
  local name=$1; shift
  if "$FF" -hide_banner -loglevel error -y -threads 2 "$@" >> "$LOG" 2>&1; then echo "ok   $name"; else echo "FAIL $name" | tee -a "$LOG"; fi
}

# video: name codec-args pix_fmt [extra]
vid() {
  local name=$1 enc=$2 pix=$3; shift 3
  for ext in mp4 mkv; do
    run "$name.$ext" $V -c:v $enc -pix_fmt $pix "$@" "$OUT/$name.$ext"
  done
}

vid h264_baseline libx264 yuv420p -profile:v baseline -level 3.0
vid h264_main libx264 yuv420p -profile:v main
vid h264_high libx264 yuv420p -profile:v high -bf 2
vid h264_high10 libx264 yuv420p10le -profile:v high10
vid h264_high422 libx264 yuv422p -profile:v high422
vid h264_high444 libx264 yuv444p -profile:v high444
vid h264_interlaced libx264 yuv420p -flags +ildct+ilme -x264opts tff=1 -profile:v high
vid hevc_main libx265 yuv420p -x265-params log-level=error
vid hevc_main10 libx265 yuv420p10le -x265-params log-level=error
vid hevc_444 libx265 yuv444p -x265-params log-level=error
vid hevc_bframes libx265 yuv420p -x265-params log-level=error:bframes=4
vid av1_aom8 libaom-av1 yuv420p -cpu-used 8 -crf 40 -b:v 0
vid av1_aom10 libaom-av1 yuv420p10le -cpu-used 8 -crf 40 -b:v 0
vid av1_svt8 libsvtav1 yuv420p -preset 12
vid av1_svt10 libsvtav1 yuv420p10le -preset 12
vid vp9_p0 libvpx-vp9 yuv420p -deadline realtime -cpu-used 8
vid vp9_p1 libvpx-vp9 yuv444p -deadline realtime -cpu-used 8
vid vp9_p2 libvpx-vp9 yuv420p10le -deadline realtime -cpu-used 8
# VP8 has no ISO base media binding, so it only goes into Matroska
run vp8.mkv $V -c:v libvpx -pix_fmt yuv420p -deadline realtime -cpu-used 8 "$OUT/vp8.mkv"
vid mpeg2 mpeg2video yuv420p -bf 2
vid mpeg2_422 mpeg2video yuv422p -profile:v 0
# colour-tagged stream, to exercise VUI colour description
vid h264_slices libx264 yuv420p -profile:v high -slices 3 -x264opts aud=1
vid hevc_slices libx265 yuv420p -x265-params log-level=error:slices=3
vid h264_bt709 libx264 yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv

# audio: name codec-args [extra]
aud() {
  local name=$1 enc=$2; shift 2
  for ext in mp4 mkv; do
    run "$name.$ext" $A -ac 2 -c:a $enc "$@" "$OUT/$name.$ext"
  done
}

aud aac_lc aac -b:a 96k
aud aac_lc_mono aac -b:a 64k -ac 1
aud aac_51 aac -b:a 256k -ac 6 -channel_layout 5.1
aud opus_stereo libopus -b:a 96k
aud opus_mono libopus -b:a 48k -ac 1
aud opus_51 libopus -b:a 256k -ac 6 -mapping_family 1 -channel_layout 5.1
aud flac16 flac -sample_fmt s16
aud flac24 flac -sample_fmt s32 -bits_per_raw_sample 24
aud ac3 ac3 -b:a 192k
aud eac3 eac3 -b:a 192k
aud mp3_cbr libmp3lame -b:a 128k
aud mp3_vbr libmp3lame -q:a 2
run vorbis.mkv $A -ac 2 -c:a libvorbis -b:a 96k "$OUT/vorbis.mkv"
run vorbis.ogg $A -ac 2 -c:a libvorbis -b:a 96k "$OUT/vorbis.ogg"
run vorbis_mono.mkv $A -c:a libvorbis -ac 1 "$OUT/vorbis_mono.mkv"
run opus.ogg $A -ac 2 -c:a libopus "$OUT/opus.ogg"

# HE-AAC needs an encoder that supports SBR; the stock aac encoder does not, so these are skipped quietly when libfdk_aac is absent.
if "$FF" -hide_banner -encoders 2>/dev/null | grep -q libfdk_aac; then
  aud aac_he libfdk_aac -profile:a aac_he -b:a 48k
  aud aac_hev2 libfdk_aac -profile:a aac_he_v2 -b:a 32k -ac 2
fi

# elementary streams
run h264.annexb $V -c:v libx264 -profile:v high -bf 2 -f h264 "$OUT/h264.h264"
run hevc.annexb $V -c:v libx265 -x265-params log-level=error -f hevc "$OUT/hevc.hevc"
run av1.obu $V -c:v libaom-av1 -cpu-used 8 -crf 40 -b:v 0 -f obu "$OUT/av1.obu"
run av1.ivf $V -c:v libaom-av1 -cpu-used 8 -crf 40 -b:v 0 -f ivf "$OUT/av1.ivf"
run vp9.ivf $V -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -f ivf "$OUT/vp9.ivf"
run vp8.ivf $V -c:v libvpx -deadline realtime -cpu-used 8 -f ivf "$OUT/vp8.ivf"
run mpeg2.m2v $V -c:v mpeg2video -bf 2 -f mpeg2video "$OUT/mpeg2.m2v"
run aac.adts $A -ac 2 -c:a aac -b:a 96k -f adts "$OUT/aac.aac"
run aac_mono.adts $A -c:a aac -ac 1 -f adts "$OUT/aac_mono.aac"
run ac3.raw $A -ac 2 -c:a ac3 -f ac3 "$OUT/ac3.ac3"
run eac3.raw $A -ac 2 -c:a eac3 -f eac3 "$OUT/eac3.eac3"
run mp3.raw $A -ac 2 -c:a libmp3lame -b:a 128k -f mp3 -write_xing 1 "$OUT/mp3.mp3"
run mp3_noxing.raw $A -ac 2 -c:a libmp3lame -b:a 128k -f mp3 -write_xing 0 -id3v2_version 0 "$OUT/mp3_noxing.mp3"
run flac.raw $A -ac 2 -c:a flac -f flac "$OUT/flac.flac"

# AMR needs libopencore_amrnb / libvo_amrwbenc, which most builds lack.
if "$FF" -hide_banner -encoders 2>/dev/null | grep -q libopencore_amrnb; then
  run amrnb.3gp -f lavfi -i sine=frequency=440:sample_rate=8000:duration=2 -ac 1 -c:a libopencore_amrnb -b:a 12.2k "$OUT/amrnb.3gp"
  run amrnb.amr -f lavfi -i sine=frequency=440:sample_rate=8000:duration=2 -ac 1 -c:a libopencore_amrnb -b:a 12.2k "$OUT/amrnb.amr"
fi
if "$FF" -hide_banner -encoders 2>/dev/null | grep -q libvo_amrwbenc; then
  run amrwb.3gp -f lavfi -i sine=frequency=440:sample_rate=16000:duration=2 -ac 1 -c:a libvo_amrwbenc -b:a 23.05k "$OUT/amrwb.3gp"
  run amrwb.amr -f lavfi -i sine=frequency=440:sample_rate=16000:duration=2 -ac 1 -c:a libvo_amrwbenc -b:a 23.05k "$OUT/amrwb.amr"
fi
echo done

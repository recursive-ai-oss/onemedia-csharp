#!/usr/bin/env bash
# Generates the MP4-family inputs the ISO BMFF demux differential runs on, from synthetic lavfi sources.
# Usage: OUT=<dir> [FFMPEG=ffmpeg] [MP4BOX=MP4Box] tools/gen-isobmff-fixtures.sh
# Every file is a few hundred KB at most. Files written by ffmpeg/MP4Box are black-box oracle output; their versions are
# recorded in $OUT/VERSIONS.txt.
set -u
OUT=${OUT:?set OUT to the output directory}
FF=${FFMPEG:-ffmpeg}
MP4BOX=${MP4BOX:-}
mkdir -p "$OUT"
cd "$OUT"
{ "$FF" -version | head -1; [ -n "$MP4BOX" ] && "$MP4BOX" -version 2>&1 | head -1; } > VERSIONS.txt

V="-f lavfi -i testsrc2=size=160x120:rate=25:duration=3"
A="-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3"
A48="-f lavfi -i sine=frequency=440:sample_rate=48000:duration=3"
Q="-hide_banner -loglevel error -y"
x264b="-c:v libx264 -preset veryfast -bf 2 -g 25 -pix_fmt yuv420p"

g() { # name, args...
  local name=$1; shift
  "$FF" $Q "$@" "$name" || echo "FAILED: $name" >&2
}

# Video codecs, with B-frames where the encoder has them.
g h264_aac.mp4        $V $A $x264b -c:a aac -shortest
g h264_aac_faststart.mp4 $V $A $x264b -c:a aac -shortest -movflags +faststart
g h264_aac.mov        $V $A $x264b -c:a aac -shortest
g h264_negcts.mp4     $V $x264b -movflags +negative_cts_offsets
g h264_nob.mp4        $V -c:v libx264 -preset veryfast -bf 0 -pix_fmt yuv420p
g hevc.mp4            $V -c:v libx265 -preset ultrafast -x265-params log-level=error:bframes=2 -tag:v hvc1 -pix_fmt yuv420p
g av1.mp4             $V -c:v libsvtav1 -g 25 -pix_fmt yuv420p
g vp9.mp4             $V -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -pix_fmt yuv420p
g mpeg4_mp3.mp4       $V $A -c:v mpeg4 -c:a libmp3lame -shortest
g mpeg2.mp4           $V -c:v mpeg2video -bf 2
g mjpeg.mov           $V -c:v mjpeg -pix_fmt yuvj420p
g mpeg4_aac.3gp       $V $A -c:v mpeg4 -s 176x144 -c:a aac -ar 16000 -ac 1 -shortest -f 3gp
g mpeg4_aac.3g2       $V $A -c:v mpeg4 -s 176x144 -c:a aac -ar 16000 -ac 1 -shortest -f 3g2

# Audio codecs.
g aac.m4a             $A -c:a aac -f ipod
g mp3.mp4             $A -c:a libmp3lame
g opus.mp4            $A48 -c:a libopus -strict -2
g flac.mp4            $A -c:a flac -strict -2
g ac3.mp4             $A48 -c:a ac3
g eac3.mp4            $A48 -c:a eac3
g alac.m4a            $A -c:a alac -f ipod
g pcm.mov             $A -c:a pcm_s16le
g pcm_be.mov          $A -c:a pcm_s16be

# Edit lists: priming (AAC default), an empty edit from an input offset, and a trimmed start.
g aac_offset.mp4      -itsoffset 1 $V -itsoffset 1 $A $x264b -c:a aac
g video_offset.mp4    -itsoffset 0.5 $V $A $x264b -c:a aac -shortest
g h264_ss.mp4         $V $A $x264b -c:a aac -ss 0.3 -shortest
g h264_noedit.mp4     $V $A $x264b -c:a aac -shortest -use_editlist 0
g two_audio.mp4       $V $A $A48 -map 0 -map 1 -map 2 $x264b -c:a aac

# Fragmented MP4 and CMAF.
g frag_keyframe.mp4   $V $A $x264b -c:a aac -shortest -movflags frag_keyframe+empty_moov+default_base_moof
g frag_plain.mp4      $V $A $x264b -c:a aac -shortest -movflags frag_keyframe+empty_moov
g frag_dash.mp4       $V $A $x264b -c:a aac -shortest -movflags frag_keyframe+empty_moov+default_base_moof+dash
g frag_cmaf.mp4       $V $A $x264b -c:a aac -shortest -movflags cmaf
g frag_sepmoof.mp4    $V $A $x264b -c:a aac -shortest -movflags frag_keyframe+empty_moov+separate_moof+default_base_moof
g frag_dur.mp4        $V $A $x264b -c:a aac -shortest -movflags empty_moov+default_base_moof -frag_duration 300000
g frag_negcts.mp4     $V $x264b -movflags frag_keyframe+empty_moov+default_base_moof+negative_cts_offsets
g frag_video_only.mp4 $V $x264b -movflags frag_keyframe+empty_moov+default_base_moof
g frag_audio_only.m4a $A -c:a aac -movflags frag_keyframe+empty_moov+default_base_moof -f mp4

# MP4Box as a black-box writer and fragmenter.
if [ -n "$MP4BOX" ]; then
  cp h264_aac.mp4 mp4box_src.mp4
  "$MP4BOX" -add h264_aac.mp4 -new mp4box_add.mp4 >/dev/null 2>&1 || echo "FAILED: mp4box_add.mp4" >&2
  "$MP4BOX" -frag 500 -out mp4box_frag.mp4 h264_aac.mp4 >/dev/null 2>&1 || echo "FAILED: mp4box_frag.mp4" >&2
  "$MP4BOX" -dash 1000 -frag 1000 -rap -out mp4box_dash.mpd h264_aac.mp4 >/dev/null 2>&1 || echo "FAILED: mp4box_dash" >&2
  "$MP4BOX" -add h264_aac.mp4#video -add h264_aac.mp4#audio:lang=fra -new mp4box_lang.mp4 >/dev/null 2>&1
  rm -f mp4box_src.mp4
fi

# Multi-description stsd: concatenating two clips of different size with the concat demuxer does not do this, so the
# unit tests build that file from boxes instead.
ls -1 | grep -v VERSIONS.txt | wc -l

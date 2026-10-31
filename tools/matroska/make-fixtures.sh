#!/usr/bin/env bash
# Writes the Matroska/WebM test files used by the differential checks: ffmpeg- and mkvmerge-written files covering the common codecs,
# lacing, missing Cues, live (unknown-size) layout, attachments, chapters and multi-track files. The inputs are synthetic
# (ffmpeg's lavfi test sources), so the files are ours and tiny.
#
#   tools/matroska/make-fixtures.sh <output-dir>
#
# Environment: FFMPEG (default ffmpeg), MKVMERGE (default mkvmerge).
set -euo pipefail
out=${1:?output directory}
FFMPEG=${FFMPEG:-ffmpeg}
MKVMERGE=${MKVMERGE:-mkvmerge}
mkdir -p "$out"
cd "$out"

ff() { "$FFMPEG" -hide_banner -loglevel error -y "$@"; }
v='-f lavfi -i testsrc2=size=160x112:rate=25:duration=3'
a='-f lavfi -i sine=frequency=440:sample_rate=48000:duration=3'
a44='-f lavfi -i sine=frequency=660:sample_rate=44100:duration=3'

# Video codecs, each with audio. -bf 2 gives the H.264 and HEVC files out-of-order presentation times.
ff $v $a -c:v libx264 -bf 2 -g 25 -c:a aac -shortest v-h264-aac.mkv
ff $v $a -c:v libx265 -x265-params log-level=error:bframes=2:keyint=25 -c:a libopus -shortest v-hevc-opus.mkv
ff $v $a -c:v libaom-av1 -cpu-used 8 -g 25 -c:a libopus -shortest v-av1-opus.webm
ff $v $a -c:v libvpx -g 25 -c:a libvorbis -shortest v-vp8-vorbis.webm
ff $v $a -c:v libvpx-vp9 -g 25 -c:a libopus -shortest v-vp9-opus.webm
ff $v $a -c:v mpeg2video -g 12 -bf 2 -c:a ac3 -shortest v-mpeg2-ac3.mkv

# Audio-only files.
ff $a -c:a libvorbis a-vorbis.mka
ff $a -c:a flac a-flac.mka
ff $a -c:a libopus a-opus.mka
ff $a -c:a libmp3lame a-mp3.mka
ff $a -c:a ac3 a-ac3.mka
ff $a -c:a eac3 a-eac3.mka
ff $a -c:a pcm_s16le a-pcm16.mka
ff $a -c:a pcm_f32le a-pcmf32.mka
ff $a -c:a aac a-aac.mka

# Subtitles, attachments, chapters.
cat > subs.srt <<'EOF'
1
00:00:00,500 --> 00:00:01,500
First line

2
00:00:02,000 --> 00:00:02,900
Second line
EOF
cat > subs.ass <<'EOF'
[Script Info]
ScriptType: v4.00+

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour
Style: Default,Arial,20,&H00FFFFFF

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,Hello
Dialogue: 0,0:00:02.00,0:00:02.90,Default,,0,0,0,,World
EOF
printf 'attachment payload\n' > attach.txt
cat > chapters.ffmeta <<'EOF'
;FFMETADATA1
[CHAPTER]
TIMEBASE=1/1000
START=0
END=1500
title=One
[CHAPTER]
TIMEBASE=1/1000
START=1500
END=3000
title=Two
EOF
ff $v $a -i subs.srt -i subs.ass -map 0:v -map 1:a -map 2 -map 3 -c:v libx264 -c:a aac -c:s:0 srt -c:s:1 ass -shortest s-srt-ass.mkv
ff $v $a -i subs.srt -map 0:v -map 1:a -map 2 -c:v libvpx -c:a libvorbis -c:s webvtt -shortest s-webvtt.mkv
ff $v $a -attach attach.txt -metadata:s:t:0 mimetype=text/plain -c:v libx264 -c:a aac -shortest m-attachment.mkv
ff $v $a -i chapters.ffmeta -map_chapters 2 -c:v libx264 -c:a aac -shortest m-chapters.mkv

# Several audio tracks at different rates, and a track with a non-default TimestampScale from mkvmerge.
ff $v $a $a44 -map 0:v -map 1:a -map 2:a -c:v libx264 -c:a aac -shortest m-two-audio.mkv

# ffmpeg layouts: live (unknown sizes, no Cues), Cues at the front, CRC-32 on structural elements, small clusters.
ff $v $a -c:v libx264 -c:a aac -shortest -f matroska -live 1 l-live.mkv
ff $v $a -c:v libx264 -c:a aac -shortest -cues_to_front 1 -write_crc32 1 l-cues-front-crc.mkv
ff $v $a -c:v libx264 -c:a aac -shortest -cluster_size_limit 20000 -cluster_time_limit 400 l-small-clusters.mkv
ff $v $a -c:v libx264 -c:a aac -shortest -reserve_index_space 4096 l-reserved-index.mkv
ff $v $a -c:v libvpx -c:a libvorbis -shortest -f webm -live 1 l-live-webm.webm

# mkvmerge rewrites: no Cues, forced Xiph and EBML lacing, no lacing, BlockGroups only, other TimestampScale, header copy at the end.
src=v-h264-aac.mkv
"$MKVMERGE" -q -o k-no-cues.mkv --no-cues "$src" >/dev/null
"$MKVMERGE" -q -o k-lacing-xiph.mkv --engage lacing_xiph "$src" >/dev/null
"$MKVMERGE" -q -o k-lacing-ebml.mkv --engage lacing_ebml "$src" >/dev/null
"$MKVMERGE" -q -o k-no-lacing.mkv --disable-lacing "$src" >/dev/null
"$MKVMERGE" -q -o k-blockgroups.mkv --engage no_simpleblocks "$src" >/dev/null
"$MKVMERGE" -q -o k-scale-500000.mkv --timestamp-scale 500000 "$src" >/dev/null
"$MKVMERGE" -q -o k-scale-1000333.mkv --timestamp-scale 1000333 "$src" >/dev/null
"$MKVMERGE" -q -o k-headers-twice.mkv --engage write_headers_twice "$src" >/dev/null
"$MKVMERGE" -q -o k-zlib.mkv --compression 0:zlib "$src" >/dev/null
"$MKVMERGE" -q -o k-opus-lacing.mkv --engage lacing_ebml a-opus.mka >/dev/null
"$MKVMERGE" -q -o k-vorbis-lacing.mkv --engage lacing_xiph a-vorbis.mka >/dev/null
"$MKVMERGE" -q -o k-multi.mkv v-hevc-opus.mkv --default-track-flag 1:1 s-srt-ass.mkv >/dev/null 2>&1 || true
"$MKVMERGE" -q -o k-clusters-1s.mkv --cluster-length 1000ms --clusters-in-meta-seek "$src" >/dev/null

rm -f subs.srt subs.ass attach.txt chapters.ffmeta
ls | wc -l

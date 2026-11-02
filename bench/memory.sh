#!/bin/bash
# Peak resident memory of demux, remux, packaging, repair and tag edits on a small file and on one past 4 GiB, one process each.
# usage: memory.sh BENCH_BINARY CORPUS_DIR WORK_DIR   (the work dir is emptied after every run: outputs reach 4 GiB)
B=$1; C=$2; W=$3
run() { # area input
  rm -rf "$W/out"; mkdir -p "$W/out"
  "$B" memcheck "$1" "$C/$2" "$W/out" 2>&1 | tail -1
  rm -rf "$W/out"
}
for area in demux remux package repair tag; do
  case $area in
    demux)   files="samples/MPEG-4/video.mp4 generated/large/over4gb-moov-at-end.mp4 generated/mkv/h264-aac.mkv generated/large/over4gb.mkv generated/avi/tagged-mpeg4-mp3.avi generated/avi/large/odml-4p6gb.avi generated/pcm/wav-pcm_s16le-mono44k.wav generated/large/over4gb-rf64.wav generated/mpeg/ts/mpeg2-interlaced.ts generated/mpeg/large/ts-2gb.ts" ;;
    remux)   files="samples/MPEG-4/video.mp4 generated/large/over4gb-moov-at-end.mp4 generated/mkv/h264-aac.mkv generated/large/over4gb.mkv generated/avi/tagged-mpeg4-mp3.avi generated/avi/large/odml-4p6gb.avi" ;;
    package) files="samples/MPEG-4/video.mp4 generated/large/over4gb-moov-at-end.mp4 generated/mkv/h264-aac.mkv generated/large/over4gb.mkv" ;;
    repair)  files="samples/MPEG-4/video.mp4 generated/large/over4gb-moov-at-end.mp4 generated/mkv/h264-aac.mkv generated/large/over4gb.mkv generated/avi/tagged-mpeg4-mp3.avi generated/avi/large/odml-4p6gb.avi" ;;
    tag)     files="samples/MPEG-4/video.mp4 generated/large/over4gb-moov-at-end.mp4 generated/mkv/h264-aac.mkv generated/large/over4gb.mkv generated/pcm/wav-pcm_s16le-mono44k.wav generated/large/over4gb-rf64.wav" ;;
  esac
  for f in $files; do [ -e "$C/$f" ] && run $area $f || echo "$area\t$f\tnot in the corpus"; done
done

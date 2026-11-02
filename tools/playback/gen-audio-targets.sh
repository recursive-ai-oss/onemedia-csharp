#!/usr/bin/env bash
# Writes a small set of files whose audio a browser profile has to have re-encoded: MP3, Vorbis, FLAC, Opus and PCM at sample rates AAC-LC
# has and lacks, mono and stereo, with and without an audio start offset, and one longer file. Encoded by ffmpeg from generated input
# (lavfi test pattern and sine tone). Nothing here is committed; the output goes to the directory given as the first argument and is used
# as MATRIX for run-makeplayable.sh.
#
#   usage: gen-audio-targets.sh OUTDIR          FFMPEG=/path/to/ffmpeg overrides the encoder binary
set -euo pipefail

OUT="${1:?usage: gen-audio-targets.sh OUTDIR}"
FFMPEG="${FFMPEG:-ffmpeg}"
mkdir -p "$OUT"
cd "$OUT"

V=(-f lavfi -i "testsrc2=size=320x240:rate=25:duration=2")
X264=(-c:v libx264 -preset veryfast -g 25 -sc_threshold 0 -profile:v high -pix_fmt yuv420p)
# tone RATE CHANNELS DURATION: a sine in the first channel of a stereo pair would be silent on the other, so both channels carry a tone.
tone() { echo "aevalsrc=exprs=$(if [ "$2" = 2 ]; then echo '0.5*sin(2*PI*440*t)|0.4*sin(2*PI*660*t)'; else echo '0.5*sin(2*PI*440*t)'; fi):s=$1:d=$3"; }
enc() { "$FFMPEG" -v error -y "$@"; }

enc "${V[@]}" -f lavfi -i "$(tone 44100 2 2)" "${X264[@]}" -c:a libmp3lame -b:a 128k mkv-h264-mp3-44k1-stereo.mkv
enc "${V[@]}" -f lavfi -i "$(tone 22050 1 2)" "${X264[@]}" -c:a libmp3lame -b:a 48k avi-h264-mp3-22k05-mono.avi
enc "${V[@]}" -f lavfi -i "$(tone 44100 2 2)" "${X264[@]}" -c:a libvorbis -b:a 96k mkv-h264-vorbis-44k1-stereo.mkv
enc "${V[@]}" -f lavfi -i "$(tone 44100 2 2)" "${X264[@]}" -c:a flac mkv-h264-flac-44k1-stereo.mkv
enc -f lavfi -i "$(tone 96000 2 2)" -c:a flac flac-96k-stereo.flac
enc "${V[@]}" -f lavfi -i "$(tone 48000 2 2)" "${X264[@]}" -c:a libopus -b:a 96k mkv-h264-opus-48k-stereo.mkv
# The audio starts half a second after the video: the output needs an empty edit as well as the AAC lead-in.
enc "${V[@]}" -itsoffset 0.5 -f lavfi -i "$(tone 44100 2 2)" "${X264[@]}" -c:a libmp3lame -b:a 128k mkv-h264-mp3-offset.mkv
enc -f lavfi -i "$(tone 48000 2 2)" -c:a libopus -b:a 64k mka-opus-48k-stereo.mka
enc -f lavfi -i "$(tone 88200 2 2)" -c:a pcm_s16le wav-pcm16-88k2-stereo.wav
enc -f lavfi -i "$(tone 8000 1 2)" -c:a pcm_s16le wav-pcm16-8k-mono.wav
enc -f lavfi -i "$(tone 32000 2 2)" -c:a pcm_s24le wav-pcm24-32k-stereo.wav
enc -f lavfi -i "$(tone 11025 1 2)" -c:a flac flac-11k025-mono.flac
enc -f lavfi -i "$(tone 44100 2 2)" -c:a libvorbis -b:a 96k ogg-vorbis-44k1-stereo.ogg
enc -f lavfi -i "$(tone 44100 1 30)" -c:a flac flac-44k1-mono-30s.flac
ls | wc -l

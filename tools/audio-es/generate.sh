#!/usr/bin/env bash
# Generates the audio elementary-stream corpus: MP3 (every rate, mode, with and without Xing/Info/LAME, free format), MP2, ADTS AAC, AC-3,
# E-AC-3 and AMR files, tagged MP3 variants, and damaged variants. Everything is made from synthetic input (a sine mixed with noise) by ffmpeg,
# plus tools/audio-es/make_tags.py and make_freeformat.py, so the files are ours to keep and small enough to commit if wanted.
#
#   generate.sh            writes into $ONEMEDIA_HOME/corpus/generated/audio-es and appends a MANIFEST.tsv with SHA-256, size and tool versions
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
O="${ONEMEDIA_HOME:-$HOME/onemedia}"
G="$O/corpus/generated/audio-es"
FF="${FFMPEG:-$O/oracles/bin/ffmpeg}"
PY="${PYTHON:-python3}"
mkdir -p "$G/mp3" "$G/tagged" "$G/aac" "$G/ac3" "$G/amr" "$G/_defects" "$G/_src"
ok=0; failed=0

# enc OUT RATE LAYOUT ENCODER-ARGS...: three seconds of a sine mixed with noise, converted to LAYOUT, encoded with the given arguments.
enc() {
  local out=$1 rate=$2 layout=$3; shift 3
  [ -s "$out" ] && return 0
  if "$FF" -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=$rate:duration=3" -f lavfi -i "anoisesrc=amplitude=0.1:seed=3:sample_rate=$rate:duration=3" \
      -filter_complex "[0][1]amix=inputs=2:duration=first,aformat=channel_layouts=$layout" -bitexact "$@" "$out" 2>"$G/.last.log" && [ -s "$out" ]; then
    ok=$((ok+1))
  else
    rm -f "$out"; failed=$((failed+1)); echo "FAILED $out: $(tail -1 "$G/.last.log")" >&2
  fi
}

M=(-c:a libmp3lame -write_id3v2 0)
# MP3: rates (MPEG-1: 48/44.1/32 kHz, MPEG-2: 24/22.05/16, MPEG-2.5: 12/11.025/8), modes and tag headers
enc "$G/mp3/cbr128-44k-info.mp3"      44100 stereo "${M[@]}" -b:a 128k
enc "$G/mp3/cbr128-44k-noxing.mp3"    44100 stereo "${M[@]}" -b:a 128k -write_xing 0
enc "$G/mp3/cbr128-44k-ffmpeg-id3.mp3" 44100 stereo -c:a libmp3lame -b:a 128k
enc "$G/mp3/vbr-q2-44k-xing.mp3"      44100 stereo "${M[@]}" -q:a 2
enc "$G/mp3/vbr-q2-44k-noxing.mp3"    44100 stereo "${M[@]}" -q:a 2 -write_xing 0
enc "$G/mp3/abr128-44k.mp3"           44100 stereo "${M[@]}" -abr 1 -b:a 128k
enc "$G/mp3/cbr128-48k.mp3"           48000 stereo "${M[@]}" -b:a 128k
enc "$G/mp3/cbr128-32k.mp3"           32000 stereo "${M[@]}" -b:a 128k
enc "$G/mp3/cbr64-24k.mp3"            24000 stereo "${M[@]}" -b:a 64k
enc "$G/mp3/cbr64-22k.mp3"            22050 stereo "${M[@]}" -b:a 64k
enc "$G/mp3/cbr64-16k.mp3"            16000 stereo "${M[@]}" -b:a 64k
enc "$G/mp3/cbr32-12k-mpeg25.mp3"     12000 stereo "${M[@]}" -b:a 32k
enc "$G/mp3/cbr32-11k-mpeg25.mp3"     11025 stereo "${M[@]}" -b:a 32k
enc "$G/mp3/cbr16-8k-mpeg25.mp3"       8000 stereo "${M[@]}" -b:a 16k
enc "$G/mp3/cbr8-8k-mono-mpeg25-noxing.mp3" 8000 mono "${M[@]}" -b:a 8k -write_xing 0
enc "$G/mp3/cbr64-44k-mono.mp3"       44100 mono "${M[@]}" -b:a 64k
enc "$G/mp3/cbr32-22k-mono.mp3"       22050 mono "${M[@]}" -b:a 32k
enc "$G/mp3/cbr320-44k.mp3"           44100 stereo "${M[@]}" -b:a 320k
enc "$G/mp3/cbr64-44k-stereo.mp3"     44100 stereo "${M[@]}" -b:a 64k
enc "$G/mp3/cbr128-44k-crc.mp3"       44100 stereo "${M[@]}" -b:a 128k -error_protection 1
enc "$G/mp3/cbr128-44k-nojoint.mp3"   44100 stereo "${M[@]}" -b:a 128k -joint_stereo 0
enc "$G/mp3/vbr-q6-22k-xing.mp3"      22050 stereo "${M[@]}" -q:a 6
enc "$G/mp3/vbr-q0-48k-noxing.mp3"    48000 stereo "${M[@]}" -q:a 0 -write_xing 0
enc "$G/mp3/cbr192-mp2.mp2"           44100 stereo -c:a mp2 -b:a 192k
enc "$G/mp3/cbr64-22k-mp2.mp2"        22050 stereo -c:a mp2 -b:a 64k
# Free format: the same frames with the bit rate index set to 0 (the frame length is then the distance between headers).
[ -s "$G/mp3/freeformat-128k-44k.mp3" ] || "$PY" "$HERE/make_freeformat.py" "$G/mp3/cbr128-44k-noxing.mp3" "$G/mp3/freeformat-128k-44k.mp3" && ok=$((ok+1))
[ -s "$G/mp3/freeformat-64k-22k.mp3" ] || "$PY" "$HERE/make_freeformat.py" "$G/mp3/cbr64-22k.mp3" "$G/mp3/freeformat-64k-22k.mp3" && ok=$((ok+1))
# A longer file for seeking and the large-file paths.
if [ ! -s "$G/mp3/long-60s.mp3" ]; then
  "$FF" -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=330:sample_rate=44100:duration=60" -bitexact "${M[@]}" -b:a 128k "$G/mp3/long-60s.mp3" && ok=$((ok+1))
fi

# Tagged variants of one plain file (no Xing frame, so the tags are the only addition).
SRC="$G/_src/plain.mp3"
[ -s "$SRC" ] || cp "$G/mp3/cbr128-44k-noxing.mp3" "$SRC"
[ -s "$G/tagged/id3v24-basic.mp3" ] || "$PY" "$HERE/make_tags.py" "$SRC" "$G/tagged"
# The same layouts around an MP3 with an Info frame, so the Xing frame sits behind a tag.
[ -s "$G/_src/info.mp3" ] || cp "$G/mp3/cbr128-44k-info.mp3" "$G/_src/info.mp3"
mkdir -p "$G/tagged-info"
[ -s "$G/tagged-info/id3v24-basic.mp3" ] || "$PY" "$HERE/make_tags.py" "$G/_src/info.mp3" "$G/tagged-info"

# Tags written by other tools (mp3gain for APEv2, id3lib's id3v2 for ID3v2.3 and ID3v1); ExifTool cannot write MP3 files. The files are committed in testdata/audio-es.
mkdir -p "$G/tagged-ext"
cp "$HERE"/../../testdata/audio-es/ext-*.mp3 "$G/tagged-ext/"

# AAC in ADTS
A=(-c:a aac -f adts)
enc "$G/aac/lc-44k-mono.aac"       44100 mono   "${A[@]}" -b:a 64k
enc "$G/aac/lc-48k-stereo.aac"     48000 stereo "${A[@]}" -b:a 96k
enc "$G/aac/lc-32k-stereo.aac"     32000 stereo "${A[@]}" -b:a 64k
enc "$G/aac/lc-22k-stereo.aac"     22050 stereo "${A[@]}" -b:a 48k
enc "$G/aac/lc-16k-mono.aac"       16000 mono   "${A[@]}" -b:a 32k
enc "$G/aac/lc-8k-mono.aac"         8000 mono   "${A[@]}" -b:a 16k
enc "$G/aac/lc-96k-stereo.aac"     96000 stereo "${A[@]}" -b:a 192k
enc "$G/aac/lc-44k-51.aac"         44100 5.1    "${A[@]}" -b:a 256k
enc "$G/aac/lc-48k-quad.aac"       48000 quad   "${A[@]}" -b:a 192k
enc "$G/aac/main-44k-stereo.aac"   44100 stereo "${A[@]}" -profile:a aac_main -b:a 96k
enc "$G/aac/ltp-44k-stereo.aac"    44100 stereo "${A[@]}" -profile:a aac_ltp -b:a 96k
enc "$G/aac/lc-44k-stereo-id3.aac" 44100 stereo "${A[@]}" -b:a 96k -write_id3v2 1
enc "$G/aac/lc-44k-stereo-crc.aac" 44100 stereo "${A[@]}" -b:a 96k -adts_protection 1 2>/dev/null

# AC-3 and E-AC-3
enc "$G/ac3/ac3-48k-stereo-192k.ac3"  48000 stereo -c:a ac3 -b:a 192k
enc "$G/ac3/ac3-44k-stereo-128k.ac3"  44100 stereo -c:a ac3 -b:a 128k
enc "$G/ac3/ac3-32k-stereo-96k.ac3"   32000 stereo -c:a ac3 -b:a 96k
enc "$G/ac3/ac3-48k-mono-96k.ac3"     48000 mono   -c:a ac3 -b:a 96k
enc "$G/ac3/ac3-48k-51-448k.ac3"      48000 5.1    -c:a ac3 -b:a 448k
enc "$G/ac3/ac3-48k-51-640k.ac3"      48000 5.1    -c:a ac3 -b:a 640k
enc "$G/ac3/ac3-44k-51-384k.ac3"      44100 5.1    -c:a ac3 -b:a 384k
enc "$G/ac3/ac3-48k-quad-256k.ac3"    48000 quad   -c:a ac3 -b:a 256k
enc "$G/ac3/ac3-48k-stereo-32k.ac3"   48000 stereo -c:a ac3 -b:a 64k
enc "$G/ac3/eac3-48k-stereo-192k.eac3" 48000 stereo -c:a eac3 -b:a 192k
enc "$G/ac3/eac3-44k-stereo-128k.eac3" 44100 stereo -c:a eac3 -b:a 128k
enc "$G/ac3/eac3-32k-stereo-96k.eac3"  32000 stereo -c:a eac3 -b:a 96k
enc "$G/ac3/eac3-48k-51-448k.eac3"     48000 5.1    -c:a eac3 -b:a 448k
enc "$G/ac3/eac3-48k-mono-96k.eac3"    48000 mono   -c:a eac3 -b:a 96k

# AMR-NB (every mode) and AMR-WB (every mode)
for br in 4750 5150 5900 6700 7400 7950 10200 12200; do
  enc "$G/amr/nb-$br.amr" 8000 mono -c:a libopencore_amrnb -b:a "$br" -f amr
done
for br in 6600 8850 12650 14250 15850 18250 19850 23050 23850; do
  enc "$G/amr/wb-$br.awb" 16000 mono -c:a libvo_amrwbenc -b:a "$br" -f amr
done

# Damaged variants: not part of the packet sweep (the directory name starts with an underscore); each has a documented outcome.
P="$G/mp3/cbr128-44k-noxing.mp3"
[ -s "$G/_defects/truncated-mid-frame.mp3" ] || head -c 32300 "$P" > "$G/_defects/truncated-mid-frame.mp3"
[ -s "$G/_defects/junk-between-frames.mp3" ] || { head -c 5000 "$P"; head -c 300 /dev/zero; tail -c +5001 "$P"; } > "$G/_defects/junk-between-frames.mp3"
[ -s "$G/_defects/leading-garbage.mp3" ] || { head -c 57 /dev/urandom | tr '\377' '\376'; cat "$P"; } > "$G/_defects/leading-garbage.mp3"
[ -s "$G/_defects/trailing-garbage.mp3" ] || { cat "$P"; head -c 100 /dev/zero; } > "$G/_defects/trailing-garbage.mp3"
[ -s "$G/_defects/id3v2-size-too-big.mp3" ] || { printf 'ID3\x04\x00\x00\x00\x7f\x7f\x7f'; cat "$P"; } > "$G/_defects/id3v2-size-too-big.mp3"

{
  printf 'path\tsha256\tsize\n'
  (cd "$G" && find . -type f ! -name 'MANIFEST.tsv' ! -name '.last.log' | sort | while read -r f; do printf '%s\t%s\t%s\n' "$f" "$(sha256sum "$f" | cut -d' ' -f1)" "$(stat -c %s "$f")"; done)
} > "$G/MANIFEST.tsv"
echo "# generated with $("$FF" -version | head -1 | cut -d' ' -f1-3)" >> "$G/MANIFEST.tsv"
echo "audio-es corpus: $ok made, $failed failed, $(find "$G" -type f -not -name '*.tsv' -not -name '.last.log' | wc -l) files"

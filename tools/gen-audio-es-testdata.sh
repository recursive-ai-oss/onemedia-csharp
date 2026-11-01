#!/usr/bin/env bash
# Regenerates the tiny audio elementary-stream fixtures in testdata/audio-es: MP3 (with and without a LAME/Xing frame, MPEG-2, mono), MP2,
# ADTS AAC, AC-3, E-AC-3, AMR-NB and AMR-WB, and the tagged MP3 variants of tools/audio-es/make_tags.py. The input is a synthetic sine written
# by ffmpeg (version in VERSIONS.txt); the AMR files are laid out by this script from the storage format (RFC 4867 §5), because ffmpeg builds
# without an AMR encoder cannot write them. The tagged variants are one file's audio with each tag layout around it.
# Usage: tools/gen-audio-es-testdata.sh [ffmpeg]
set -eu
FF=${1:-ffmpeg}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../testdata/audio-es"
mkdir -p "$OUT/tagged"; cd "$OUT"
Q="-hide_banner -loglevel error -y -bitexact"
sine() { echo "-f lavfi -i sine=frequency=440:sample_rate=$1:duration=0.3"; }
$FF $Q $(sine 44100) -c:a libmp3lame -b:a 48k -write_id3v2 0 lame-info.mp3
$FF $Q $(sine 44100) -c:a libmp3lame -b:a 48k -write_id3v2 0 -write_xing 0 plain.mp3
$FF $Q $(sine 22050) -ac 1 -c:a libmp3lame -b:a 24k -write_id3v2 0 mpeg2-mono.mp3
$FF $Q $(sine 44100) -c:a mp2 -b:a 96k plain.mp2
$FF $Q $(sine 44100) -c:a aac -b:a 32k -f adts lc.aac
$FF $Q $(sine 48000) -c:a ac3 -b:a 96k ac3.ac3
$FF $Q $(sine 48000) -c:a eac3 -b:a 96k eac3.eac3
python3 - <<'PY'
import random
r = random.Random(7)
def frames(tocs, sizes, n):
    out = bytearray()
    for i in range(n):
        k = i % len(tocs)
        out += bytes([tocs[k]]) + bytes(r.randrange(1, 256) for _ in range(sizes[k]))
    return bytes(out)
# AMR-NB: modes 7 (12.2 kbit/s, 31 bytes) and 0 (4.75 kbit/s, 12 bytes); AMR-WB: modes 8 (23.85, 60 bytes) and 0 (6.60, 17 bytes).
open('nb.amr', 'wb').write(b'#!AMR\n' + frames([0x3C, 0x04], [31, 12], 40))
open('wb.awb', 'wb').write(b'#!AMR-WB\n' + frames([0x44, 0x04], [60, 17], 40))
PY
python3 "$HERE/audio-es/make_tags.py" plain.mp3 tagged
{ $FF -version | head -1; } > VERSIONS.txt
ls tagged | wc -l
du -sh .

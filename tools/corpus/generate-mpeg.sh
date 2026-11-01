#!/usr/bin/env bash
# Generates MPEG transport stream, program stream and raw video test files with ffmpeg from synthetic input (lavfi test sources),
# plus damaged variants made by ts_tools.py. Writes into $ONEMEDIA_HOME/corpus/generated/mpeg and a manifest
# there (MANIFEST.tsv: path, tool, sha256, size, command).
#
#   generate-mpeg.sh [FILTER]   FILTER is a grep pattern on the output path (default: everything)
#   LARGE=1 generate-mpeg.sh    also write the 2 GB transport stream used for the I/O and memory checks
#
# Re-running keeps existing files. A codec the pinned ffmpeg cannot encode is reported as FAILED and the run continues.
set -uo pipefail

O="${ONEMEDIA_HOME:-$HOME/onemedia}"
G="$O/corpus/generated/mpeg"
FF="${FFMPEG:-$O/oracles/bin/ffmpeg}"
PY="${PYTHON:-python3}"
TOOLS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FILTER="${1:-.}"
mkdir -p "$G"
MAN="$G/MANIFEST.tsv"
[ -s "$MAN" ] || printf 'path\ttool\tsha256\tsize\tcommand\n' > "$MAN"
ffv=$("$FF" -version | head -1 | cut -d' ' -f1-3)
ok=0; failed=0; skipped=0

FFG=(-hide_banner -loglevel error -y)
FFO=(-bitexact -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1)
vid() { echo -f lavfi -i "testsrc2=size=$1:rate=$2:duration=${3:-4}"; }
tone() { echo -f lavfi -i "sine=frequency=${1:-440}:sample_rate=${2:-48000}:duration=${3:-4}"; }

record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$(sha256sum "$G/$1" | cut -d' ' -f1)" "$(stat -c %s "$G/$1")" "$3" >> "$MAN"; }

# gen REL LABEL RUNNER ARGS...: runs RUNNER with ARGS where @OUT@ is the output path.
gen() {
  local rel=$1 label=$2 runner=$3; shift 3
  echo "$rel" | grep -qE "$FILTER" || return 0
  local out="$G/$rel"
  if [ -s "$out" ]; then skipped=$((skipped+1)); return 0; fi
  mkdir -p "$(dirname "$out")"
  local part="${out%.*}.part.${out##*.}" args=() a
  for a in "$@"; do args+=("${a//@OUT@/$part}"); done
  if "$runner" "${args[@]}" >"$G/.last.log" 2>&1 && [ -s "$part" ]; then
    mv "$part" "$out"; record "$rel" "$label" "$runner $*"; ok=$((ok+1))
  else
    rm -f "$part"; failed=$((failed+1)); echo "FAILED $rel: $(tail -1 "$G/.last.log")" >&2
  fi
}
ff() { local rel=$1; shift; gen "$rel" "$ffv" "$FF" "${FFG[@]}" "$@" "${FFO[@]}" @OUT@; }
tool() { local rel=$1 src=$2; shift 2; gen "$rel" "ts_tools.py" "$PY" "$TOOLS/ts_tools.py" "$@" "$G/$src" @OUT@; }

X264=(-c:v libx264 -preset veryfast -threads 1 -pix_fmt yuv420p)
X265=(-c:v libx265 -preset ultrafast -x265-params log-level=error:pools=1:frame-threads=1 -pix_fmt yuv420p)
M2V=(-c:v mpeg2video -pix_fmt yuv420p)

# --- transport streams -----------------------------------------------------------------------
V="$(vid 320x240 25)"; A="$(tone 440 48000)"
ff ts/h264-aac.ts   $V $A "${X264[@]}" -g 25 -bf 2 -c:a aac -f mpegts
ff ts/h264-ac3.ts   $V $A "${X264[@]}" -g 25 -bf 2 -c:a ac3 -f mpegts
ff ts/h264-mp2.ts   $V $A "${X264[@]}" -g 25 -bf 0 -c:a mp2 -f mpegts
ff ts/h264-mp3.ts   $V $A "${X264[@]}" -g 25 -c:a libmp3lame -f mpegts
ff ts/h264-eac3.ts  $V $A "${X264[@]}" -g 25 -c:a eac3 -f mpegts
ff ts/h264-only.ts  $V "${X264[@]}" -g 12 -bf 3 -b_strategy 2 -f mpegts
ff ts/h264-baseline.ts $V $A "${X264[@]}" -profile:v baseline -g 30 -c:a aac -f mpegts
ff ts/h264-high-50fps.ts $(vid 640x360 50) $A "${X264[@]}" -profile:v high -g 50 -bf 4 -c:a aac -f mpegts
ff ts/h264-30000-1001.ts $(vid 320x240 30000/1001) $A "${X264[@]}" -g 30 -bf 2 -c:a aac -f mpegts
ff ts/h264-interlaced.ts $V $A "${X264[@]}" -flags +ildct+ilme -x264opts tff=1 -g 25 -c:a aac -f mpegts
ff ts/hevc-aac.ts   $V $A "${X265[@]}" -g 25 -c:a aac -f mpegts
ff ts/hevc-ac3.ts   $V $A "${X265[@]}" -g 25 -bf 2 -c:a ac3 -f mpegts
ff ts/hevc-only.ts  $V "${X265[@]}" -g 25 -f mpegts
ff ts/mpeg2-mp2.ts  $V $A "${M2V[@]}" -g 12 -bf 2 -b:v 1M -c:a mp2 -f mpegts
ff ts/mpeg2-ac3.ts  $V $A "${M2V[@]}" -g 15 -bf 2 -b:v 2M -c:a ac3 -f mpegts
ff ts/mpeg2-interlaced.ts $V $A "${M2V[@]}" -flags +ildct+ilme -top 1 -g 12 -b:v 2M -c:a mp2 -f mpegts
ff ts/mpeg1-mp2.ts  $V $A -c:v mpeg1video -g 12 -b:v 500k -c:a mp2 -f mpegts
ff ts/mpeg4-aac.ts  $V $A -c:v mpeg4 -g 25 -c:a aac -f mpegts
ff ts/aac-only.ts   $A -c:a aac -f mpegts
ff ts/mp2-only.ts   $A -c:a mp2 -f mpegts
ff ts/ac3-only.ts   $A -c:a ac3 -f mpegts
ff ts/opus-h264.ts  $V $A "${X264[@]}" -g 25 -c:a libopus -strict -2 -f mpegts
ff ts/h264-3audio.ts $V $A $(tone 880 48000) $(tone 220 48000) -map 0:v -map 1:a -map 2:a -map 3:a "${X264[@]}" -g 25 -c:a aac -metadata:s:a:0 language=eng -metadata:s:a:1 language=fra -metadata:s:a:2 language=deu -f mpegts
ff ts/two-programs.ts $V $A $V $A -map 0:v -map 1:a -map 2:v -map 3:a "${X264[@]}" -g 25 -c:a aac -program title=first:st=0:st=1 -program title=second:st=2:st=3 -f mpegts
ff ts/named-service.ts $V $A "${X264[@]}" -g 25 -c:a aac -metadata service_name="Test Channel" -metadata service_provider="OneMedia Tests" -f mpegts
ff ts/pat-pmt-fast.ts $V $A "${X264[@]}" -g 25 -c:a aac -pat_period 0.2 -sdt_period 0.5 -mpegts_flags +resend_headers+pat_pmt_at_frames -f mpegts
ff ts/wrap-33bit.ts $V $A "${X264[@]}" -g 25 -c:a aac -output_ts_offset 95441 -f mpegts
ff ts/pid-custom.ts $V $A "${X264[@]}" -g 25 -c:a aac -mpegts_start_pid 300 -mpegts_pmt_start_pid 4096 -mpegts_original_network_id 7 -mpegts_transport_stream_id 9 -f mpegts

# M2TS (192-byte packets: four-byte TP_extra_header in front)
ff m2ts/h264-ac3.m2ts $V $A "${X264[@]}" -g 25 -bf 2 -c:a ac3 -f mpegts -mpegts_m2ts_mode 1
ff m2ts/h264-aac.m2ts $V $A "${X264[@]}" -g 25 -c:a aac -f mpegts -mpegts_m2ts_mode 1
ff m2ts/hevc-ac3.m2ts $V $A "${X265[@]}" -g 25 -c:a ac3 -f mpegts -mpegts_m2ts_mode 1
ff m2ts/mpeg2-ac3.m2ts $V $A "${M2V[@]}" -g 12 -bf 2 -b:v 2M -c:a ac3 -f mpegts -mpegts_m2ts_mode 1

# Variants made from the files above: 204-byte packets, lost packets, truncation, damage in the middle
tool ts/h264-aac-204.ts   ts/h264-aac.ts   parity204
tool ts/h264-ac3-cc-gaps.ts ts/h264-ac3.ts  drop --pid auto-video --every 40 --count 5
tool ts/h264-aac-truncated.ts ts/h264-aac.ts truncate --bytes -123
tool ts/h264-aac-garbage.ts ts/h264-aac.ts  garbage --at 0.5 --bytes 83
tool ts/h264-aac-errorflag.ts ts/h264-aac.ts errorflag --pid auto-video --every 30 --count 4
tool ts/h264-aac-dups.ts  ts/h264-aac.ts   duplicate --pid auto-video --every 50 --count 3

# --- program streams ---------------------------------------------------------------------------
ff ps/mpeg2-mp2.vob  $V $A "${M2V[@]}" -g 12 -bf 2 -b:v 1M -c:a mp2 -f vob
ff ps/dvd-ac3.vob    $V $A "${M2V[@]}" -g 12 -bf 2 -b:v 2M -c:a ac3 -f dvd
ff ps/dvd-ac3-ntsc.vob $(vid 720x480 30000/1001) $A "${M2V[@]}" -g 15 -bf 2 -b:v 4M -c:a ac3 -f dvd -target ntsc-dvd
ff ps/dvd-pcm.vob    $V $A "${M2V[@]}" -g 12 -b:v 2M -c:a pcm_s16be -f dvd
ff ps/mpeg1-mp2.mpg  $V $A -c:v mpeg1video -g 12 -b:v 500k -c:a mp2 -f mpeg
ff ps/mpeg1-vcd.mpg  $(vid 352x240 30000/1001) $A -target ntsc-vcd
ff ps/mpeg2-svcd.mpg $(vid 480x480 30000/1001) $A -target ntsc-svcd
ff ps/mpeg2-only.mpg $V "${M2V[@]}" -g 12 -bf 2 -b:v 1M -f mpeg2video
ff ps/h264-mp2.mpg   $V $A "${X264[@]}" -g 25 -c:a mp2 -f mpeg -strict -2
ff ps/mpeg2-mp3.mpg  $V $A "${M2V[@]}" -g 12 -b:v 1M -c:a libmp3lame -f mpeg
ff ps/mpeg2-audio-only.mpg $A -c:a mp2 -f mpeg
ff ps/mpeg2-interlaced.vob $V $A "${M2V[@]}" -flags +ildct+ilme -top 1 -g 12 -b:v 3M -c:a ac3 -f vob

# --- raw video ---------------------------------------------------------------------------------
n=0
for size in 160x120 320x240 640x360; do for rate in 24 25 30000/1001; do
  n=$((n+1))
  ff "es/h264-$n.264" $(vid $size $rate 3) "${X264[@]}" -g 20 -bf $((n % 4)) -f h264
  ff "es/hevc-$n.265" $(vid $size $rate 3) "${X265[@]}" -g 20 -bf $((n % 3)) -f hevc
  ff "es/mpeg2-$n.m2v" $(vid $size $rate 3) "${M2V[@]}" -g 12 -bf $((n % 3)) -b:v 1M -f mpeg2video
  ff "es/mpeg1-$n.m1v" $(vid $size $rate 3) -c:v mpeg1video -g 12 -b:v 600k -pix_fmt yuv420p -f mpeg1video
  ff "es/av1-$n.obu" $(vid $size $rate 3) -c:v libaom-av1 -cpu-used 8 -g 20 -crf 40 -b:v 0 -threads 1 -pix_fmt yuv420p -f obu
  ff "es/av1-$n.ivf" $(vid $size $rate 3) -c:v libaom-av1 -cpu-used 8 -g 20 -crf 40 -b:v 0 -threads 1 -pix_fmt yuv420p -f ivf
  ff "es/vp9-$n.ivf" $(vid $size $rate 3) -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -g 20 -threads 1 -pix_fmt yuv420p -f ivf
  ff "es/vp8-$n.ivf" $(vid $size $rate 3) -c:v libvpx -deadline realtime -cpu-used 8 -b:v 300k -g 20 -threads 1 -pix_fmt yuv420p -f ivf
done; done
ff es/h264-nosc.264 $(vid 320x240 25 3) "${X264[@]}" -g 20 -bsf:v h264_mp4toannexb -f h264
ff es/h264-baseline.264 $(vid 320x240 25 3) "${X264[@]}" -profile:v baseline -g 20 -f h264
ff es/h264-high10.264 $(vid 320x240 25 3) -c:v libx264 -preset veryfast -threads 1 -pix_fmt yuv420p10le -profile:v high10 -g 20 -f h264
ff es/h264-4k-noparam-first.264 $(vid 1280x720 25 2) "${X264[@]}" -g 20 -x264-params repeat-headers=1 -f h264

# --- large files (I/O and memory checks) -------------------------------------------------------
if [ "${LARGE:-0}" = 1 ]; then
  ff large/ts-2gb-seed.ts $(vid 1280x720 25 120) $(tone 440 48000 120) "${X264[@]}" -g 50 -bf 2 -b:v 12M -maxrate 12M -bufsize 6M -c:a ac3 -f mpegts
  # The pinned ffmpeg and ffprobe crash on the SDT that ffmpeg itself writes, so the seed is stripped before it is looped and the result again.
  if [ -s "$G/large/ts-2gb-seed.ts" ] && [ ! -s "$G/large/ts-2gb.ts" ]; then
    reps=$(( 2100000000 / $(stat -c %s "$G/large/ts-2gb-seed.ts") + 1 ))
    "$PY" "$TOOLS/ts_tools.py" stripsdt "$G/large/ts-2gb-seed.ts" "$G/large/ts-seed-nosdt.ts" &&
      "$FF" "${FFG[@]}" -stream_loop "$reps" -i "$G/large/ts-seed-nosdt.ts" -c copy -f mpegts "$G/large/ts-2gb-sdt.ts" &&
      "$PY" "$TOOLS/ts_tools.py" stripsdt "$G/large/ts-2gb-sdt.ts" "$G/large/ts-2gb.ts" &&
      record large/ts-2gb.ts "$ffv" "ffmpeg -stream_loop $reps -c copy, then ts_tools.py stripsdt" && ok=$((ok+1)) || failed=$((failed+1))
    rm -f "$G/large/ts-2gb-sdt.ts" "$G/large/ts-seed-nosdt.ts"
  fi
fi

echo "mpeg generator: $ok written, $skipped kept, $failed failed"

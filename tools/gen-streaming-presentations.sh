#!/usr/bin/env bash
# Writes HLS and DASH presentations with ffmpeg from generated (lavfi) input, and optionally from files given with --from, so the presentation
# reader can be compared with ffprobe on the same playlists.
#
#   gen-streaming-presentations.sh OUTDIR [--small | --long] [--from FILE]...
#
# --small writes the handful of two-to-three second presentations that are committed as test data. --long writes three 100-segment
# presentations (HLS with TS segments, HLS with fMP4 segments, DASH) for the I/O measurement. Without either the full matrix is written.
# Tools come from $ONEMEDIA_HOME/oracles/bin when present, else the PATH. OUTDIR/MANIFEST.tsv records name, kind, ffmpeg version and files.
set -uo pipefail
out="${1:?usage: gen-streaming-presentations.sh OUTDIR [--small] [--from FILE]...}"; shift
small=0; long=0; from=()
while [ $# -gt 0 ]; do
  case "$1" in
    --small) small=1 ;;
    --long) long=1 ;;
    --from) shift; from+=("$1") ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
  shift
done
home="${ONEMEDIA_HOME:-$HOME/onemedia}"
ffmpeg="$home/oracles/bin/ffmpeg"; [ -x "$ffmpeg" ] || ffmpeg=ffmpeg
version="$("$ffmpeg" -version | head -1 | cut -d' ' -f3)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
manifest="$out/MANIFEST.tsv"; : > "$manifest"
dur=6; [ $small = 1 ] && dur=2; [ $long = 1 ] && dur=100
vsrc=(-f lavfi -i "testsrc2=size=160x120:rate=25:duration=$dur")
asrc=(-f lavfi -i "sine=frequency=440:sample_rate=44100:duration=$dur")
v264=(-c:v libx264 -preset ultrafast -b:v 80k -maxrate 80k -bufsize 160k -g 25 -keyint_min 25 -sc_threshold 0 -pix_fmt yuv420p)
v265=(-c:v libx265 -preset ultrafast -b:v 80k -g 25 -keyint_min 25 -x265-params "scenecut=0:log-level=error" -pix_fmt yuv420p)
aaac=(-c:a aac -b:a 32k -ar 44100)
failed=0

# run NAME KIND DIR-RELATIVE-PLAYLIST ffmpeg-args...: runs ffmpeg in a fresh directory and records the result.
run() {
  local name="$1" kind="$2" playlist="$3"; shift 3
  local dir="$out/$name"; rm -rf "$dir"; mkdir -p "$dir"
  if (cd "$dir" && "$ffmpeg" -v error -y "$@" >"$dir/.ffmpeg.log" 2>&1) && [ -s "$dir/$playlist" ]; then
    rm -f "$dir/.ffmpeg.log"
    printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$kind" "$playlist" "$version" "$(find "$dir" -type f | wc -l)" >> "$manifest"
  else
    echo "FAILED $name: $(tail -2 "$dir/.ffmpeg.log" | tr '\n' ' ')" >&2; failed=$((failed+1)); rm -rf "$dir"
  fi
}

hls() { # hls NAME ffmpeg-input-and-codec-args... -- hls-args...
  local name="$1"; shift
  run "$name" hls index.m3u8 "$@"
}

if [ $long = 1 ]; then
  hlsargs=(-f hls -hls_playlist_type vod)
  run hls-ts-100 hls index.m3u8 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_segment_filename 'seg%03d.ts' index.m3u8
  run hls-fmp4-100 hls index.m3u8 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  run dash-100 dash manifest.mpd "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" -f dash -seg_duration 1 -init_seg_name 'init-$RepresentationID$.m4s' -media_seg_name 'chunk-$RepresentationID$-$Number%05d$.m4s' manifest.mpd
  echo "wrote $(wc -l < "$manifest") presentations to $out"
  exit 0
fi

# ---- HLS --------------------------------------------------------------------------------------------------------------------------------
hlsargs=(-f hls -hls_playlist_type vod)
hls hls-ts-av "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_segment_filename 'seg%03d.ts' index.m3u8
hls hls-fmp4-av "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
hls hls-ts-single "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_flags single_file -hls_segment_filename 'media.ts' index.m3u8
hls hls-fmp4-single "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 1 -hls_segment_type fmp4 -hls_flags single_file -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'media.mp4' index.m3u8
if [ $small = 0 ]; then
  hls hls-ts-v "${vsrc[@]}" "${v264[@]}" -an "${hlsargs[@]}" -hls_time 2 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-ts-a "${asrc[@]}" "${aaac[@]}" -vn "${hlsargs[@]}" -hls_time 2 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-fmp4-v "${vsrc[@]}" "${v264[@]}" -an "${hlsargs[@]}" -hls_time 2 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  hls hls-fmp4-a "${asrc[@]}" "${aaac[@]}" -vn "${hlsargs[@]}" -hls_time 2 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  hls hls-ts-hevc "${vsrc[@]}" "${asrc[@]}" "${v265[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 2 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-fmp4-hevc "${vsrc[@]}" "${asrc[@]}" "${v265[@]}" -tag:v hvc1 "${aaac[@]}" "${hlsargs[@]}" -hls_time 2 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  hls hls-ts-ac3 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" -c:a ac3 -b:a 96k "${hlsargs[@]}" -hls_time 2 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-ts-mpeg2 "${vsrc[@]}" "${asrc[@]}" -c:v mpeg2video -b:v 200k -g 25 -pix_fmt yuv420p "${aaac[@]}" "${hlsargs[@]}" -hls_time 2 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-ts-seg3 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 3 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-ts-pdt "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 2 -hls_flags program_date_time+independent_segments -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls hls-fmp4-seg3 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${hlsargs[@]}" -hls_time 3 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  # Two variants in one multivariant playlist, audio muxed into each.
  run hls-ts-master2 hls master.m3u8 "${vsrc[@]}" "${asrc[@]}" -filter_complex "[0:v]split=2[a][b];[b]scale=80:60[c]" -map "[a]" -map "[c]" -map 1:a -map 1:a "${v264[@]}" "${aaac[@]}" -f hls -hls_time 2 -hls_playlist_type vod -master_pl_name master.m3u8 -var_stream_map "v:0,a:0 v:1,a:1" -hls_segment_filename 'v%v_seg%03d.ts' 'v%v.m3u8'
  run hls-fmp4-master2 hls master.m3u8 "${vsrc[@]}" "${asrc[@]}" -filter_complex "[0:v]split=2[a][b];[b]scale=80:60[c]" -map "[a]" -map "[c]" -map 1:a -map 1:a "${v264[@]}" "${aaac[@]}" -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_type fmp4 -master_pl_name master.m3u8 -var_stream_map "v:0,a:0 v:1,a:1" -hls_fmp4_init_filename 'v%v_init.mp4' -hls_segment_filename 'v%v_seg%03d.m4s' 'v%v.m3u8'
  # A separate audio rendition, listed in an audio group.
  run hls-ts-audiogroup hls master.m3u8 "${vsrc[@]}" "${asrc[@]}" -map 0:v -map 1:a "${v264[@]}" "${aaac[@]}" -f hls -hls_time 2 -hls_playlist_type vod -master_pl_name master.m3u8 -var_stream_map "v:0,agroup:aud a:0,agroup:aud,default:yes" -hls_segment_filename 'v%v_seg%03d.ts' 'v%v.m3u8'
  run hls-fmp4-audiogroup hls master.m3u8 "${vsrc[@]}" "${asrc[@]}" -map 0:v -map 1:a "${v264[@]}" "${aaac[@]}" -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_type fmp4 -master_pl_name master.m3u8 -var_stream_map "v:0,agroup:aud a:0,agroup:aud,default:yes" -hls_fmp4_init_filename 'v%v_init.mp4' -hls_segment_filename 'v%v_seg%03d.m4s' 'v%v.m3u8'
fi

# ---- DASH -------------------------------------------------------------------------------------------------------------------------------
dash() { local name="$1"; shift; run "$name" dash manifest.mpd "$@"; }
dashargs=(-f dash -seg_duration 1 -init_seg_name 'init-$RepresentationID$.m4s' -media_seg_name 'chunk-$RepresentationID$-$Number%05d$.m4s')
dash dash-av-timeline "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" manifest.mpd
dash dash-av-number "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -use_timeline 0 manifest.mpd
dash dash-av-list "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -use_template 0 -use_timeline 0 manifest.mpd
dash dash-av-single "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -single_file 1 -single_file_name 'media-$RepresentationID$.mp4' manifest.mpd
if [ $small = 0 ]; then
  dash dash-v "${vsrc[@]}" "${v264[@]}" -an "${dashargs[@]}" -seg_duration 2 manifest.mpd
  dash dash-a "${asrc[@]}" "${aaac[@]}" -vn "${dashargs[@]}" -seg_duration 2 manifest.mpd
  dash dash-hevc "${vsrc[@]}" "${asrc[@]}" "${v265[@]}" -tag:v hvc1 "${aaac[@]}" "${dashargs[@]}" -seg_duration 2 manifest.mpd
  dash dash-seg3 "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -seg_duration 3 manifest.mpd
  dash dash-webm "${vsrc[@]}" "${asrc[@]}" -c:v libvpx-vp9 -b:v 80k -g 25 -keyint_min 25 -c:a libopus -b:a 32k -f dash -dash_segment_type webm -seg_duration 2 -init_seg_name 'init-$RepresentationID$.webm' -media_seg_name 'chunk-$RepresentationID$-$Number%05d$.webm' manifest.mpd
  dash dash-av-single-list "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -single_file 1 -use_template 0 -use_timeline 0 -single_file_name 'media-$RepresentationID$.mp4' manifest.mpd
  dash dash-2reps "${vsrc[@]}" "${asrc[@]}" -filter_complex "[0:v]split=2[a][b];[b]scale=80:60[c]" -map "[a]" -map "[c]" -map 1:a "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -seg_duration 2 -adaptation_sets "id=0,streams=v id=1,streams=a" manifest.mpd
  dash dash-streaming "${vsrc[@]}" "${asrc[@]}" "${v264[@]}" "${aaac[@]}" "${dashargs[@]}" -seg_duration 2 -streaming 1 -ldash 1 manifest.mpd
fi

# ---- from given files (stream copy; needs a keyframe about every second) -----------------------------------------------------------------
i=0
for f in "${from[@]}"; do
  i=$((i+1)); n=$(printf 'f%02d' "$i")
  hls "hls-ts-$n" -i "$f" -t 20 -c copy "${hlsargs[@]}" -hls_time 4 -hls_segment_filename 'seg%03d.ts' index.m3u8
  hls "hls-fmp4-$n" -i "$f" -t 20 -c copy "${hlsargs[@]}" -hls_time 4 -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename 'seg%03d.m4s' index.m3u8
  dash "dash-$n" -i "$f" -t 20 -c copy "${dashargs[@]}" -seg_duration 4 manifest.mpd
done

echo "wrote $(wc -l < "$manifest") presentations to $out ($failed failed), ffmpeg $version"

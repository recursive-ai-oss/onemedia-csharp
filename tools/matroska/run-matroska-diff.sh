#!/usr/bin/env bash
# Runs the Matroska checks against the oracle binaries: packet differential against ffprobe, mkvinfo structure and track headers,
# forward-only equality, and the OneAudio comparison. Writes fixtures with make-fixtures.sh when the work directory has none.
#
#   tools/matroska/run-matroska-diff.sh <work-dir> [extra-media-dir...]
#
# Environment: ORACLES (directory with ffprobe, ffmpeg, mkvinfo, mkvmerge; default ~/onemedia/oracles/bin),
#              TEST_FILES (directory holding the published test1.mkv..test8.mkv).
# Needs the .NET SDK on PATH. Reports one line per file and a total; exit status is non-zero when any file differs without an explanation.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="${1:?work directory}"
shift
ORACLES="${ORACLES:-$HOME/onemedia/oracles/bin}"
export ONEMEDIA_SCRATCH="$work"   # the build writes to $work/artifacts
art="$work/artifacts"
mkdir -p "$work/logs" "$work/fx"

"$ORACLES/ffprobe" -version | head -1
"$ORACLES/mkvinfo" --version

dotnet build "$here/tools/OneMedia.MatroskaDiff" -c Release -v quiet -clp:NoSummary || exit 2
dotnet build "$here/tools/OneMedia.MatroskaOneAudio" -c Release -v quiet -clp:NoSummary || exit 2
diff_dll="$art/bin/OneMedia.MatroskaDiff/release/OneMedia.MatroskaDiff.dll"
oa_dll="$art/bin/OneMedia.MatroskaOneAudio/release/OneMedia.MatroskaOneAudio.dll"

if [ -z "$(ls -A "$work/fx")" ]; then
  FFMPEG="$ORACLES/ffmpeg" MKVMERGE="$ORACLES/mkvmerge" "$here/tools/matroska/make-fixtures.sh" "$work/fx" > "$work/logs/fixtures.log" 2>&1
fi

files=("$work"/fx/*.mk? "$work"/fx/*.webm)
if [ -n "${TEST_FILES:-}" ]; then files+=("$TEST_FILES"/test[1-8].mkv); fi
for extra in "$@"; do files+=("$extra"/*.mk? "$extra"/*.webm); done

bad=0; n=0
for f in "${files[@]}"; do
  [ -f "$f" ] || continue
  n=$((n+1))
  b="$(basename "$f")"
  out="$work/logs/diff-$b.log"
  dotnet "$diff_dll" diff "$f" --ffprobe "$ORACLES/ffprobe" > "$out" 2>&1; rc=$?
  echo "diff $b rc=$rc $(tail -n 1 "$out")"
  [ $rc -eq 0 ] || bad=$((bad+1))
done
echo "ffprobe differential: $n files, $bad with unexplained differences"

for f in "${files[@]}"; do
  [ -f "$f" ] || continue
  b="$(basename "$f")"
  dotnet "$diff_dll" mkvinfo "$f" --mkvinfo "$ORACLES/mkvinfo" > "$work/logs/mkvinfo-$b.log" 2>&1; rc1=$?
  dotnet "$diff_dll" mkvtracks "$f" --mkvinfo "$ORACLES/mkvinfo" > "$work/logs/mkvtracks-$b.log" 2>&1; rc2=$?
  echo "mkvinfo $b structure rc=$rc1: $(tail -n 1 "$work/logs/mkvinfo-$b.log") | tracks rc=$rc2: $(tail -n 1 "$work/logs/mkvtracks-$b.log")"
done

# Forward-only: the same packets through a pipe as through the seekable source.
fbad=0; fn=0
for f in "${files[@]}"; do
  [ -f "$f" ] || continue
  a="$(dotnet "$diff_dll" digest "$f" 2>&1 | tail -n 1)"
  c="$(cat "$f" | dotnet "$diff_dll" digest-stdin - 2>&1 | tail -n 1)"
  fn=$((fn+1))
  if [ "$a" != "$c" ]; then fbad=$((fbad+1)); echo "forward-only differs: $(basename "$f"): $a / $c"; fi
done
echo "forward-only: $fn files, $fbad differ"

# OneAudio 1.4.0: the audio packets, configuration and priming against its Ogg and MP4 readers on the same audio remuxed by ffmpeg.
mkdir -p "$work/oa"
for f in a-opus.mka k-opus-lacing.mkv v-hevc-opus.mkv v-vp9-opus.webm a-vorbis.mka k-vorbis-lacing.mkv v-vp8-vorbis.webm a-flac.mka a-aac.mka v-h264-aac.mkv; do
  [ -f "$work/fx/$f" ] || continue
  case "$f" in *aac*) ext=m4a ;; *) ext=ogg ;; esac
  o="$work/oa/$f.$ext"
  "$ORACLES/ffmpeg" -hide_banner -loglevel error -y -i "$work/fx/$f" -map 0:a:0 -c copy "$o" || { echo "oneaudio $f: ffmpeg remux failed"; continue; }
  echo "oneaudio $f: $(dotnet "$oa_dll" "$work/fx/$f" "$o" 2>&1 | tr '\n' ' ')"
done
exit $bad

#!/usr/bin/env bash
# Builds, instruments and runs a SharpFuzz target under libFuzzer, with a time budget. Adapted from the OneAudio fuzz script.
#
#   tools/fuzz.sh setup                       install the tools (once per machine)
#   tools/fuzz.sh run <target> [seconds]      fuzz (targets are listed in fuzz/OneMedia.Fuzz/Program.cs; default isobmff)
#   tools/fuzz.sh replay <target> <file>...   run saved inputs through the plain (uninstrumented) build
#
# Environment:
#   ONEMEDIA_FUZZ_HOME  tools and working directories (default ~/.cache/onemedia-fuzz)
#   FUZZ_MAX_LEN        longest input in bytes (default 262144)
#   FUZZ_PREBUILT       directory with bin/ and bin-plain/ from an earlier build, reused instead of building per target
# Needs the .NET SDK on PATH, curl and sha256sum. The libFuzzer driver comes as a pinned release binary (a fuzzing driver only; nothing here ships).
# Crashing and hanging inputs land in $ONEMEDIA_FUZZ_HOME/work/<target>/crashes; minimise them into testdata/fuzz with a regression test.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
home="${ONEMEDIA_FUZZ_HOME:-$HOME/.cache/onemedia-fuzz}"
tools="$home/tools"
driver="$tools/libfuzzer-dotnet"
sharpfuzz="$tools/sharpfuzz"

DRIVER_URL="https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-ubuntu"
DRIVER_SHA256="c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958"
SHARPFUZZ_CLI_VERSION="2.3.0"

cmd="${1:-}"; shift || true

setup() {
  mkdir -p "$tools"
  if [ ! -x "$driver" ]; then
    curl -fL --retry 3 -o "$driver.part" "$DRIVER_URL"
    echo "$DRIVER_SHA256  $driver.part" | sha256sum -c - >/dev/null || { echo "libfuzzer-dotnet: SHA-256 mismatch" >&2; rm -f "$driver.part"; exit 1; }
    chmod +x "$driver.part"; mv "$driver.part" "$driver"
  fi
  if [ ! -x "$sharpfuzz" ]; then
    dotnet tool install SharpFuzz.CommandLine --version "$SHARPFUZZ_CLI_VERSION" --tool-path "$tools" >/dev/null
  fi
  echo "tools ready in $tools"
}

seed() {
  local target="$1" corpus="$2"
  mkdir -p "$corpus"
  if [ -d "$here/testdata/fuzz/$target" ]; then cp -n "$here/testdata/fuzz/$target"/* "$corpus/" 2>/dev/null || true; fi
  case "$target" in
    isobmff) # The first byte picks the path (bit 0: forward-only, bit 1: seeks); the rest is the file.
             for f in "$here"/testdata/isobmff/*.mp4 "$here"/testdata/isobmff/*.mov "$here"/testdata/isobmff/*.3gp "$here"/testdata/isobmff/family/*.mp4 "$here"/testdata/isobmff/family/*.m4a "$here"/testdata/isobmff/family/*.3gp; do
               [ -f "$f" ] || continue
               { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f").seed"
               { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
             done ;;
    asf) # The first byte picks the path (bit 0: forward-only; bit 1: seeks; bit 2: the asynchronous reader); the rest is the file.
         for f in "$here"/testdata/asf/*.asf "$here"/testdata/asf/*.wmv "$here"/testdata/asf/*.wma; do
           [ -f "$f" ] || continue
           { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f").seed"
           { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
           { printf '\006'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
         done ;;
    riff-wav|aiff|caf) # The first byte picks the path (bit 0: forward-only, 1: seeks, 2: ranged async, 3: metadata); the rest is the file.
             case "$target" in riff-wav) pat="*.wav";; aiff) pat="*.aif*";; caf) pat="*.caf";; esac
             for f in "$here"/testdata/pcm/$pat; do
               [ -f "$f" ] || continue
               { printf '\012'; cat "$f"; } > "$corpus/$(basename "$f").seed"
               { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
               { printf '\014'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
             done ;;
    faststart) # The whole input is an ISO file. Seeds: every progressive file we have, moov-last copies included once the remux check wrote them.
             for f in "$here"/testdata/isobmff/*.mp4 "$here"/testdata/isobmff/*.mov "$here"/testdata/isobmff/*.3gp "$here"/testdata/isobmff/family/*.mp4 "$here"/testdata/isobmff/family/*.3gp; do
               [ -f "$f" ] || continue
               cp "$f" "$corpus/$(basename "$f").seed"
             done ;;
    remux) # The first byte picks the output (bit 0: fragmented, bit 1: movie box last); the rest is any media file.
             for f in "$here"/testdata/isobmff/*.mp4 "$here"/testdata/audio/* "$here"/testdata/xiph-flv/* "$here"/testdata/mpeg/* "$here"/testdata/audio-es/*.* "$here"/testdata/avi/*.avi; do
               [ -f "$f" ] || continue
               [ "$(stat -c %s "$f")" -le 262144 ] || continue
               { printf '\000'; cat "$f"; } > "$corpus/$(basename "$f").seed"
               { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f")-last.seed"
             done ;;
    registry) # The first byte picks the path (bit 0: seek around; bit 1: read the metadata); the rest is the file. Seeds: one of every format we have.
             for f in "$here"/testdata/isobmff/*.mp4 "$here"/testdata/audio/* "$here"/testdata/xiph-flv/* "$here"/testdata/pcm/* "$here"/testdata/asf/* "$here"/testdata/mpeg/* "$here"/testdata/audio-es/*.* "$here"/testdata/audio-es/tagged/*.mp3 "$here"/testdata/matroska/*.mkv; do
               [ -f "$f" ] || continue
               [ "$(stat -c %s "$f")" -le 262144 ] || continue
               { printf '\003'; cat "$f"; } > "$corpus/$(basename "$f").seed"
             done ;;
    tags) # The first byte picks the path (bit 0: Matroska rather than ISO BMFF; bit 1: the asynchronous reader); the rest is the file.
          for f in "$here"/testdata/metadata/*.mp4 "$here"/testdata/metadata/*.mov "$here"/testdata/metadata/*.m4a "$here"/testdata/metadata/*.3gp "$here"/testdata/isobmff/*.mp4; do
            [ -f "$f" ] || continue
            { printf '\000'; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
          done
          for f in "$here"/testdata/metadata/*.mkv "$here"/testdata/metadata/*.webm "$here"/testdata/metadata/*.mka; do
            [ -f "$f" ] || continue
            { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\003'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
          done ;;
    exif) # The whole input is a TIFF/Exif block (its first byte also picks how far a header may be searched for).
          for f in "$here"/testdata/facts/*.tiff; do
            [ -f "$f" ] && cp "$f" "$corpus/$(basename "$f").seed"
          done ;;
    sidecars) # The first byte picks the parser (0 XMP, 1 Sony XML, 2 DJI SRT, 3 auto-detect); the rest is the sidecar.
          for f in "$here"/testdata/facts/sidecar.xmp "$here"/testdata/facts/sony-clip.xml "$here"/testdata/facts/dji.srt; do
            [ -f "$f" ] || continue
            case "$f" in *.xmp) b=0;; *.xml) b=1;; *) b=2;; esac
            { printf "\\$(printf '%03o' $b)"; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\003'; cat "$f"; } > "$corpus/$(basename "$f")-auto.seed"
          done ;;
    mpegts) # The first byte picks the path (bit 0: forward-only; bit 1: seek and read the metadata); the rest is the file.
            for f in "$here"/testdata/mpeg/*.ts "$here"/testdata/mpeg/*.m2ts; do
              [ -f "$f" ] || continue
              { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f").seed"
              { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
            done ;;
    mpegps) # As mpegts.
            for f in "$here"/testdata/mpeg/*.vob "$here"/testdata/mpeg/*.mpg; do
              [ -f "$f" ] || continue
              { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f").seed"
              { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
            done ;;
    video-es) # As mpegts, plus bits 2-4: the stream kind (H.264, HEVC, VVC, MPEG video, AV1 OBU) and bit 7: IVF instead.
            for f in "$here"/testdata/mpeg/*.264 "$here"/testdata/mpeg/*.265 "$here"/testdata/mpeg/*.m2v "$here"/testdata/mpeg/*.ivf; do
              [ -f "$f" ] || continue
              case "$f" in
                *.264) b=0 ;; *.265) b=4 ;; *.m2v) b=12 ;; *.ivf) b=128 ;; *) b=0 ;;
              esac
              { printf "\\$(printf '%03o' $((b | 2)))"; cat "$f"; } > "$corpus/$(basename "$f").seed"
              { printf "\\$(printf '%03o' $((b | 1)))"; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
            done ;;
    tagwrite) # The whole file is the input: one fixture of each format the tag writer edits.
              for f in "$here"/testdata/audio-es/plain.mp3 "$here"/testdata/audio-es/tagged/*.mp3 "$here"/testdata/audio/aac.m4a "$here"/testdata/audio/aac.mka "$here"/testdata/audio/flac.flac \
                       "$here"/testdata/audio/opus.ogg "$here"/testdata/audio/vorbis.ogg "$here"/testdata/pcm/bwf-v0.wav "$here"/testdata/pcm/*.aif* "$here"/testdata/asf/*.wma "$here"/testdata/asf/*.wmv "$here"/testdata/metadata/*.mp4 "$here"/testdata/metadata/*.mkv; do
                if [ -f "$f" ] && [ "$(stat -c %s "$f")" -le 262144 ]; then cp "$f" "$corpus/$(basename "$f").seed"; fi
              done ;;
    writer-iso|writer-ebml|writer-riff) # The first byte picks the path (bit 0: re-emit every header; bit 1: append padding and read back); the rest is the file.
             case "$target" in
               writer-iso) pats="$here/testdata/isobmff/*.mp4 $here/testdata/isobmff/*.mov $here/testdata/isobmff/*.3gp $here/testdata/metadata/*.mp4";;
               writer-ebml) pats="$here/testdata/matroska/*.mkv $here/testdata/metadata/*.mkv $here/testdata/metadata/*.webm";;
               *) pats="$here/testdata/pcm/*.wav $here/testdata/pcm/*.aif* $here/testdata/avi/*.avi";;
             esac
             for f in $pats; do
               [ -f "$f" ] || continue
               [ "$(stat -c %s "$f")" -le 262144 ] || continue
               { printf '\000'; cat "$f"; } > "$corpus/$(basename "$f").seed"
               { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-strict.seed"
               { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f")-append.seed"
             done ;;
    telemetry-file) # The first byte picks the path (bit 0: forward-only; bit 1: materialise every channel); the rest is an ISO file.
          for f in "$here"/testdata/telemetry/*.mp4; do
            [ -f "$f" ] || continue
            { printf '\002'; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
          done ;;
    telemetry-djmd) for f in "$here"/testdata/telemetry/djmd-*.mp4; do [ -f "$f" ] && cp "$f" "$corpus/$(basename "$f").seed"; done ;;
    telemetry-dji-text) for f in "$here"/testdata/telemetry/dji-text.srt; do [ -f "$f" ] && cp "$f" "$corpus/$(basename "$f").seed"; done ;;
    telemetry-mdpm) for f in "$here"/testdata/telemetry/mdpm-gps.mts; do [ -f "$f" ] && head -c 4096 "$f" > "$corpus/$(basename "$f").seed"; done ;;
    mdpm) # The SEI payload as the reader gets it (after the NAL header): one message of type 5 holding the MDPM block.
          printf '\005\044\027\356\214\140\370\115\021\331\214\326\010\000\040\014\232\146MDPM\003\030\000\040\022\001\031\046\011\030\065\340\001\003\005\221\200' > "$corpus/mdpm.seed" ;;
    id3|ape) # The first byte picks the path (id3: bit 0 reads a tag at the start rather than a tail; ape: bit 0 reads from the footer); the rest is the file.
             for f in "$here"/testdata/audio-es/tagged/*.mp3 "$here"/testdata/audio-es/*.mp3; do
               [ -f "$f" ] || continue
               { printf '\000'; cat "$f"; } > "$corpus/$(basename "$f").seed"
               { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-1.seed"
             done ;;
    mpa-es) # The first byte: bit 0 forward-only, bit 1 asynchronous, bit 2 seeks; the rest is the file.
            for f in "$here"/testdata/audio-es/tagged/*.mp3 "$here"/testdata/audio-es/*.mp3 "$here"/testdata/audio-es/*.mp2; do
              [ -f "$f" ] || continue
              { printf '\004'; cat "$f"; } > "$corpus/$(basename "$f").seed"
              { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
              { printf '\006'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
            done ;;
    adts-ac3-amr-es) # As mpa-es, with bits 4-5 choosing ADTS (0), AC-3/E-AC-3 (1) or AMR (2).
            for pair in "aac:0" "ac3:16" "eac3:16" "amr:32" "awb:32"; do
              for f in "$here"/testdata/audio-es/*.${pair%%:*}; do
                [ -f "$f" ] || continue
                hi=${pair#*:}
                { printf "\\$(printf '%03o' $((hi | 4)))"; cat "$f"; } > "$corpus/$(basename "$f").seed"
                { printf "\\$(printf '%03o' $((hi | 1)))"; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
                { printf "\\$(printf '%03o' $((hi | 6)))"; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
              done
            done ;;
    ogg|flac|flv|vorbis-comment) # The first byte picks the path (bit 0: forward-only, bit 1: seeks, bit 2: asynchronous open); the rest is the file.
          local pat
          case "$target" in ogg) pat="*.ogg *.opus *.oga";; flac) pat="*.flac";; flv) pat="*.flv";; *) pat="*.ogg *.flac";; esac
          for p in $pat; do
            for f in "$here"/testdata/xiph-flv/$p "$here"/testdata/audio/$p; do
              [ -f "$f" ] || continue
              { printf '\000'; cat "$f"; } > "$corpus/$(basename "$f").seed"
              { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
              { printf '\006'; cat "$f"; } > "$corpus/$(basename "$f")-async.seed"
            done
          done ;;
    riff-avi) # The first byte picks the path (bit 0: forward-only; bit 1: seeks; bit 2: registry against direct open; bit 3: metadata); the rest is the file.
          for f in "$here"/testdata/avi/*.avi; do
            [ -f "$f" ] || continue
            { printf '\016'; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
          done ;;
    dv) # The first byte picks the path (bit 0: forward-only; bit 1: seeks; bit 2: metadata); the rest is the file.
          for f in "$here"/testdata/avi/*.dv; do
            [ -f "$f" ] || continue
            { printf '\006'; cat "$f"; } > "$corpus/$(basename "$f").seed"
            { printf '\001'; cat "$f"; } > "$corpus/$(basename "$f")-fwd.seed"
          done ;;
    cc-data) # The first byte picks the carriage (0 H.264, 1 HEVC, 2 MPEG-2; the rest of the value is the NAL length size); the rest is one video sample.
          { printf '\000\000\000\000\001\006\004\012\265\000\061GA94\003\301\377\374\224\057\375\200\200\377\200'; } > "$corpus/h264-annexb.seed"
          { printf '\012\000\000\000\012\006\004\012\265\000\061GA94\003\301\377\374\224\057\375\200\200\377\200'; } > "$corpus/h264-len4.seed"
          { printf '\001\000\000\001\104\001\004\012\265\000\061GA94\003\301\377\374\224\057\375\200\200\377\200'; } > "$corpus/hevc-annexb.seed"
          { printf '\002\000\000\001\262GA94\003\301\377\374\224\057\375\200\200\377\000\000\001\000'; } > "$corpus/mpeg2.seed" ;;
    cea608) # The first byte: bit 0 clear requires odd parity; then groups of three bytes (two caption bytes, then the field in the low bit).
          { printf '\000\224\040\000\224\256\000\224\160\000\311\343\000\224\057\000'; } > "$corpus/popon.seed"
          { printf '\001\024\045\000\024\160\000\110\151\000\024\055\000\024\055\000'; } > "$corpus/rollup.seed" ;;
    cea708) # Groups of three bytes (type in the low bit, two data bytes): one DTVCC packet with a service block, windows and text.
          { printf '\001\102\042\000\101\102\000\230\040\000\016\000\000\001\103\104\000\015\000'; } > "$corpus/packet.seed"
          { printf '\001\104\145\000\230\000\000\203\000\000\001\110\111\000\216\000'; } > "$corpus/windows.seed" ;;
    scc) printf 'Scenarist_SCC V1.0\n\n00:00:01;00\t9420 9420 94ae 94ae 9470 9470 c8e5 ecec ef80 942f 942f\n\n00:00:03;00\t942c 942c\n' > "$corpus/pop.seed"
         printf '00:00:00:00\t9425 9425 94ad 94ad c1c2 \n' > "$corpus/rollup.seed" ;;
  esac
  if [ -z "$(ls -A "$corpus")" ]; then printf 'xxxxxxxxxx' > "$corpus/empty.seed"; fi
}

build() {
  local work="$1"
  rm -rf "$work/bin" "$work/bin-plain"
  # FUZZ_PREBUILT: a directory holding bin/ and bin-plain/ built once, so parallel targets share one build.
  if [ -n "${FUZZ_PREBUILT:-}" ] && [ -d "$FUZZ_PREBUILT/bin" ]; then
    cp -r "$FUZZ_PREBUILT/bin" "$work/bin"; cp -r "$FUZZ_PREBUILT/bin-plain" "$work/bin-plain"; return
  fi
  dotnet publish "$here/fuzz/OneMedia.Fuzz/OneMedia.Fuzz.csproj" -c Release -o "$work/bin-plain" -v quiet -clp:NoSummary >/dev/null
  cp -r "$work/bin-plain" "$work/bin"
  "$sharpfuzz" "$work/bin/RecursiveAI.OneMedia.dll" >/dev/null
}

case "$cmd" in
  setup) setup ;;
  run)
    target="${1:-isobmff}"; shift || true
    seconds="${1:-600}"
    setup
    work="$home/work/$target"
    mkdir -p "$work/crashes"
    build "$work"
    seed "$target" "$work/corpus"
    exe="$work/bin/RecursiveAI.OneMedia.Fuzz"
    echo "fuzzing $target for ${seconds}s; corpus $work/corpus, crashes $work/crashes"
    rm -f "$work"/fuzz-*.log
    # -timeout turns a hang into a saved input; -rss_limit_mb 0 because the .NET child process, not the driver, owns memory.
    ( cd "$work" && ONEMEDIA_FUZZ_TARGET="$target" "$driver" --target_path="$exe" \
      -max_total_time="$seconds" -timeout=20 -rss_limit_mb=0 -max_len="${FUZZ_MAX_LEN:-262144}" \
      -artifact_prefix="$work/crashes/" "$work/corpus" ) > "$work/last-run.log" 2>&1 || true
    grep -E '^#[0-9]+' "$work/last-run.log" | tail -n 1 || true
    grep -E '^Done|ERROR|SUMMARY|Test unit written' "$work/last-run.log" | head -n 5 || true
    echo "crashes:"; ls -1 "$work/crashes" | sed 's/^/  /'
    ;;
  replay)
    target="${1:?usage: fuzz.sh replay <target> <file>...}"; shift
    work="$home/work/$target"
    mkdir -p "$work"
    [ -d "$work/bin-plain" ] || build "$work"
    ONEMEDIA_FUZZ_TARGET="$target" "$work/bin-plain/RecursiveAI.OneMedia.Fuzz" "$@"
    ;;
  *) sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

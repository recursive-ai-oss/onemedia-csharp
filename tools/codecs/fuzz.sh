#!/usr/bin/env bash
# Builds, instruments and runs the codec-layer fuzz targets under libFuzzer, with a time budget.
#
#   tools/codecs/fuzz.sh setup                        install the tools (once per machine)
#   tools/codecs/fuzz.sh run <target> [seconds]       fuzz one target (default 600 s)
#   tools/codecs/fuzz.sh replay <target> <file>...    run saved inputs through the plain (uninstrumented) build
#
# Targets: codecs-nal codecs-av1-vpx-mpeg2 codecs-aac codecs-audio codecs-config
# Environment:
#   ONEMEDIA_FUZZ_HOME       tools and per-target work directories (default ~/onemedia/fuzz)
#   ONEMEDIA_CODEC_STREAMS   directory made by tools/codecs/make-streams.sh, used for seeds
# Crashing, hanging or leaking inputs land in $ONEMEDIA_FUZZ_HOME/work/<target>/crashes; minimise them and add them to the tests.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
home="${ONEMEDIA_FUZZ_HOME:-$HOME/onemedia/fuzz}"
tools="$home/tools"
driver="$tools/libfuzzer-dotnet"
sharpfuzz="$tools/sharpfuzz"

# Pinned fuzzing drivers (nothing here is shipped).
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
}

build() {
  local work="$1"
  rm -rf "$work/bin" "$work/bin-plain"
  dotnet publish "$here/fuzz/OneMedia.Fuzz/OneMedia.Fuzz.csproj" -c Release -o "$work/bin-plain" -v quiet -clp:NoSummary >/dev/null
  cp -r "$work/bin-plain" "$work/bin"
  "$sharpfuzz" "$work/bin/RecursiveAI.OneMedia.dll" >/dev/null
}

# Seeds: heads of the generated elementary streams, prefixed with the selector byte the target reads.
seed() {
  local target="$1" corpus="$2" s="${ONEMEDIA_CODEC_STREAMS:-}"
  mkdir -p "$corpus"
  [ -d "$here/testdata/fuzz/$target" ] && cp -n "$here/testdata/fuzz/$target"/* "$corpus/" 2>/dev/null || true
  [ -n "$s" ] && [ -d "$s" ] || { printf '\000\000' > "$corpus/empty.seed"; return; }
  case "$target" in
    codecs-nal)            for f in "$s"/*.h264 "$s"/*.hevc; do { printf '\000'; head -c 3000 "$f"; } > "$corpus/$(basename "$f").seed"; done ;;
    codecs-av1-vpx-mpeg2)  for f in "$s"/*.obu "$s"/*.m2v "$s"/*.ivf; do head -c 3000 "$f" > "$corpus/$(basename "$f").seed"; done ;;
    codecs-aac)            for f in "$s"/*.aac; do head -c 3000 "$f" > "$corpus/$(basename "$f").seed"; done ;;
    codecs-audio)          for f in "$s"/*.ac3 "$s"/*.eac3 "$s"/*.mp3 "$s"/*.flac; do head -c 3000 "$f" > "$corpus/$(basename "$f").seed"; done ;;
    codecs-config)
      # configuration records pulled out of the generated MP4 files: <codec><format><box content>
      if command -v python3 >/dev/null; then
        python3 - "$s" "$corpus" <<'PY'
import os, struct, sys
src, out = sys.argv[1], sys.argv[2]
boxes = {b'avcC': (0, 1), b'hvcC': (1, 2), b'av1C': (3, 4), b'vpcC': (4, 5), b'esds': (7, 6), b'dOps': (8, 8), b'dfLa': (10, 9), b'dac3': (11, 10), b'dec3': (12, 11)}
for name in sorted(os.listdir(src)):
    if not name.endswith('.mp4'):
        continue
    data = open(os.path.join(src, name), 'rb').read()
    for tag, (codec, fmt) in boxes.items():
        i = data.find(tag)
        if i >= 4:
            size = struct.unpack('>I', data[i - 4:i])[0]
            open(os.path.join(out, name + '.' + tag.decode() + '.seed'), 'wb').write(bytes([codec, fmt]) + data[i + 4:i - 4 + size][:2000])
PY
      fi ;;
  esac
  [ -n "$(ls -A "$corpus")" ] || printf '\000\000' > "$corpus/empty.seed"
}

case "$cmd" in
  setup) setup; echo "tools ready in $tools" ;;
  run)
    target="${1:?usage: fuzz.sh run <target> [seconds]}"; shift
    seconds="${1:-600}"
    setup
    work="$home/work/$target"
    mkdir -p "$work/crashes"
    build "$work"
    seed "$target" "$work/corpus"
    # libFuzzer starts one process per input; a wrapper picks the target.
    printf '#!/bin/sh\nexec dotnet "%s/bin/RecursiveAI.OneMedia.Fuzz.dll" %s\n' "$work" "$target" > "$work/run.sh"
    chmod +x "$work/run.sh"
    echo "fuzzing $target for ${seconds}s; corpus $work/corpus, crashes $work/crashes"
    rm -f "$work"/fuzz-*.log
    ( cd "$work" && "$driver" --target_path="$work/run.sh" \
      -max_total_time="$seconds" -timeout=20 -rss_limit_mb=0 -max_len=65536 \
      -artifact_prefix="$work/crashes/" "$work/corpus" ) > "$work/last-run.log" 2>&1 || true
    grep -E '^#[0-9]+' "$work/last-run.log" | tail -n 1 || true
    grep -E '^Done|ERROR|SUMMARY' "$work/last-run.log" | head -n 5 || true
    echo "crashes:"; ls -1 "$work/crashes" | sed 's/^/  /'
    ;;
  replay)
    target="${1:?usage: fuzz.sh replay <target> <file>...}"; shift
    work="$home/work/$target"
    mkdir -p "$work"
    [ -d "$work/bin-plain" ] || build "$work"
    dotnet "$work/bin-plain/RecursiveAI.OneMedia.Fuzz.dll" "$target" "$@"
    ;;
  *) sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

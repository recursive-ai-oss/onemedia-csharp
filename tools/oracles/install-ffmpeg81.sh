#!/usr/bin/env bash
# Installs the ffmpeg/ffprobe 8.1 release build (linux x86_64, GPL static) next to the other oracles, for the transport streams that the
# 7.0.2 build in install-linux.sh cannot read. Only executed, never opened or shipped; only bin/ffmpeg and bin/ffprobe are kept.
#
#   ONEMEDIA_HOME   state root, default ~/onemedia; lands in $ONEMEDIA_HOME/oracles/ffmpeg-8.1
#
# The URL names a rolling release, so the pin is the SHA-256 of the archive the oracle runs were made with. The release moves on: when the
# check fails, run with RECORD=1 to install the current build and print its SHA-256 (and record the printed `-version` line with the results).
set -euo pipefail
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
O="$HOME_DIR/oracles"; D="$O/_dl"; T="$O/ffmpeg-8.1"
URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz
SHA=a651976b11ebe8659b667cc7116debc246840eb6d460596cc4f9c650a8805ce7
mkdir -p "$D"
if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "a benchmark run holds the machine; not starting" >&2; exit 3; fi
[ -x "$T/bin/ffprobe" ] && [ "$(cat "$T/.pin" 2>/dev/null)" = "$SHA" ] && { echo "ffmpeg 8.1 already installed"; exit 0; }
[ -f "$D/ffmpeg81.tar.xz" ] || curl -fsSL "$URL" -o "$D/ffmpeg81.tar.xz"
GOT=$(sha256sum "$D/ffmpeg81.tar.xz" | cut -d' ' -f1)
if [ "$GOT" != "$SHA" ]; then
  [ "${RECORD:-0}" = 1 ] || { echo "SHA-256 mismatch for $URL: $GOT (pinned $SHA); RECORD=1 installs it anyway" >&2; exit 1; }
  echo "recorded SHA-256 $GOT"; SHA=$GOT
fi
rm -rf "$T" "$D/ffmpeg81-x"; mkdir -p "$T/bin" "$D/ffmpeg81-x"
tar -xJf "$D/ffmpeg81.tar.xz" -C "$D/ffmpeg81-x" --wildcards '*/bin/ffmpeg' '*/bin/ffprobe'
cp "$D"/ffmpeg81-x/*/bin/ffmpeg "$D"/ffmpeg81-x/*/bin/ffprobe "$T/bin/"
rm -rf "$D/ffmpeg81-x" "$D/ffmpeg81.tar.xz"
echo "$SHA" > "$T/.pin"
"$T/bin/ffprobe" -version | head -1

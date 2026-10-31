#!/usr/bin/env bash
# Builds MP4Box from the published GPAC release source archive with its standard toolchain (./configure and make)
# and installs it under $ONEMEDIA_HOME/oracles/gpac-build. For machines where the release package is for another
# CPU (the macOS package is x86_64 only). The build tree is never read; MP4Box is only executed.
#
#   RECORD=1   print the archive's SHA-256 instead of failing when no pin is set
set -euo pipefail

HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
O="$HOME_DIR/oracles"; D="$O/_dl"; B="$O/_build/gpac"; P="$O/gpac-build"
URL=https://github.com/gpac/gpac/archive/refs/tags/v26.07.0.tar.gz
SHA=57822c1a74dcb83d76ff1f671e1b4fae2e7614e8194a5adb9f20661e0e9421dd
mkdir -p "$O/bin" "$D"

sha() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

[ -x "$O/bin/MP4Box" ] && [ -n "$SHA" ] && [ "$(cat "$O/gpac-build.pin" 2>/dev/null)" = "$SHA" ] && exit 0
if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "a benchmark run holds the machine; not starting" >&2; exit 3; fi

[ -s "$D/gpac-26.07.0.tar.gz" ] || curl -fsSL --retry 3 -o "$D/gpac-26.07.0.tar.gz" "$URL"
got=$(sha "$D/gpac-26.07.0.tar.gz")
if [ -z "$SHA" ]; then
  [ "${RECORD:-0}" = 1 ] || { echo "no pin for the GPAC archive (sha256 $got); rerun with RECORD=1" >&2; exit 4; }
  echo "RECORD gpac-source sha256 $got"; SHA=$got
elif [ "$got" != "$SHA" ]; then
  echo "GPAC archive: sha256 $got does not match the pin" >&2; exit 4
fi

rm -rf "$B" "$P"; mkdir -p "$B"
tar -xzf "$D/gpac-26.07.0.tar.gz" -C "$B" --strip-components=1
JOBS=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)
( cd "$B" && ./configure --prefix="$P" >"$O/_build/gpac-configure.log" 2>&1 && make -j"$JOBS" >"$O/_build/gpac-make.log" 2>&1 && make install >"$O/_build/gpac-install.log" 2>&1 )

printf '#!/bin/sh\nDYLD_LIBRARY_PATH="%s/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}" LD_LIBRARY_PATH="%s/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" exec "%s/bin/MP4Box" "$@"\n' "$P" "$P" "$P" > "$O/bin/MP4Box"
chmod +x "$O/bin/MP4Box"; echo "$SHA" > "$O/gpac-build.pin"
"$O/bin/MP4Box" -version 2>&1 | head -1

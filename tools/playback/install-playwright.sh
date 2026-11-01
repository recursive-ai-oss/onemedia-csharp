#!/usr/bin/env bash
# Installs Playwright (Python package, pinned) and its Chromium, Firefox and WebKit builds user-locally.
#
#   ONEMEDIA_HOME   state root, default ~/onemedia; everything lands in $ONEMEDIA_HOME/oracles/playwright
#
# Playwright is only executed through its public API (tools/playback/probe.py); its installed files are never read.
# No sudo, no system paths. Idempotent.
set -euo pipefail

HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
P="$HOME_DIR/oracles/playwright"
PLAYWRIGHT_VER="${PLAYWRIGHT_VER:-1.55.0}"
mkdir -p "$P"

# uv makes the Python 3.13 environment; where it is missing (macOS) it is bootstrapped from pip into the same folder.
UV="$(command -v uv || true)"
if [ -z "$UV" ]; then
  [ -x "$P/uvboot/bin/uv" ] || { python3 -m venv "$P/uvboot"; "$P/uvboot/bin/pip" install -q uv; }
  UV="$P/uvboot/bin/uv"
fi

if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "a benchmark run holds the machine; not starting" >&2; exit 3; fi

export PLAYWRIGHT_BROWSERS_PATH="$P/browsers"
if [ ! -x "$P/venv/bin/python" ]; then
  "$UV" venv --python 3.13 "$P/venv"
fi
"$UV" pip install --python "$P/venv/bin/python" "playwright==$PLAYWRIGHT_VER" >/dev/null

# WebKit on Linux needs libavif16 and its two runtime libraries, which a minimal host may lack; unpack the distro packages
# user-locally (apt-get download needs no root). probe.py adds $P/libs to LD_LIBRARY_PATH when it exists.
L="$P/libs"
if [ "$(uname -s)" = Linux ] && { [ ! -e "$L/usr/lib/x86_64-linux-gnu/libgav1.so.1" ] || [ ! -e "$L/usr/lib/x86_64-linux-gnu/libyuv.so.0" ] || [ ! -e "$L/usr/lib/x86_64-linux-gnu/libavif.so.16" ]; }; then
  mkdir -p "$L/dl"
  ( cd "$L/dl" && apt-get download libavif16 libgav1-1 libyuv0 >/dev/null )
  for d in "$L"/dl/*.deb; do dpkg -x "$d" "$L"; done
fi
export LD_LIBRARY_PATH="$L/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"

"$P/venv/bin/python" -m playwright install chromium firefox webkit
# The WebKit launcher sets its own library path, so the three libraries are linked into the browser's own library folder.
for w in "$P"/browsers/webkit-*/minibrowser-wpe/lib; do
  [ -d "$w" ] || continue
  for so in "$L"/usr/lib/x86_64-linux-gnu/lib*.so*; do ln -sf "$so" "$w/$(basename "$so")"; done
done

"$P/venv/bin/python" -c "import importlib.metadata as m; print('playwright', m.version('playwright'))" | tee "$P/version.txt"

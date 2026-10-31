#!/usr/bin/env bash
# Builds mkvalidator 0.6.0 from its published release tarball with its documented toolchain (CMake and a C
# compiler) and installs the executable under $ONEMEDIA_HOME/oracles/bin. The build tree is never read; the
# result is executed only. CMake comes from the PyPI wheel through uv when the machine has none.
set -euo pipefail

HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
O="$HOME_DIR/oracles"; D="$O/_dl"; B="$O/_build/mkvalidator"
URL=https://sourceforge.net/projects/matroska/files/mkvalidator/mkvalidator-0.6.0.tar.bz2/download
SHA=f9eaa2138fade7103e6df999425291d2947c5355294239874041471e3aa243f0
mkdir -p "$O/bin" "$D"

[ -x "$O/bin/mkvalidator" ] && [ "$(cat "$O/mkvalidator.pin" 2>/dev/null)" = "$SHA" ] && exit 0
if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "a benchmark run holds the machine; not starting" >&2; exit 3; fi

if [ ! -s "$D/mkvalidator-0.6.0.tar.bz2" ]; then curl -fsSL --retry 3 -o "$D/mkvalidator-0.6.0.tar.bz2" "$URL"; fi
got=$(sha256sum "$D/mkvalidator-0.6.0.tar.bz2" | cut -d' ' -f1)
[ "$got" = "$SHA" ] || { echo "mkvalidator: sha256 $got does not match the pin" >&2; exit 4; }

CMAKE=$(command -v cmake || true)
if [ -z "$CMAKE" ]; then
  uv venv -q "$O/_cmake" && uv pip install -q --python "$O/_cmake/bin/python" cmake
  CMAKE="$O/_cmake/bin/cmake"
fi

rm -rf "$B"; mkdir -p "$B"; tar -xjf "$D/mkvalidator-0.6.0.tar.bz2" -C "$B" --strip-components=1
( cd "$B" && "$CMAKE" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 . >/dev/null && make -j4 >/dev/null )
exe=$(find "$B" -type f -name mkvalidator -perm -u+x | head -1)
[ -n "$exe" ] || { echo "mkvalidator build produced no executable" >&2; exit 5; }
cp "$exe" "$O/bin/mkvalidator"; echo "$SHA" > "$O/mkvalidator.pin"
"$O/bin/mkvalidator" --version 2>&1 | head -1 || true

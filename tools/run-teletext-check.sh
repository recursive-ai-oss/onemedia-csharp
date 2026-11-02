#!/usr/bin/env bash
# Builds tools/OneMedia.TeletextCheck and writes our teletext subtitle cues for every .ts in a corpus folder.
# Usage: tools/run-teletext-check.sh [corpus-dir] [out-dir]   (defaults under $HOME/onemedia)
set -euo pipefail
export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10" DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
HERE="$(cd "$(dirname "$0")/.." && pwd)"
CORPUS="${1:-$HOME/onemedia/corpus/teletext}"
OUT="${2:-$HOME/onemedia/u52-teletext/ours}"
BIN="$HOME/onemedia/u52-teletext/bin"
nice dotnet build "$HERE/tools/OneMedia.TeletextCheck" -c Release -m:4 -v quiet -clp:NoSummary -p:NuGetAudit=false -o "$BIN" 2>&1 | tail -3
rm -rf "$OUT"; mkdir -p "$OUT"
for f in "$CORPUS"/*.ts; do
  echo "== $(basename "$f")"
  dotnet "$BIN/OneMedia.TeletextCheck.dll" "$f" "$OUT"
done > "$OUT/summary.txt" 2>&1
tail -3 "$OUT/summary.txt"

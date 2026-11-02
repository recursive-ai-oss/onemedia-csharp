#!/usr/bin/env bash
# Packet differential for presentations: ours against ffprobe on the same playlists.
#   run-streaming-diff.sh PRESENTATIONS-DIR OUT-DIR
# PRESENTATIONS-DIR is written by gen-streaming-presentations.sh. For a multivariant playlist with muxed variants ffprobe is pointed at the first
# variant's media playlist (v0.m3u8), because ffprobe lists every variant and we play one.
set -uo pipefail
in="${1:?usage: run-streaming-diff.sh PRESENTATIONS-DIR OUT-DIR}"; out="${2:?}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 ONEMEDIA_HOME="${ONEMEDIA_HOME:-$HOME/onemedia}"
dll="${ONEMEDIA_SCRATCH:-$HOME/onemedia}/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
args=()
while IFS=$'\t' read -r name kind playlist rest; do
  p="$in/$name/$playlist"
  case "$name" in
    *master2) args+=("$p=$in/$name/v0.m3u8") ;;
    *) args+=("$p") ;;
  esac
done < "$in/MANIFEST.tsv"
rm -rf "$out"; mkdir -p "$out"
# One process per presentation, each with a time limit, because ffprobe can stall on a manifest it cannot make sense of.
for a in "${args[@]}"; do
  timeout 180 dotnet "$dll" presdiff "$a" --out "$out" --exceptions "$here/OneMedia.Oracles/exceptions.tsv" > /dev/null 2>"$out/last.err" || echo "no result: $a (exit $?)" | tee -a "$out/failures.txt"
done
dotnet "$dll" presdiff --out "$out" --exceptions "$here/OneMedia.Oracles/exceptions.tsv" | tee "$out/summary.log"

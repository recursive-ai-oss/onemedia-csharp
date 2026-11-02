#!/usr/bin/env bash
# Builds the comparison tool and compares our readers with ffprobe on the audio elementary-stream corpus: packets (diff), stream and
# duration facts (infocheck). Run from the repository root; results go to $OUT (default ~/onemedia/mp3-es/results).
set -uo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
O="${ONEMEDIA_HOME:-$HOME/onemedia}"
OUT="${OUT:-$O/mp3-es/results}"
export ONEMEDIA_ARTIFACTS="${ONEMEDIA_ARTIFACTS:-$O/mp3-es/artifacts}"
G="$O/corpus/generated/audio-es"
mkdir -p "$OUT"
dotnet build tools/OneMedia.Oracles/OneMedia.Oracles.csproj -c Release -v q -clp:NoSummary 2>&1 | tail -5
T="$ONEMEDIA_ARTIFACTS/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
ONEMEDIA_HOME="$O" dotnet "$T" diff --family audio-es --root "$G" --out "$OUT/diff" --exceptions tools/OneMedia.Oracles/exceptions.tsv --jobs 4 | tail -3
cat "$OUT/diff/summary.txt" | head -60
ONEMEDIA_HOME="$O" dotnet "$T" infocheck $(find "$G" -type f \( -name '*.mp3' -o -name '*.mp2' -o -name '*.aac' -o -name '*.ac3' -o -name '*.eac3' -o -name '*.amr' -o -name '*.awb' \) ! -path '*/_*' | sort) > "$OUT/infocheck.txt" 2>&1
tail -1 "$OUT/infocheck.txt"; grep -c '^same' "$OUT/infocheck.txt"; grep '^DIFF' "$OUT/infocheck.txt" | head -40

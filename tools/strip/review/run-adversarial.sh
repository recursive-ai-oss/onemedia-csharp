#!/usr/bin/env bash
# usage: run-adversarial.sh <dir of inputs> <work dir> [strip args...]
# Strips each file with the onemedia tool (ONEMEDIA_DLL), searches original and result with search.py, prints one block per file.
set -u
IN=$1; W=$2; shift 2
mkdir -p "$W"
here=$(cd "$(dirname "$0")" && pwd)
for f in "$IN"/*; do
  n=$(basename "$f")
  case "$n" in *.names|*.xml|*.srt) continue;; esac
  o="$W/$n"
  rm -f "$o"
  echo "=== $n"
  dotnet "$ONEMEDIA_DLL" strip "$f" "$o" "$@" > "$W/$n.report" 2>&1; echo "exit=$?  $(head -c 300 "$W/$n.report" | tr '\n' '|')"
  if [ -f "$o" ]; then python3 "$here/search.py" "$f" "$o"; else echo "NO OUTPUT"; fi
done

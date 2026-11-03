#!/usr/bin/env bash
# Fetches the freely licensed device recordings listed in devices-manifest.tsv into $ONEMEDIA_HOME/corpus/devices.
#   devices-fetch.sh [FILTER]     FILTER is a grep pattern on the whole manifest row (class, device, id)
# Files are real recordings and may carry people, places and device identifiers: they are never committed,
# never copied into results, and every row is fetch-only. A file whose sha256 differs from the manifest is deleted.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
D="${ONEMEDIA_HOME:-$HOME/onemedia}/corpus/devices"
FILTER="${1:-.}"
tail -n +2 "$HERE/devices-manifest.tsv" | grep -E "$FILTER" | while IFS=$'\t' read -r id path url sha size cls dev lic credit use; do
  dest="$D/$path"; mkdir -p "$(dirname "$dest")"
  if [ -s "$dest" ] && [ "$(stat -c %s "$dest")" = "$size" ]; then continue; fi
  echo "fetching $id ($cls)"
  curl -fsSL --retry 3 -A "onemedia-corpus/1.0" -o "$dest" "$url" || { echo "FAILED $id" >&2; rm -f "$dest"; continue; }
  got=$(sha256sum "$dest" | cut -d' ' -f1)
  [ "$got" = "$sha" ] || { echo "$id: sha256 mismatch" >&2; rm -f "$dest"; }
done

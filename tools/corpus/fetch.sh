#!/usr/bin/env bash
# Fetches the published sample media listed in manifest.tsv into $ONEMEDIA_HOME/corpus (default ~/onemedia/corpus).
#
#   fetch.sh [FILTER]       download what is missing; FILTER is a grep pattern on the manifest id
#   RECORD=1 fetch.sh       also fill empty hash/size columns of manifest.tsv in place (review the diff)
#   VERIFY=1 fetch.sh       re-hash files that already exist
#
# Files are never committed. Entries marked "fetch-only" have licences that do not allow redistribution
# here (or are unknown); "committable" entries may be cut into small clips under their notice.
# Downloads are network work: each stops at once if a benchmark lock appears.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
MANIFEST="$HERE/manifest.tsv"
C="${ONEMEDIA_HOME:-$HOME/onemedia}/corpus"
FILTER="${1:-.}"
mkdir -p "$C/_dl"

# manifest columns: id path url archive dl_sha256 dl_size file_sha256 file_size licence use
sha() { sha256sum "$1" | cut -d' ' -f1; }

download() { # download URL DEST: resumable, stops when a benchmark lock appears
  curl -fsSL --retry 3 -C - -o "$2" "$1" &
  local pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then kill "$pid" 2>/dev/null || true; echo "benchmark lock present; stopping" >&2; exit 3; fi
    sleep 5
  done
  wait "$pid"
}

# Empty columns are written as '-' because read would otherwise merge adjacent tabs.
d() { [ "$1" = - ] && echo "" || echo "$1"; }
m() { [ -n "$1" ] && echo "$1" || echo -; }
emit() { printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$path" "$url" "$(m "$archive")" "$(m "$dlsha")" "$(m "$dlsize")" "$(m "$filesha")" "$(m "$filesize")" "$licence" "$use" >> "$TMP"; }

TMP=$(mktemp "$HERE/.manifest.XXXXXX")
trap 'rm -f "$TMP"' EXIT
fail=0
while IFS=$'\t' read -r id path url archive dlsha dlsize filesha filesize licence use; do
  case "$id" in ''|\#*|id) printf '%s\n' "$id${path:+	$path	$url	$archive	$dlsha	$dlsize	$filesha	$filesize	$licence	$use}" >> "$TMP"; continue;; esac
  archive=$(d "$archive"); dlsha=$(d "$dlsha"); dlsize=$(d "$dlsize"); filesha=$(d "$filesha"); filesize=$(d "$filesize")
  if ! echo "$id" | grep -qE "$FILTER"; then
    emit; continue
  fi
  dest="$C/$path"; mkdir -p "$(dirname "$dest")"
  if [ ! -s "$dest" ]; then
    echo "fetching $id"
    if [ "$archive" = zip ]; then
      dl="$C/_dl/$(basename "$path").zip"
      if ! download "$url" "$dl"; then echo "FAILED $id" >&2; fail=1; rm -f "$dl"; emit; continue; fi
      got=$(sha "$dl")
      if [ -n "$dlsha" ] && [ "$got" != "$dlsha" ]; then echo "$id: archive sha256 $got != $dlsha" >&2; rm -f "$dl"; exit 4; fi
      dlsha=$got; dlsize=$(stat -c %s "$dl")
      unzip -q -o -j "$dl" "$(basename "$path")" -d "$(dirname "$dest")"; rm -f "$dl"
    else
      if ! download "$url" "$dest.part"; then echo "FAILED $id" >&2; fail=1; rm -f "$dest.part"; emit; continue; fi
      mv "$dest.part" "$dest"
    fi
    got=$(sha "$dest")
    if [ -n "$filesha" ] && [ "$got" != "$filesha" ]; then echo "$id: sha256 $got != pinned $filesha" >&2; rm -f "$dest"; exit 4; fi
    if [ -z "$filesha" ] && [ "${RECORD:-0}" != 1 ]; then echo "$id: no pin (sha256 $got); RECORD=1 to accept" >&2; fi
    filesha=$got; filesize=$(stat -c %s "$dest"); [ "$archive" = zip ] || { dlsha=$got; dlsize=$filesize; }
  elif [ "${VERIFY:-0}" = 1 ] && [ -n "$filesha" ] && [ "$(sha "$dest")" != "$filesha" ]; then
    echo "$id: existing file does not match its pin" >&2; fail=1
  fi
  if [ "${RECORD:-0}" = 1 ]; then
    [ -n "$filesha" ] || { filesha=$(sha "$dest"); filesize=$(stat -c %s "$dest"); [ "$archive" = zip ] || { dlsha=$filesha; dlsize=$filesize; }; }
  fi
  emit
done < "$MANIFEST"

if [ "${RECORD:-0}" = 1 ]; then cp "$TMP" "$MANIFEST"; echo "manifest updated"; fi
exit $fail

#!/usr/bin/env bash
# Reads the dates macOS shows for the check files: Spotlight's content creation date (mdls) and AVFoundation's creationDate; then tries Photos.
#   tools/date-check/run-on-mac.sh <folder-of-files> <results-file>
# Needs swiftc, mdls, mdimport and osascript. Photos is only tried when it answers a trivial AppleScript query without a prompt.
set -u
src=$1; out=$2
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/datecheck.XXXXXX")
swiftc -O "$here/mac-dates.swift" -o "$work/mac-dates" 2>"$work/swiftc.log" || { echo "swiftc failed"; tail -5 "$work/swiftc.log"; }
{
  echo "# $(sw_vers -productName) $(sw_vers -productVersion) ($(sw_vers -buildVersion)), time zone $(date +%Z) $(date +%z)"
  echo "## AVFoundation creationDate"
  [ -x "$work/mac-dates" ] && "$work/mac-dates" "$src"/* 2>&1
  echo "## Spotlight (mdls)"
  for f in "$src"/*; do mdimport "$f" 2>/dev/null; done
  sleep 5
  for f in "$src"/*; do
    echo "$(basename "$f")"
    mdls -name kMDItemContentCreationDate -name kMDItemContentModificationDate -name kMDItemRecordingYear -name kMDItemContentType "$f" 2>&1 | sed 's/^/    /'
  done
  echo "## Photos"
  if timeout 20 osascript -e 'tell application "Photos" to count of albums' >"$work/photos.out" 2>&1; then echo "Photos answered: $(cat "$work/photos.out")"; else echo "Photos: not available headless: $(head -c 300 "$work/photos.out")"; fi
} > "$out" 2>&1
rm -rf "$work"

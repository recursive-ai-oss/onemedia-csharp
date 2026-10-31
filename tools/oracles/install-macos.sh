#!/usr/bin/env bash
# Installs the pinned reference tools used as black-box oracles on macOS (arm64), user-locally.
#
#   ONEMEDIA_HOME   state root, default ~/onemedia; tools land in $ONEMEDIA_HOME/oracles
#   RECORD=1        download and print SHA-256 for entries that have no pin yet instead of failing
#
# The tools are only ever executed. Disk images and packages are unpacked with hdiutil and pkgutil; their
# contents are not read. Idempotent. No sudo, no system paths.
set -euo pipefail

HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
O="$HOME_DIR/oracles"
D="$O/_dl"
mkdir -p "$O/bin" "$D"

FFMPEG_URL=https://www.osxexperts.net/ffmpeg80arm.zip
FFMPEG_SHA=0d4efcaf6a098430a708e0af694a84792938921fa126162787ae98c6151d7a95
FFPROBE_URL=https://www.osxexperts.net/ffprobe80arm.zip
FFPROBE_SHA=b46eb342707ec0d31d3e8337bb56831e59c9e20918f414fd7a9d65a32fcb348f
MKVTOOLNIX_URL=https://mkvtoolnix.download/macos/releases/103.0/MKVToolNix-103.0-1-arm64.dmg
MKVTOOLNIX_SHA=05670ef070783a4afa23b9a289373472ac168112c83926dae17e29ab32198501
MEDIAINFO_URL=https://mediaarea.net/download/binary/mediainfo/26.10/MediaInfo_CLI_26.10_Mac.dmg
MEDIAINFO_SHA=2345a52f6e5281a15e9ef87acbe5756a9c4727d5629873c013f148a8f82525ab
EXIFTOOL_URL=https://sourceforge.net/projects/exiftool/files/Image-ExifTool-13.60.tar.gz/download
EXIFTOOL_SHA=c8e3c42576f46d862e4cd9c192989776e7363931e890c95056bcd6293e83ea36

sha() { shasum -a 256 "$1" | cut -d' ' -f1; }

fetch() { # fetch NAME URL EXPECTED_SHA FILE
  local name=$1 url=$2 want=$3 file=$4
  if [ -s "$D/$file" ] && [ -n "$want" ] && [ "$(sha "$D/$file")" = "$want" ]; then return 0; fi
  echo "downloading $name"
  curl -fsSL --retry 3 -o "$D/$file.part" "$url"
  [ -s "$D/$file.part" ] || { rm -f "$D/$file.part"; echo "$name: empty download from $url" >&2; exit 4; }
  mv "$D/$file.part" "$D/$file"
  local got; got=$(sha "$D/$file")
  if [ -z "$want" ]; then
    [ "${RECORD:-0}" = 1 ] || { echo "no pin for $name (sha256 $got); rerun with RECORD=1 to accept" >&2; exit 4; }
    echo "RECORD $name sha256 $got"
  elif [ "$got" != "$want" ]; then
    rm -f "$D/$file"; echo "$name: sha256 $got does not match the pin $want" >&2; exit 4
  fi
}
installed() { [ "$(cat "$O/$1/.pin" 2>/dev/null)" = "$2" ]; }
wrap() { printf '#!/bin/sh\n%s\n' "$2" > "$O/bin/$1"; chmod +x "$O/bin/$1"; }

# Copies everything from a mounted image into DEST, then detaches it.
from_dmg() { # from_dmg DMG DEST
  local mnt="$D/mnt.$$"; mkdir -p "$mnt"
  hdiutil attach -nobrowse -readonly -mountpoint "$mnt" "$1" >/dev/null
  mkdir -p "$2"; cp -R "$mnt"/. "$2"/ || true
  hdiutil detach "$mnt" >/dev/null; rmdir "$mnt" 2>/dev/null || true
}

fetch ffmpeg "$FFMPEG_URL" "$FFMPEG_SHA" ffmpeg.zip
fetch ffprobe "$FFPROBE_URL" "$FFPROBE_SHA" ffprobe.zip
if ! installed ffmpeg "$FFMPEG_SHA$FFPROBE_SHA"; then
  rm -rf "$O/ffmpeg"; mkdir -p "$O/ffmpeg"
  unzip -q -o "$D/ffmpeg.zip" -d "$O/ffmpeg"; unzip -q -o "$D/ffprobe.zip" -d "$O/ffmpeg"
  chmod +x "$O/ffmpeg/ffmpeg" "$O/ffmpeg/ffprobe"; xattr -dr com.apple.quarantine "$O/ffmpeg" 2>/dev/null || true
  echo "$FFMPEG_SHA$FFPROBE_SHA" > "$O/ffmpeg/.pin"
fi
ln -sf "$O/ffmpeg/ffmpeg" "$O/bin/ffmpeg"; ln -sf "$O/ffmpeg/ffprobe" "$O/bin/ffprobe"

# MP4Box: the release package for macOS is x86_64 only, so build the release source (see build-gpac.sh).
bash "$(dirname "$0")/build-gpac.sh"

fetch mkvtoolnix "$MKVTOOLNIX_URL" "$MKVTOOLNIX_SHA" mkvtoolnix.dmg
if ! installed mkvtoolnix "$MKVTOOLNIX_SHA"; then
  rm -rf "$O/mkvtoolnix"; from_dmg "$D/mkvtoolnix.dmg" "$O/mkvtoolnix"; echo "$MKVTOOLNIX_SHA" > "$O/mkvtoolnix/.pin"
fi
for t in mkvinfo mkvmerge mkvextract mkvpropedit; do
  p=$(find "$O/mkvtoolnix" -type f -name "$t" -perm -u+x | head -1)
  [ -n "$p" ] && ln -sf "$p" "$O/bin/$t"
done

fetch mediainfo "$MEDIAINFO_URL" "$MEDIAINFO_SHA" mediainfo.dmg
if ! installed mediainfo "$MEDIAINFO_SHA"; then
  rm -rf "$O/mediainfo"; from_dmg "$D/mediainfo.dmg" "$O/mediainfo"
  # The command line image carries an installer package; unpack it without installing.
  pkg=$(find "$O/mediainfo" -maxdepth 2 -name '*.pkg' | head -1)
  [ -z "$pkg" ] || pkgutil --expand-full "$pkg" "$O/mediainfo/expanded"
  echo "$MEDIAINFO_SHA" > "$O/mediainfo/.pin"
fi

fetch exiftool "$EXIFTOOL_URL" "$EXIFTOOL_SHA" exiftool.tar.gz
if ! installed exiftool "$EXIFTOOL_SHA"; then
  rm -rf "$O/exiftool"; mkdir -p "$O/exiftool"; tar -xzf "$D/exiftool.tar.gz" -C "$O/exiftool" --strip-components=1; echo "$EXIFTOOL_SHA" > "$O/exiftool/.pin"
fi
wrap exiftool "exec perl \"$O/exiftool/exiftool\" \"\$@\""

for t in mediainfo; do
  p=$(find "$O/mediainfo" -type f -name "$t" -perm -u+x 2>/dev/null | head -1)
  [ -n "$p" ] && ln -sf "$p" "$O/bin/$t"
done

echo "=== versions"
"$O/bin/ffprobe" -version | head -1
"$O/bin/MP4Box" -version 2>&1 | head -1
"$O/bin/mkvinfo" --version | head -1
"$O/bin/mediainfo" --Version | tail -1
echo "exiftool $("$O/bin/exiftool" -ver)"
echo "=== sha256 (as pinned)"
for n in FFMPEG FFPROBE MKVTOOLNIX MEDIAINFO EXIFTOOL; do v=${n}_SHA; echo "$n ${!v}"; done

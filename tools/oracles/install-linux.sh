#!/usr/bin/env bash
# Installs the pinned reference tools used as black-box oracles (linux x86_64), user-locally.
#
#   ONEMEDIA_HOME   state root, default ~/onemedia; tools land in $ONEMEDIA_HOME/oracles
#   RECORD=1        download and print SHA-256 for entries that have no pin yet instead of failing
#
# The tools are only ever executed. Archives are unpacked and run; their contents are not read.
# Idempotent: a download whose SHA-256 matches is reused, an install whose marker matches is skipped.
# No sudo, no system paths.
set -euo pipefail

HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
O="$HOME_DIR/oracles"
D="$O/_dl"
mkdir -p "$O/bin" "$D"

if [ -n "${ONEMEDIA_BENCH_LOCK:-}" ] && [ -e "$ONEMEDIA_BENCH_LOCK" ]; then echo "a benchmark run holds the machine; not starting" >&2; exit 3; fi

# --- pins: name | url | sha256 ------------------------------------------------------------------
FFMPEG_VER=7.0.2
FFMPEG_URL=https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
FFMPEG_SHA=abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67
GPAC_VER=26.07
GPAC_URL=https://download.tsi.telecom-paristech.fr/gpac/release/26.07/gpac_26.07-rev0-ga07cbfff-master_amd64.deb
GPAC_SHA=3c2a8f5f8a7b5c013c3170b1ccad2c6994e6959f49c7e98830fde8dd4fb2f053
MKVTOOLNIX_VER=103.0
MKVTOOLNIX_URL=https://mkvtoolnix.download/appimage/MKVToolNix_GUI-103.0-x86_64.AppImage
MKVTOOLNIX_SHA=fefc571382f090d65b1038ba36383222167d248ff9c9a54f6678aa21182a03ee
MEDIAINFO_VER=26.10
MEDIAINFO_URL=https://mediaarea.net/download/binary/mediainfo/26.10/MediaInfo_CLI_26.10_Lambda_x86_64.zip
MEDIAINFO_SHA=3087b8d4906a47d876a66a500b8a6af63de8e9da9992d295a702b4ca79fb3fdd
EXIFTOOL_VER=13.60
EXIFTOOL_URL=https://sourceforge.net/projects/exiftool/files/Image-ExifTool-13.60.tar.gz/download
EXIFTOOL_SHA=c8e3c42576f46d862e4cd9c192989776e7363931e890c95056bcd6293e83ea36
# -------------------------------------------------------------------------------------------------

sha() { sha256sum "$1" | cut -d' ' -f1; }

# fetch NAME URL EXPECTED_SHA FILE: downloads FILE unless present with the expected hash.
fetch() {
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

# stamp NAME: skip when the install directory already holds this exact pin.
installed() { [ "$(cat "$O/$1/.pin" 2>/dev/null)" = "$2" ]; }

wrap() { # wrap NAME SCRIPT-BODY
  printf '#!/bin/sh\n%s\n' "$2" > "$O/bin/$1"; chmod +x "$O/bin/$1"
}

# ffmpeg and ffprobe: static release build.
fetch ffmpeg "$FFMPEG_URL" "$FFMPEG_SHA" ffmpeg.tar.xz
if ! installed ffmpeg "$FFMPEG_SHA"; then
  rm -rf "$O/ffmpeg"; mkdir -p "$O/ffmpeg"
  tar -xJf "$D/ffmpeg.tar.xz" -C "$O/ffmpeg" --strip-components=1 --wildcards '*/ffmpeg' '*/ffprobe'
  echo "$FFMPEG_SHA" > "$O/ffmpeg/.pin"
fi
ln -sf "$O/ffmpeg/ffmpeg" "$O/bin/ffmpeg"; ln -sf "$O/ffmpeg/ffprobe" "$O/bin/ffprobe"

# MP4Box from the GPAC release package, unpacked user-locally.
fetch gpac "$GPAC_URL" "$GPAC_SHA" gpac.deb
if ! installed gpac "$GPAC_SHA"; then
  rm -rf "$O/gpac"; mkdir -p "$O/gpac"; dpkg-deb -x "$D/gpac.deb" "$O/gpac"; echo "$GPAC_SHA" > "$O/gpac/.pin"
fi
# The GPAC package links two libraries a plain desktop may lack; take them from the distribution archive,
# check them against pins and unpack them beside MP4Box.
EXTRA_SHA_libjpeg62=a220c29779e556ed6f2a3682249a74a4d1887d64dfe6e8fa8527e914c57d8bb3
EXTRA_SHA_libmad0=b482b33dc7c3490bb47c6e92351630b678f3d16c2d9c72236f8ab8ec63b1fcb3
for lib in libjpeg62 libmad0; do
  want=EXTRA_SHA_$lib; want=${!want}
  f=$(ls "$D"/${lib}_*.deb 2>/dev/null | head -1 || true)
  if [ -z "$f" ] || [ "$(sha "$f")" != "$want" ]; then
    rm -f "$D"/${lib}_*.deb; ( cd "$D" && apt-get download "$lib" >/dev/null ); f=$(ls "$D"/${lib}_*.deb | head -1)
    got=$(sha "$f"); if [ "$got" != "$want" ]; then [ "${RECORD:-0}" = 1 ] && echo "RECORD $lib sha256 $got" || { echo "$lib: sha256 $got does not match the pin" >&2; exit 4; }; fi
  fi
  dpkg-deb -x "$f" "$O/gpac"
done
wrap MP4Box "LD_LIBRARY_PATH=\"$O/gpac/usr/lib:$O/gpac/usr/lib/x86_64-linux-gnu:$O/gpac/lib/x86_64-linux-gnu\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}\" GPAC_MODULES_PATH=\"$O/gpac/usr/lib/gpac\" exec \"$O/gpac/usr/bin/MP4Box\" \"\$@\""

# mkvinfo, mkvmerge, mkvextract, mkvpropedit from the AppImage, extracted so FUSE is not needed.
fetch mkvtoolnix "$MKVTOOLNIX_URL" "$MKVTOOLNIX_SHA" mkvtoolnix.AppImage
if ! installed mkvtoolnix "$MKVTOOLNIX_SHA"; then
  rm -rf "$O/mkvtoolnix"; mkdir -p "$O/mkvtoolnix"; chmod +x "$D/mkvtoolnix.AppImage"
  ( cd "$O/mkvtoolnix" && "$D/mkvtoolnix.AppImage" --appimage-extract >/dev/null )
  echo "$MKVTOOLNIX_SHA" > "$O/mkvtoolnix/.pin"
fi
for t in mkvinfo mkvmerge mkvextract mkvpropedit; do
  wrap "$t" "LD_LIBRARY_PATH=\"$O/mkvtoolnix/squashfs-root/usr/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}\" exec \"$O/mkvtoolnix/squashfs-root/usr/bin/$t\" \"\$@\""
done

# MediaInfo command line, static build.
fetch mediainfo "$MEDIAINFO_URL" "$MEDIAINFO_SHA" mediainfo.zip
if ! installed mediainfo "$MEDIAINFO_SHA"; then
  rm -rf "$O/mediainfo"; mkdir -p "$O/mediainfo"; unzip -q -o "$D/mediainfo.zip" -d "$O/mediainfo"; echo "$MEDIAINFO_SHA" > "$O/mediainfo/.pin"
fi
MI=$(find "$O/mediainfo" -type f -name mediainfo | head -1); chmod +x "$MI"; ln -sf "$MI" "$O/bin/mediainfo"

# ExifTool release archive, executed only.
fetch exiftool "$EXIFTOOL_URL" "$EXIFTOOL_SHA" exiftool.tar.gz
if ! installed exiftool "$EXIFTOOL_SHA"; then
  rm -rf "$O/exiftool"; mkdir -p "$O/exiftool"; tar -xzf "$D/exiftool.tar.gz" -C "$O/exiftool" --strip-components=1; echo "$EXIFTOOL_SHA" > "$O/exiftool/.pin"
fi
wrap exiftool "exec perl \"$O/exiftool/exiftool\" \"\$@\""

# mkvalidator, when a build exists (see build-mkvalidator.sh).
[ -x "$O/bin/mkvalidator" ] || echo "note: mkvalidator not installed (tools/oracles/build-mkvalidator.sh)"

echo "=== versions"
"$O/bin/ffprobe" -version | head -1
"$O/bin/MP4Box" -version 2>&1 | head -1
"$O/bin/mkvinfo" --version | head -1
"$O/bin/mediainfo" --Version | tail -1
echo "exiftool $("$O/bin/exiftool" -ver)"
echo "=== sha256 (as pinned)"
for n in FFMPEG GPAC MKVTOOLNIX MEDIAINFO EXIFTOOL; do v=${n}_SHA; echo "$n ${!v}"; done

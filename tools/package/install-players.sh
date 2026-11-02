#!/usr/bin/env bash
# Fetches the released hls.js and dash.js browser bundles from the npm registry into $ONEMEDIA_HOME/oracles/players.
# Only the minified bundle of each package is extracted; it is loaded by a page of ours and never opened or read.
# Each tarball is pinned by the SHA-1 the registry publishes for it; the SHA-256 of what was fetched is printed for the record.
set -euo pipefail
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; DEST="$HOME_DIR/oracles/players"; mkdir -p "$DEST/dl"
fetch() { # name version sha1 member-pattern output
  local name="$1" ver="$2" sha1="$3" member="$4" out="$5" tgz="$DEST/dl/$1-$2.tgz"
  [ -s "$tgz" ] || curl -fsSL "https://registry.npmjs.org/$name/-/$name-$ver.tgz" -o "$tgz"
  [ "$(sha1sum "$tgz" | cut -d' ' -f1)" = "$sha1" ] || { echo "sha1 mismatch for $tgz" >&2; exit 1; }
  echo "$name $ver sha256 $(sha256sum "$tgz" | cut -d' ' -f1)"
  tar -xzOf "$tgz" "$member" > "$DEST/$out"
}
fetch hls.js 1.7.4 1857f0cf1b849da5d7ea86b42bce8a321bf912de package/dist/hls.min.js hls.min.js
fetch dashjs 5.2.1 c36648002e0e278f897aa7e2fc9a79ae05560d57 package/dist/modern/umd/dash.all.min.js dash.all.min.js
ls -l "$DEST"/*.js

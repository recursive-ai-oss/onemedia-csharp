#!/usr/bin/env bash
# Process-level checks of the safe writer through the command-line tool: symlinked targets, permissions and ownership, read-only folders,
# a target held open, a full disk (a small tmpfs in a user namespace, no root needed), and kill -9 during Save.
#
#   tools/run-writer-review.sh build
#   tools/run-writer-review.sh env        symlink, permissions, read-only, held-open, in-place, empty-edit scenarios
#   tools/run-writer-review.sh fulldisk   needs `unshare -rm` (user namespaces); skipped with a message otherwise
#   tools/run-writer-review.sh kill [rounds] [file]   kill -9 the tool at random points of an atomic replace
#
# Environment: ONEMEDIA_ORACLES (default ~/onemedia/oracles/bin), W (work dir, default ~/onemedia/u35-review/w).
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
export PATH=$HOME/dotnet10:${ONEMEDIA_ORACLES:-$HOME/onemedia/oracles/bin}:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
W=${W:-$HOME/onemedia/u35-review/w}
BIN=${BIN:-$HOME/onemedia/u35-review/bin}
mkdir -p "$W" "$BIN"
TAGS() { dotnet "$BIN/onemedia.dll" tags "$@"; }
sha() { sha256sum "$1" | cut -c1-16; }
say() { printf '%s\n' "$*"; }

big_mp4() { # $1 = output, $2 = approximate size in MB (random-ish video so it does not compress)
  ffmpeg -v error -y -f lavfi -i "testsrc2=size=640x360:rate=25:duration=${2:-3}" -f lavfi -i "sine=duration=${2:-3}" -c:v mpeg4 -q:v 2 -c:a aac "$1"
}

case "${1:-}" in
build)
  cd "$HERE" && dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary -m:4 -o "$BIN" 2>&1 | tail -3
  ;;
env)
  cd "$W" && rm -rf env && mkdir env && cd env
  cp "$HERE/testdata/metadata/tags_ilst.mp4" real.mp4
  say "== symlink: Save through a link"
  mkdir linkdir && ln -s ../real.mp4 linkdir/link.mp4
  h0=$(sha real.mp4)
  TAGS linkdir/link.mp4 --set title=ViaLink --replace | head -2
  say "link is still a symlink: $([ -L linkdir/link.mp4 ] && echo yes || echo NO)   real file changed: $([ "$(sha real.mp4)" != "$h0" ] && echo yes || echo NO)"
  ls -l linkdir | tail -n +2
  say "== permissions and ownership"
  cp "$HERE/testdata/metadata/tags_ilst.mp4" perm.mp4; chmod 640 perm.mp4; ln perm.mp4 perm-hard.mp4
  setfattr -n user.test -v keep perm.mp4 2>&1 | head -1
  touch -d '2020-01-02 03:04:05' perm.mp4
  stat -c 'before mode=%a owner=%U group=%G links=%h mtime=%y inode=%i' perm.mp4
  TAGS perm.mp4 --set title=Perm --replace | head -1
  stat -c 'after  mode=%a owner=%U group=%G links=%h mtime=%y inode=%i' perm.mp4
  say "hard link sees new title: $(TAGS perm-hard.mp4 | grep -c Perm)   xattr kept: $(getfattr -n user.test perm.mp4 2>&1 | grep -c keep)"
  say "== read-only file in a writable folder"
  cp "$HERE/testdata/metadata/tags_ilst.mp4" ro.mp4; chmod 444 ro.mp4
  TAGS ro.mp4 --set title=RO --replace | head -1; echo "exit=$?"; stat -c 'mode after=%a' ro.mp4
  TAGS ro.mp4 --set title=RO2 --in-place | head -1
  say "== read-only folder"
  mkdir rodir; cp "$HERE/testdata/metadata/tags_ilst.mp4" rodir/a.mp4; chmod 555 rodir
  h=$(sha rodir/a.mp4); TAGS rodir/a.mp4 --set title=X --replace; echo "exit=$?"; say "unchanged: $([ "$(sha rodir/a.mp4)" = "$h" ] && echo yes || echo NO); stray files: $(ls -a rodir | grep -c tmp)"
  TAGS rodir/a.mp4 --set title=Y --out rodir/b.mp4; echo "exit(out)=$?"
  chmod 755 rodir
  say "== target held open (Linux: rename succeeds, the holder keeps the old inode)"
  cp "$HERE/testdata/metadata/tags_ilst.mp4" held.mp4
  ( exec 9<held.mp4; sleep 4 ) & hp=$!
  sleep 1; TAGS held.mp4 --set title=Held --replace | head -1; echo "exit=$?"; wait $hp
  say "== SaveAs onto an existing file / onto itself"
  cp "$HERE/testdata/metadata/tags_ilst.mp4" s1.mp4; cp s1.mp4 s2.mp4
  TAGS s1.mp4 --set title=Z --out s2.mp4; echo "exit(existing)=$?"
  TAGS s1.mp4 --set title=Z --out s1.mp4; echo "exit(self)=$?"
  ln -s s1.mp4 s1link.mp4; TAGS s1.mp4 --set title=Z --out s1link.mp4; echo "exit(self via symlink)=$?"; say "s1 intact: $(TAGS s1.mp4 | grep -c Z)"
  say "== stray temp files: $(find . -name '*.onemedia-*.tmp' | wc -l)"
  ;;
fulldisk)
  cd "$W" && rm -rf fd && mkdir fd
  big_mp4 fd/src.mp4 6
  ls -l fd/src.mp4 | awk '{print "source bytes", $5}'
  unshare -rm bash -c '
    export PATH='"$PATH"' DOTNET_ROOT='"$DOTNET_ROOT"' DOTNET_NOLOGO=1
    mkdir -p q && mount -t tmpfs -o size=8m tmpfs q
    cp fd/src.mp4 q/a.mp4; dd if=/dev/zero of=q/filler bs=1k count=$(( $(df -k q | tail -1 | tr -s " " | cut -d" " -f4) - 1500 )) 2>/dev/null   # leaves about 1.5 MB: a copy of a no longer fits
    df -k q | tail -1
    a0=$(sha256sum q/a.mp4 | cut -c1-16)
    dotnet '"$BIN"'/onemedia.dll tags q/a.mp4 --set title=Full --replace; echo "exit(replace)=$?"
    echo "a unchanged: $([ "$(sha256sum q/a.mp4 | cut -c1-16)" = "$a0" ] && echo yes || echo NO); files: $(ls -a q | tr "\n" " ")"
    dotnet '"$BIN"'/onemedia.dll tags q/a.mp4 --set title=Full --out q/c.mp4; echo "exit(out)=$?"; echo "files: $(ls -a q | tr "\n" " ")"
    dotnet '"$BIN"'/onemedia.dll tags q/a.mp4 --set title=Full2 --in-place; echo "exit(in-place)=$?"
  ' 2>&1 | cut -c1-300
  ;;
kill)
  rounds=${2:-100}; f=${3:-}
  cd "$W" && rm -rf kill && mkdir kill && cd kill
  [ -n "$f" ] || { big_mp4 big.mp4 1200; f=big.mp4; }
  [ "$f" = big.mp4 ] || cp "$f" big.mp4
  ls -l big.mp4 | awk '{print "file bytes", $5}'
  h0=$(sha big.mp4)
  # a reference result of an uninterrupted replace, to compare complete outcomes with
  cp big.mp4 ref.mp4; s=$(date +%s%N); TAGS ref.mp4 --set title=Killed --replace >/dev/null; e=$(date +%s%N); ref=$(sha ref.mp4)
  total_ms=$(( (e - s) / 1000000 )); say "uninterrupted replace: ${total_ms} ms (includes start-up)"
  orig=0; done_=0; bad=0; strays=0
  for i in $(seq 1 "$rounds"); do
    cp big.mp4 t.mp4
    TAGS t.mp4 --set title=Killed --replace >/dev/null 2>&1 & p=$!
    sleep "$(awk -v t=$total_ms -v r=$RANDOM 'BEGIN{printf "%.3f", (0.05 + 1.05*r/32767)*t/1000}')"
    kill -9 $p 2>/dev/null; wait $p 2>/dev/null
    h=$(sha t.mp4)
    if [ "$h" = "$h0" ]; then orig=$((orig+1)); elif [ "$h" = "$ref" ]; then done_=$((done_+1)); else bad=$((bad+1)); say "round $i: NEITHER (hash $h)"; fi
    n=$(ls -a | grep -c 'onemedia-.*tmp'); strays=$((strays+n)); rm -f .t.mp4.onemedia-*.tmp
  done
  say "rounds=$rounds original=$orig complete=$done_ neither=$bad stray temp files seen=$strays"
  ;;
report)
  # What the write-back report claims against what ExifTool and MediaInfo read back, for one edit set per fixture.
  cd "$W" && rm -rf rep && mkdir rep && cd rep
  for f in metadata/tags_ilst.mp4 metadata/tags_keys.mov isobmff/bframes_aac.mp4 audio/aac.m4a metadata/mkvmerge_global.mkv audio/opus.webm audio/flac.flac audio/opus.ogg audio/vorbis.ogg \
           pcm/bwf-v0.wav pcm/aiff-id3.aiff audio-es/tagged/id3v24-basic.mp3 audio-es/tagged/id3v1-only.mp3 audio-es/tagged/apev2-only.mp3 asf/wmav2-tagged.wma; do
    b=$(basename "$f"); cp "$HERE/testdata/$f" "$b"
    echo "=== $f"
    exiftool -a -G1 -s "$b" 2>/dev/null | grep -iE 'title|artist|album|genre|comment|date|year|track|description' | sort > "$b.before"
    TAGS "$b" --set title="Titre é中" --set artist="Artiste" --set album="Album" --set genre=Rock --set date=2024-05-06 --set tracknumber=7 --set comment="Un commentaire" --remove composer --replace
    exiftool -a -G1 -s "$b" 2>/dev/null | grep -iE 'title|artist|album|genre|comment|date|year|track|description' | sort > "$b.after"
    echo "-- exiftool after (changed lines)"; diff "$b.before" "$b.after" | grep '^>' | cut -c1-110
    echo "-- mediainfo general/audio fields"; mediainfo "$b" 2>/dev/null | grep -iE '^(Track name|Performer|Album|Genre|Comment|Recorded date|Track name/Position|Composer)\s' | cut -c1-110
  done
  ;;
*) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

#!/usr/bin/env bash
# Plays remuxed WebM/Matroska files in headless browsers, progressively and appended whole to a MediaSource, next to the files they came from.
#
#   usage: remux-mse.sh BROWSER [BROWSER ...]       e.g.  remux-mse.sh chromium firefox
#   Needs the playback matrix of run.sh in $WORK/matrix and its manifest.json.
#   Sets written to $WORK/remux-mse/{orig,same,webm}: the matrix files as written by ffmpeg, remuxed to their own container, and
#   remuxed to WebM (VP8/VP9/AV1 with Opus/Vorbis only). Results: $WORK/remux-mse/<set>.<browser>.jsonl and summary lines on stdout.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
WORK="${WORK:-$HOME_DIR/playback}"
DOTNET_DIR="${DOTNET_DIR:-$HOME/dotnet10}"
export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$WORK}"
PY="$HOME_DIR/oracles/playwright/venv/bin/python"
OUT="$WORK/remux-mse"
mkdir -p "$OUT/orig" "$OUT/same" "$OUT/webm"

dotnet build "$ROOT/src/OneMedia.Tool" -c Release -v quiet -clp:NoSummary -m:4 >/dev/null
TOOL=(dotnet "$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Tool/release/onemedia.dll")

for f in "$WORK"/matrix/*.webm "$WORK"/matrix/*.mkv "$WORK"/matrix/*.mka; do
  [ -e "$f" ] || continue
  b=$(basename "$f")
  ln -sf "$f" "$OUT/orig/$b"
  "${TOOL[@]}" remux "$f" "$OUT/same/$b" --overwrite >/dev/null 2>&1 || echo "remux refused: $b"
  case "$b" in
    *av1*|*vp8*|*vp9*|*opus.webm|*vorbis.webm)
      w="${b%.*}.webm"
      "${TOOL[@]}" remux "$f" "$OUT/webm/$w" --format webm --overwrite >/dev/null 2>&1 || echo "webm refused: $b" ;;
  esac
done
# An extra input made by hand (VP9 + Opus with priming), if present.
[ -f "$WORK/extra/in.mkv" ] && { ln -sf "$WORK/extra/in.mkv" "$OUT/orig/extra-in.mkv"; "${TOOL[@]}" remux "$WORK/extra/in.mkv" "$OUT/same/extra-in.mkv" --overwrite >/dev/null 2>&1; "${TOOL[@]}" remux "$WORK/extra/in.mkv" "$OUT/webm/extra-in.webm" --format webm --overwrite >/dev/null 2>&1; } || true

python3 - "$WORK/manifest.json" "$OUT" <<'EOF'
import json, os, sys
manifest = {e["file"]: e for e in json.load(open(sys.argv[1]))}
out = sys.argv[2]
for s in ("orig", "same", "webm"):
    rows = []
    for name in sorted(os.listdir(os.path.join(out, s))):
        key = name if name in manifest else None
        if key is None:
            stem = os.path.splitext(name)[0]
            key = next((k for k in manifest if os.path.splitext(k)[0] == stem), None)
        if key is None:
            e = {"container": "webm" if name.endswith(".webm") else "matroska", "contentType": "video/webm" if name.endswith(".webm") else "video/x-matroska",
                 "mime": 'video/webm; codecs="vp9,opus"', "hasVideo": True, "hasAudio": True}
        else:
            e = dict(manifest[key])
        e["file"] = name
        if name.endswith(".webm"):
            e["contentType"] = "video/webm"
            e["mime"] = e["mime"].replace("video/x-matroska", "video/webm")
        e["modes"] = ["progressive", "mediasource"]
        rows.append(e)
    json.dump(rows, open(os.path.join(out, s + ".json"), "w"), indent=1)
EOF
for br in "$@"; do
  for s in orig same webm; do
    rm -f "$OUT/$s.$br.jsonl"
    "$PY" "$HERE/probe.py" --browser "$br" --manifest "$OUT/$s.json" --media "$OUT/$s" --out "$OUT/$s.$br.jsonl" --only "" || true
    python3 "$HERE/mse-summary.py" "$OUT/$s.$br.jsonl" "$br $s"
  done
done

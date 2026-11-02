#!/usr/bin/env bash
# Plays the packaged presentations under $1 in Chrome (system build), Firefox and WebKit through hls.js, dash.js and, in WebKit, native HLS.
# Results: $2/<browser>.jsonl, one line per presentation and player. Resumes after a browser freeze (the checker exits 3).
#   usage: run-browsers.sh <root> <results-dir> [browser...]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
ROOT="$1"; RES="$2"; shift 2; BROWSERS="${*:-chrome firefox webkit}"
PY="$HOME_DIR/oracles/playwright/venv/bin/python"; PLAYERS="$HOME_DIR/oracles/players"; export ONEMEDIA_HOME="$HOME_DIR"
mkdir -p "$RES"
for b in $BROWSERS; do
  case "$b" in chrome) args="--browser chromium --executable /usr/bin/google-chrome";; *) args="--browser $b";; esac
  resume=""
  for attempt in 1 2 3 4 5 6; do
    timeout 1500 "$PY" "$HERE/browser-check.py" $args --root "$ROOT" --players "$PLAYERS" --out "$RES/$b.jsonl" $resume 2>> "$RES/$b.err" && break
    resume="--resume"
  done
done

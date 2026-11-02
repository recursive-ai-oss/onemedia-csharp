#!/usr/bin/env bash
# Runs the installed DASH-IF conformance validator (see install-dashif.sh) over a packaged presentation: serves <dir> on a local port and
# analyses <dir>/manifest.mpd with every module enabled. Output goes to stdout; the exit status is the validator's.
#   usage: run-dashif.sh <dir> [module...]
set -uo pipefail
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; D="$HOME_DIR/oracles/dashif"
DIR="$(cd "$1" && pwd)"; shift
export PATH="$D/php:$D/jdk/bin:$HOME_DIR/oracles/bin:$PATH" JAVA_HOME="$D/jdk" OTEL_SDK_DISABLED=true OTEL_TRACES_EXPORTER=none OTEL_METRICS_EXPORTER=none OTEL_LOGS_EXPORTER=none
PORT=$((20000 + RANDOM % 20000))
( cd "$DIR" && exec python3 -m http.server "$PORT" --bind 127.0.0.1 > /dev/null 2>&1 ) & SRV=$!
trap 'kill $SRV 2>/dev/null' EXIT
sleep 1
cd "$D/app"
# The validator keeps what it fetched in its cache (a database table) and reuses it for the next run, whatever the URL; every run starts with an empty cache.
php artisan cache:clear > /dev/null 2>&1
if [ $# -eq 0 ]; then php artisan app:analyze-manifest --all "http://127.0.0.1:$PORT/manifest.mpd"; else php artisan app:analyze-manifest "http://127.0.0.1:$PORT/manifest.mpd" "$@"; fi

#!/usr/bin/env bash
# Runs probe.py until every observation is in the output file: a browser that freezes is recorded as hung by the watchdog
# (exit status 3) and the run resumes after it.
#   usage: probe-loop.sh PYTHON probe.py-arguments...
set -u
PY="$1"; shift
HERE="$(cd "$(dirname "$0")" && pwd)"
first=1
for attempt in 1 2 3 4 5 6 7 8 9 10; do
  if [ "$first" = 1 ]; then "$PY" "$HERE/probe.py" "$@"; else "$PY" "$HERE/probe.py" "$@" --resume; fi
  rc=$?
  first=0
  [ "$rc" = 3 ] || exit "$rc"
done
exit 3

#!/usr/bin/env bash
# Runs tools/run-mux-diff.sh for every layout the ISO muxer writes. Summaries land in $WORK/<tag>/summary.tsv.
#   ORACLES=... CORPUS=... WORK=... [JOBS=4] tools/run-mux-sweep.sh
set -u
for run in "first:" "last:" "last:--no-seek" "frag:" "frag:--cmaf --sidx --mfra"; do
  layout=${run%%:*}
  opts=${run#*:}
  LAYOUT=$layout OPTS="$opts" "$(dirname "$0")/run-mux-diff.sh" > "$WORK/$layout$(echo "$opts" | tr -d ' -').log" 2>&1
done
echo done > "$WORK/sweep.done"

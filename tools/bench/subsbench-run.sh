#!/usr/bin/env bash
# Runs the subtitle rows once and records what the machine was doing around them.
# Usage: subsbench-run.sh BENCH_DLL OUT_PREFIX [subsbench args...]
# Writes OUT_PREFIX.tsv (the rows) and OUT_PREFIX.env (CPU, cores, load before, during and after).
set -u
dll=$1
out=$2
shift 2
load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1 /proc/loadavg; else sysctl -n vm.loadavg | tr -d '{}' | awk '{print $1}'; fi; }
cpu() {
  if [ -r /proc/cpuinfo ]; then grep -m1 -E 'model name|^Model' /proc/cpuinfo | cut -d: -f2 | sed 's/^ *//'; else sysctl -n machdep.cpu.brand_string; fi
}
cores() { nproc 2>/dev/null || sysctl -n hw.logicalcpu; }
{
  echo "cpu=$(cpu)"
  echo "cores=$(cores)"
  echo "os=$(uname -sr) $(uname -m)"
  echo "start=$(date -u +%Y-%m-%dT%H:%MZ)"
  echo "load_before=$(load)"
} > "$out.env"
( while :; do load >> "$out.load"; sleep 5; done ) &
sampler=$!
t0=$(date +%s)
dotnet "$dll" subsbench "$@" > "$out.tsv" 2> "$out.err"
rc=$?
t1=$(date +%s)
kill $sampler 2>/dev/null
echo "load_after=$(load)" >> "$out.env"
echo "elapsed_s=$((t1 - t0))" >> "$out.env"
echo "load_during=$(awk '{s+=$1; if($1>m)m=$1; n++} END{printf "mean %.2f, max %.2f over %d samples", s/n, m, n}' "$out.load" 2>/dev/null)" >> "$out.env"
exit $rc

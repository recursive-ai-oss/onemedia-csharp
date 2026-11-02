#!/usr/bin/env bash
# Repairs the multi-gigabyte corpus files on the worker and reports peak resident memory: the healthy file (must come back identical), and
# a copy cut at 70% (a cut recording). Every file over 1 GB that this makes is deleted right after the check that uses it.
#   tools/run-repair-large.sh <work-dir>
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
W=$1; COR=$HOME/onemedia/corpus
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 ONEMEDIA_SCRATCH=$W DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
mkdir -p "$W"
( cd "$here" && dotnet build tools/OneMedia.RepairCheck -c Release -v quiet -clp:NoSummary -p:NuGetAudit=false 2>&1 | grep -E ' error ' | head -3 )
RC=$(find "$W/artifacts/bin/OneMedia.RepairCheck" -name OneMedia.RepairCheck.dll | head -1)
run() { # label input output -> prints the first report line and peak RSS in MiB
  python3 - "$RC" "$2" "$3" <<'PY'
import resource, subprocess, sys, time
t = time.time()
p = subprocess.run(["dotnet", "exec", sys.argv[1], "repair", sys.argv[2], sys.argv[3]], capture_output=True, text=True)
r = resource.getrusage(resource.RUSAGE_CHILDREN)
print((p.stdout.splitlines() or [p.stderr[:200]])[0], "| peak RSS %.0f MiB | %.0f s" % (r.ru_maxrss / 1024, time.time() - t))
PY
}
for f in generated/large/over4gb.mkv generated/avi/large/odml-4p6gb.avi generated/large/over4gb-moov-at-end.mp4 generated/mpeg/large/ts-2gb.ts; do
  in=$COR/$f; [ -f "$in" ] || { echo "missing $f"; continue; }
  ext="${f##*.}"; size=$(stat -c %s "$in")
  echo "== $f ($((size / 1048576)) MiB)"
  rm -f "$W/healthy.$ext"
  echo "healthy: $(run x "$in" "$W/healthy.$ext")"
  if cmp -s "$in" "$W/healthy.$ext"; then echo "  output identical to input"; else echo "  output DIFFERS from input"; fi
  rm -f "$W/healthy.$ext"
  head -c $((size * 7 / 10)) "$in" > "$W/cut.$ext"
  rm -f "$W/fixed.$ext"
  echo "cut 70%: $(run x "$W/cut.$ext" "$W/fixed.$ext")"
  if [ -f "$W/fixed.$ext" ]; then ffprobe -v error -show_entries format=duration,size -of csv=p=0 "$W/fixed.$ext" 2>&1 | head -3 | tr '\n' ' '; echo; fi
  rm -f "$W/cut.$ext" "$W/fixed.$ext"
done
rm -rf "$W/artifacts"

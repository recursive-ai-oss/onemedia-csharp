#!/usr/bin/env bash
# Runs the Matroska muxer sweeps as queued heavy jobs, each within the queue's 30-minute limit.
#
#   tools/matroska/wq-sweep.sh <work-dir> <stage>...      stage: tests | mkv | iso0..iso3 | live | big
#
# Lists are expected at <work-dir>/mkv-files.txt, <work-dir>/chunk-0N (ISO) and <work-dir>/live-files.txt.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="${1:?work dir}"; shift
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 ONEMEDIA_SCRATCH="$work"
for s in "$@"; do
  log="$work/sweep-$s.log"
  case "$s" in
    tests) cmd="dotnet build $here/tests/OneMedia.Tests -m:4 -v quiet -clp:NoSummary && dotnet exec $work/artifacts/bin/OneMedia.Tests/debug/RecursiveAI.OneMedia.Tests.dll -class '*Matroska*' -class '*Mux*'" ; cores=4;;
    mkv) cmd="$here/tools/matroska/run-mux-checks.sh $work/m-mkv $work/mkv-files.txt"; cores=4;;
    iso?) n=${s#iso}; cmd="$here/tools/matroska/run-mux-checks.sh $work/m-iso$n $work/chunk-0$n"; cores=4;;
    live) cmd="$here/tools/matroska/run-mux-checks.sh $work/m-live $work/live-files.txt --live"; cores=4;;
    big) cmd="dotnet build $here/tools/OneMedia.MatroskaMuxCheck -c Release -v quiet -clp:NoSummary && mkdir -p $work/m-big && dotnet exec $work/artifacts/bin/OneMedia.MatroskaMuxCheck/release/OneMedia.MatroskaMuxCheck.dll check $HOME/onemedia/corpus/generated/large/over4gb.mkv $work/m-big/over4gb.out.mkv --ffprobe $HOME/onemedia/oracles/bin/ffprobe --no-probe && ls -l $work/m-big && $HOME/onemedia/oracles/bin/mkvalidator $work/m-big/over4gb.out.mkv | tail -5; $HOME/onemedia/oracles/bin/mkvinfo $work/m-big/over4gb.out.mkv | head -5; rm -f $work/m-big/over4gb.out.mkv"; cores=2;;
    smoke) cmd="cd $here && dotnet publish smoke/OneMedia.AotSmoke -c Release -r linux-x64 -o $work/aot -warnaserror 2>&1 | tail -5; $work/aot/OneMedia.AotSmoke | grep -E 'FAIL|matroska|mux|^ok' | tail -40; dotnet build smoke/OneMedia.WasmSmoke -c Release 2>&1 | grep -E 'error|warning|Warn' | head -5; cd $work/artifacts/bin/OneMedia.WasmSmoke/release_browser-wasm/AppBundle && node main.mjs 2>&1 | grep -E 'FAIL|matroska|mux|^ok' | tail -40"; cores=6;;
    bench) cmd="cd $here/bench/OneMedia.Bench && dotnet run -c Release -- run --rows mkv-mux --out $work/bench-out --reps 3"; cores=6;;
    *) echo "unknown stage $s"; continue;;
  esac
  mkdir -p "$work"
  wq run --class heavy --cores $cores --max 28m --label "onemedia u29 mkvmux $s" -- bash -c "$cmd" > "$log" 2>&1
  echo "$s exit $? ($log)"
done

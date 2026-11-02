#!/bin/bash
# Plays reconstructed files in headless browsers: the 60 % cut of a few ffmpeg-written recordings, rebuilt with and without a reference.
#   tools/run-reconstruct-playback.sh WORKDIR [BROWSER...]     default browsers: chromium firefox webkit
# Needs the dll built by run-reconstruct-checks.sh (WORKDIR/scratch) and the fixtures it generated (WORKDIR/fx); run that first or pass the same WORKDIR.
set -euo pipefail
W=${1:?work dir}; shift || true
BROWSERS=${*:-chromium firefox webkit}
HERE=$(cd "$(dirname "$0")" && pwd)
export PATH=$HOME/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEMEDIA_SCRATCH=$W/scratch
PY=$HOME/onemedia/oracles/playwright/venv/bin/python
export PLAYWRIGHT_BROWSERS_PATH=$HOME/onemedia/oracles/playwright/browsers
DLL=$W/scratch/artifacts/bin/OneMedia.ReconstructCheck/release/OneMedia.ReconstructCheck.dll
test -f "$DLL" || (cd "$HERE/OneMedia.ReconstructCheck" && dotnet build -c Release -v quiet -clp:NoSummary -p:NuGetAudit=false -m:4 | tail -2)
P=$W/play; rm -rf "$P"; mkdir -p "$P/cuts" "$P/out"
for n in h264b-aac h264-mp3 h264-only; do
  F=$W/fx/$n.mp4
  python3 "$HERE/reconstruct-fixtures.py" cut "$F" "$P/cuts"
  C=$P/cuts/$n.cut60.mp4
  dotnet "$DLL" reconstruct "$C" "$P/out/$n-ref.mp4" --ref "$F" | head -1
  dotnet "$DLL" inband "$F" "$P/$n.inband.mp4"
  python3 "$HERE/reconstruct-fixtures.py" cut "$P/$n.inband.mp4" "$P/cuts"
  dotnet "$DLL" reconstruct "$P/cuts/$n.inband.cut60.mp4" "$P/out/$n-noref.mp4" | head -1
done
for b in $BROWSERS; do
  echo "== $b"
  "$PY" "$HERE/reconstruct-playback.py" "$b" "$P"/out/*.mp4 || true
done
rm -rf "$P"

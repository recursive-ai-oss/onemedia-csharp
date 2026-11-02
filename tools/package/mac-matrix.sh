#!/usr/bin/env bash
# Packages a few generated inputs on macOS and plays them in Playwright's WebKit and Chrome: hls.js, dash.js and native HLS (WebKit on macOS has it).
# State under $ONEMEDIA_HOME (default ~/onemedia/u62-package). Results: $WORK/results/*.jsonl
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; WORK="${WORK:-$HOME_DIR/u62-package}"
export PATH="$HOME_DIR/dotnet:$PATH" DOTNET_ROOT="$HOME_DIR/dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMEDIA_SCRATCH="$WORK"
FFMPEG="${FFMPEG:-$HOME_DIR/oracles/bin/ffmpeg}"
mkdir -p "$WORK/logs" "$WORK/matrix"
"$HERE/install-players.sh" > "$WORK/logs/players.log" 2>&1
"$HERE/gen-inputs.sh" "$FFMPEG" "$WORK/inputs" > "$WORK/logs/gen.log" 2>&1
dotnet build "$ROOT/src/OneMedia.Tool/OneMedia.Tool.csproj" -c Release -v quiet -clp:NoSummary -p:NuGetAudit=false > "$WORK/logs/build.log" 2>&1 || { tail -5 "$WORK/logs/build.log"; exit 1; }
TOOL=(dotnet "$WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll")
rm -rf "$WORK/matrix"; mkdir -p "$WORK/matrix"
IN="$WORK/inputs/h264-aac.mp4"
"${TOOL[@]}" package "$IN" "$WORK/matrix/cmaf" --segment 4 > "$WORK/logs/p1.log" 2>&1
"${TOOL[@]}" package "$IN" "$WORK/matrix/cmaf-single" --segment 4 --single-file > "$WORK/logs/p2.log" 2>&1
"${TOOL[@]}" package "$IN" "$WORK/matrix/ts" --segment 4 --ts > "$WORK/logs/p3.log" 2>&1
"${TOOL[@]}" package "$IN" "$WORK/matrix/ts-single" --segment 4 --ts --single-file > "$WORK/logs/p4.log" 2>&1
"${TOOL[@]}" package "$WORK/inputs/h264-aac-subs.mkv" "$WORK/matrix/ts-subs" --segment 4 --ts > "$WORK/logs/p5.log" 2>&1
"${TOOL[@]}" package "$WORK/inputs/h264-aac-subs.mkv" "$WORK/matrix/cmaf-subs" --segment 4 > "$WORK/logs/p6.log" 2>&1
"${TOOL[@]}" package "$WORK/inputs/ladder-180.mp4" "$WORK/inputs/ladder-360.mp4" "$WORK/inputs/ladder-720.mp4" "$WORK/matrix/ladder" --segment 4 > "$WORK/logs/p7.log" 2>&1
PY="$HOME_DIR/oracles/playwright/venv/bin/python"; export ONEMEDIA_HOME="$HOME_DIR"
mkdir -p "$WORK/results"
for b in webkit chromium; do
  extra=""; [ "$b" = chromium ] && extra="--executable /Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
  resume=""
  for attempt in 1 2 3 4; do
    if [ "$b" = chromium ]; then
      "$PY" "$HERE/browser-check.py" --browser chromium --executable "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --root "$WORK/matrix" --players "$HOME_DIR/oracles/players" --out "$WORK/results/mac-$b.jsonl" $resume 2>> "$WORK/results/mac-$b.err" && break
    else
      "$PY" "$HERE/browser-check.py" --browser "$b" --root "$WORK/matrix" --players "$HOME_DIR/oracles/players" --out "$WORK/results/mac-$b.jsonl" $resume 2>> "$WORK/results/mac-$b.err" && break
    fi
    resume="--resume"
  done
done
python3 - <<PY
import json, collections
for b in ("webkit", "chromium"):
    c = collections.Counter()
    for l in open("$WORK/results/mac-%s.jsonl" % b):
        r = json.loads(l); c[(r["player"], bool(r.get("pass")))] += 1
    print(b, dict(c))
PY

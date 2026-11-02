#!/usr/bin/env bash
# Builds the browser-wasm test host and runs the suite in headless Chromium.
#   ONEMEDIA_SCRATCH  build output root (as for every build in this repository)
#   PYTHON            Python with the Playwright package (default python3); set PLAYWRIGHT_BROWSERS_PATH to its browsers
# Extra arguments go to tools/wasm-suite/run.py (for example: --log suite.log -- --quiet).
set -eu
R=$(cd "$(dirname "$0")/../.." && pwd)
dotnet build "$R/tests/OneMedia.Tests.Browser" -c Release -v quiet -clp:NoSummary
BUNDLE=$(find "${ONEMEDIA_SCRATCH:+$ONEMEDIA_SCRATCH/}artifacts" "$R/artifacts" -type d -name AppBundle -path '*OneMedia.Tests.Browser/release*' 2>/dev/null | head -1)
"${PYTHON:-python3}" "$R/tools/wasm-suite/run.py" "$BUNDLE" "$@"

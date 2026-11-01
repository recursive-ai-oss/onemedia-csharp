#!/usr/bin/env bash
# Builds the onemedia tool and runs tools/trim/trim-matrix.py: generated H.264/HEVC/AV1/VP9 sources with AAC/Opus, several frame rates and GOP shapes,
# random trims checked against a black-box decode.
#
#   ORACLES=<dir with ffmpeg ffprobe> WORK=<scratch dir> [SEED=1] [PER_FILE=3] [ONLY=h264,hevc] [QUICK=1] [VARIANTS=mp4,last,frag,mov,mkv] tools/trim/run-trim-matrix.sh
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg and ffprobe}
WORK=${WORK:?scratch directory}
export ONEMEDIA_SCRATCH="$WORK"
dotnet build src/OneMedia.Tool -c Release -v quiet -clp:NoSummary
TOOL="dotnet $WORK/artifacts/bin/OneMedia.Tool/release/onemedia.dll"
"$ORACLES/ffmpeg" -version | head -1
${PYTHON:-python3} tools/trim/trim-matrix.py --oracles "$ORACLES" --tool "$TOOL" --work "$WORK/matrix" --seed "${SEED:-1}" --per-file "${PER_FILE:-3}" --variants "${VARIANTS:-mp4}" \
  ${ONLY:+--only "$ONLY"} ${QUICK:+--quick}

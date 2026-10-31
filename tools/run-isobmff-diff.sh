#!/usr/bin/env bash
# Builds the comparison tool, regenerates the fixtures with the pinned oracles and compares every file with ffprobe, MP4Box and OneAudio.
# Usage (from the repository root): ORACLES=<dir with ffmpeg ffprobe MP4Box> WORK=<scratch dir> [EXTRA="files..."] tools/run-isobmff-diff.sh
set -eu
ORACLES=${ORACLES:?directory holding ffmpeg, ffprobe and MP4Box}
WORK=${WORK:?scratch directory}
mkdir -p "$WORK/gen"
OUT="$WORK/gen" FFMPEG="$ORACLES/ffmpeg" MP4BOX="$ORACLES/MP4Box" tools/gen-isobmff-fixtures.sh > "$WORK/gen.log" 2>&1 || true
export ONEMEDIA_SCRATCH="$WORK"
dotnet build tools/OneMedia.IsoBmffDiff -c Release -v quiet -clp:NoSummary
TOOL="$WORK/artifacts/bin/OneMedia.IsoBmffDiff/release/OneMedia.IsoBmffDiff.dll"
"$ORACLES/ffprobe" -version | head -1
"$ORACLES/MP4Box" -version 2>&1 | head -1
FILES=$(ls "$WORK"/gen/*.mp4 "$WORK"/gen/*.mov "$WORK"/gen/*.m4a "$WORK"/gen/*.3gp "$WORK"/gen/*.3g2 "$WORK"/gen/*.m4s 2>/dev/null || true)
dotnet "$TOOL" --ffprobe "$ORACLES/ffprobe" --mp4box "$ORACLES/MP4Box" $FILES
if [ -n "${EXTRA:-}" ]; then
  dotnet "$TOOL" --ffprobe "$ORACLES/ffprobe" --mp4box "$ORACLES/MP4Box" --no-audio $EXTRA
fi

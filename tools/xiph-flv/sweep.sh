#!/usr/bin/env bash
# Runs the packet differential and the stream comparison for Ogg/FLAC (family xiph) or FLV (family flv) against ffprobe.
#
#   sweep.sh xiph|flv [ROOT]    ROOT defaults to $ONEMEDIA_HOME/corpus; results go to $ONEMEDIA_HOME/results/<family>-<label>
#
#   SWEEP_LABEL   suffix of the result directory (default: now)
#   SWEEP_CORES   parallel files (default 4)
set -euo pipefail

FAMILY=${1:?usage: sweep.sh xiph|flv [ROOT]}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"
ROOT=${2:-$HOME_DIR/corpus}
LABEL=${SWEEP_LABEL:-$(date +%Y%m%d-%H%M%S)}
OUT="$HOME_DIR/results/$FAMILY-$LABEL"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEMEDIA_HOME="$HOME_DIR" ONEMEDIA_SCRATCH="${ONEMEDIA_SCRATCH:-$HOME_DIR/scratch}"
export PATH="$HOME_DIR/oracles/bin:$PATH"

dotnet build "$REPO/tools/OneMedia.Oracles" -c Release -v quiet -clp:NoSummary >/dev/null
DLL="$ONEMEDIA_SCRATCH/artifacts/bin/OneMedia.Oracles/release/OneMedia.Oracles.dll"
mkdir -p "$OUT"
dotnet "$DLL" diff --family "$FAMILY" --root "$ROOT" --out "$OUT" --exceptions "$REPO/tools/OneMedia.Oracles/exceptions.tsv" --jobs "${SWEEP_CORES:-4}" | tee "$OUT/summary.txt"
mapfile -t FILES < <(find "$ROOT" \( -path '*/_*' -o -name '*.part.*' \) -prune -o -type f \( $( [ "$FAMILY" = flv ] && echo "-name *.flv" || echo "-name *.ogg -o -name *.oga -o -name *.ogv -o -name *.opus -o -name *.spx -o -name *.flac" ) \) -print | sort)
dotnet "$DLL" infocheck "${FILES[@]}" | tee "$OUT/infocheck.txt" | tail -40

# Packets against OneAudio's Ogg reader (single audio streams only) and tags against ATL.NET, ExifTool and MediaInfo.
if [ "$FAMILY" != flv ]; then
  dotnet "$DLL" oneaudio-ogg "${FILES[@]}" > "$OUT/oneaudio-ogg.txt" 2>&1 || true
  tail -1 "$OUT/oneaudio-ogg.txt"
  dotnet "$DLL" atl "${FILES[@]}" > "$OUT/atl.txt" 2>&1 || true
  tail -1 "$OUT/atl.txt"
fi
dotnet "$DLL" meta --out "$OUT" "${FILES[@]}" > "$OUT/meta.txt" 2>&1 || true
tail -5 "$OUT/meta.txt"

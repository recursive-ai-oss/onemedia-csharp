#!/usr/bin/env bash
# Builds the folder the Windows check reads: each fixture twice, as it was (b_<name>) and with a capture time written (a_<name>).
# The time is chosen so the UTC date and the local date of its offset differ (00:30 at +02:00 is still the day before in UTC), so the date an
# application shows reveals which clock it reads.   tools/date-check/prepare.sh <dll-dir-of-DateWriteCheck> <out-folder> [files...]
set -eu
here=$(cd "$(dirname "$0")/../.." && pwd)
bin=$1; out=$2; shift 2
WHEN=${WHEN:-2024-05-01T00:30:00+02:00}
mkdir -p "$out"
files=("$@")
if [ ${#files[@]} -eq 0 ]; then files=("$here"/testdata/datewrite/* "$here"/testdata/metadata/tags_udta.mov "$here"/testdata/metadata/mp4box_itags.mp4 "$here"/testdata/metadata/et_xmp_gps.mp4); fi
for f in "${files[@]}"; do
  n=$(basename "$f")
  cp "$f" "$out/b_$n"
  dotnet "$bin/OneMedia.DateWriteCheck.dll" "$f" "$out/a_$n" "$WHEN" | cut -c1-80
done

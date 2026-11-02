#!/usr/bin/env bash
# Writes an SCC that uses every extended and special CEA-608 character once, and decodes it with OneMedia, ffmpeg and ccextractor (both black-box).
#   run-caption-extended-check.sh <outdir>
# The three texts per caption must be equal; differences are printed.
set -uo pipefail
export PATH=~/dotnet10:$HOME/onemedia/oracles/bin:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
OUT=$1; mkdir -p "$OUT"
HERE=$(cd "$(dirname "$0")" && pwd)
dotnet build "$HERE/OneMedia.CaptionCheck/OneMedia.CaptionCheck.csproj" -c Release -m:4 -v quiet -clp:NoSummary -o "$OUT/_bin" >"$OUT/build.log" 2>&1 || { tail "$OUT/build.log"; exit 1; }
C="dotnet $OUT/_bin/OneMedia.CaptionCheck.dll"
$C mkscc-ext "$OUT/ext.scc" > "$OUT/expected.txt"
$C scc2srt "$OUT/ext.scc" "$OUT/ours.srt"
ffmpeg -v error -y -i "$OUT/ext.scc" "$OUT/ffm.srt"
ccextractor "$OUT/ext.scc" -out=srt -o "$OUT/ccx.srt" >/dev/null 2>&1
python3 - "$OUT" <<'PY'
import re, sys
out = sys.argv[1]
def texts(path):
    try: data = open(path, 'rb').read().decode('utf-8', errors='replace').replace('\r', '')
    except FileNotFoundError: return None
    res = []
    for block in re.split(r'\n\s*\n', data.strip()):
        lines = block.split('\n')
        if len(lines) >= 3: res.append(re.sub(r'<[^>]+>|\{\\an\d\}', '', ' '.join(lines[2:])).strip())
    return res
exp = [l.rstrip('\n') for l in open(out + '/expected.txt', encoding='utf-8') if l.strip()]
for name in ('ours', 'ffm', 'ccx'):
    got = texts(f'{out}/{name}.srt')
    if got is None: print(name, 'no output'); continue
    print(name, 'captions', len(got), 'equal to expected:', sum(1 for a, b in zip(got, exp) if a == b), 'of', len(exp))
    for a, b in zip(got, exp):
        if a != b:
            print('  differs:', ''.join(c for c, d in zip(a, b) if c != d), '|', ''.join(d for c, d in zip(a, b) if c != d), '(got | expected, positions that differ)', len(a), len(b))
PY

#!/bin/bash
# Counts the read calls and bytes one bench cell makes on its input file.
# usage: iocount.sh BENCH_DLL ROW IMPL_INDEX RELATIVE_PATH
set -e
DLL=$1; ROW=$2; IMPL=$3; REL=$4
CORPUS=${ONEMEDIA_HOME:-$HOME/onemedia}/corpus
OUT=$(mktemp -p "${TMPDIR:-$HOME/onemedia/u5-bench}")
strace -f -e trace=openat,close,read,pread64,readv,preadv -o "$OUT" dotnet "$DLL" once "$ROW" "$IMPL" "$REL" "$CORPUS" > "$OUT.stdout"
python3 - "$OUT" "$CORPUS/$REL" <<'E'
import re, sys
log, path = sys.argv[1], sys.argv[2]
fds = set(); calls = 0; total = 0; sizes = {}
for line in open(log):
    line = re.sub(r'^\d+\s+', '', line)
    m = re.match(r'openat\(.*"([^"]+)".*\) = (\d+)', line)
    if m:
        if m.group(1) == path: fds.add(m.group(2))
        continue
    m = re.match(r'close\((\d+)\)', line)
    if m:
        fds.discard(m.group(1)); continue
    m = re.match(r'(pread64|read|readv|preadv)\((\d+),.*\) = (\d+)$', line)
    if m and m.group(2) in fds:
        n = int(m.group(3))
        calls += 1; total += n
print(f"read calls {calls}, bytes {total}")
E
cat "$OUT.stdout"
rm -f "$OUT" "$OUT.stdout"

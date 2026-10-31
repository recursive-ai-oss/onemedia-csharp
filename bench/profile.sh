#!/bin/bash
# Samples one bench cell in a loop with dotnet-trace and prints the hottest methods by self and inclusive time.
# usage: profile.sh BENCH_DLL ROW IMPL_INDEX RELATIVE_PATH TIMES [OUTDIR]
set -e
DLL=$1; ROW=$2; IMPL=$3; REL=$4; N=$5; OUT=${6:-$HOME/onemedia/u5-bench/trace}
mkdir -p "$OUT"; cd "$OUT"; rm -f trace.* 
CORPUS=${ONEMEDIA_HOME:-$HOME/onemedia}/corpus
TOOLS=$HOME/onemedia/u5-bench/tools
"$TOOLS/dotnet-trace" collect --providers Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:4 --format Speedscope -o trace -- dotnet "$DLL" once "$ROW" "$IMPL" "$REL" "$CORPUS" "$N" > trace.log 2>&1
python3 - "${TOP:-30}" <<'P'
import json, sys, glob, collections
top = int(sys.argv[1])
d = json.load(open(glob.glob('trace*.speedscope.json')[0]))
frames = [x['name'] for x in d['shared']['frames']]
selfc = collections.Counter(); incl = collections.Counter(); total = 0.0
for p in d['profiles']:
    stack = []; last = p['startValue']
    for e in p['events']:
        dt = e['at'] - last; last = e['at']
        if stack:
            selfc[stack[-1]] += dt
            for fr in set(stack): incl[fr] += dt
            total += dt
        if e['type'] == 'O': stack.append(e['frame'])
        elif stack: stack.pop()
print(f'sampled {total:.0f} ms')
print('-- self')
for f, w in selfc.most_common(top): print(f'{100*w/total:5.1f}%  {frames[f][:140]}')
print('-- inclusive')
for f, w in incl.most_common(top): print(f'{100*w/total:5.1f}%  {frames[f][:140]}')
P

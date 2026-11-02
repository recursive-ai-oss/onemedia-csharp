#!/usr/bin/env python3
"""Claim check for the strip review: does the report tell the truth about what changed?
usage: check-report.py <originals dir> <outputs dir>   (outputs hold <name> and <name>.report as written by run-adversarial.sh)
For every output of the same length as its input: each changed byte must lie inside a reported range (or within 12 bytes before one: key and id bytes
that are renamed sit just in front of the value range), and each reported range must contain a changed byte. Files that change length (tag-writer
formats rewrite their tag area) are listed, not compared."""
import os
import re
import sys

bad = 0
for n in sorted(os.listdir(sys.argv[2])):
    if n.endswith('.report'):
        continue
    a = open(os.path.join(sys.argv[1], n), 'rb').read()
    b = open(os.path.join(sys.argv[2], n), 'rb').read()
    if len(a) != len(b):
        print('%-34s length %d -> %d (rewritten tag area, not compared)' % (n, len(a), len(b)))
        continue
    rngs = []
    for l in open(os.path.join(sys.argv[2], n + '.report')).read().splitlines():
        m = re.search(r' at (\d+)\+(\d+)\s*$', l)
        if m and not l.startswith('NOT'):
            rngs.append((int(m.group(1)), int(m.group(1)) + int(m.group(2)), l[:70]))
    changed = [i for i in range(len(a)) if a[i] != b[i]]
    unreported = [i for i in changed if not any(s - 12 <= i < e for s, e, _ in rngs)]
    unchanged = [l for s, e, l in rngs if a[s:e] == b[s:e]]
    if unreported or unchanged:
        bad += 1
        print('%-34s changed=%d unreported=%d %s reported-but-unchanged=%d %s' % (n, len(changed), len(unreported), unreported[:3], len(unchanged), unchanged[:2]))
print('files with a mismatch:', bad)

#!/usr/bin/env python3
"""Summarises metadata-compare.tsv without printing values: per fact, verdict counts by device class, with the shape of the
values (digits replaced by 'd') for the rows that differ. Output is safe to share: no dates, places or names.

  device-facts-summary.py METADATA_COMPARE_TSV DEVICES_MANIFEST_TSV [FIELD]
"""
import csv, re, sys, collections

rows = list(csv.reader(open(sys.argv[1], encoding="utf-8"), delimiter="\t"))[1:]
man = {r[0]: r for r in csv.reader(open(sys.argv[2], encoding="utf-8"), delimiter="\t")}
field = sys.argv[3] if len(sys.argv) > 3 else None

def shape(v):
    v = re.sub(r"\d", "d", v)
    v = re.sub(r"d+(\.d+)?", lambda m: "d" if "." not in m.group(0) else "d.d", v)
    return v[:44]

cnt = collections.Counter(); ex = collections.defaultdict(collections.Counter)
for r in rows:
    f, kind, fld, ours, et, mi, verdict = r[:7]
    if kind != "fact" or (field and fld != field): continue
    fid = f.split(".")[0]
    cls = man.get(fid, ["", "", "", "", "", "?"])[5]
    cnt[(fld, verdict, cls)] += 1
    if verdict in ("differs", "ours-missing"):
        ex[(fld, verdict)][(cls, shape(ours), shape(et), shape(mi), r[7] if len(r) > 7 else "", r[8] if len(r) > 8 else "")] += 1
for (fld, v, cls), c in sorted(cnt.items()):
    if v != "match": print(f"{c:4d} {fld:12s} {v:14s} {cls}")
print()
for (fld, v), cs in sorted(ex.items()):
    print("==", fld, v)
    for k, c in cs.most_common(14): print(f"  {c:3d}", k)

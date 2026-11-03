#!/usr/bin/env python3
"""Cuts device recordings at 60 % of their length and reconstructs them with and without a reference.

  device-repair-check.py WORK CHECK_DLL MANIFEST LINKS_DIR [--count 10]

Targets are picked from the device manifest, one per device model first (MP4/MOV/3GP only, up to --count), then more of the models
that have several files. For each target T:
  none     reconstruct the cut copy with no reference
  sibling  reconstruct with another recording of the same device model as the reference (when the manifest has one)
  self     reconstruct with the healthy T itself as the reference
and compare the result packet by packet with T (OneMedia.ReconstructCheck compare). One summary line per run; no file names or values are printed.
"""
import collections, csv, os, subprocess, sys

work, dll, manifest, links = sys.argv[1:5]
count = int(sys.argv[sys.argv.index("--count") + 1]) if "--count" in sys.argv else 10
DOTNET = os.environ.get("DOTNET", "dotnet")
os.makedirs(work, exist_ok=True)

rows = [r for r in csv.reader(open(manifest, encoding="utf-8"), delimiter="\t")][1:]
by = collections.defaultdict(list)
for r in rows:
    ext = os.path.splitext(r[1])[1].lower()
    if ext in (".mp4", ".mov", ".3gp", ".m4v") and r[6] != "-":
        p = [f for f in os.listdir(links) if f.startswith(r[0] + ".")]
        if p: by[r[6]].append((r[0], os.path.join(links, p[0]), int(r[4]), r[5]))
targets = []
for dev, files in sorted(by.items(), key=lambda kv: -len(kv[1])):
    files.sort(key=lambda x: x[2])
    targets.append((dev, files[len(files) // 2], files))
# one per model first, preferring the ones with siblings, then the rest
targets = targets[:count]

def cut(path, out):
    n = os.path.getsize(path)
    with open(path, "rb") as f, open(out, "wb") as w:
        left = int(n * 0.6)
        while left > 0:
            b = f.read(min(1 << 20, left)); w.write(b); left -= len(b)

def run(args):
    return subprocess.run([DOTNET, dll, *args], capture_output=True, text=True, timeout=1200)

out = open(os.path.join(work, "device-repair.tsv"), "w")
out.write("id\tclass\tmode\tresult\tsummary\n")
for dev, (tid, tpath, size, cls), files in targets:
    cutp = os.path.join(work, tid + os.path.splitext(tpath)[1] + ".cut")
    cut(tpath, cutp)
    sib = next((f for f in files if f[0] != tid), None)
    for mode, ref in (("none", None), ("sibling", sib[1] if sib else None), ("self", tpath)):
        if mode == "sibling" and not sib:
            out.write(f"{tid}\t{cls}\t{mode}\tn/a\tno second recording of this device model\n"); continue
        res = os.path.join(work, tid + "." + mode + os.path.splitext(tpath)[1])
        args = ["reconstruct", cutp, res] + (["--ref", ref] if ref else [])
        r = run(args)
        if r.returncode != 0 or not os.path.exists(res):
            last = (r.stdout.strip().splitlines() or r.stderr.strip().splitlines() or ["?"])[-1]
            out.write(f"{tid}\t{cls}\t{mode}\trefused\t{last[:160]}\n"); continue
        c = run(["compare", tpath, res])
        cmp_ = [l for l in c.stdout.splitlines() if l.startswith("cmp")]
        rep = [l for l in r.stdout.splitlines() if l.startswith("report")]
        out.write(f"{tid}\t{cls}\t{mode}\tok\t{' | '.join(rep)[:200]} || {' | '.join(x[4:] for x in cmp_)[:400]}\n")
        os.remove(res)
    os.remove(cutp)
out.close()
print(open(os.path.join(work, "device-repair.tsv")).read()[:6000])

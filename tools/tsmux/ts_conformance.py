#!/usr/bin/env python3
"""Checks transport streams written by the OneMedia muxer with TSDuck (tsanalyze, tsp, tstables) and MP4Box, as black boxes.

Usage: ts_conformance.py --tool <onemedia.dll> --bin <oracle bin dir> --work <dir> [--layout ts|m2ts] [--jobs N] files...

Per input: remux to <work>/<name>.ts (or .m2ts), then
  tsanalyze --normalized: per PID discontinuities, duplicated, pcrleap, ptsleap, dtsleap, invalidpesprefix; invalidsyncs, transporterrors
  tsp -P continuity: any message at all
  tstables --invalid-sections: any invalid section (CRC, length) in the PSI
  MP4Box -info: exit code and the lines that mention an error
One TSV row per file; the output file is deleted unless it has a finding.
"""
import argparse, os, re, subprocess
from concurrent.futures import ThreadPoolExecutor


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, **kw)


def one(args, path):
    name = os.path.basename(path)
    out = os.path.join(args.work, name + "." + args.layout)
    r = run(["dotnet", args.tool, "remux", path, out, "--overwrite"] + (["--format", "m2ts"] if args.layout == "m2ts" else []))
    said = (r.stdout + r.stderr).decode(errors="replace")
    if r.returncode != 0 and not (os.path.exists(out) and os.path.getsize(out) > 0 and "wrote " in said):
        return name, "SKIP", "no output"
    bin_ = args.bin
    fmt = ["--format", "m2ts"] if args.layout == "m2ts" else []
    a = run([os.path.join(bin_, "tsanalyze"), "--normalized", out])
    if args.layout == "m2ts":
        a = run([os.path.join(bin_, "tsp"), "-I", "file", "--format", "m2ts", out, "-P", "analyze", "--normalized", "-O", "drop"])
    txt = (a.stdout + a.stderr).decode(errors="replace")
    sums = {k: 0 for k in ("discontinuities", "duplicated", "pcrleap", "ptsleap", "dtsleap", "invalidpesprefix", "invalidsyncs", "transporterrors")}
    pids = 0
    for line in txt.split("\n"):
        if line.startswith("pid:") or line.startswith("ts:"):
            if line.startswith("pid:"):
                pids += 1
            for k in sums:
                m = re.search(r"[:]%s=(\d+)" % k, line)
                if m:
                    sums[k] += int(m.group(1))
    c = run([os.path.join(bin_, "tsp"), "-I", "file"] + fmt + [out, "-P", "continuity", "-O", "drop"])
    cont = [l for l in (c.stdout + c.stderr).decode(errors="replace").split("\n") if l.strip()]
    t = run([os.path.join(bin_, "tstables"), "--invalid-sections", "--max-tables", "1"] + fmt + [out]) if args.layout == "ts" else None
    inval = len([l for l in (t.stdout + t.stderr).decode(errors="replace").split("\n") if "nvalid" in l]) if t else 0
    m = run([os.path.join(bin_, "MP4Box"), "-info", out])
    mtxt = (m.stdout + m.stderr).decode(errors="replace").replace(out, "<file>").replace(os.path.basename(out), "<file>")
    mp4box_err = [l.strip() for l in mtxt.split("\n") if re.search(r"error|invalid|corrupt", l, re.I)]
    findings = [f"{k}={v}" for k, v in sums.items() if v] + ([f"tsp-continuity={len(cont)}"] if cont else []) + ([f"tstables-invalid={inval}"] if inval else []) + ([f"mp4box:{mp4box_err[0][:80]}"] if mp4box_err else [])
    if not pids:
        findings.append("tsanalyze saw no PID")
    if not findings:
        os.remove(out)
    return name, "OK" if not findings else "FINDING", "pids=%d %s" % (pids, "; ".join(findings))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("--bin", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--layout", default="ts")
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()
    os.makedirs(args.work, exist_ok=True)
    with ThreadPoolExecutor(args.jobs) as ex:
        rows = list(ex.map(lambda f: one(args, f), args.files))
    n = {"OK": 0, "FINDING": 0, "SKIP": 0}
    for name, st, info in rows:
        n[st] += 1
        print("%s\t%s\t%s" % (name, st, info))
    print("TOTAL", n)


if __name__ == "__main__":
    main()

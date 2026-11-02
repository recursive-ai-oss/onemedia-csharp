#!/usr/bin/env python3
"""Reconstructs every cut-off copy of every healthy fixture, with and without a reference, and checks the results.

  reconstruct-sweep.py WORKDIR CHECK_DLL [--healthy DIR]...  [--jobs N] [--only SUBSTRING]

For each healthy file H in the --healthy directories (made by reconstruct-fixtures.py gen, or the Blender and GoPro samples):
  1. cut-off copies of H are made (reconstruct-fixtures.py cut);
  2. each is reconstructed with H as the reference, then compared with H packet by packet (OneMedia.ReconstructCheck compare);
  3. H is copied with its parameter sets in the stream (ReconstructCheck inband), cut the same way and reconstructed
     without a reference, then compared with that copy;
  4. every output is decoded to nothing by ffmpeg (errors counted) and read by MP4Box -info (exit status).
Results go to WORKDIR/results.tsv and a per-class summary is printed. Environment: FFMPEG, MP4BOX (defaults ffmpeg, MP4Box), DOTNET.
"""
import concurrent.futures
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
MP4BOX = os.environ.get("MP4BOX", "MP4Box")
DOTNET = os.environ.get("DOTNET", "dotnet")


def run(cmd, timeout=900):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except FileNotFoundError:
        return subprocess.CompletedProcess(cmd, 127, "", "not installed")


def check(dll, *args):
    return run([DOTNET, dll, *args])


def one(dll, work, original, cutfile, mode, reference):
    out = os.path.join(work, "out", os.path.basename(cutfile))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    args = ["reconstruct", cutfile, out]
    if reference:
        args += ["--ref", reference]
    r = check(dll, *args)
    row = {"file": os.path.basename(cutfile), "mode": mode, "report": "", "cmp": [], "ffmpeg_errors": "", "mp4box": ""}
    for line in r.stdout.splitlines():
        if line.startswith("report"):
            row["report"] = line
        if line.startswith("failed"):
            row["report"] = line
    if r.returncode != 0 or not os.path.exists(out):
        row["cmp"] = ["FAILED " + (r.stdout.strip().splitlines()[-1] if r.stdout.strip() else r.stderr.strip()[-200:])]
        return row
    c = check(dll, "compare", original, out)
    row["cmp"] = [line for line in c.stdout.splitlines() if line.startswith("cmp")]
    f = run([FFMPEG, "-v", "error", "-xerror", "-i", out, "-f", "null", "-"])
    row["ffmpeg_errors"] = str(len([x for x in f.stderr.splitlines() if x.strip()])) if f.returncode == 0 else "rc=%d" % f.returncode
    m = run([MP4BOX, "-info", out])
    row["mp4box"] = "n/a" if m.returncode == 127 else "ok" if m.returncode == 0 and "Error" not in (m.stdout + m.stderr) else "rc=%d" % m.returncode
    os.remove(out)
    return row


def parse_cmp(line):
    m = re.search(r"cmp track=(\d+) (\w+) (\S+) recon=(\d+) original=(\d+) compared=(\d+) bytes=(\d+) keys=(\d+) times=(\d+)", line)
    return m.groups() if m else None


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        print(__doc__)
        return 2
    work, dll = args[0], args[1]
    healthy_dirs, jobs, only = [], 4, ""
    i = 2
    while i < len(args):
        if args[i] == "--healthy":
            healthy_dirs.append(args[i + 1])
            i += 2
        elif args[i] == "--jobs":
            jobs = int(args[i + 1])
            i += 2
        elif args[i] == "--only":
            only = args[i + 1]
            i += 2
        else:
            i += 1
    os.makedirs(work, exist_ok=True)
    tasks = []
    for d in healthy_dirs:
        for name in sorted(os.listdir(d)):
            path = os.path.join(d, name)
            if not os.path.isfile(path) or ".cut" in name or ".inband" in name or only not in name:
                continue
            if os.path.splitext(name)[1] not in (".mp4", ".mov", ".3gp", ".m4a", ".m4v"):
                continue
            tag = os.path.splitext(name)[0] + os.path.splitext(name)[1].replace(".", "-")
            cutdir = os.path.join(work, "cuts", tag)
            run([sys.executable, os.path.join(HERE, "reconstruct-fixtures.py"), "cut", path, cutdir])
            for cf in sorted(os.listdir(cutdir)):
                tasks.append((path, os.path.join(cutdir, cf), "reference", path))
            inband = os.path.join(work, "inband", tag + ".inband" + os.path.splitext(name)[1])
            os.makedirs(os.path.dirname(inband), exist_ok=True)
            if check(dll, "inband", path, inband).returncode == 0:
                icut = os.path.join(work, "cuts", tag + ".inband")
                run([sys.executable, os.path.join(HERE, "reconstruct-fixtures.py"), "cut", inband, icut])
                for cf in sorted(os.listdir(icut)):
                    tasks.append((inband, os.path.join(icut, cf), "no-reference", None))
    rows = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as ex:
        futures = [ex.submit(one, dll, work, o, c, m, ref) for (o, c, m, ref) in tasks]
        for fut in concurrent.futures.as_completed(futures):
            rows.append(fut.result())
    rows.sort(key=lambda r: (r["mode"], r["file"]))
    with open(os.path.join(work, "results.tsv"), "w") as out:
        out.write("file\tmode\treport\tcmp\tffmpeg_errors\tmp4box\n")
        for r in rows:
            out.write("\t".join([r["file"], r["mode"], r["report"], " | ".join(r["cmp"]), r["ffmpeg_errors"], r["mp4box"]]) + "\n")
    # Summary per class (file name up to ".cut") and mode.
    classes = {}
    for r in rows:
        cls = (r["file"].split(".cut")[0], r["mode"])
        c = classes.setdefault(cls, {"runs": 0, "fail": 0, "fraction": 0.0, "pk": {}, "ffbad": 0, "mp4bad": 0})
        c["runs"] += 1
        m = re.search(r"fraction=([0-9.]+)", r["report"])
        if not m:
            c["fail"] += 1
            continue
        c["fraction"] += float(m.group(1))
        for line in r["cmp"]:
            g = parse_cmp(line)
            if g:
                key = f"{g[1]}:{g[2]}"
                t = c["pk"].setdefault(key, [0, 0, 0, 0])
                t[0] += int(g[3])
                t[1] += int(g[4])
                t[2] += int(g[5])
                t[3] += min(int(g[6]), int(g[7]), int(g[8]))
        c["ffbad"] += 0 if r["ffmpeg_errors"] in ("0", "") else 1
        c["mp4bad"] += 0 if r["mp4box"] in ("ok", "n/a") else 1
    print("class\tmode\truns\tfailed\tmdat-recovered%\tpackets recovered/original, matched/compared\tffmpeg-bad\tmp4box-bad")
    for (cls, mode), c in sorted(classes.items()):
        ok = c["runs"] - c["fail"]
        pk = "; ".join(f"{k} {v[0]}/{v[1]} matched {v[3]}/{v[2]}" for k, v in sorted(c["pk"].items()))
        print(f"{cls}\t{mode}\t{c['runs']}\t{c['fail']}\t{100.0 * c['fraction'] / max(ok, 1):.2f}\t{pk}\t{c['ffbad']}\t{c['mp4bad']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

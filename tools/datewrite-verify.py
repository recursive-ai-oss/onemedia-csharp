#!/usr/bin/env python3
"""Verifies capture-time writes against black-box oracles. For each input file: set the capture time with OneMedia.DateWriteCheck, then read the
output with ExifTool, MediaInfo and ffprobe and list every date field they show, comparing each with the instant written. Also checks that
the per-track payload hash is unchanged, that the resolver reads the written value back, and that the validators complain no more than before.

  datewrite-verify.py --tool "dotnet bin/OneMedia.DateWriteCheck.dll" --work DIR [--when ISO] [--header-times utc|local] [--jobs N] files...

One TSV line per file on stdout (status, counts); with --detail a JSON line per file lists every oracle field and its verdict; a summary goes
to stderr. A field matches when it shows the written instant, or the written wall clock (UTC or the stated offset), to the second.
Oracles are looked up on PATH (ffprobe, ffmpeg, exiftool, mediainfo, MP4Box, mkvalidator).
"""
import argparse, concurrent.futures as cf, json, os, re, subprocess, sys
from datetime import datetime, timedelta, timezone

DATE = re.compile(r"(\d{4})[:\-](\d\d)[:\-](\d\d)(?:[ T](\d\d):(\d\d):(\d\d)(?:\.\d+)?)?\s*(Z|UTC|[+-]\d\d:?\d\d)?")
SKIP_KEYS = re.compile(r"(Brand|Duration|TimeScale|TimeCode|SelectionTime|PreviewTime|PosterTime|CurrentTime|FileModif|FileAccess|FileInode|FilePerm|ModifyDate_\w*File)", re.I)
SKIP_GROUPS = ("System", "ExifTool", "File")


def run(cmd, timeout=300):
    try:
        p = subprocess.run(cmd, capture_output=True, timeout=timeout)
        return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return 124, "", "timeout"


def parse(text):
    """Returns (wall datetime, tz offset timedelta or None, date_only) or None."""
    m = DATE.search(text)
    if not m:
        return None
    y, mo, d, h, mi, s, tz = m.groups()
    try:
        wall = datetime(int(y), int(mo), int(d), int(h or 0), int(mi or 0), int(s or 0))
    except ValueError:
        return None
    off = None
    if tz:
        if tz in ("Z", "UTC"):
            off = timedelta(0)
        else:
            sign = -1 if tz[0] == "-" else 1
            t = tz[1:].replace(":", "")
            off = sign * timedelta(hours=int(t[:2]), minutes=int(t[2:]))
    return wall, off, h is None


def verdict(text, when):
    p = parse(text)
    if p is None:
        return None
    wall, off, date_only = p
    utc_wall = when.astimezone(timezone.utc).replace(tzinfo=None)
    local_wall = when.replace(tzinfo=None)
    if date_only:
        return "match" if wall.date() in (utc_wall.date(), local_wall.date()) else "DIFF"
    if off is not None:
        return "match" if abs((wall - off) - utc_wall) < timedelta(seconds=1) else "DIFF"
    # A wall clock without a zone shows UTC (header times) or the stated local time (zone-less fields).
    return "match" if abs(wall - utc_wall) < timedelta(seconds=1) or abs(wall - local_wall) < timedelta(seconds=1) else "DIFF"


def label_only(text, when):
    """True when a field shows the written wall clock but labels it UTC (MediaInfo does that for zone-less fields)."""
    p = parse(text)
    return bool(p and p[1] == timedelta(0) and abs(p[0] - when.replace(tzinfo=None)) < timedelta(seconds=1))


def exif_fields(path):
    rc, out, err = run(["exiftool", "-j", "-G1", "-a", "-s", "-api", "largefilesupport=1", path])
    try:
        d = json.loads(out)[0]
    except Exception:
        return {}
    return {k: str(v) for k, v in d.items() if not k.split(":")[0] in SKIP_GROUPS and k != "SourceFile"}


def mediainfo_fields(path):
    rc, out, err = run(["mediainfo", "--Output=JSON", path])
    try:
        d = json.loads(out)
    except Exception:
        return {}
    res = {}
    for i, t in enumerate(d.get("media", {}).get("track", [])):
        kind = t.get("@type", "")
        for k, v in t.items():
            if k.startswith("@") or k.startswith("File_Modified") or k in ("extra",) or not isinstance(v, str):
                continue
            res[f"{kind}{i}:{k}"] = v
    return res


def ffprobe_fields(path):
    rc, out, err = run(["ffprobe", "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", path])
    try:
        d = json.loads(out)
    except Exception:
        return {}
    res = {f"format:{k}": str(v) for k, v in d.get("format", {}).get("tags", {}).items()}
    for s in d.get("streams", []):
        for k, v in s.get("tags", {}).items():
            res[f"stream{s.get('index')}:{k}"] = str(v)
    return res


def date_fields(fields, when):
    rows = []
    for k, v in sorted(fields.items()):
        if SKIP_KEYS.search(k):
            continue
        r = verdict(v, when)
        if r == "DIFF" and label_only(v, when):
            r = "label"
        if r:
            rows.append({"field": k, "value": v[:80], "verdict": r})
    return rows


def validity(path, ext):
    msgs = []
    rc, _, err = run(["ffprobe", "-v", "error", path])
    msgs.append(len([l for l in err.splitlines() if l.strip()]))
    rc, _, err = run(["ffmpeg", "-v", "error", "-i", path, "-f", "null", "-"], 600)
    msgs.append(len([l for l in err.splitlines() if l.strip()]))
    if ext in (".mp4", ".mov", ".m4a", ".m4v", ".3gp"):
        rc, out, err = run(["MP4Box", "-info", path])
        msgs.append(rc)
    elif ext in (".mkv", ".webm", ".mka"):
        rc, out, err = run(["mkvalidator", path])
        msgs.append(rc)
    else:
        msgs.append(0)
    return msgs


def one(args, path):
    base = os.path.basename(path)
    ext = os.path.splitext(base)[1].lower()
    outdir = os.path.join(args.work, "out")
    os.makedirs(outdir, exist_ok=True)
    out = os.path.join(outdir, "%08x_%s" % (abs(hash(path)) & 0xFFFFFFFF, base))
    cmd = args.tool.split() + [path, out, args.when] + ([args.header_times] if args.header_times else [])
    rc, o, e = run(cmd, 900)
    line = o.strip().splitlines()[-1] if o.strip() else "ERROR\t%s\t%s" % (path, e.strip()[:200])
    if not line.startswith("DATE"):
        return line, None
    row = json.loads(line.split("\t", 2)[2])
    row["file"] = path
    when = datetime.fromisoformat(args.when.replace("Z", "+00:00"))
    try:
        for name, fn in (("exiftool", exif_fields), ("mediainfo", mediainfo_fields), ("ffprobe", ffprobe_fields)):
            row[name] = date_fields(fn(out), when)
        v_in, v_out = validity(path, ext), validity(out, ext)
        row["valid"] = "ok" if all(o_ <= i_ for o_, i_ in zip(v_out, v_in)) else "WORSE in=%s out=%s" % (v_in, v_out)
    finally:
        if not args.keep:
            try:
                os.remove(out)
            except OSError:
                pass
    return line, row


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--when", default="2024-05-01T12:30:00+02:00")
    ap.add_argument("--header-times", default=None)
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--detail", default=None)
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()
    files = []
    for f in args.files:
        if f.startswith("@"):
            files += [l.strip() for l in open(f[1:]) if l.strip()]
        else:
            files.append(f)
    t = dict(label=0, files=0, errors=0, resolver_fail=0, payload_fail=0, valid_worse=0, fields=0, matched=0, diff=0, files_with_diff=0, written=0)
    detail = open(args.detail, "w") if args.detail else None
    with cf.ThreadPoolExecutor(args.jobs) as ex:
        for line, row in ex.map(lambda p: one(args, p), files):
            t["files"] += 1
            if row is None:
                t["errors"] += 1
                print(line.replace("\n", " "), flush=True)
                continue
            allrows = [r for k in ("exiftool", "mediainfo", "ffprobe") for r in row[k]]
            nd = sum(r["verdict"] == "DIFF" for r in allrows)
            t["label"] += sum(r["verdict"] == "label" for r in allrows)
            t["fields"] += len(allrows)
            t["matched"] += sum(r["verdict"] == "match" for r in allrows)
            t["diff"] += nd
            t["files_with_diff"] += nd > 0
            t["written"] += len(row["written"])
            t["resolver_fail"] += not row["resolvedOk"]
            t["payload_fail"] += not row["payloadEqual"]
            t["valid_worse"] += row["valid"] != "ok"
            print("%s\twritten=%d\tfields=%d\tdiff=%d\tresolver=%s\tpayload=%s\tvalid=%s" % (row["file"], len(row["written"]), len(allrows), nd, "ok" if row["resolvedOk"] else "FAIL", "ok" if row["payloadEqual"] else "FAIL", row["valid"]), flush=True)
            if detail:
                detail.write(json.dumps(row) + "\n")
    print(json.dumps(t), file=sys.stderr)


main()

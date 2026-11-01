#!/usr/bin/env python3
"""Verifies tag edits against black-box oracles. For each input file: apply the fixed edit set with OneMedia.TagWriteCheck, then compare
the output with the input through ffprobe (packet data hashes), ExifTool and MediaInfo (every reported field), and the validators.

  tagwrite-verify.py --tool "dotnet bin/OneMedia.TagWriteCheck.dll" --work DIR [--jobs N] files...

One TSV line per file on stdout; a summary on stderr. Oracles are looked up on PATH (ffprobe, ffmpeg, exiftool, mediainfo, MP4Box, mkvalidator).
"""
import argparse, concurrent.futures as cf, json, os, re, shutil, subprocess, sys


def run(cmd, timeout=300):
    try:
        p = subprocess.run(cmd, capture_output=True, timeout=timeout)
        return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return 124, "", "timeout"


def attached_pics(path):
    rc, out, err = run(["ffprobe", "-v", "error", "-show_entries", "stream=index:stream_disposition=attached_pic", "-of", "csv", path])
    pics = set()
    for l in out.splitlines():
        p = l.split(",")
        if len(p) >= 3 and p[2] == "1":
            pics.add(p[1])
    return pics


def ffprobe_hashes(path):
    pics = attached_pics(path)
    rc, out, err = run(["ffprobe", "-v", "error", "-show_entries", "packet=stream_index,pts,dts,duration,size,flags,data_hash",
                        "-show_data_hash", "sha256", "-of", "csv", path], 600)
    # Cover pictures show up as one-packet video streams; the edit changes them on purpose, so they are left out of the comparison.
    out = "\n".join(l for l in out.splitlines() if l.split(",")[1:2] and l.split(",")[1] not in pics)
    return out, len([l for l in err.splitlines() if l.strip()])


def exif(path):
    rc, out, err = run(["exiftool", "-j", "-G1", "-a", "-s", "-api", "largefilesupport=1", "-b", path])
    try:
        d = json.loads(out)[0]
    except Exception:
        return {}
    return {re.sub(r"^(ID3v2)_\d", r"", k): str(v) for k, v in d.items() if not k.startswith(("System:", "ExifTool:", "SourceFile"))}


def mediainfo(path):
    rc, out, err = run(["mediainfo", "--Output=JSON", path])
    try:
        d = json.loads(out)
    except Exception:
        return {}
    res = {}
    for t in d.get("media", {}).get("track", []):
        kind = t.get("@type", "")
        for k, v in t.items():
            if k.startswith("@") or k in ("StreamSize", "FileSize", "Extra", "File_Modified_Date", "File_Modified_Date_Local", "CompleteName", "FileNameExtension", "FileName"):
                continue
            res[f"{kind}:{k}"] = json.dumps(v, sort_keys=True) if not isinstance(v, str) else v
    return res


def diff(a, b):
    changed = [k for k in sorted(set(a) | set(b)) if a.get(k) != b.get(k)]
    return changed


def validity(path, ext):
    """Returns a (count, text) of validator complaints."""
    msgs = []
    rc, _, err = run(["ffprobe", "-v", "error", path])
    msgs.append(len([l for l in err.splitlines() if l.strip()]))
    if ext in (".mp3", ".flac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".m4a", ".mp4", ".mov", ".mkv", ".webm", ".mka", ".wma", ".wmv", ".asf"):
        rc, _, err = run(["ffmpeg", "-v", "error", "-i", path, "-f", "null", "-"], 600)
        msgs.append(len([l for l in err.splitlines() if l.strip()]))
    else:
        msgs.append(0)
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
    rc, o, e = run(args.tool.split() + ["edit", path, out], 900)
    line = o.strip().splitlines()[-1] if o.strip() else "ERROR\t%s\t%s" % (path, e.strip()[:200])
    if not line.startswith("EDIT"):
        return line, None
    row = {"file": path, "line": line}
    try:
        h_in, e_in = ffprobe_hashes(path)
        h_out, e_out = ffprobe_hashes(out)
        row["ffprobe"] = "equal" if h_in == h_out else "DIFF"
        x_in, x_out = exif(path), exif(out)
        row["exif_fields"] = len(x_in)
        d = diff(x_in, x_out)
        row["exif_changed"] = d
        m_in, m_out = mediainfo(path), mediainfo(out)
        row["mi_fields"] = len(m_in)
        row["mi_changed"] = diff(m_in, m_out)
        v_in, v_out = validity(path, ext), validity(out, ext)
        row["valid"] = "ok" if all(o_ <= i_ for o_, i_ in zip(v_out, v_in)) else "WORSE in=%s out=%s" % (v_in, v_out)
        # The new values must show in both oracles.
        row["exif_title"] = any("Edited Title" in v for v in x_out.values())
        row["exif_artist"] = any("Edited Artist" in v for v in x_out.values())
        row["exif_comment"] = any("An edited comment" in v for v in x_out.values())
        row["mi_title"] = any("Edited Title" in v for v in m_out.values())
        row["exif_picture"] = any(re.search("Picture|Cover|Image|Attach", k) and k.split(":")[-1] not in ("ImageSize", "ImageWidth", "ImageHeight") for k in x_out)
        row["mi_picture"] = any("Cover" in k or k.startswith("Image:") for k in m_out)
        row["mi_artist"] = any("Edited Artist" in v for v in m_out.values())
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
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--detail", default=None, help="write per-file JSON lines here")
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()
    files = []
    for f in args.files:
        if f.startswith("@"):
            files += [l.strip() for l in open(f[1:]) if l.strip()]
        else:
            files.append(f)
    totals = dict(files=0, errors=0, readback_fail=0, payload_fail=0, copies_fail=0, ffprobe_diff=0, exif_fields=0, exif_changed=0, mi_fields=0, mi_changed=0,
                  valid_worse=0, exif_title_missing=0, mi_title_missing=0, mi_artist_missing=0, exif_artist_missing=0, exif_picture_missing=0, mi_picture_missing=0)
    detail = open(args.detail, "w") if args.detail else None
    with cf.ThreadPoolExecutor(args.jobs) as ex:
        for line, row in ex.map(lambda p: one(args, p), files):
            print(line.replace("\n", " ") + ("\t" + "ffprobe=%s\tvalid=%s" % (row["ffprobe"], row["valid"]) if row else ""), flush=True)
            totals["files"] += 1
            if row is None:
                totals["errors"] += 1
                continue
            if "FAIL" in row["line"].split("\t")[4]:
                totals["readback_fail"] += 1
            if "payload=ok" not in row["line"]:
                totals["payload_fail"] += 1
            if "copies=ok" not in row["line"]:
                totals["copies_fail"] += 1
            totals["ffprobe_diff"] += row["ffprobe"] != "equal"
            totals["exif_fields"] += row["exif_fields"]
            totals["exif_changed"] += len(row["exif_changed"])
            totals["mi_fields"] += row["mi_fields"]
            totals["mi_changed"] += len(row["mi_changed"])
            totals["valid_worse"] += row["valid"] != "ok"
            totals["exif_title_missing"] += not row["exif_title"]
            totals["exif_artist_missing"] += not row["exif_artist"]
            totals["mi_title_missing"] += not row["mi_title"]
            totals["mi_artist_missing"] += not row["mi_artist"]
            totals["exif_picture_missing"] += not row["exif_picture"]
            totals["mi_picture_missing"] += not row["mi_picture"]
            if detail:
                detail.write(json.dumps({k: v for k, v in row.items() if k != "line"} | {"line": row["line"][:200]}) + "\n")
    print(json.dumps(totals), file=sys.stderr)


main()

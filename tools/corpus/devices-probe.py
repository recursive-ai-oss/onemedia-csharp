#!/usr/bin/env python3
"""Downloads candidate files (devices-discover.py output), keeps those that look like real device recordings.

  devices-probe.py CAND.tsv DEST_DIR LOG.jsonl [--budget-gb 14] [--siblings] [--keep-name REGEX]

A file is kept when ExifTool reports a camera or phone make/model, an Android or Apple capture key, an AVCHD
MDPM block, or a screen-recorder marker. Everything else is deleted straight after the check. Only licences that
allow reuse (CC0, public-domain mark, CC BY, CC BY-SA) are fetched, at most three files per item and 80 MiB each.
Files stay under DEST_DIR (never committed). One JSON line per downloaded file is appended to LOG.jsonl.
"""
import csv, json, os, subprocess, sys, urllib.parse, urllib.request, concurrent.futures as cf, hashlib, threading

ET = os.path.expanduser(os.environ.get("EXIFTOOL", "~/onemedia/oracles/bin/exiftool"))
UA = "onemedia-corpus-research/1.0"
PREF = {"mov": 0, "mp4": 0, "m4v": 1, "3gp": 1, "mts": 0, "m2ts": 0, "mkv": 2, "avi": 2, "mpg": 3, "mpeg": 3, "ts": 3, "vob": 3, "wmv": 3, "webm": 4, "flv": 4}
OKLIC = ("/publicdomain/zero/", "/publicdomain/mark/", "/licenses/by/", "/licenses/by-sa/")
DEVICE_KEYS = ("Make", "Model", "AndroidVersion", "AndroidMake", "AndroidModel", "ComAppleQuicktimeMake", "ComAppleQuicktimeModel",
               "ComAppleQuicktimeSoftware", "ComAppleQuicktimeCreationdate", "ComAppleQuicktimeLocationISO6709", "DeviceManufacturer", "DeviceModelName",
               "DeviceSerialNo", "ExposureProgram", "MDPMVersion", "CameraModelName", "LensModel", "FirmwareVersion", "Firmware")
SCREEN = ("obs", "screen record", "screencapture", "screen capture", "replaykit", "scrcpy", "kazam", "simplescreenrecorder", "quicktime player")

lock = threading.Lock()
# Optional: --keep-name REGEX keeps files by file name too (Android and OBS screen recorders write no make or model).
NAMEKEEP = None
if "--keep-name" in sys.argv:
    import re
    NAMEKEEP = re.compile(sys.argv[sys.argv.index("--keep-name") + 1], re.I)

def fetch(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=120) as r, open(dest, "wb") as w:
        h = hashlib.sha256()
        while True:
            b = r.read(1 << 20)
            if not b:
                break
            w.write(b); h.update(b)
    return h.hexdigest()

def probe(row, dest_dir, probe_dir):
    ident, name = row["identifier"], row["file"]
    url = row.get("url") or f"https://archive.org/download/{urllib.parse.quote(ident)}/{urllib.parse.quote(name)}"
    safe = (ident + "__" + name.replace("/", "_"))[:180]
    tmp = os.path.join(probe_dir, safe)
    try:
        sha = fetch(url, tmp)
    except Exception as e:
        if os.path.exists(tmp): os.remove(tmp)
        return {"id": ident, "file": name, "error": str(e)[:80]}
    try:
        out = subprocess.run([ET, "-j", "-G0:1", "-n", "-api", "largefilesupport=1", "-ee0", tmp], capture_output=True, timeout=120).stdout
        tags = json.loads(out)[0]
    except Exception:
        tags = {}
    flat = {k.split(":")[-1]: v for k, v in tags.items()}
    dev = {k: flat[k] for k in DEVICE_KEYS if k in flat and str(flat[k]).strip()}
    text = " ".join(str(flat.get(k, "")) for k in ("Software", "Encoder", "HandlerDescription", "Comment", "Description")).lower()
    screen = any(s in text for s in SCREEN)
    keep = bool(dev) or screen or bool(NAMEKEEP and NAMEKEEP.search(name))
    res = {"id": ident, "file": name, "sha256": sha, "size": os.path.getsize(tmp), "url": url, "licenseurl": row["licenseurl"],
           "creator": row["creator"], "keep": keep, "device": {k: str(v)[:60] for k, v in dev.items() if k not in ("DeviceSerialNo",)}, "screen": screen,
           "container": flat.get("FileType", ""), "ext_compat": flat.get("MajorBrand", "")}
    if keep:
        os.replace(tmp, os.path.join(dest_dir, safe))
        res["path"] = safe
    else:
        os.remove(tmp)
    return res

def main():
    cand, dest_dir, log = sys.argv[1:4]
    budget = float(sys.argv[sys.argv.index("--budget-gb") + 1]) * 1e9 if "--budget-gb" in sys.argv else 14e9
    probe_dir = os.path.join(os.path.dirname(dest_dir.rstrip("/")), "_probe")
    os.makedirs(dest_dir, exist_ok=True); os.makedirs(probe_dir, exist_ok=True)
    done = set()
    if os.path.exists(log):
        for l in open(log):
            d = json.loads(l); done.add((d["id"], d["file"]))
    rows = [r for r in csv.DictReader(open(cand, encoding="utf-8"), delimiter="\t", quoting=csv.QUOTE_NONE)
            if any(k in r["licenseurl"] for k in OKLIC) and int(r["size"]) <= 80 * 1024 * 1024]
    by = {}
    for r in rows:
        by.setdefault(r["identifier"], []).append(r)
    pick = []
    if "--siblings" in sys.argv:
        # second pass: more files from items that already yielded a device recording (at most 12 per item)
        good = {json.loads(l)["id"] for l in open(log) if json.loads(l).get("keep")}
        for ident, rs in by.items():
            if ident in good:
                pick += sorted(rs, key=lambda r: r["file"])[:12]
        by = {}
    for ident, rs in by.items():
        rs.sort(key=lambda r: (PREF.get(r["file"].rsplit(".", 1)[-1].lower(), 9), r["file"]))
        # spread over the item: first, middle, last of the preferred type
        sel = rs[:1] + rs[len(rs) // 2:len(rs) // 2 + 1] + rs[-1:] if len(rs) > 3 else rs
        pick += sel
    pick = [r for r in {(r["identifier"], r["file"]): r for r in pick}.values() if (r["identifier"], r["file"]) not in done]
    print(len(pick), "files to probe", flush=True)
    kept = [0]
    def work(r):
        if kept[0] >= budget: return
        res = probe(r, dest_dir, probe_dir)
        with lock:
            if res.get("keep"): kept[0] += res["size"]
            open(log, "a").write(json.dumps(res) + "\n")
    with cf.ThreadPoolExecutor(4) as ex:
        list(ex.map(work, pick))
    print("kept bytes", kept[0])

main()

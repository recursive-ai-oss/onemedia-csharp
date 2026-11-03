#!/usr/bin/env python3
"""Groups the differential's per-file JSON by cause: one line per differing file with its first examples and the track codecs.

  device-triage.py DIFF_DIR [FFPROBE]
"""
import json, os, subprocess, sys, collections

d = sys.argv[1]
ffprobe = sys.argv[2] if len(sys.argv) > 2 else "ffprobe"
rows = []
for f in sorted(os.listdir(os.path.join(d, "files"))):
    j = json.load(open(os.path.join(d, "files", f)))
    if not j.get("differences"):
        continue
    path = j.get("file") or j.get("path") or ""
    rows.append((os.path.basename(path), j))
print(len(rows), "files with differences")
for name, j in rows:
    try:
        out = subprocess.run([ffprobe, "-v", "error", "-show_entries", "stream=index,codec_name,codec_type", "-of", "csv=p=0", j.get("file") or j.get("path")],
                             capture_output=True, text=True, timeout=60).stdout.split()
    except Exception:
        out = []
    ex = j.get("examples", [])[:3]
    print(name, "|", ";".join(out), "|", json.dumps(j["differences"], separators=(",", ":")), "|",
          " ".join(f"t{e['track']}p{e['packet']}:{e['field']} {e['theirs']}->{e['ours']}" for e in ex))

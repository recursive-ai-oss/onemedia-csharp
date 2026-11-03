#!/usr/bin/env python3
"""Finds candidate device recordings in Zenodo records that state a reuse-friendly licence (CC0, CC BY, CC BY-SA).

  devices-discover-zenodo.py OUT.tsv [QUERY...]

Same columns as devices-discover.py plus a url column, so devices-probe.py reads both. Nothing is downloaded here.
"""
import json, sys, time, urllib.parse, urllib.request

UA = "onemedia-corpus-research/1.0"
EXT = {".mp4", ".mov", ".m4v", ".3gp", ".mts", ".m2ts", ".mkv", ".avi", ".mpg", ".ts", ".wmv", ".flv", ".webm"}
LIC = {"cc-zero": "https://creativecommons.org/publicdomain/zero/1.0/", "cc-by-4.0": "https://creativecommons.org/licenses/by/4.0/",
       "cc-by-sa-4.0": "https://creativecommons.org/licenses/by-sa/4.0/", "cc-by-3.0": "https://creativecommons.org/licenses/by/3.0/"}
QUERIES = ["iPhone video", "smartphone video recording", "GoPro", "DJI drone video", "dashcam", "dash camera video", "AVCHD", "camcorder video",
           "Samsung Galaxy video", "Pixel phone video", "action camera footage", "drone footage dataset", "driving video dataset", "body camera video",
           "screen recording", "mobile phone video dataset", "raw video files camera", "UAV video",
           "iPhone 15", "iPhone 14", "iPhone 13", "iPhone 12", "iPhone 11", "Pixel 8", "Pixel 7", "Samsung Galaxy S", "Huawei video", "Xiaomi video",
           "GoPro HERO", "DJI Mavic", "DJI Mini", "DJI Phantom", "DJI Air", "Insta360", "Sony camcorder", "Canon video", "Panasonic video", "Nikon video",
           "MTS video files", "AVCHD recordings", "traffic camera video", "in-car camera", "vlog recordings", "smartphone footage", "mobile video recordings",
           "video recordings of experiments", "interview recordings video", "field recordings video", "wildlife camera video", "bodycam", "sports video dataset",
           "egocentric video", "first-person video", "Dolby Vision", "HDR video", "slow motion video", "time-lapse video", "spatial video", "Apple Vision Pro"]

def get(url):
    for a in range(4):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": UA}), timeout=60) as r:
                return json.load(r)
        except Exception:
            time.sleep(3 + 4 * a)
    return None

def main():
    out = sys.argv[1]; qs = sys.argv[2:] or QUERIES
    seen = set(); rows = []
    for q in qs:
        for page in (1, 2, 3, 4):
            d = get("https://zenodo.org/api/records?" + urllib.parse.urlencode({"q": q, "type": "video", "size": 25, "page": page, "access_right": "open"}))
            if not d: break
            for h in d["hits"]["hits"]:
                if h["id"] in seen: continue
                seen.add(h["id"])
                lic = LIC.get(h["metadata"].get("license", {}).get("id", ""), "")
                if not lic: continue
                creators = "; ".join(c.get("name", "") for c in h["metadata"].get("creators", [])[:3])
                for f in h.get("files", []):
                    name = f["key"]; ext = "." + name.rsplit(".", 1)[-1].lower() if "." in name else ""
                    if ext not in EXT or not 65536 <= f["size"] <= 250 * 2**20: continue
                    rows.append([f"zenodo-{h['id']}", name, str(f["size"]), f.get("checksum", ""), lic, creators, h["metadata"]["title"],
                                 f["links"]["self"]])
    with open(out, "w", encoding="utf-8") as w:
        w.write("identifier\tfile\tsize\tsha1\tlicenseurl\tcreator\ttitle\turl\n")
        for r in rows: w.write("\t".join(x.replace("\t", " ").replace("\n", " ") for x in r) + "\n")
    print(len(seen), "records,", len(rows), "files")
main()

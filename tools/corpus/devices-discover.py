#!/usr/bin/env python3
"""Finds candidate device recordings on the Internet Archive that carry a stated Creative Commons licence.

  devices-discover.py OUT.tsv [QUERY...]

Writes one row per candidate file: identifier, file name, size, sha1 (as the Archive lists it), licence URL,
creator, title. Only original (not derivative) files with a video container extension and a size between
64 KiB and 250 MiB are listed. Nothing is downloaded here: devices-fetch.sh does that for the rows kept in
devices-manifest.tsv. Items without a licenseurl are skipped.
"""
import json, sys, time, urllib.parse, urllib.request

UA = "onemedia-corpus-research/1.0"
EXT = {".mp4", ".mov", ".m4v", ".3gp", ".3g2", ".mts", ".m2ts", ".mkv", ".avi", ".mpg", ".mpeg", ".ts", ".vob", ".wmv", ".asf", ".flv", ".webm", ".dv"}
DEFAULT_QUERIES = [
    "iPhone", "iPhone 4K HEVC", "Live Photo", "slow motion iPhone", "Galaxy", "Samsung", "Pixel", "Android phone video",
    "GoPro", "HERO", "DJI", "Mavic", "drone footage", "dashcam", "dash cam", "AVCHD", "XAVC", "Sony camcorder", "Canon camcorder",
    "Panasonic Lumix", "Canon EOS video", "Nikon video", "camcorder", "OBS recording", "screen recording", "screencast", "QuickTime screen recording",
    "raw camera footage", "unedited footage", "MTS", "M2TS", "3GP phone",
    "Shot on iPhone", "HEVC", "Dolby Vision", "Canon EOS 5D", "Lumix GH", "Sony Alpha", "Sony A7", "Nikon D", "Olympus", "Fujifilm", "Insta360",
    "Osmo", "Phantom drone", "Garmin VIRB", "BlackVue", "Viofo", "Nextbase", "Huawei", "Xiaomi", "OnePlus", "Nokia", "Motorola", "Xperia",
    "HDV", "MiniDV", "Handycam", "Flip video", "Kodak Zi8", "Vixia", "Lumix", "action camera", "Android screen recording", "iPhone screen recording",
]

def get(url):
    for attempt in range(4):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": UA}), timeout=60) as r:
                return json.load(r)
        except Exception as e:  # network hiccup or throttle: back off and retry
            time.sleep(2 + attempt * 3)
    return None

def main():
    out = sys.argv[1]
    queries = sys.argv[2:] or DEFAULT_QUERIES
    seen = set()
    rows = []
    for q in queries:
        docs = []
        for page in (1, 2, 3):
            qs = f'({q}) AND mediatype:movies AND licenseurl:*'
            url = "https://archive.org/advancedsearch.php?" + urllib.parse.urlencode(
                {"q": qs, "fl[]": "identifier", "rows": 50, "page": page, "output": "json"})
            d = get(url)
            if not d or "response" not in d:
                break
            docs += d["response"]["docs"]
        for doc in docs:
            ident = doc["identifier"]
            if ident in seen:
                continue
            seen.add(ident)
            m = get(f"https://archive.org/metadata/{ident}")
            if not m or "files" not in m:
                continue
            meta = m.get("metadata", {})
            lic = meta.get("licenseurl", "")
            if not lic:
                continue
            creator = meta.get("creator", "")
            if isinstance(creator, list):
                creator = "; ".join(creator)
            title = meta.get("title", "")
            for f in m["files"]:
                name = f.get("name", "")
                ext = "." + name.rsplit(".", 1)[-1].lower() if "." in name else ""
                if f.get("source") != "original" or ext not in EXT:
                    continue
                size = int(f.get("size", 0) or 0)
                if not 65536 <= size <= 250 * 1024 * 1024:
                    continue
                rows.append([ident, name, str(size), f.get("sha1", ""), lic, str(creator), str(title)])
    with open(out, "w", encoding="utf-8") as w:
        w.write("identifier\tfile\tsize\tsha1\tlicenseurl\tcreator\ttitle\n")
        for r in rows:
            w.write("\t".join(x.replace("\t", " ").replace("\n", " ") for x in r) + "\n")
    print(f"{len(seen)} items, {len(rows)} candidate files")

main()

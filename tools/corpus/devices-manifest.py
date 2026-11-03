#!/usr/bin/env python3
"""Turns the devices-probe.py log into devices-manifest.tsv (one row per distinct kept file, by sha256).

  devices-manifest.py PROBE.jsonl > devices-manifest.tsv

Columns: id, path (under $ONEMEDIA_HOME/corpus/devices), url, sha256, size, class, device (as the file states it),
licence (the licence URL the source states), credit (the creator the source states), use (always fetch-only).
The "class" column is a coarse label for the report only.
"""
import json, sys

def cls(x):
    d = x["device"]; mk = (d.get("Make") or d.get("AndroidMake") or d.get("ComAppleQuicktimeMake") or d.get("DeviceManufacturer") or "").strip()
    md = (d.get("Model") or d.get("AndroidModel") or d.get("ComAppleQuicktimeModel") or d.get("DeviceModelName") or d.get("CameraModelName") or "").strip()
    n = x["file"].lower()
    full = (mk + " " + md).lower()
    if "hero" in full or "gopro" in full or "gopr" in n or "gopro" in x["id"].lower(): return "action-cam"
    if "dji" in full or "dji_" in n or "osmo" in full: return "action-cam-dji"
    if "dashcam" in n or "dashcam" in x["id"].lower() or "dr260" in n or "dash-cam" in n: return "dashcam"
    if mk == "" and md.startswith("Canon"): return "camera-compact-dslr"
    if mk == "Apple" and md.startswith("iPhone"): return "phone-iphone"
    if mk == "Apple": return "screen-mac" if md.startswith("Mac") else "phone-iphone"
    if mk in ("Google", "motorola", "Xiaomi", "samsung", "SAMSUNG") or "pixel" in md.lower(): return "phone-android"
    if x["container"] == "M2TS" or mk in ("264", "4113", "259"): return "camcorder-avchd"
    if mk == "Sony": return "camcorder-sony"
    if mk: return "camera-compact-dslr"
    if x["screen"] or "screen" in n or "obs" in n: return "screen-recorder"
    return "other"

def main():
    seen = set(); rows = []
    for l in open(sys.argv[1], encoding="utf-8"):
        x = json.loads(l)
        if not x.get("keep") or x["sha256"] in seen: continue
        seen.add(x["sha256"]); rows.append(x)
    print("id\tpath\turl\tsha256\tsize\tclass\tdevice\tlicence\tcredit\tuse")
    for x in sorted(rows, key=lambda x: (cls(x), x["id"], x["file"])):
        d = x["device"]
        dev = " ".join(v for v in (d.get("Make") or d.get("AndroidMake") or d.get("ComAppleQuicktimeMake") or d.get("DeviceManufacturer") or "",
                                   d.get("Model") or d.get("AndroidModel") or d.get("ComAppleQuicktimeModel") or d.get("DeviceModelName") or d.get("CameraModelName") or "") if v).strip() or "-"
        credit = (x.get("creator") or "-").replace("\t", " ")
        print("\t".join([x["sha256"][:12], "raw/" + x["path"], x["url"], x["sha256"], str(x["size"]), cls(x), dev, x["licenseurl"], credit or "-", "fetch-only"]))

main()

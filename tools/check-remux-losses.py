#!/usr/bin/env python3
"""Spot-checks the loss reports of a remux against ExifTool and MediaInfo.

usage: check-remux-losses.py <remuxcheck.dll> <exiftool> <mediainfo> <workdir> <file>...

For each file: remux to MP4 with OneMedia.RemuxCheck (--report), then read the descriptive tags (title, artist, album, comment, genre,
description, copyright, composer, year) of the input and of the output with ExifTool and MediaInfo. The report is right when
  - the input has tags and the report lists a Tag loss (tags were not written, so it must say so), or the input has none and the report lists none;
  - the output has none of these tags (nothing was carried that the report does not say);
  - every dropped track the report names (Track loss) is absent from the output: the output has as many video/audio streams as the report says were written.
Prints one TSV line per file: name, tags in input (ExifTool/MediaInfo), Tag losses reported, tags in output, tracks reported written / found by MediaInfo, verdict.
"""
import json
import os
import subprocess
import sys

FIELDS = ["Title", "Artist", "Album", "Comment", "Genre", "Description", "Copyright", "Composer", "Year", "Author", "Director"]
MI_TEMPLATE = "General;%Title%|%Performer%|%Album%|%Comment%|%Genre%|%Copyright%|%Composer%|%Recorded_Date%\\n"


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, check=False).stdout


def exif_tags(exiftool, path):
    out = run([exiftool, "-j", "-a", "-G1", "-s"] + ["-" + f for f in FIELDS] + [path])
    try:
        rows = json.loads(out)
    except ValueError:
        return set()
    found = set()
    for k, v in (rows[0] if rows else {}).items():
        name = k.split(":")[-1]
        if name in FIELDS and str(v).strip():
            found.add(name)
    return found


def mediainfo_tags(mediainfo, path):
    out = run([mediainfo, "--Output=" + MI_TEMPLATE, path]).strip()
    return {i for i, part in enumerate(out.split("|")) if part.strip()}


def mediainfo_streams(mediainfo, path):
    v = run([mediainfo, "--Output=General;%VideoCount%/%AudioCount%", path]).strip()
    return v


def main():
    dll, exiftool, mediainfo, work = sys.argv[1:5]
    os.makedirs(work, exist_ok=True)
    bad = 0
    for f in sys.argv[5:]:
        out = os.path.join(work, "check.mp4")
        for e in ("", ".map", ".pkts"):
            if os.path.exists(out + e):
                os.remove(out + e)
        text = subprocess.run(["dotnet", dll, "remux", f, out, "--report"], capture_output=True, text=True, check=False).stdout
        lines = text.splitlines()
        if not lines or "\tok\t" not in lines[0]:
            print("%s\tnot-written\t%s" % (os.path.basename(f), lines[0].split("\t", 2)[-1][:80] if lines else ""))
            continue
        tag_losses = [l for l in lines if l.startswith("\tloss: Tag/")]
        written = sum(1 for l in open(out + ".map"))
        tin_e, tin_m = exif_tags(exiftool, f), mediainfo_tags(mediainfo, f)
        tout_e, tout_m = exif_tags(exiftool, out), mediainfo_tags(mediainfo, out)
        sv = mediainfo_streams(mediainfo, out)
        counts = [int(x or 0) for x in sv.split("/")] if "/" in sv else [0, 0]
        found = sum(counts)
        problems = []
        if (tin_e or tin_m) and not tag_losses:
            problems.append("input has tags but no Tag loss reported")
        if tag_losses and not (tin_e or tin_m):
            problems.append("Tag loss reported but neither tool sees descriptive tags in the input")
        if tout_e or tout_m:
            problems.append("output still has tags")
        if written != found:
            problems.append("tracks written %d, MediaInfo finds %d video+audio" % (written, found))
        if problems:
            bad += 1
        print("%s\tin=%s/%s\ttagLosses=%d\tout=%s/%s\ttracks=%d/%d\t%s" % (
            os.path.basename(f), sorted(tin_e), sorted(tin_m), len(tag_losses), sorted(tout_e), sorted(tout_m), written, found,
            "OK" if not problems else "CHECK: " + "; ".join(problems)))
    print("unexplained=%d" % bad)


main()

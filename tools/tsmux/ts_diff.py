#!/usr/bin/env python3
"""Remux files to MPEG-TS with the onemedia tool and compare the packets of the output with the input's, stream by stream.

Usage: ts_diff.py --tool <onemedia.dll> --ffprobe <path> --work <dir> [--tsp <path> --tsanalyze <path>] [--jobs N] [--layout ts|m2ts] files...

For every file: `onemedia remux in out.ts`, `ffprobe -show_packets -show_data` on both, then per matched stream:
  count, pts, dts (exact, after one constant offset for the whole file), keyframe flag, size and payload.
The payload is compared after the reverse repackaging: H.264/HEVC access units as lists of NAL units without access unit delimiters and
parameter sets (the muxer adds delimiters and may add parameter sets), AAC as the raw access unit without the ADTS header, everything else
byte for byte. Matroska stores no decode times, so for .mkv/.webm inputs only pts is compared.
Also checks the output as a transport stream: continuity counters, PCR interval, PAT/PMT repetition and CRCs, sync bytes.
Writes one TSV row per file to stdout and a JSON detail file per file to <work>/detail/.
"""
import argparse, json, os, subprocess, sys, hashlib, re
from fractions import Fraction
from concurrent.futures import ThreadPoolExecutor

VIDEO_NAL = {"h264": 9, "hevc": 35}
PS_TYPES = {"h264": {7, 8}, "hevc": {32, 33, 34}}
SUPPORTED = {"h264", "hevc", "mpeg2video", "mpeg1video", "aac", "mp3", "mp2", "ac3", "eac3"}


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, **kw)


def probe(ffprobe, path, hexdata=True, fallback=None):
    cmd = ["-v", "error", "-show_packets", "-show_streams", "-of", "json"]
    if hexdata:
        cmd.append("-show_data")
    r = run([ffprobe] + cmd + [path])
    used = ffprobe
    if (r.returncode != 0 or not r.stdout) and fallback:
        # ffprobe 7.0.2 crashes on some transport streams; the 8.x build reads them
        r = run([fallback] + cmd + [path])
        used = fallback
    if r.returncode != 0 and not r.stdout:
        raise RuntimeError("ffprobe failed: " + r.stderr.decode(errors="replace")[:300])
    return json.loads(r.stdout), used


def unhex(data):
    out = bytearray()
    for line in data.split("\n"):
        if len(line) < 12:
            continue
        out += bytes.fromhex(line[10:49].replace(" ", ""))
    return bytes(out)


def split_annexb(b):
    nals = []
    i = 0
    n = len(b)
    starts = []
    while True:
        j = b.find(b"\x00\x00\x01", i)
        if j < 0:
            break
        starts.append(j + 3)
        i = j + 3
    for k, s in enumerate(starts):
        e = starts[k + 1] - 3 if k + 1 < len(starts) else n
        nal = b[s:e]
        while nal.endswith(b"\x00"):
            nal = nal[:-1]
        nals.append(nal)
    return nals


def split_lp(b):
    for size in (4, 2, 3, 1):
        nals = []
        p = 0
        ok = True
        while p < len(b):
            if p + size > len(b):
                ok = False
                break
            ln = int.from_bytes(b[p:p + size], "big")
            p += size
            if ln == 0 or p + ln > len(b):
                ok = False
                break
            nals.append(b[p:p + ln])
            p += ln
        if ok and nals:
            return nals
    return None


def nal_type(codec, nal):
    return (nal[0] & 0x1F) if codec == "h264" else (nal[0] >> 1) & 0x3F


def norm_video(codec, nals):
    return [n for n in nals if nal_type(codec, n) != VIDEO_NAL[codec] and nal_type(codec, n) not in PS_TYPES[codec]]


def tb(s):
    n, d = s.split("/")
    return Fraction(int(n), int(d))


def rnd(x):
    # half away from zero, as the library rounds
    return Fraction(int(x + Fraction(1, 2)) if x >= 0 else -int(-x + Fraction(1, 2)))


def tick(p, key, base):
    v = p.get(key)
    if v is None or v == "N/A":
        return None
    return Fraction(int(v)) * base * 90000


def streams_of(doc, only_supported):
    st = [s for s in doc["streams"] if (not only_supported or s.get("codec_name") in SUPPORTED)]
    out = []
    for s in st:
        pk = [p for p in doc["packets"] if p["stream_index"] == s["index"]]
        out.append((s, pk))
    return out


def check_ts(path, layout):
    """Structure checks on the written transport stream; returns (problems list, facts dict)."""
    data = open(path, "rb").read()
    size = 192 if layout == "m2ts" else 188
    pre = size - 188
    problems = []
    if len(data) % size:
        problems.append("length not a multiple of %d" % size)
    cc = {}
    last_cc_payload = {}
    pcr_pid = None
    pmt_pid = None
    pcrs = []          # (pkt index, pcr27)
    psi_times = {"pat": [], "pmt": []}
    npk = len(data) // size
    sync_bad = 0
    cc_errors = 0
    clock = None  # latest pcr27
    for i in range(npk):
        p = data[i * size + pre:(i + 1) * size]
        if p[0] != 0x47:
            sync_bad += 1
            continue
        pusi = (p[1] >> 6) & 1
        pid = ((p[1] & 0x1F) << 8) | p[2]
        afc = (p[3] >> 4) & 3
        c = p[3] & 15
        payload_off = 4
        if afc & 2:
            al = p[4]
            if al > 183:
                problems.append("adaptation field too long at packet %d" % i)
                continue
            if al > 0 and (p[5] & 0x10) and pid_is_pcr(pcr_pid, pid):
                v = int.from_bytes(p[6:12], "big")
                base = v >> 15
                ext = v & 0x1FF
                clock = base * 300 + ext
                pcrs.append((i, clock))
            payload_off = 5 + al
        has_payload = bool(afc & 1)
        if pid != 0x1FFF:
            if pid in cc:
                expect = (cc[pid] + 1) & 15 if has_payload else cc[pid]
                # §2.4.3.3: a packet without payload repeats the previous counter
                if c != expect:
                    cc_errors += 1
                    if cc_errors <= 5:
                        problems.append("continuity error pid %d at packet %d (got %d expected %d)" % (pid, i, c, expect))
            cc[pid] = c
        if pid == 0 and has_payload and pusi:
            ptr = p[payload_off]
            sec = p[payload_off + 1 + ptr:]
            sl = ((sec[1] & 0xF) << 8) | sec[2]
            body = sec[:3 + sl]
            if crc32_mpeg(body) != 0:
                problems.append("PAT crc at packet %d" % i)
            psi_times["pat"].append(clock)
            if pmt_pid is None:
                pmt_pid = ((sec[10] & 0x1F) << 8) | sec[11]
        elif pmt_pid is not None and pid == pmt_pid and has_payload and pusi:
            ptr = p[payload_off]
            sec = p[payload_off + 1 + ptr:]
            sl = ((sec[1] & 0xF) << 8) | sec[2]
            body = sec[:3 + sl]
            if crc32_mpeg(body) != 0:
                problems.append("PMT crc at packet %d" % i)
            psi_times["pmt"].append(clock)
            if pcr_pid is None:
                pcr_pid = ((sec[8] & 0x1F) << 8) | sec[9]
    if sync_bad:
        problems.append("%d packets without sync byte" % sync_bad)
    # PCR PID is only known after the first PMT: re-scan PCRs if the first ones were missed
    ivs = [(pcrs[k + 1][1] - pcrs[k][1]) / 27000.0 for k in range(len(pcrs) - 1)]
    max_iv = max(ivs) if ivs else 0
    min_iv = min(ivs) if ivs else 0
    if not pcrs:
        problems.append("no PCR")
    elif max_iv > 100.0:
        problems.append("PCR interval %.1f ms" % max_iv)
    if min_iv < 0:
        problems.append("PCR went backwards")
    psi_gap = {}
    for name in ("pat", "pmt"):
        ts = [t for t in psi_times[name] if t is not None]
        if not psi_times[name]:
            problems.append("no " + name)
        elif len(ts) > 1:
            gap = max((ts[k + 1] - ts[k]) / 27000.0 for k in range(len(ts) - 1))
            psi_gap[name] = round(gap, 1)
            if gap > 500.0:  # ETSI TR 101 290 priority 1: PAT and PMT at least every 500 ms
                problems.append("%s repetition gap %.0f ms" % (name, gap))
    return problems, {"packets": npk, "pcrs": len(pcrs), "max_pcr_ms": round(max_iv, 2), "pat": len(psi_times["pat"]), "pmt": len(psi_times["pmt"]), "cc_errors": cc_errors, "max_psi_ms": max(psi_gap.values()) if psi_gap else 0}


def pid_is_pcr(pcr_pid, pid):
    return pcr_pid is None or pid == pcr_pid


_crc_table = None


def crc32_mpeg(b):
    global _crc_table
    if _crc_table is None:
        t = []
        for i in range(256):
            c = i << 24
            for _ in range(8):
                c = ((c << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if c & 0x80000000 else (c << 1) & 0xFFFFFFFF
            t.append(c)
        _crc_table = t
    crc = 0xFFFFFFFF
    for x in b:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ _crc_table[(crc >> 24) ^ x]
    return crc


def compare(inp, out, is_mkv):
    diffs = []
    counts = {"packets": 0, "stream_mismatch": 0, "pts": 0, "dts": 0, "flags": 0, "payload": 0, "count": 0}
    delta = None
    for si, ((s_in, p_in), (s_out, p_out)) in enumerate(zip(inp, out)):
        codec = s_in.get("codec_name", "?")
        if s_out.get("codec_name", "?") != codec:
            diffs.append("stream %d codec %s vs %s" % (si, codec, s_out["codec_name"]))
            counts["stream_mismatch"] += 1
            continue
        if len(p_in) != len(p_out):
            counts["count"] += 1
            diffs.append("stream %d (%s): %d packets in, %d out" % (si, codec, len(p_in), len(p_out)))
        tin = tb(s_in["time_base"])
        tout = Fraction(1, 90000)
        for k, (a, b) in enumerate(zip(p_in, p_out)):
            counts["packets"] += 1
            apts = tick(a, "pts", tin)
            adts = tick(a, "dts", tin)
            bpts = tick(b, "pts", tout)
            bdts = tick(b, "dts", tout)
            if bdts is None:
                bdts = bpts  # §2.4.3.7: a PES packet with only a PTS has a decode time equal to it
            if delta is None and apts is not None and bpts is not None:
                delta = bpts - rnd(apts)
            exp_pts = rnd(apts) + (delta or 0) if apts is not None else None
            if exp_pts is not None and bpts != exp_pts:
                counts["pts"] += 1
                if counts["pts"] <= 3:
                    diffs.append("stream %d pkt %d pts %s vs expected %s" % (si, k, bpts, exp_pts))
            if not is_mkv and adts is not None:
                exp_dts = rnd(adts) + (delta or 0)
                if bdts != exp_dts:
                    counts["dts"] += 1
                    if counts["dts"] <= 3:
                        diffs.append("stream %d pkt %d dts %s vs expected %s" % (si, k, bdts, exp_dts))
            ka = "K" in a.get("flags", "")
            kb = "K" in b.get("flags", "")
            if ka != kb:
                counts["flags"] += 1
                if counts["flags"] <= 3:
                    diffs.append("stream %d pkt %d keyframe flag %s vs %s" % (si, k, ka, kb))
            da = unhex(a["data"]) if "data" in a else None
            db = unhex(b["data"]) if "data" in b else None
            if da is None or db is None:
                continue
            ok = True
            if codec in VIDEO_NAL:
                na = split_lp(da)
                if na is None:
                    na = split_annexb(da)
                nb = split_annexb(db)
                ok = norm_video(codec, na) == norm_video(codec, nb)
            elif codec == "aac":
                if da[0] == 0xFF and (da[1] & 0xF6) == 0xF0:
                    ok = db == da  # input is ADTS already
                else:
                    ok = db[7 if (db[1] & 1) else 9:] == da
            else:
                ok = da == db
            if not ok:
                counts["payload"] += 1
                if counts["payload"] <= 3:
                    diffs.append("stream %d pkt %d payload differs (%d vs %d bytes)" % (si, k, len(da), len(db)))
    return counts, diffs


def raw_video_times(path, layout):
    """PTS/DTS of every video PES packet as written (pid -> list), read from the PES headers themselves.
    ffprobe drops a DTS equal to the PTS on a stream whose SPS announces reordering, so its dts column is a guess there."""
    data = open(path, "rb").read()
    size = 192 if layout == "m2ts" else 188
    pre = size - 188
    out = {}
    def ts(b):
        return (((b[0] >> 1) & 7) << 30) | (b[1] << 22) | ((b[2] >> 1) << 15) | (b[3] << 7) | (b[4] >> 1)
    for i in range(0, len(data) // size * size, size):
        p = data[i + pre:i + size]
        if p[0] != 0x47 or not (p[1] & 0x40) or not (p[3] & 0x10):
            continue
        pid = ((p[1] & 0x1F) << 8) | p[2]
        o = 4
        if p[3] & 0x20:
            o = 5 + p[4]
        if o + 19 <= 188 and p[o:o + 3] == b"\0\0\1" and 0xE0 <= p[o + 3] <= 0xEF:
            fl = p[o + 7]
            if fl & 0x80:
                pts = ts(p[o + 9:o + 14])
                dts = ts(p[o + 14:o + 19]) if fl & 0x40 else pts
                out.setdefault(pid, []).append((pts, dts))
    return out


def one(args, path):
    name = os.path.basename(path)
    ext = "m2ts" if args.layout == "m2ts" else "ts"
    out = os.path.join(args.work, "out", name + "." + ext)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    row = {"file": name}
    r = run(["dotnet", args.tool, "remux", path, out, "--overwrite"] + (["--format", "m2ts"] if args.layout == "m2ts" else []))
    row["remux_rc"] = r.returncode
    said = (r.stdout + r.stderr).decode(errors="replace")
    if r.returncode != 0 and not (os.path.exists(out) and os.path.getsize(out) > 0 and "wrote " in said):
        row["error"] = said.strip().split("\n")[-1][:200]
        if "UnsupportedCodec" in said:
            row["skipped"] = "no track the muxer can carry"
        return row
    row["refused_tracks"] = said.count(": not written")
    try:
        din, used_in = probe(args.ffprobe, path, fallback=args.ffprobe8)
        dout, used_out = probe(args.ffprobe, out, fallback=args.ffprobe8)
        row["ffprobe_in"] = os.path.basename(os.path.dirname(used_in)) if used_in != args.ffprobe else "7.0.2"
        row["ffprobe_out"] = os.path.basename(os.path.dirname(used_out)) if used_out != args.ffprobe else "7.0.2"
        if not any(p_["stream_index"] is not None for p_ in din["packets"]):
            row["skipped"] = "input has no packets"
            return row
        sin = streams_of(din, True)
        sout = streams_of(dout, False)
        raw = raw_video_times(out, args.layout)
        for s_o, p_o in sout:
            lst = raw.get(int(str(s_o.get("id", "0")), 0))
            if lst and len(lst) == len(p_o):
                for pk, (rp, rd) in zip(p_o, lst):
                    pk["pts"], pk["dts"] = str(rp), str(rd)
        # streams the muxer refuses (unsupported codecs) are not in the output; the report lists them
        counts, diffs = compare(sin, sout, path.lower().endswith((".mkv", ".webm", ".mka")))
        row.update(counts)
        row["streams_in"] = len(sin)
        row["streams_out"] = len(sout)
        if len(sin) != len(sout):
            diffs.append("streams: %d supported in, %d out" % (len(sin), len(sout)))
        row["diffs"] = diffs
    except Exception as e:  # noqa: BLE001
        row["error"] = "probe: " + str(e)[:200]
        return row
    problems, facts = check_ts(out, args.layout)
    row["ts_problems"] = problems
    row.update(facts)
    if args.tsp:
        r = run([args.tsp, "-I", "file", out, "-P", "continuity", "-O", "drop"])
        txt = (r.stdout + r.stderr).decode(errors="replace")
        row["tsp_continuity_msgs"] = len([l for l in txt.split("\n") if "ontinuity" in l and "rror" not in l.split("ontinuity")[0][:0]])
        row["tsp_rc"] = r.returncode
    if args.ffmpeg:
        ro = run([args.ffmpeg, "-v", "error", "-i", out, "-f", "null", "-"])
        ri = run([args.ffmpeg, "-v", "error", "-i", path, "-f", "null", "-"])
        eo = [l for l in ro.stderr.decode(errors="replace").split("\n") if l.strip()]
        row["ffmpeg_err_out"] = len(eo)
        row["ffmpeg_err_sample"] = eo[:3]
        row["ffmpeg_err_in"] = len([l for l in ri.stderr.decode(errors="replace").split("\n") if l.strip()])
    if not args.keep and not (args.keep_bad and (row.get("diffs") or row.get("ts_problems"))):
        os.remove(out)
    return row


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("--ffprobe", default="ffprobe")
    ap.add_argument("--ffmpeg")
    ap.add_argument("--ffprobe8")
    ap.add_argument("--tsp")
    ap.add_argument("--work", required=True)
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--layout", default="ts")
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--keep-bad", action="store_true")
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()
    os.makedirs(os.path.join(args.work, "detail"), exist_ok=True)
    with ThreadPoolExecutor(args.jobs) as ex:
        rows = list(ex.map(lambda f: one(args, f), args.files))
    tot = {"files": len(rows), "ok": 0, "pkts": 0, "bad": 0, "skipped": 0}
    for r in rows:
        json.dump(r, open(os.path.join(args.work, "detail", r["file"] + ".json"), "w"), indent=1)
        if r.get("skipped"):
            tot["skipped"] += 1
            print("%s\tSKIP\t%s" % (r["file"], r["skipped"]))
            continue
        bad = r.get("error") or r.get("diffs") or r.get("ts_problems")
        tot["pkts"] += r.get("packets", 0)
        if r.get("ffmpeg_err_out", 0) > r.get("ffmpeg_err_in", 0):
            bad = bad or ["ffmpeg errors out>in"]
        print("%s\t%s\tpkts=%s\t%s" % (r["file"], "OK" if not bad else "DIFF", r.get("packets", 0), "; ".join((r.get("diffs") or []) + (r.get("ts_problems") or []) + ([r["error"]] if r.get("error") else []))[:300]))
        tot["ok" if not bad else "bad"] += 1
    print("TOTAL", json.dumps(tot))


if __name__ == "__main__":
    main()

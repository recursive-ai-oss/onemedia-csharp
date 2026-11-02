#!/usr/bin/env python3
"""Compares OneMedia's timed events with black-box oracles.

  events-compare.py tmcd  FILE...            start timecode against ffprobe's `timecode` stream tag
  events-compare.py tmcd-remux FILE...       remuxes to MOV and MP4 and compares ffprobe's timecode of input and output
  events-compare.py scte  FILE --pid N [--pid M ...] [--unique]       SCTE 35 sections against TSDuck's tstables (XML) on that PID
  events-compare.py id3   FILE               ID3 packets (PTS, size, SHA-256) against ffprobe -show_packets, frames against an independent decode
  events-compare.py emsg  FILE               emsg boxes against MP4Box -diso

Environment: EVENTSCHECK (command that runs the EventsCheck tool, default `dotnet OneMedia.EventsCheck.dll`), ONEMEDIA_ORACLES (default
~/onemedia/oracles). Exit status is the number of differing items (0 = all agree). Prints one line per file and every difference.
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ORACLES = os.environ.get("ONEMEDIA_ORACLES", os.path.expanduser("~/onemedia/oracles"))
EVENTSCHECK = os.environ.get("EVENTSCHECK", "dotnet OneMedia.EventsCheck.dll").split()
FFPROBE = os.path.join(ORACLES, "bin", "ffprobe")
MP4BOX = os.path.join(ORACLES, "bin", "MP4Box")
TSDUCK = os.path.join(ORACLES, "tsduck-3.44-4676", "usr")


def ours(path, kind):
    out = subprocess.run([*EVENTSCHECK, path, "--kind", kind], check=True, capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if l.strip()]


def run(cmd, env=None):
    return subprocess.run(cmd, check=False, capture_output=True, text=True, env=env).stdout


def num(v):
    return int(v.replace(",", ""), 0)


# ---- timecode ----

def cmd_tmcd(files):
    bad = 0
    for f in files:
        probe = json.loads(run([FFPROBE, "-v", "error", "-show_entries", "stream=codec_tag_string:stream_tags=timecode", "-of", "json", f]))
        theirs = [s["tags"]["timecode"] for s in probe["streams"] if s.get("codec_tag_string") == "tmcd" and "timecode" in s.get("tags", {})]
        mine = [re.search(r"start=(\S+)", l).group(1) for l in ours(f, "tmcd") if l.startswith("tmcd ")]
        ok = bool(theirs) and mine[:1] == theirs[:1]
        print(f"{'ok  ' if ok else 'DIFF'} {os.path.basename(f)} ffprobe={theirs} ours={mine[:1]}")
        bad += 0 if ok else 1
    return bad


def cmd_tmcd_remux(files):
    """Remuxes each file to MOV and MP4 and checks that ffprobe reads the same start timecode from the output as from the input."""
    bad = 0
    work = os.environ.get("TMPDIR", "/tmp")
    for f in files:
        def tag(p):
            probe = json.loads(run([FFPROBE, "-v", "error", "-show_entries", "stream=codec_tag_string:stream_tags=timecode", "-of", "json", p]))
            return [s["tags"]["timecode"] for s in probe["streams"] if s.get("codec_tag_string") == "tmcd" and "timecode" in s.get("tags", {})]
        want = tag(f)
        for ext in ("mov", "mp4"):
            out = os.path.join(work, f"remux-{os.path.basename(f)}.{ext}")
            subprocess.run([*EVENTSCHECK, f, "--remux", out, "--kind", "none"], check=True, capture_output=True, text=True)
            got = tag(out)
            mine = [re.search(r"start=(\S+)", l).group(1) for l in ours(out, "tmcd") if l.startswith("tmcd ")]
            ok = bool(want) and got == want and mine[:1] == want[:1]
            print(f"{'ok  ' if ok else 'DIFF'} remux {os.path.basename(f)} -> {ext}: input={want} output ffprobe={got} ours={mine[:1]}")
            bad += 0 if ok else 1
            os.remove(out)
    return bad


# ---- SCTE 35 ----

def canon_xml(table):
    """One line per section in the form the EventsCheck tool prints after its first `|`, or 'bad' for a section TSDuck rejected."""
    if table.tag == "generic_short_table":
        return "bad"
    parts = [f"adj={num(table.get('pts_adjustment'))} tier={num(table.get('tier'))}"]
    cmd = []
    descs = []
    for ch in table:
        a = ch.attrib
        if ch.tag == "splice_null":
            cmd.append("null")
        elif ch.tag == "bandwidth_reservation":
            cmd.append("bw")
        elif ch.tag == "private_command":
            data = "".join((ch.text or "").split())
            cmd.append(f"priv id={num(a['identifier'])} data={data}")
        elif ch.tag == "time_signal":
            cmd.append("ts" + (f" pts={num(a['pts_time'])}" if "pts_time" in a else ""))
        elif ch.tag == "splice_insert":
            s = f"insert evt={num(a['splice_event_id'])} cancel={int(a['splice_event_cancel'] == 'true')}"
            if a["splice_event_cancel"] != "true":
                s += f" oon={int(a['out_of_network'] == 'true')} imm={int(a['splice_immediate'] == 'true')} prog={int(ch.find('component') is None)}"
                if "pts_time" in a:
                    s += f" pts={num(a['pts_time'])}"
                bd = ch.find("break_duration")
                if bd is not None:
                    s += f" dur={num(bd.get('duration'))} auto={int(bd.get('auto_return') == 'true')}"
                s += f" uid={num(a['unique_program_id'])} avail={num(a['avail_num'])} exp={num(a['avails_expected'])}"
            cmd.append(s)
        elif ch.tag == "splice_schedule":
            s = "sched"
            for ev in ch.findall("splice_event"):
                e = ev.attrib
                s += f" [evt={num(e['splice_event_id'])} cancel={int(e['splice_event_cancel'] == 'true')}"
                if e["splice_event_cancel"] != "true":
                    s += f" oon={int(e['out_of_network'] == 'true')} utc={e.get('utc_splice_time')}"
                    bd = ev.find("break_duration")
                    if bd is not None:
                        s += f" dur={num(bd.get('duration'))} auto={int(bd.get('auto_return') == 'true')}"
                    s += f" uid={num(e['unique_program_id'])} avail={num(e['avail_num'])} exp={num(e['avails_expected'])}"
                s += "]"
            cmd.append(s)
        elif ch.tag == "splice_avail_descriptor":
            descs.append(f"avail prov={num(a['provider_avail_id'])}")
        elif ch.tag == "splice_segmentation_descriptor":
            s = f"seg evt={num(a['segmentation_event_id'])} cancel={int(a['segmentation_event_cancel'] == 'true')}"
            if a["segmentation_event_cancel"] != "true":
                s += f" type={num(a['segmentation_type_id'])} num={num(a['segment_num'])} exp={num(a['segments_expected'])}"
                if "sub_segment_num" in a:
                    s += f" sub={num(a['sub_segment_num'])} subexp={num(a['sub_segments_expected'])}"
                if "segmentation_duration" in a:
                    s += f" dur={num(a['segmentation_duration'])}"
                up = ch.find("segmentation_upid")
                s += f" upidtype={num(up.get('type'))} upid={''.join((up.text or '').split())}"
            descs.append(s)
        elif ch.tag != "metadata":
            descs.append(f"desc tag=? ({ch.tag})")
    return " ".join(parts) + " | " + " ".join(cmd) + "".join(" | " + d for d in descs)


def our_canon(line):
    # "scte crc=ok adj=0 tier=4095 cw=255 time=.. specified=0 | insert ..." -> "adj=0 tier=4095 | insert ..." ; crc state first
    m = re.match(r"scte crc=(\w+) adj=(\d+) tier=(\d+) cw=\d+ time=\S+ specified=\d \|(.*)$", line)
    return m.group(1), f"adj={m.group(2)} tier={m.group(3)} |{m.group(4)}"


def cmd_scte(files, pids, unique=False):
    env = dict(os.environ, LD_LIBRARY_PATH=f"{TSDUCK}/lib/x86_64-linux-gnu:{TSDUCK}/lib")
    bad = 0
    for f in files:
        xmlfile = os.path.join(os.environ.get("TMPDIR", "/tmp"), "events-compare-" + os.path.basename(f) + ".xml")
        subprocess.run([f"{TSDUCK}/bin/tstables", *[a for p in pids for a in ("--pid", str(p))], "--tid", "0xFC", "--xml-output", xmlfile, f], env=env, capture_output=True)
        root = ET.parse(xmlfile).getroot()
        theirs = [canon_xml(t) for t in root if t.tag in ("splice_information_table", "generic_short_table")]
        mine = [our_canon(l) for l in ours(f, "scte")]
        diffs = []
        if unique:
            # A long capture repeats each message. TSDuck reports the distinct ones, so compare the distinct canonical forms.
            t_set = {t for t in theirs if t != "bad"}
            m_set = {m for crc, m in mine if crc == "ok"}
            m_set = {re.sub(r"utc=(\d+)", lambda mm: "utc=" + gps_to_text(int(mm.group(1))), m) for m in m_set}
            for x in sorted(t_set - m_set):
                diffs.append(f"only in tsduck: {x}")
            for x in sorted(m_set - t_set):
                diffs.append(f"only in ours: {x}")
            print(f"distinct: tsduck={len(t_set)} ours={len(m_set)} ; sections ours={len(mine)}")
        else:
            if len(theirs) != len(mine):
                diffs.append(f"count: tsduck={len(theirs)} ours={len(mine)}")
            for i, (t, (crc, m)) in enumerate(zip(theirs, mine)):
                if t == "bad":
                    if crc != "bad":
                        diffs.append(f"#{i}: tsduck rejects the section, ours crc={crc}")
                    continue
                if crc != "ok":
                    diffs.append(f"#{i}: ours crc={crc}, tsduck accepts")
                # TSDuck prints schedule times as dates; convert ours (GPS epoch, 18 leap seconds in 2033) before comparing
                m2 = re.sub(r"utc=(\d+)", lambda mm: "utc=" + gps_to_text(int(mm.group(1))), m)
                if t != m2:
                    diffs.append(f"#{i}:\n    tsduck: {t}\n    ours:   {m2}")
        print(f"{'ok  ' if not diffs else 'DIFF'} {os.path.basename(f)} sections={len(mine)}")
        for d in diffs:
            print("   ", d)
        bad += len(diffs)
    return bad


def gps_to_text(seconds):
    import datetime
    # TSDuck shows the time as UTC; GPS time runs ahead of UTC by the leap seconds counted since 1980 (18 from 2017).
    return (datetime.datetime(1980, 1, 6) + datetime.timedelta(seconds=seconds - 18)).strftime("%Y-%m-%d %H:%M:%S")


# ---- ID3 ----

def synchsafe(b):
    return (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3]


def decode_tags(data):
    """Independent decode of back-to-back ID3v2.4 tags: list of (frame id, text) for the frames the tests use."""
    out = []
    p = 0
    while p + 10 <= len(data) and data[p:p + 3] == b"ID3":
        size = synchsafe(data[p + 6:p + 10])
        body = data[p + 10:p + 10 + size]
        q = 0
        frames = []
        while q + 10 <= len(body) and body[q] != 0:
            fid = body[q:q + 4].decode("latin-1")
            fs = synchsafe(body[q + 4:q + 8])
            payload = body[q + 10:q + 10 + fs]
            frames.append((fid, payload))
            q += 10 + fs
        out.append(frames)
        p += 10 + size
    return out


def cmd_id3(files):
    bad = 0
    for f in files:
        probe = json.loads(run([FFPROBE, "-v", "error", "-select_streams", "d", "-show_entries", "packet=pts,size,data_hash", "-show_data_hash", "sha256", "-of", "json", f]))
        theirs = [(int(p["pts"]), int(p["size"]), p["data_hash"].split(":")[-1]) for p in probe.get("packets", [])]
        lines = ours(f, "id3")
        mine = []
        for l in lines:
            m = re.match(r"id3-packet pts=(\d+) size=(\d+) sha256=(\w+)", l)
            if m:
                mine.append((int(m.group(1)), int(m.group(2)), m.group(3)))
        diffs = []
        # ffprobe also lists packets of the SCTE PID (none here); only timed-id3 data packets have a hash here
        if theirs != mine:
            diffs.append(f"packets differ: ffprobe={theirs} ours={mine}")
        # frames: dump the packet bytes through ffprobe and decode them independently, then compare with our frame lines
        dump = run([FFPROBE, "-v", "error", "-select_streams", "d", "-show_packets", "-show_data", "-of", "default=nw=1", f])
        packets = []
        cur = None
        for line in dump.splitlines():
            if line.startswith("data="):
                cur = bytearray()
                packets.append(cur)
            elif cur is not None and re.match(r"^[0-9a-f]{8}:", line):
                hexpart = line[9:].split("  ")[0] if False else re.sub(r"\s", "", line[9:49])
                cur += bytes.fromhex(hexpart)
        # trim each packet to its declared size
        expected_frames = []
        for pk, (pts, size, _) in zip(packets, theirs):
            for frames in decode_tags(bytes(pk[:size])):
                expected_frames.append((pts, [fid for fid, _ in frames]))
        got = []
        for l in lines:
            m = re.match(r"id3 pts=(\d+)/(\d+) ver=\S+ size=\d+ frames=(.*)$", l)
            if m:
                ids = re.findall(r"(?:^|;)([A-Z0-9]{4})=", m.group(3))
                got.append((int(m.group(1)), ids))
        if expected_frames != got:
            diffs.append(f"frames differ: independent={expected_frames} ours={got}")
        print(f"{'ok  ' if not diffs else 'DIFF'} {os.path.basename(f)} packets={len(mine)} tags={len(got)}")
        for d in diffs:
            print("   ", d)
        bad += len(diffs)
    return bad


# ---- emsg ----

def cmd_emsg(files):
    from fractions import Fraction
    bad = 0
    for f in files:
        dump = run([MP4BOX, "-diso", f, "-std"])
        # MP4Box lists the boxes in file order; a version 0 box is timed from the baseMediaDecodeTime of the next moof's first fragment.
        track_scale = {}
        theirs = []
        pending = []
        scale = None
        for line in dump.splitlines():
            m = re.search(r"<MediaHeaderBox [^>]*TimeScale=\"(\d+)\"", line)
            if m:
                scale = int(m.group(1))
            if "<EventMessageBox" in line:
                a = dict(re.findall(r'(\w+)="([^"]*)"', line))
                if a.get("Version") == "1":
                    t = Fraction(int(a["presentation_time"]), int(a["timescale"]))
                    theirs.append((a["scheme_id_uri"], a["value"], int(a["event_id"]), t, a["message_data"][2:].upper(), int(a["event_duration"]), int(a["timescale"])))
                else:
                    pending.append(a)
            m = re.search(r'baseMediaDecodeTime="(\d+)"', line)
            if m and pending and scale:
                base = Fraction(int(m.group(1)), scale)
                for a in pending:
                    t = base + Fraction(int(a["presentation_time_delta"]), int(a["timescale"]))
                    theirs.append((a["scheme_id_uri"], a["value"], int(a["event_id"]), t, a["message_data"][2:].upper(), int(a["event_duration"]), int(a["timescale"])))
                pending = []
        mine = []
        for l in ours(f, "emsg"):
            m = re.match(r"emsg scheme=(\S+) value=(\S*) kind=\S+ time=(-?\d+)/(\d+) duration=(\S+) id=(\d+) data=(\w*) at=", l)
            mine.append((m.group(1), m.group(2), int(m.group(6)), Fraction(int(m.group(3)), int(m.group(4))), m.group(7).upper(), m.group(5)))
        diffs = []
        if len(theirs) != len(mine):
            diffs.append(f"count: MP4Box={len(theirs)} ours={len(mine)}")
        key = lambda x: (x[0], x[1], x[2], x[4])
        for t in sorted(theirs, key=key):
            o = [x for x in mine if key(x) == key(t)]
            if not o:
                diffs.append(f"missing in ours: {t[:3]}")
                continue
            o = o[0]
            if o[3] != t[3]:
                diffs.append(f"time {t[:3]}: MP4Box={t[3]} ours={o[3]}")
            # unknown (0xFFFFFFFF) and zero durations: ours says `unknown` for 0xFFFFFFFF, and the box's own value otherwise
            expected = "unknown" if t[5] == 0xFFFFFFFF else None
            if expected and o[5] != expected:
                diffs.append(f"duration {t[:3]}: expected unknown, ours={o[5]}")
        print(f"{'ok  ' if not diffs else 'DIFF'} {os.path.basename(f)} emsg={len(mine)} matched={len(theirs) - len([d for d in diffs if d.startswith('missing')])}")
        for d in diffs:
            print("   ", d)
        bad += len(diffs)
    return bad


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    cmd = sys.argv[1]
    args = sys.argv[2:]
    if cmd == "scte":
        pids = []
        while "--pid" in args:
            i = args.index("--pid")
            pids.append(int(args[i + 1], 0))
            del args[i:i + 2]
        unique = "--unique" in args
        if unique:
            args.remove("--unique")
        return cmd_scte(args, pids or [0x102], unique)
    return {"tmcd": cmd_tmcd, "tmcd-remux": cmd_tmcd_remux, "id3": cmd_id3, "emsg": cmd_emsg}[cmd](args)


if __name__ == "__main__":
    sys.exit(min(main(), 255))

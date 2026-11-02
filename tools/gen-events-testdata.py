#!/usr/bin/env python3
"""Writes the small fixtures for the timed-event tests into testdata/events/.

  scte35-id3.ts        transport stream with an ID3 metadata PID (0x15) and a SCTE 35 PID (0x86): see SCTE_MESSAGES and ID3_TAGS below
  emsg.mp4             fragmented MP4 (ffmpeg) with emsg boxes of the SCTE 35, ID3 and an unknown scheme inserted before the moof boxes
  emsg.expected.txt    what those emsg boxes should resolve to (times derived from the fragments' own tfdt boxes)
  timecode-df.mov      ffmpeg -timecode 01:02:03;04 (drop-frame, 29.97 fps)
  timecode-ndf.mp4     ffmpeg -timecode 10:00:00:12 (non-drop, 25 fps)

The transport stream and the emsg boxes are built here, byte by byte, from the layouts the standards describe; the C# parsers are
written separately and the tests compare them with the values listed here and with independent tools' readings of the same files.
Usage: gen-events-testdata.py OUTDIR   (ffmpeg on PATH, or ONEMEDIA_FFMPEG)
"""
import os
import struct
import subprocess
import sys

FFMPEG = os.environ.get("ONEMEDIA_FFMPEG", "ffmpeg")
CLOCK = 90000


def crc32_mpeg(data):
    crc = 0xFFFFFFFF
    for b in data:
        crc ^= b << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if crc & 0x80000000 else (crc << 1) & 0xFFFFFFFF
    return crc


# ---- SCTE 35 -----------------------------------------------------------------------------------

def splice_time(pts):
    if pts is None:
        return bytes([0x7F])
    return bytes([0xFE | ((pts >> 32) & 1)]) + struct.pack(">I", pts & 0xFFFFFFFF)


def break_duration(auto_return, duration):
    return bytes([(0x80 if auto_return else 0) | 0x7E | ((duration >> 32) & 1)]) + struct.pack(">I", duration & 0xFFFFFFFF)


def splice_insert(event_id, out, pts, duration=None, auto_return=False, immediate=False, cancel=False, upid=1, avail=0, expected=0):
    body = struct.pack(">I", event_id) + bytes([0xFF if cancel else 0x7F])
    if cancel:
        return body
    flags = (0x80 if out else 0) | 0x40 | (0x20 if duration is not None else 0) | (0x10 if immediate else 0) | 0x0F
    body += bytes([flags])
    if not immediate:
        body += splice_time(pts)
    if duration is not None:
        body += break_duration(auto_return, duration)
    return body + struct.pack(">HBB", upid, avail, expected)


def avail_descriptor(provider):
    return bytes([0x00, 8]) + b"CUEI" + struct.pack(">I", provider)


def segmentation_descriptor(event_id, type_id, upid_type, upid, duration, seg_num, seg_expected, sub=None):
    d = struct.pack(">I", event_id) + bytes([0x7F])  # not cancelled
    # program_segmentation_flag, segmentation_duration_flag, delivery_not_restricted_flag, then 5 reserved bits
    d += bytes([0x80 | (0x40 if duration is not None else 0) | 0x20 | 0x1F])
    if duration is not None:
        d += bytes([(duration >> 32) & 0xFF]) + struct.pack(">I", duration & 0xFFFFFFFF)
    d += bytes([upid_type, len(upid)]) + upid + bytes([type_id, seg_num, seg_expected])
    if sub is not None:
        d += bytes(sub)
    return bytes([0x02, len(d) + 4]) + b"CUEI" + d


def section(command_type, command, descriptors=b"", pts_adjustment=0, tier=0xFFF, corrupt_crc=False):
    body = bytes([0x00])  # protocol_version
    body += bytes([(pts_adjustment >> 32) & 1]) + struct.pack(">I", pts_adjustment & 0xFFFFFFFF)  # not encrypted
    body += bytes([0xFF])  # cw_index
    body += bytes([(tier >> 4) & 0xFF, ((tier & 0xF) << 4) | ((len(command) >> 8) & 0xF), len(command) & 0xFF])
    body += bytes([command_type]) + command
    body += struct.pack(">H", len(descriptors)) + descriptors
    length = len(body) + 4
    s = bytes([0xFC, 0x30 | ((length >> 8) & 0xF), length & 0xFF]) + body
    crc = crc32_mpeg(s)
    if corrupt_crc:
        crc ^= 0x1
    return s + struct.pack(">I", crc)


BASE = 900000  # 10 s on the 90 kHz clock

# (arrival time in seconds of stream time, name, section bytes)
SCTE_MESSAGES = [
    (10.5, "insert-out", section(0x05, splice_insert(0x12, True, BASE + 5 * CLOCK, 60 * CLOCK, True, upid=1, avail=18, expected=255), avail_descriptor(0x12))),
    (11.0, "splice-null", section(0x00, b"")),
    (11.5, "time-signal-placement-start", section(0x06, splice_time(BASE + 7 * CLOCK), segmentation_descriptor(0x1001, 0x34, 0x0F, b"urn:example:ad", 30 * CLOCK, 1, 2, (1, 1)))),
    (12.0, "insert-immediate", section(0x05, splice_insert(0x13, True, None, immediate=True, upid=2, avail=1, expected=2))),
    (12.5, "time-signal-adjusted", section(0x06, splice_time(8589934000), segmentation_descriptor(0x1002, 0x10, 0x08, b"AIRING01", None, 0, 0), pts_adjustment=1000000)),
    (13.0, "bandwidth-reservation", section(0x07, b"")),
    (13.5, "private-command", section(0xFF, b"ONMD" + b"\x01\x02\x03")),
    (14.0, "insert-cancel", section(0x05, splice_insert(0x12, False, None, cancel=True))),
    (14.5, "schedule", section(0x04, bytes([1]) + struct.pack(">I", 0x20) + bytes([0x7F, 0xE0 | 0x1F]) + struct.pack(">I", 1700000000) + break_duration(False, 27 * CLOCK) + struct.pack(">HBB", 3, 1, 1))),
    (15.0, "insert-in", section(0x05, splice_insert(0x12, False, BASE + 65 * CLOCK, upid=1, avail=18, expected=255), avail_descriptor(0x12))),
    (15.5, "bad-crc", section(0x05, splice_insert(0x14, True, BASE + 9 * CLOCK, 10 * CLOCK, False), avail_descriptor(0x14), corrupt_crc=True)),
]


# ---- ID3 ---------------------------------------------------------------------------------------

def synchsafe(n):
    return bytes([(n >> 21) & 0x7F, (n >> 14) & 0x7F, (n >> 7) & 0x7F, n & 0x7F])


def id3_frame(frame_id, payload):
    return frame_id.encode("ascii") + synchsafe(len(payload)) + b"\x00\x00" + payload


def id3_tag(frames):
    body = b"".join(frames)
    return b"ID3\x04\x00\x00" + synchsafe(len(body)) + body


def txxx(desc, value):
    return id3_frame("TXXX", b"\x03" + desc.encode() + b"\x00" + value.encode())


def priv(owner, data):
    return id3_frame("PRIV", owner.encode() + b"\x00" + data)


def tit2(text):
    return id3_frame("TIT2", b"\x03" + text.encode())


def wxxx(desc, url):
    return id3_frame("WXXX", b"\x03" + desc.encode() + b"\x00" + url.encode("latin-1"))


ID3_TAGS = [
    (10.0, [id3_tag([priv("com.apple.streaming.transportStreamTimestamp", struct.pack(">Q", BASE))])]),
    (11.0, [id3_tag([txxx("key", "value1")])]),
    (12.0, [id3_tag([tit2("Segment two"), wxxx("site", "https://example.com/a")])]),
    (13.0, [id3_tag([txxx("a", "1")]), id3_tag([txxx("b", "2")])]),  # two tags in one PES payload
    (16.0, [id3_tag([txxx("last", "end")])]),
    (17.0, [b"\x01\x02\x03\x04"]),  # not an ID3 tag: the reader reports it and moves on
]


# ---- transport stream --------------------------------------------------------------------------

PID_PMT, PID_ID3, PID_SCTE = 0x100, 0x101, 0x102


class Mux:
    def __init__(self):
        self.out = bytearray()
        self.cc = {}

    def _cc(self, pid, advance=True):
        c = self.cc.get(pid, 0)
        if advance:
            self.cc[pid] = (c + 1) & 15
        return c

    def section(self, pid, sec):
        data = b"\x00" + sec  # pointer_field
        for i in range(0, len(data), 184):
            chunk = data[i:i + 184]
            chunk += b"\xFF" * (184 - len(chunk))
            pusi = 0x40 if i == 0 else 0
            self.out += bytes([0x47, pusi | (pid >> 8), pid & 0xFF, 0x10 | self._cc(pid)]) + chunk

    def pes(self, pid, pes):
        for i in range(0, len(pes), 184):
            chunk = pes[i:i + 184]
            pusi = 0x40 if i == 0 else 0
            if len(chunk) == 184:
                self.out += bytes([0x47, pusi | (pid >> 8), pid & 0xFF, 0x10 | self._cc(pid)]) + chunk
            else:
                room = 183 - len(chunk)  # adaptation_field_length
                if room == 0:
                    af = b"\x00"
                else:
                    af = bytes([room, 0x00]) + b"\xFF" * (room - 1)
                self.out += bytes([0x47, pusi | (pid >> 8), pid & 0xFF, 0x30 | self._cc(pid)]) + af + chunk


def psi_section(table_id, ext, body):
    s = bytes([table_id, 0xB0, 0, ext >> 8, ext & 0xFF, 0xC1, 0, 0]) + body
    length = len(s) - 3 + 4
    s = bytes([s[0], 0xB0 | (length >> 8), length & 0xFF]) + s[3:]
    return s + struct.pack(">I", crc32_mpeg(s))


def pes_packet(pts, payload):
    pts_bytes = bytes([0x21 | ((pts >> 29) & 0x0E), (pts >> 22) & 0xFF, 0x01 | ((pts >> 14) & 0xFE), (pts >> 7) & 0xFF, 0x01 | ((pts << 1) & 0xFE)])
    header = bytes([0x80, 0x80, 5]) + pts_bytes
    length = len(header) + len(payload)
    return b"\x00\x00\x01\xBD" + struct.pack(">H", length) + header + payload


def build_ts():
    m = Mux()
    pat = psi_section(0x00, 1, struct.pack(">HH", 1, 0xE000 | PID_PMT))
    metadata = struct.pack(">H", 0xFFFF) + b"ID3 " + b"\xFF" + b"ID3 " + bytes([0x00, 0x3F])
    id3_desc = bytes([0x05, 4]) + b"ID3 " + bytes([0x26, len(metadata)]) + metadata
    scte_desc = bytes([0x05, 4]) + b"CUEI"
    pmt_body = struct.pack(">HH", 0xE000 | 0x1FFF, 0xF000)  # PCR PID: none; no program info
    pmt_body += bytes([0x15]) + struct.pack(">HH", 0xE000 | PID_ID3, 0xF000 | len(id3_desc)) + id3_desc
    pmt_body += bytes([0x86]) + struct.pack(">HH", 0xE000 | PID_SCTE, 0xF000 | len(scte_desc)) + scte_desc
    pmt = psi_section(0x02, 1, pmt_body)
    events = []
    for t, payloads in ID3_TAGS:
        events.append((t, 0, "id3", payloads))
    for t, name, sec in SCTE_MESSAGES:
        events.append((t, 1, "scte", sec))
    events.sort(key=lambda e: (e[0], e[1]))
    m.section(0, pat)
    m.section(PID_PMT, pmt)
    for t, _, kind, data in events:
        if kind == "id3":
            m.pes(PID_ID3, pes_packet(int(t * CLOCK), b"".join(data)))
        else:
            m.section(PID_SCTE, data)
        m.section(0, pat)
        m.section(PID_PMT, pmt)
    return bytes(m.out)


# ---- emsg in fMP4 ------------------------------------------------------------------------------

def box(kind, payload):
    return struct.pack(">I", 8 + len(payload)) + kind + payload


def emsg_v0(scheme, value, timescale, delta, duration, ident, data):
    return box(b"emsg", b"\x00\x00\x00\x00" + scheme.encode() + b"\x00" + value.encode() + b"\x00" + struct.pack(">IIII", timescale, delta, duration, ident) + data)


def emsg_v1(scheme, value, timescale, time, duration, ident, data):
    return box(b"emsg", b"\x01\x00\x00\x00" + struct.pack(">IQII", timescale, time, duration, ident) + scheme.encode() + b"\x00" + value.encode() + b"\x00" + data)


def top_boxes(data):
    p = 0
    while p + 8 <= len(data):
        size, kind = struct.unpack(">I4s", data[p:p + 8])
        yield kind, p, data[p:p + size]
        p += size


def find(data, path):
    """First box with the given path of 4-byte names inside the (container) box bytes in `data`."""
    kind = path[0]
    p = 8 if len(data) > 8 and data[4:8] in (b"moov", b"trak", b"mdia", b"moof", b"traf") else 0
    while p + 8 <= len(data):
        size, k = struct.unpack(">I4s", data[p:p + 8])
        if k == kind:
            return data[p:p + size] if len(path) == 1 else find(data[p:p + size], path[1:])
        p += size
    return None


def build_emsg_mp4(ffmpeg_out, expected_path):
    base = open(ffmpeg_out, "rb").read()
    scte = SCTE_MESSAGES[0][2]
    id3 = id3_tag([txxx("emsg", "one")])
    inserts = [
        [emsg_v1("urn:scte:scte35:2013:bin", "1001", CLOCK, 2 * CLOCK, 60 * CLOCK, 7, scte),
         emsg_v0("https://aomedia.org/emsg/ID3", "1", 1000, 500, 0, 1, id3)],
        [emsg_v0("urn:mpeg:dash:event:2012", "1", 1000, 250, 0xFFFFFFFF, 2, b"hello"),
         emsg_v0("https://aomedia.org/emsg/ID3", "1", 1000, 100, 0, 2, id3_tag([txxx("emsg", "two")]))],
    ]
    out = bytearray()
    expected = []
    moof_index = 0
    movie = None
    for kind, _, b in top_boxes(base):
        if kind == b"moov":
            movie = b
        if kind == b"moof":
            if moof_index < len(inserts):
                traf = find(b, [b"traf"])
                tfdt = find(traf, [b"tfdt"])
                version = tfdt[8]
                decode = struct.unpack(">Q", tfdt[12:20])[0] if version == 1 else struct.unpack(">I", tfdt[12:16])[0]
                tfhd = find(traf, [b"tfhd"])
                track_id = struct.unpack(">I", tfhd[12:16])[0]
                # the track's timescale comes from its mdhd
                timescale = None
                p = 8
                while p + 8 <= len(movie):
                    size, k = struct.unpack(">I4s", movie[p:p + 8])
                    if k == b"trak":
                        tkhd = find(movie[p:p + size], [b"tkhd"])
                        tid = struct.unpack(">I", tkhd[20:24])[0] if tkhd[8] == 0 else struct.unpack(">I", tkhd[28:32])[0]
                        mdhd = find(movie[p:p + size], [b"mdia", b"mdhd"])
                        ts = struct.unpack(">I", mdhd[20:24])[0] if mdhd[8] == 0 else struct.unpack(">I", mdhd[28:32])[0]
                        if tid == track_id:
                            timescale = ts
                    p += size
                for e in inserts[moof_index]:
                    out += e
                    # expected line: scheme, value, id, time numerator/denominator as an exact fraction
                    v = e[8]
                    q = 12
                    if v == 0:
                        zero = e.index(b"\x00", q)
                        scheme = e[q:zero].decode()
                        zero2 = e.index(b"\x00", zero + 1)
                        value = e[zero + 1:zero2].decode()
                        ts_e, delta, dur, ident = struct.unpack(">IIII", e[zero2 + 1:zero2 + 17])
                        # time = decode / timescale + delta / ts_e
                        num = decode * ts_e + delta * timescale
                        den = timescale * ts_e
                    else:
                        ts_e, pt, dur, ident = struct.unpack(">IQII", e[q:q + 20])
                        zero = e.index(b"\x00", q + 20)
                        scheme = e[q + 20:zero].decode()
                        zero2 = e.index(b"\x00", zero + 1)
                        value = e[zero + 1:zero2].decode()
                        num, den = pt, ts_e
                    expected.append(f"{scheme}\t{value}\t{ident}\t{num}/{den}\t{dur}")
            moof_index += 1
        out += b
    open(ffmpeg_out, "wb").write(bytes(out))
    open(expected_path, "w").write("\n".join(expected) + "\n")


def run(*args):
    subprocess.run([FFMPEG, "-v", "error", "-y", *args], check=True)


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    open(os.path.join(outdir, "scte35-id3.ts"), "wb").write(build_ts())
    emsg = os.path.join(outdir, "emsg.mp4")
    run("-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "aac", "-b:a", "24k", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000", "-f", "mp4", emsg)
    build_emsg_mp4(emsg, os.path.join(outdir, "emsg.expected.txt"))
    run("-f", "lavfi", "-i", "testsrc2=s=64x48:r=30000/1001:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "40", "-timecode", "01:02:03;04", os.path.join(outdir, "timecode-df.mov"))
    run("-f", "lavfi", "-i", "testsrc2=s=64x48:r=25:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "40", "-timecode", "10:00:00:12", os.path.join(outdir, "timecode-ndf.mp4"))


if __name__ == "__main__":
    main()

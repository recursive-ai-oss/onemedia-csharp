#!/usr/bin/env python3
"""Writes tiny MP4 files with one data track, for the DJI telemetry checks.

kinds:
  djmd   protobuf samples in a 'djmd' sample entry (value wire types chosen by --wire: f32, f64, varint)
  text   DJI per-frame text in a 'tx3g' subtitle track (two-byte length prefix)

The files hold no video: the tracks are what the readers and ExifTool look at. Every value is synthetic.

usage: gen-dji-fixtures.py OUTDIR
"""
import os
import struct
import sys


def box(typ, *parts):
    body = b"".join(parts)
    return struct.pack(">I4s", 8 + len(body), typ) + body


def full(typ, version, flags, *parts):
    return box(typ, struct.pack(">I", (version << 24) | flags), *parts)


def mp4(entry_type, handler, samples, timescale=1000, delta=100, text=False, udta=b""):
    """samples: list of bytes; one sample per `delta` ticks."""
    n = len(samples)
    dur = n * delta
    ftyp = box(b"ftyp", b"isom", struct.pack(">I", 512), b"isomiso2mp41")
    mvhd = full(b"mvhd", 0, 0, struct.pack(">IIII", 0, 0, timescale, dur), struct.pack(">IH", 0x10000, 0x100), b"\0" * 10,
                struct.pack(">9I", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000), b"\0" * 24, struct.pack(">I", 2))
    tkhd = full(b"tkhd", 0, 3, struct.pack(">IIIII", 0, 0, 1, 0, dur), b"\0" * 8, struct.pack(">hhhh", 0, 0, 0, 0),
                struct.pack(">9I", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000), struct.pack(">II", 0, 0))
    mdhd = full(b"mdhd", 0, 0, struct.pack(">IIII", 0, 0, timescale, dur), struct.pack(">HH", 0x55C4, 0))
    hdlr = full(b"hdlr", 0, 0, struct.pack(">I", 0), handler, b"\0" * 12, b"DJI meta\0")
    if text:
        # 3GPP TS 26.245 §5.16: display flags, justification, background colour, box, style record, then a font table box.
        entry = box(entry_type, b"\0" * 6, struct.pack(">H", 1), b"\0" * (4 + 1 + 1 + 4 + 8 + 12), box(b"ftab", struct.pack(">H", 0)))
    else:
        entry = box(entry_type, b"\0" * 6, struct.pack(">H", 1))
    stsd = full(b"stsd", 0, 0, struct.pack(">I", 1), entry)
    stts = full(b"stts", 0, 0, struct.pack(">III", 1, n, delta))
    stsc = full(b"stsc", 0, 0, struct.pack(">IIII", 1, 1, n, 1))
    stsz = full(b"stsz", 0, 0, struct.pack(">II", 0, n), b"".join(struct.pack(">I", len(s)) for s in samples))
    # chunk offset patched after the layout is known
    stco = full(b"stco", 0, 0, struct.pack(">II", 1, 0))
    stbl = box(b"stbl", stsd, stts, stsc, stsz, stco)
    nmhd = full(b"nmhd", 0, 0)
    dinf = box(b"dinf", full(b"dref", 0, 0, struct.pack(">I", 1), full(b"url ", 0, 1)))
    minf = box(b"minf", nmhd, dinf, stbl)
    mdia = box(b"mdia", mdhd, hdlr, minf)
    moov = box(b"moov", mvhd, box(b"trak", tkhd, mdia), box(b"udta", udta) if udta else b"")
    mdat_payload = b"".join(samples)
    data_off = len(ftyp) + len(moov) + 8
    moov = moov.replace(struct.pack(">IIII", 20, 0, 0, 0)[:0] + full(b"stco", 0, 0, struct.pack(">II", 1, 0)), full(b"stco", 0, 0, struct.pack(">II", 1, data_off)))
    return ftyp + moov + box(b"mdat", mdat_payload)


def varint(v):
    if v < 0:
        v += 1 << 64
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def field(num, wt, payload):
    return varint((num << 3) | wt) + payload


def msg(num, *parts):
    body = b"".join(parts)
    return field(num, 2, varint(len(body)) + body)


def num(wire, n, v):
    if wire == "f32":
        return field(n, 5, struct.pack("<f", v))
    if wire == "f64":
        return field(n, 1, struct.pack("<d", v))
    return field(n, 0, varint(int(round(v))))


def string(n, s):
    b = s.encode()
    return field(n, 2, varint(len(b)) + b)


UNITS = 1  # the coordinate-units field of the GPS message


def djmd_sample(wire, i, proto="dvtm_wm265e.proto"):
    """A Mavic-3 style message laid out by the field paths the tag documentation lists for dvtm_wm265e."""
    lat, lon = 52.5 + i * 1e-4, 13.4 + i * 1e-4
    return b"".join([
        msg(1, msg(1, string(1, proto), string(5, "SYNTHETIC-SERIAL"), string(10, "Synthetic Model"))),
        msg(2, msg(2, num("v", 1, 3840), num("v", 2, 2160), num(wire, 3, 29.97))),
        msg(3,
            msg(1, num("v", 1, i + 1), num("v", 2, 1000 * i)),
            msg(2, msg(2, num(wire, 1, 100 + i)), msg(3, num(wire, 1, 1000 + i)), msg(6, num(wire, 1, 1.5))),
            msg(3, msg(3, num(wire, 1, 1.5), num(wire, 2, -2.5), num(wire, 3, 90.5 + i)),
                msg(4, msg(1, num("v", 1, UNITS), num(wire, 2, lat * (1e7 if wire == "varint" else 1)), num(wire, 3, lon * (1e7 if wire == "varint" else 1))),
                    num(wire, 2, 123.456 * (1000 if wire == "varint" else 1))),
                msg(5, num(wire, 1, 12.345 * (1000 if wire == "varint" else 1)))),
            msg(4, msg(3, num(wire, 1, -30.5), num(wire, 2, 0.5), num(wire, 3, 45.25)))),
    ])


def djmd_real_sample(i):
    """The value conventions ExifTool reads back correctly: coordinates as doubles, altitudes as integer millimetres (the relative one may be a
    float), attitude as integer tenths of a degree, ISO and colour temperature as integers."""
    lat, lon = 52.5 + i * 1e-4, 13.4 + i * 1e-4
    return b"".join([
        msg(1, msg(1, string(1, "dvtm_wm265e.proto"), string(5, "SYNTHETIC-SERIAL"), string(10, "Synthetic Model"))),
        msg(3,
            msg(1, num("v", 1, i + 1)),
            msg(2, msg(2, num("v", 1, 100 + i))),
            msg(3, msg(3, num("v", 1, 15), num("v", 2, -25), num("v", 3, 905 + i)),
                msg(4, msg(1, num("v", 1, 1), num("f64", 2, lat), num("f64", 3, lon)), num("v", 2, 123456 + i)),
                msg(5, num("f32", 1, 12345.0 + i))),
            msg(4, msg(3, num("v", 1, -305), num("v", 2, 5), num("v", 3, 452)))),
    ])


def djmd_osmo_sample(i):
    """An Osmo Action style message (dvtm_ac203): GPS at 3-4-2-1, altitude at 3-4-2-2, ISO at 3-2-3-1, colour temperature at 3-2-6-1."""
    lat, lon = -33.8 - i * 1e-4, 151.2 + i * 1e-4
    return b"".join([
        msg(1, msg(1, string(1, "dvtm_ac203.proto"), string(5, "SYNTHETIC-SERIAL"), string(10, "Synthetic Action"))),
        msg(3,
            msg(1, num("v", 1, i + 1)),
            msg(2, msg(3, num("v", 1, 400 + i)), msg(6, num("v", 1, 5600))),
            msg(4, msg(2, msg(1, num("v", 1, 1), num("f64", 2, lat), num("f64", 3, lon)), num("v", 2, 21500 + i)))),
    ])


def klv(key, typ, size, repeat, data):
    if typ == b"c":
        repeat = len(data)  # text: one byte per element
    data = data + b"\0" * (-len(data) % 4)
    return key + typ + bytes([size]) + struct.pack(">H", repeat) + data


def gopro_device_files(out):
    """A GoPro-style file: a gpmd track whose device is named "Fusion", and a udta GPMF atom with the camera model (MINF)."""
    isog = klv(b"ISOG", b"f", 4, 1, struct.pack(">f", 100.0))
    strm_body = klv(b"STNM", b"c", 1, 17, b"ISO sensor gain") + isog
    strm = b"STRM" + b"\0" + bytes([1]) + struct.pack(">H", len(strm_body)) + strm_body
    dev_body = klv(b"DVID", b"L", 4, 1, struct.pack(">I", 1)) + klv(b"DVNM", b"c", 1, 6, b"Fusion") + strm
    devc = b"DEVC" + b"\0" + bytes([1]) + struct.pack(">H", len(dev_body)) + dev_body
    u_body = klv(b"DVID", b"L", 4, 1, struct.pack(">I", 1)) + klv(b"DVNM", b"c", 1, 15, b"Global Settings") + klv(b"MINF", b"c", 1, 11, b"HERO8 Black")
    u_devc = b"DEVC" + b"\0" + bytes([1]) + struct.pack(">H", len(u_body)) + u_body
    atom = box(b"GPMF", u_devc)
    open(os.path.join(out, "gpmf-device.mp4"), "wb").write(mp4(b"gpmd", b"meta", [devc, devc], udta=atom))


def srt_text(i):
    lat, lon = 52.5 + i * 1e-4, 13.4 + i * 1e-4
    return (f'<font size="36">SrtCnt : {i + 1}, DiffTime : 33ms\n2024-01-15 14:30:{22 + i // 10:02d},{(i % 10) * 100:03d}\n'
            f'[iso : {100 + i}] [shutter : 1/{1000 + i}] [fnum : 280] [ev : -0.7] [ct : 5500] [color_md : default] [focal_len : 240] '
            f'[latitude: {lat:.6f}] [longitude: {lon:.6f}] [rel_alt: {10.2 + i:.3f} abs_alt: {142.76 + i:.3f}]</font>').encode()


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    global UNITS
    for u in (0, 2, 3):
        UNITS = u
        for wire in ("f64", "varint"):
            open(os.path.join(out, f"probe-units{u}-{wire}.mp4"), "wb").write(mp4(b"djmd", b"meta", [djmd_sample(wire, 0)]))
    UNITS = 1
    open(os.path.join(out, "djmd-real.mp4"), "wb").write(mp4(b"djmd", b"meta", [djmd_real_sample(i) for i in range(5)]))
    open(os.path.join(out, "djmd-osmo.mp4"), "wb").write(mp4(b"djmd", b"meta", [djmd_osmo_sample(i) for i in range(5)]))
    for wire in ("f32", "f64", "varint"):
        samples = [djmd_sample(wire, i) for i in range(5)]
        open(os.path.join(out, f"djmd-{wire}.mp4"), "wb").write(mp4(b"djmd", b"meta", samples))
    gopro_device_files(out)
    texts = [srt_text(i) for i in range(5)]
    blocks = []
    for i, t in enumerate(texts):
        a, b = i * 33, (i + 1) * 33
        blocks.append(f"{i + 1}\n00:00:00,{a:03d} --> 00:00:00,{b:03d}\n".encode() + t + b"\n\n")
    open(os.path.join(out, "dji-text.srt"), "wb").write(b"".join(blocks))
    # One block per layout family the descriptions list: bracketed pairs, GPS() with BAROMETER, unit suffix, compact line, decimal units.
    families = [
        "[latitude: 59.302335] [longitude: 18.203059] [rel_alt: 1.300 abs_alt: 132.860] [iso : 2700] [shutter : 1/30.0] [fnum : 170]",
        "GPS(39.906217,116.391305,69.800) BAROMETER(91.2)\nHOME(39.906206,116.391400) D=5.2m H=1.5m",
        "GPS(36.6146,-6.1120,0.0M) BAROMETER:0.3M",
        "F/5.6, SS 400, ISO 100, EV 0, GPS (-58.851745, -34.237922, 15), HOME (-58.847509, -34.232707, -57.98m), D 698.70m, H 85.80m, H.S 0.00m/s, V.S 0.00m/s, F.PRY (2.7°, -7.0°, 110.1°), G.PRY (-24.4°, 0.0°, 110.4°)",
        '<font size="28">FrameCnt: 1, DiffTime: 40ms\n2026-05-27 13:14:22.911\n[iso: 200] [shutter: 1/6400.0] [fnum: 1.8] [ev: -0.7] [color_md: dlog_m] [focal_len: 24.00] [latitude: 53.365108] [longitude: 6.460719] [rel_alt: 6.000 abs_alt: -122.769] [ct: 5262, tint: 0] [gb_yaw: 10.5 gb_pitch: -20.25 gb_roll: 0.0]</font>',
    ]
    blocks = [f"{i + 1}\n00:00:0{i},000 --> 00:00:0{i + 1},000\n{t}\n\n".encode() for i, t in enumerate(families)]
    open(os.path.join(out, "dji-formats.srt"), "wb").write(b"".join(blocks))
    samples = [struct.pack(">H", len(t)) + t for t in texts]
    open(os.path.join(out, "dji-text.mp4"), "wb").write(mp4(b"tx3g", b"sbtl", samples, text=True))


if __name__ == "__main__":
    main()

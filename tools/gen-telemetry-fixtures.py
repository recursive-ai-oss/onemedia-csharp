#!/usr/bin/env python3
"""Writes the small synthetic telemetry fixtures in testdata/telemetry/ and, with --long, a large GoPro-style file for memory checks.

Everything is made up from the published format descriptions (GoPro's GPMF page, Google's CAMM page, Apple's QuickTime file format
documentation); no real device data is involved. The fixtures:

  gpmf-synthetic.mp4   gpmd track: two 1 s payloads with ACCL, GPS5 (+GPSU/GPSF/GPSP), a GPS9-style complex stream, SHUT, an unknown key,
                       a nested FACE-style block, a device-level text field, and a third payload cut off inside a triplet.
  camm-all-types.mp4   camm track: one sample of each packet type 0-7 and an unknown type 9.
  mebx-synthetic.mp4   mebx track: location text, orientation, still-image-time and a nested detected-face style group.

usage: gen-telemetry-fixtures.py <output dir> [--long <file> <payload count>]
"""
import math
import struct
import sys


def box(kind, payload=b''):
    return struct.pack('>I', 8 + len(payload)) + kind + payload


def full(kind, version, flags, payload=b''):
    return box(kind, struct.pack('>I', (version << 24) | flags) + payload)


def mp4(track_timescale, handler, entry, samples, durations=None):
    """One-track MP4: ftyp, mdat, moov. `entry` is the complete sample entry box."""
    ftyp = box(b'ftyp', b'isom' + struct.pack('>I', 512) + b'isomiso2mp41')
    mdat_payload = b''.join(samples)
    mdat = box(b'mdat', mdat_payload)
    first = len(ftyp) + 8
    durations = durations or [track_timescale] * len(samples)
    total = sum(durations)
    mvhd = full(b'mvhd', 0, 0, struct.pack('>IIII', 0, 0, 1000, total * 1000 // track_timescale) + struct.pack('>IH', 0x00010000, 0x0100) + b'\0' * 10
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + b'\0' * 24 + struct.pack('>I', 2))
    tkhd = full(b'tkhd', 0, 3, struct.pack('>IIIII', 0, 0, 1, 0, total * 1000 // track_timescale) + b'\0' * 8 + struct.pack('>HHHH', 0, 0, 0, 0)
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + struct.pack('>II', 0, 0))
    mdhd = full(b'mdhd', 0, 0, struct.pack('>IIIIHH', 0, 0, track_timescale, total, 0x55C4, 0))
    hdlr = full(b'hdlr', 0, 0, struct.pack('>I', 0) + handler + b'\0' * 12 + b'Telemetry\0')
    nmhd = full(b'nmhd', 0, 0)
    dinf = box(b'dinf', full(b'dref', 0, 0, struct.pack('>I', 1) + full(b'url ', 0, 1)))
    stsd = full(b'stsd', 0, 0, struct.pack('>I', 1) + entry)
    stts = full(b'stts', 0, 0, struct.pack('>I', len(durations)) + b''.join(struct.pack('>II', 1, d) for d in durations))
    stsc = full(b'stsc', 0, 0, struct.pack('>IIII', 1, 1, 1, 1))
    stsz = full(b'stsz', 0, 0, struct.pack('>II', 0, len(samples)) + b''.join(struct.pack('>I', len(s)) for s in samples))
    offsets, pos = [], first
    for s in samples:
        offsets.append(pos)
        pos += len(s)
    stco = full(b'stco', 0, 0, struct.pack('>I', len(offsets)) + b''.join(struct.pack('>I', o) for o in offsets))
    stbl = box(b'stbl', stsd + stts + stsc + stsz + stco)
    minf = box(b'minf', nmhd + dinf + stbl)
    trak = box(b'trak', tkhd + box(b'mdia', mdhd + hdlr + minf))
    return ftyp + mdat + box(b'moov', mvhd + trak)


def entry_box(kind, body=b''):
    return box(kind, b'\0' * 6 + struct.pack('>H', 1) + body)


# ---- GPMF ----------------------------------------------------------------------------------------------------------------------------

def klv(key, typ, size, repeat, data):
    pad = (-len(data)) % 4
    return key + (typ if isinstance(typ, bytes) else bytes([typ])) + bytes([size]) + struct.pack('>H', repeat) + data + b'\0' * pad


def nested(key, *children):
    data = b''.join(children)
    assert len(data) % 4 == 0
    return klv(key, 0, 4, len(data) // 4, data)


def text(key, s, enc='latin1'):
    b = s.encode(enc)
    return klv(key, b'c', len(b), 1, b)


def gpmf_payload(index, truncate=False):
    n = 40
    accl = b''.join(struct.pack('>hhh', int(980 * math.sin(i / 7 + index)), int(200 * math.cos(i / 5)), 100 + i) for i in range(n))
    gps5 = b''.join(struct.pack('>lllll', 331264968 + i * 3 + index * 100, -1173273541 - i * 5, -20102 + i * 2, 640 + i, 684 + i) for i in range(9))
    gps9 = b''.join(struct.pack('>lllllllHH', 331264968 + i, -1173273541, 99000 + i, 500, 510, 8000 + index, 5520000 + i * 100 + index * 1000, 120 + i, 3) for i in range(3))
    unknown = klv(b'xyzw', b'B', 3, 2, bytes([1, 2, 3, 4, 5, 6]))
    face = nested(b'FACE', klv(b'TICK', b'L', 4, 1, struct.pack('>I', 10023)), klv(b'TOCK', b'L', 4, 1, struct.pack('>I', 10320)),
                  klv(b'FACE', b'f', 16, 2, struct.pack('>ffff', .1, .2, .3, .4) + struct.pack('>ffff', .5, .6, .7, .8)))
    streams = [
        nested(b'STRM', klv(b'TSMP', b'L', 4, 1, struct.pack('>I', n * (index + 1))), text(b'STNM', 'Accelerometer'), klv(b'SIUN', b'c', 4, 1, 'm/s\xb2'.encode('latin1')),
                klv(b'SCAL', b's', 2, 1, struct.pack('>h', 418)), text(b'ORIN', 'ZXY'), klv(b'ACCL', b's', 6, n, accl)),
        nested(b'STRM', text(b'STNM', 'GPS (Lat., Long., Alt., 2D speed, 3D speed)'), klv(b'UNIT', b'c', 4, 5, b'deg\0deg\0m\0\0\0m/s\0m/s\0'),
                klv(b'SCAL', b'l', 4, 5, struct.pack('>5l', 10000000, 10000000, 1000, 1000, 100)), klv(b'GPS5', b'l', 20, 9, gps5),
                klv(b'GPSU', b'U', 16, 1, b'170417173103.000' if index == 0 else b'170417173104.000'), klv(b'GPSF', b'L', 4, 1, struct.pack('>I', 3)),
                klv(b'GPSP', b'S', 2, 1, struct.pack('>H', 606))),
        nested(b'STRM', text(b'STNM', 'GPS9'), klv(b'TYPE', b'c', 1, 9, b'lllllllSS'),
                klv(b'SCAL', b'l', 4, 9, struct.pack('>9l', 10000000, 10000000, 1000, 1000, 100, 1, 1000, 100, 1)), klv(b'GPS9', b'?', 32, 3, gps9)),
        nested(b'STRM', text(b'STNM', 'Exposure time (shutter speed)'), klv(b'SIUN', b'c', 1, 1, b's'), klv(b'SHUT', b'f', 4, 3, struct.pack('>3f', 1 / 1000, 1 / 960, 1 / 500))),
        nested(b'STRM', text(b'STNM', 'Faces'), klv(b'TYPE', b'c', 1, 4, b'ffff'), face),
        nested(b'STRM', text(b'STNM', 'Custom'), unknown),
    ]
    dev = nested(b'DEVC', klv(b'DVID', b'L', 4, 1, struct.pack('>I', 1)), text(b'DVNM', 'Camera'), text(b'MINF', 'SYNTHETIC CAMERA'), *streams)
    if truncate:
        return dev[:len(dev) - 70]
    return dev


def gpmf_file(path):
    payloads = [gpmf_payload(0), gpmf_payload(1), gpmf_payload(2, truncate=True)]
    open(path, 'wb').write(mp4(1000, b'meta', entry_box(b'gpmd'), payloads, [1001, 1001, 1001]))


def gpmf_long(path, count):
    """A long GoPro-like file: `count` one-second payloads of ACCL (200 Hz), GYRO (400 Hz) and GPS5 (18 Hz), about 8 KB each."""
    payloads = []
    for k in range(count):
        accl = b''.join(struct.pack('>hhh', (i * 13 + k) % 4000 - 2000, (i * 7) % 900, (i * 3) % 500) for i in range(200))
        gyro = b''.join(struct.pack('>hhh', (i * 5 + k) % 800 - 400, (i * 11) % 300, (i * 17) % 200) for i in range(400))
        gps5 = b''.join(struct.pack('>lllll', 331264968 + i + k, -1173273541 - i, -20102, 640, 684) for i in range(18))
        utc = ('170417%02d%02d%02d.000' % ((k // 3600) % 24, (k // 60) % 60, k % 60)).encode()
        streams = [
            nested(b'STRM', text(b'STNM', 'Accelerometer'), klv(b'SIUN', b'c', 4, 1, 'm/s\xb2'.encode('latin1')), klv(b'SCAL', b's', 2, 1, struct.pack('>h', 418)), klv(b'ACCL', b's', 6, 200, accl)),
            nested(b'STRM', text(b'STNM', 'Gyroscope'), klv(b'SCAL', b's', 2, 1, struct.pack('>h', 938)), klv(b'GYRO', b's', 6, 400, gyro)),
            nested(b'STRM', text(b'STNM', 'GPS'), klv(b'SCAL', b'l', 4, 5, struct.pack('>5l', 10000000, 10000000, 1000, 1000, 100)), klv(b'GPS5', b'l', 20, 18, gps5),
                    klv(b'GPSU', b'U', 16, 1, utc), klv(b'GPSF', b'L', 4, 1, struct.pack('>I', 3)), klv(b'GPSP', b'S', 2, 1, struct.pack('>H', 150))),
        ]
        payloads.append(nested(b'DEVC', klv(b'DVID', b'L', 4, 1, struct.pack('>I', 1)), text(b'DVNM', 'Camera'), *streams))
    open(path, 'wb').write(mp4(1000, b'meta', entry_box(b'gpmd'), payloads, [1000] * count))


# ---- CAMM ----------------------------------------------------------------------------------------------------------------------------

def camm_file(path):
    def pkt(t, body):
        return struct.pack('<HH', 0, t) + body
    gps_epoch = 1_272_000_000.5  # seconds since 1980-01-06, a date in 2020
    samples = [
        pkt(0, struct.pack('<3f', 0.0, 0.5, 0.25)),
        pkt(1, struct.pack('<ii', 8_000_000, 25_000_000)),
        pkt(2, struct.pack('<3f', 0.125, -0.25, 0.5)),
        pkt(3, struct.pack('<3f', 0.0, 9.8125, 0.25)),
        pkt(4, struct.pack('<3f', 1.5, 2.5, -3.5)),
        pkt(5, struct.pack('<ddd', 47.6205, -122.3493, 184.5)),
        pkt(6, struct.pack('<didd', gps_epoch, 3, 47.6205, -122.3493) + struct.pack('<7f', 184.5, 3.5, 4.5, 1.0, 2.0, 0.5, 0.25)),
        pkt(7, struct.pack('<3f', 22.5, -41.25, 18.0)),
        pkt(9, b'\x01\x02\x03\x04\x05\x06\x07\x08'),
        pkt(2, b'\x00\x00\x00'),  # too short: a typed defect
    ]
    open(path, 'wb').write(mp4(1000, b'meta', entry_box(b'camm'), samples, [100] * len(samples)))


# ---- mebx ----------------------------------------------------------------------------------------------------------------------------

def mebx_key(local_id, name, dtype):
    keyd = box(b'keyd', b'mdta' + name.encode())
    dtyp = box(b'dtyp', struct.pack('>II', 0, dtype))
    return struct.pack('>I', 8 + len(keyd) + len(dtyp)) + struct.pack('>I', local_id) + keyd + dtyp


def mebx_item(local_id, data):
    return struct.pack('>II', 8 + len(data), local_id) + data


def mebx_file(path):
    keys = [(1, 'com.apple.quicktime.location.ISO6709', 1), (2, 'com.apple.quicktime.video-orientation', 66), (3, 'com.apple.quicktime.still-image-time', 65),
            (4, 'com.apple.quicktime.detected-face', 28), (5, 'com.apple.quicktime.detected-face.bounds', 72), (6, 'com.apple.quicktime.detected-face.face-id', 67),
            (7, 'com.apple.quicktime.detected-face.roll-angle', 23), (8, 'com.apple.quicktime.live-photo.vitality-score', 23), (9, 'com.example.note', 1)]
    keys_box = box(b'keys', b''.join(mebx_key(*k) for k in keys))
    entry = entry_box(b'mebx', keys_box)
    cits = box(b'cits', mebx_item(5, struct.pack('>4f', .25, .125, .5, .5)) + mebx_item(6, struct.pack('>i', 7)) + mebx_item(7, struct.pack('>f', 0.5)))
    face = mebx_item(4, box(b'crec', cits))
    samples = [
        mebx_item(1, b'+47.6205-122.3493+184.500/') + mebx_item(2, struct.pack('>h', 90)) + mebx_item(8, struct.pack('>f', 0.75)),
        mebx_item(1, b'+47.6206-122.3494/') + face + mebx_item(9, b'hello'),
        mebx_item(3, struct.pack('>b', -1)) + mebx_item(0, b'') + mebx_item(0xFFFFFFFF, b'\x01\x02'),
        mebx_item(1, b'not a location') + mebx_item(2, b'\x01'),  # a wrong length and unparsable text stay raw
    ]
    open(path, 'wb').write(mp4(600, b'meta', entry, samples, [600, 600, 600, 600]))


def main():
    out = sys.argv[1]
    if len(sys.argv) > 2 and sys.argv[2] == '--long':
        gpmf_long(sys.argv[3], int(sys.argv[4]))
        return
    gpmf_file(out + '/gpmf-synthetic.mp4')
    camm_file(out + '/camm-all-types.mp4')
    mebx_file(out + '/mebx-synthetic.mp4')


main()

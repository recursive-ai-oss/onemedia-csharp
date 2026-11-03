#!/usr/bin/env python3
"""Writes the synthetic privacy-strip fixture testdata/strip/strip-mixed.mp4.

Everything is made up from the published format descriptions; the coordinates are over open water and the serial numbers are invented. One file
carries every place the strip looks at in an ISO base media file:

  moov/udta   (c)xyz, loci, LENS, CAME, MUID, GUMI (GoPro-style atoms), GPMF (device-level KLV with CASN, MUID, LINF, CINF), QVMI (a TIFF block with a GPS IFD
              and a body serial number), and make/model-style atoms that must stay ((c)mak, (c)mod)
  moov/meta   QuickTime key table with location.ISO6709, content.identifier and a model key that stays
  uuid        an XMP packet with exif:GPS*, exif:BodySerialNumber, aux:SerialNumber, a Dublin Core title that stays
  trak 1      gpmd: GPS5, GPSU, GPSF, GPSP and CASN, plus an accelerometer stream that stays
  trak 2      camm: type 6 (GPS), type 5 (location), type 1 (exposure, stays)
  trak 3      mebx: location text and an orientation value that stays
  trak 4      fdsc: an unlabelled blob that repeats the lens serial, the camera serial, its hash and the position

usage: gen-strip-fixtures.py <output dir> | --big <file> <gibibytes> | --seeds <testdata/fuzz dir>
"""
import struct
import sys

LAT_LON = b'+47.6205-122.3493+184.500/'
LENS_SERIAL = b'LXX0000000000001'
CAMERA_SERIAL = b'C0000000000042'
CAMERA_HASH = bytes(range(0xA0, 0xB0))
MEDIA_UID = bytes(range(0x10, 0x30))
FIRMWARE = b'FW9.99.01.00.00'  # 15 characters, as on the models the settings layout was checked on
BLOB_ONLY_SERIAL = b'C9999999999999'  # appears only in the unlabelled settings block


def box(kind, payload=b''):
    return struct.pack('>I', 8 + len(payload)) + kind + payload


def full(kind, version, flags, payload=b''):
    return box(kind, struct.pack('>I', (version << 24) | flags) + payload)


def klv(key, typ, size, repeat, data):
    pad = (-len(data)) % 4
    return key + typ + bytes([size]) + struct.pack('>H', repeat) + data + b'\0' * pad


def nested(key, *children):
    data = b''.join(children)
    return klv(key, b'\0', 4, len(data) // 4, data)


def text(key, s):
    return klv(key, b'c', len(s), 1, s)


def gpmf_sample(second):
    gps5 = b''.join(struct.pack('>lllll', 476205000 + i * 3 + second * 100, -1223493000 - i * 5, 184500 + i, 640 + i, 684 + i) for i in range(9))
    accl = b''.join(struct.pack('>hhh', 100 + i, 200, 300) for i in range(8))
    return nested(b'DEVC', klv(b'DVID', b'L', 4, 1, struct.pack('>I', 1)), text(b'DVNM', b'Synthetic Camera'), text(b'CASN', CAMERA_SERIAL),
                  nested(b'STRM', text(b'STNM', b'GPS'), klv(b'SCAL', b'l', 4, 5, struct.pack('>5l', 10000000, 10000000, 1000, 1000, 100)),
                         klv(b'GPS5', b'l', 20, 9, gps5), klv(b'GPSU', b'U', 16, 1, b'240101120000.000'),
                         klv(b'GPSF', b'L', 4, 1, struct.pack('>I', 3)), klv(b'GPSP', b'S', 2, 1, struct.pack('>H', 150))),
                  nested(b'STRM', text(b'STNM', b'Accelerometer'), klv(b'SCAL', b's', 2, 1, struct.pack('>h', 418)), klv(b'ACCL', b's', 6, 8, accl)))


def udta_gpmf():
    return nested(b'DEVC', text(b'MINF', b'SYNTHETIC ONE'), text(b'CASN', CAMERA_SERIAL), klv(b'MUID', b'L', 4, 8, MEDIA_UID),
                  text(b'LINF', LENS_SERIAL), klv(b'CINF', b'B', 1, 16, CAMERA_HASH))


def camm_sample(kind):
    if kind == 6:
        body = struct.pack('<didd', 1_272_000_000.5, 3, 47.6205, -122.3493) + struct.pack('<d', 184.5) + struct.pack('<7f', 1, 2, 3, 4, 5, 6, 7)
    elif kind == 5:
        body = struct.pack('<ddd', 47.6205, -122.3493, 184.5)
    else:
        body = struct.pack('<ii', 1000, 2000)
    return struct.pack('<HH', 0, kind) + body


def mebx_key(local_id, name, data_type):
    keyd = box(b'keyd', b'mdta' + name.encode())
    dtyp = box(b'dtyp', struct.pack('>II', 0, data_type))
    return box(struct.pack('>I', local_id), keyd + dtyp)


def mebx_item(local_id, value):
    return box(struct.pack('>I', local_id), value)


def tiff():
    """Little-endian TIFF: IFD0 with Make, an Exif IFD pointer and a GPS IFD pointer; Exif IFD with BodySerialNumber; GPS IFD with latitude."""
    le = '<'
    make = b'Synthetic\0'
    serial = b'EXIFSERIAL000042\0'
    # layout: header 8 | IFD0 at 8 (3 entries: 2+36+4=42) | Make at 50 | ExifIFD at 60 (1 entry: 2+12+4=18) | serial at 78 | GPS IFD at 96 (2 entries: 2+24+4=30) | lat at 126 (24 bytes)
    ifd0 = struct.pack(le + 'H', 3)
    ifd0 += struct.pack(le + 'HHII', 0x010F, 2, len(make), 50)
    ifd0 += struct.pack(le + 'HHII', 0x8769, 4, 1, 60)
    ifd0 += struct.pack(le + 'HHII', 0x8825, 4, 1, 96) + struct.pack(le + 'I', 0)
    exif = struct.pack(le + 'H', 1) + struct.pack(le + 'HHII', 0xA431, 2, len(serial), 78) + struct.pack(le + 'I', 0)
    gps = struct.pack(le + 'H', 2) + struct.pack(le + 'HHI', 1, 2, 2) + b'N\0\0\0' + struct.pack(le + 'HHII', 2, 5, 3, 126) + struct.pack(le + 'I', 0)
    lat = struct.pack(le + '6I', 47, 1, 37, 1, 1800, 100)
    data = b'II*\0' + struct.pack(le + 'I', 8) + ifd0
    assert len(data) == 50, len(data)
    data += make.ljust(10, b'\0') + exif
    assert len(data) == 78, len(data)
    data += serial.ljust(18, b'\0')
    assert len(data) == 96, len(data)
    data += gps
    assert len(data) == 126, len(data)
    return data + lat


def xmp_packet(total=1024):
    body = ('<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>\n<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            '<rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:aux="http://ns.adobe.com/exif/1.0/aux/" xmlns:dc="http://purl.org/dc/elements/1.1/" '
            'exif:GPSLatitude="47,37.230N" exif:GPSLongitude="122,29.358W" exif:BodySerialNumber="XMPBODY0000042">'
            '<aux:SerialNumber>XMPAUX0000042</aux:SerialNumber><dc:title><rdf:Alt><rdf:li xml:lang="x-default">Kept title</rdf:li></rdf:Alt></dc:title>'
            '</rdf:Description></rdf:RDF></x:xmpmeta>').encode('utf-8')
    tail = b'<?xpacket end="w"?>'
    pad = total - len(body) - len(tail)
    return body + b' ' * pad + tail


def data_item(index, value):
    return box(struct.pack('>I', index), box(b'data', struct.pack('>II', 1, 0) + value))


def udta_text(kind, value):
    return box(kind, struct.pack('>HH', len(value), 0x15C7) + value)


def loci():
    # 3GPP TS 26.244 8.3: version/flags, language, name, role, longitude, latitude, altitude (16.16 fixed), body, notes
    return full(b'loci', 0, 0, struct.pack('>H', 0x15C7) + b'Somewhere\0' + b'\0' + struct.pack('>lll', int(-122.3493 * 65536), int(47.6205 * 65536), int(184.5 * 65536)) + b'\0' + b'\0')


def track(track_id, handler, entry, samples, timescale=1000):
    total = timescale * len(samples)
    tkhd = full(b'tkhd', 0, 3, struct.pack('>IIIII', 0, 0, track_id, 0, total) + b'\0' * 8 + struct.pack('>HHHH', 0, 0, 0, 0)
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + struct.pack('>II', 0, 0))
    mdhd = full(b'mdhd', 0, 0, struct.pack('>IIIIHH', 0, 0, timescale, total, 0x55C4, 0))
    hdlr = full(b'hdlr', 0, 0, struct.pack('>I', 0) + handler + b'\0' * 12 + b'Strip fixture\0')
    dinf = box(b'dinf', full(b'dref', 0, 0, struct.pack('>I', 1) + full(b'url ', 0, 1)))
    stsd = full(b'stsd', 0, 0, struct.pack('>I', 1) + entry)
    stts = full(b'stts', 0, 0, struct.pack('>III', 1, len(samples), timescale))
    stsc = full(b'stsc', 0, 0, struct.pack('>IIII', 1, 1, 1, 1))
    stsz = full(b'stsz', 0, 0, struct.pack('>II', 0, len(samples)) + b''.join(struct.pack('>I', len(s)) for s in samples))
    return {'id': track_id, 'samples': samples, 'parts': (tkhd, mdhd, hdlr, dinf, stsd, stts, stsc, stsz)}


def entry_box(kind, body=b''):
    return box(kind, b'\0' * 6 + struct.pack('>H', 1) + body)


def build(hole=0, big_path=None):
    """The fixture bytes; with hole > 0 the media data box is a 64-bit-size box that is `hole` bytes longer (zeros), written sparsely to big_path."""
    ftyp = box(b'ftyp', b'isom' + struct.pack('>I', 512) + b'isomiso2mp41')
    keys = [(1, 'com.apple.quicktime.location.ISO6709', 1), (2, 'com.apple.quicktime.video-orientation', 66)]
    mebx_entry = entry_box(b'mebx', box(b'keys', b''.join(mebx_key(*k) for k in keys)))
    fdsc_blob = (b'GPRO' + b'\0' * 4 + FIRMWARE + LENS_SERIAL.ljust(48, b'\0') + CAMERA_HASH + BLOB_ONLY_SERIAL.ljust(15, b'\0') + b'SYNTHETIC ONE\0' + b'\0' * 20
                 + LAT_LON + b'\0' * 16)
    tracks = [
        track(1, b'meta', entry_box(b'gpmd'), [gpmf_sample(0), gpmf_sample(1)]),
        track(2, b'meta', entry_box(b'camm'), [camm_sample(6), camm_sample(5), camm_sample(1)]),
        track(3, b'meta', mebx_entry, [mebx_item(1, LAT_LON) + mebx_item(2, struct.pack('>h', 90)), mebx_item(1, b'+47.6206-122.3494/')]),
        track(4, b'meta', entry_box(b'fdsc'), [fdsc_blob]),
    ]
    mdat_payload = b''
    mdat_header = 16 if hole else 8
    first = len(ftyp) + mdat_header
    trak_boxes = b''
    pos = first
    for t in tracks:
        offsets = []
        for s in t['samples']:
            offsets.append(pos)
            pos += len(s)
            mdat_payload += s
        tkhd, mdhd, hdlr, dinf, stsd, stts, stsc, stsz = t['parts']
        stco = full(b'stco', 0, 0, struct.pack('>I', len(offsets)) + b''.join(struct.pack('>I', o) for o in offsets))
        stbl = box(b'stbl', stsd + stts + stsc + stsz + stco)
        trak_boxes += box(b'trak', tkhd + box(b'mdia', mdhd + hdlr + box(b'minf', full(b'nmhd', 0, 0) + dinf + stbl)))
    mdat = box(b'mdat', mdat_payload)
    if hole:
        mdat = struct.pack('>I', 1) + b'mdat' + struct.pack('>Q', 16 + len(mdat_payload) + hole) + mdat_payload
    mvhd = full(b'mvhd', 0, 0, struct.pack('>IIII', 0, 0, 1000, 4000) + struct.pack('>IH', 0x00010000, 0x0100) + b'\0' * 10
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + b'\0' * 24 + struct.pack('>I', 5))
    udta = box(b'udta', udta_text(b'\xa9xyz', LAT_LON) + udta_text(b'\xa9mak', b'Synthetic') + udta_text(b'\xa9mod', b'ONE') + loci()
               + box(b'FIRM', FIRMWARE) + box(b'LENS', LENS_SERIAL + b'\0' * 40) + box(b'CAME', CAMERA_HASH) + box(b'MUID', CAMERA_HASH + b'\0' * 16) + box(b'GUMI', CAMERA_HASH)
               + box(b'GPMF', udta_gpmf()) + box(b'QVMI', b'Exif\0\0' + tiff()))
    meta = box(b'meta', full(b'hdlr', 0, 0, struct.pack('>I', 0) + b'mdta' + b'\0' * 12 + b'\0')
               + full(b'keys', 0, 0, struct.pack('>I', 3) + b''.join(box(b'mdta', k.encode()) for k in
                   ('com.apple.quicktime.location.ISO6709', 'com.apple.quicktime.content.identifier', 'com.apple.quicktime.model')))
               + box(b'ilst', data_item(1, LAT_LON) + data_item(2, b'AAAA-BBBB-CCCC-0042') + data_item(3, b'Synthetic Model')))
    moov = box(b'moov', mvhd + trak_boxes + udta + meta)
    xmp = box(b'uuid', bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC') + xmp_packet())
    if hole:
        with open(big_path, 'wb') as f:
            f.write(ftyp + mdat)
            f.seek(hole, 1)
            f.write(moov + xmp)
        return None
    return ftyp + mdat + moov + xmp


def write_seeds(root):
    """Fuzz seeds: strip-file is a selector byte plus the fixture; strip-bytes is a selector byte, a key-length byte (0) and one block."""
    import os
    for name in ('strip-bytes', 'strip-file'):
        os.makedirs(os.path.join(root, name), exist_ok=True)
    open(os.path.join(root, 'strip-file', 'mixed.seed'), 'wb').write(b'\x00' + build())
    # The DJI, AVCHD and text-track fixtures of the telemetry tests are seeds for strip-file too.
    tele = os.path.join(os.path.dirname(os.path.abspath(root)), 'telemetry')
    for fixture in ('dji-text.mp4', 'djmd-real.mp4', 'djmd-osmo.mp4', 'djmd-unknown.mp4', 'mdpm-gps.mts'):
        path = os.path.join(tele, fixture)
        if os.path.exists(path):
            open(os.path.join(root, 'strip-file', fixture + '.seed'), 'wb').write(b'\x00' + open(path, 'rb').read())
    for name, block in (('gpmf', gpmf_sample(0)), ('camm6', camm_sample(6)), ('tiff', b'Exif\0\0' + tiff()), ('xmp', xmp_packet(512))):
        open(os.path.join(root, 'strip-bytes', name + '.seed'), 'wb').write(b'\x00\x00' + block)


if __name__ == '__main__':
    import os
    if len(sys.argv) > 2 and sys.argv[1] == '--seeds':
        write_seeds(sys.argv[2])
        sys.exit(0)
    if len(sys.argv) > 3 and sys.argv[1] == '--big':
        # --big <file> <gibibytes>: a sparse file with the same content and a media data box that many GiB long
        build(int(float(sys.argv[3]) * (1 << 30)), sys.argv[2])
    else:
        os.makedirs(sys.argv[1], exist_ok=True)
        open(os.path.join(sys.argv[1], 'strip-mixed.mp4'), 'wb').write(build())

#!/usr/bin/env python3
"""Writes adversarial privacy-strip inputs for review: every file hides the same invented position and serial numbers in a different place or
encoding. The values are made up (open water, invented ids). Nothing here comes from a real recording.

usage: gen-adversarial.py <output dir>
Needs ffmpeg on PATH only for the --media cases (WAV/FLAC/MP3/Ogg/MKV bases); pass --no-ffmpeg to skip them.
"""
import importlib.util
import os
import struct
import subprocess
import sys

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('fx', os.path.join(here, '..', '..', 'gen-strip-fixtures.py'))
fx = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fx)
box, full, klv, nested, text = fx.box, fx.full, fx.klv, fx.nested, fx.text

LAT, LON, ALT = '47.620512', '-122.349312', '184.5'
SN = b'ZQ9X7R5T3M1K'
OWNER = b'OWNR-ALPHA-7731'
POS = b'+47.620512-122.349312+184.5/'
OUT = {}


def out(name, data):
    OUT[name] = data


def assemble(tracks=(), udta=b'', moov_meta=b'', moov_extra=b'', top_extra=b'', trak_extra=None, pre_top=b''):
    ftyp = box(b'ftyp', b'isom' + struct.pack('>I', 512) + b'isomiso2mp41')
    pos = len(ftyp) + 8 + len(pre_top)
    payload = b''
    traks = b''
    trak_extra = trak_extra or {}
    for t in tracks:
        offs = []
        for s in t['samples']:
            offs.append(pos)
            pos += len(s)
            payload += s
        tkhd, mdhd, hdlr, dinf, stsd, stts, stsc, stsz = t['parts']
        stco = full(b'stco', 0, 0, struct.pack('>I', len(offs)) + b''.join(struct.pack('>I', o) for o in offs))
        stbl = box(b'stbl', stsd + stts + stsc + stsz + stco)
        traks += box(b'trak', tkhd + trak_extra.get(t['id'], b'') + box(b'mdia', mdhd + hdlr + box(b'minf', full(b'nmhd', 0, 0) + dinf + stbl)))
    if not payload:
        payload = b'\0' * 16
    mvhd = full(b'mvhd', 0, 0, struct.pack('>IIII', 0, 0, 1000, 4000) + struct.pack('>IH', 0x00010000, 0x0100) + b'\0' * 10
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + b'\0' * 24 + struct.pack('>I', 5))
    moov = box(b'moov', mvhd + traks + udta + moov_meta + moov_extra)
    return ftyp + pre_top + box(b'mdat', payload) + moov + top_extra


def ut(kind, value):
    return box(kind, struct.pack('>HH', len(value), 0x15C7) + value)


def di(index, value, typ=1):
    return box(struct.pack('>I', index), box(b'data', struct.pack('>II', typ, 0) + value))


def mdta_meta(items, full_meta=True):
    """items: list of (key, value bytes, data type). QuickTime keyed metadata; full_meta False writes the plain QuickTime form (no version/flags)."""
    keys = full(b'keys', 0, 0, struct.pack('>I', len(items)) + b''.join(box(b'mdta', k.encode()) for k, _, _ in items))
    ilst = box(b'ilst', b''.join(di(i + 1, v, t) for i, (_, v, t) in enumerate(items)))
    hdlr = full(b'hdlr', 0, 0, struct.pack('>I', 0) + b'mdta' + b'\0' * 12 + b'\0')
    if full_meta:
        return box(b'meta', hdlr + keys + ilst)
    return box(b'meta', hdlr + keys + ilst)  # same payload: the plain form is produced by plain_meta below


def plain_meta(items):
    keys = full(b'keys', 0, 0, struct.pack('>I', len(items)) + b''.join(box(b'mdta', k.encode()) for k, _, _ in items))
    ilst = box(b'ilst', b''.join(di(i + 1, v, t) for i, (_, v, t) in enumerate(items)))
    hdlr = full(b'hdlr', 0, 0, struct.pack('>I', 0) + b'mdta' + b'\0' * 12 + b'\0')
    return box(b'meta', hdlr + keys + ilst)


def itunes_meta(items):
    """items: (4-byte key, value)."""
    hdlr = full(b'hdlr', 0, 0, struct.pack('>I', 0) + b'mdir' + b'appl' + b'\0' * 9)
    ilst = box(b'ilst', b''.join(box(k, box(b'data', struct.pack('>II', 1, 0) + v)) for k, v in items))
    return full(b'meta', 0, 0, hdlr[0:0] + hdlr + ilst)


def xmp_uuid(xml, total=2048, utf16=False):
    pre = b'<?xpacket begin="\xef\xbb\xbf" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
    tail = b'<?xpacket end="w"?>'
    body = xml.encode('utf-8')
    pad = total - len(pre) - len(body) - len(tail)
    return box(b'uuid', bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC') + pre + body + b' ' * max(pad, 0) + tail)


XMP_HEAD = '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
XMP_TAIL = '</rdf:RDF></x:xmpmeta>'


def tiff(order, gps=True, serial_inline=False, ifd1_gps=False, gps_from_exif=False):
    """TIFF with IFD0 (Make, Exif ptr, GPS ptr), Exif IFD (BodySerialNumber, LensSerialNumber, CameraOwnerName, MakerNote), GPS IFD with lat/lon/alt rationals,
    optionally an IFD1 with its own GPS pointer."""
    e = '<' if order == 'II' else '>'
    P = lambda f, *a: struct.pack(e + f, *a)
    blobs = []

    def ifd(entries, next_ifd, base):
        n = len(entries)
        size = 2 + 12 * n + 4
        extra = b''
        raw = P('H', n)
        for tag, typ, count, val in sorted(entries, key=lambda x: x[0]):
            if isinstance(val, bytes):
                if len(val) <= 4:
                    raw += P('HHI', tag, typ, count) + val.ljust(4, b'\0')
                else:
                    raw += P('HHII', tag, typ, count, base + size + len(extra))
                    extra += val + (b'\0' if len(val) % 2 else b'')
            else:
                raw += P('HHII', tag, typ, count, val)
        raw += P('I', next_ifd)
        return raw + extra

    # Layout is built with fixed placeholders: sizes computed by building twice.
    def build(ifd0_off, exif_off, gps_off, ifd1_off):
        mk = b'Synthetic\0'
        ser = SN + b'\0'
        lens = b'LSN-' + SN + b'\0'
        owner = OWNER + b'\0'
        lat = struct.pack(e + '6I', 47, 1, 37, 1, 1234, 100)
        lon = struct.pack(e + '6I', 122, 1, 20, 1, 5753, 100)
        alt = struct.pack(e + '2I', 1845, 10)
        ex = [(0xA431, 2, len(ser), ser[:4] if serial_inline else ser), (0xA435, 2, len(lens), lens), (0xA430, 2, len(owner), owner),
              (0x927C, 7, 16, b'MAKERNOTE' + SN[:7]), (0xA420, 2, 11, b'IMGUNIQ0001\0')]
        if serial_inline:
            ex[0] = (0xA431, 2, 4, SN[:3] + b'\0')
        g = [(1, 2, 2, b'N\0'), (2, 5, 3, lat), (3, 2, 2, b'W\0'), (4, 5, 3, lon), (5, 1, 1, b'\0'), (6, 5, 1, alt),
             (0x1B, 7, 20, b'GPS' + b'X' * 17)]
        i0 = [(0x010F, 2, len(mk), mk), (0x8769, 4, 1, exif_off)]
        if gps:
            i0.append((0x8825, 4, 1, gps_off))
        d0 = ifd(i0, ifd1_off if ifd1_gps else 0, ifd0_off)
        dx = ifd(ex + ([(0x8825, 4, 1, gps_off)] if gps_from_exif else []), 0, exif_off)
        dg = ifd(g, 0, gps_off)
        d1 = ifd([(0x8825, 4, 1, gps_off)], 0, ifd1_off) if ifd1_gps else b''
        return d0, dx, dg, d1

    d0, dx, dg, d1 = build(8, 0, 0, 0)
    exif_off = 8 + len(d0)
    gps_off = exif_off + len(dx)
    ifd1_off = gps_off + len(dg)
    d0, dx, dg, d1 = build(8, exif_off, gps_off, ifd1_off)
    assert 8 + len(d0) == exif_off and exif_off + len(dx) == gps_off
    return order.encode() + (b'*\0' if order == 'II' else b'\0*') + P('I', 8) + d0 + dx + dg + d1


# ---------------------------------------------------------------------------------------------------------------- ISO udta/meta placements
def iso_cases():
    xyz = ut(b'\xa9xyz', POS)
    out('iso-udta-trak-level', assemble([fx.track(1, b'meta', fx.entry_box(b'fdsc'), [b'x' * 40])], trak_extra={1: box(b'udta', xyz + ut(b'\xa9mak', b'Synth'))}))
    out('iso-udta-twice', assemble(udta=box(b'udta', ut(b'\xa9mak', b'A')) + box(b'udta', xyz)))
    out('iso-udta-meta-ilst', assemble(udta=box(b'udta', box(b'meta', b'\0\0\0\0' + box(b'hdlr', b'\0' * 24) + box(b'ilst', box(b'\xa9xyz', box(b'data', struct.pack('>II', 1, 0) + POS)))))))
    out('iso-udta-case', assemble(udta=box(b'udta', ut(b'\xa9XYZ', POS) + box(b'LOCI', b'\0' * 4 + POS) + box(b'Loci', POS))))
    out('iso-udta-largesize', assemble(udta=box(b'udta', struct.pack('>I', 1) + b'\xa9xyz' + struct.pack('>Q', 16 + 4 + len(POS)) + struct.pack('>HH', len(POS), 0x15C7) + POS + ut(b'\xa9mak', b'A'))))
    out('iso-udta-nested-odd', assemble(udta=box(b'udta', box(b'udta', xyz) + box(b'XYZ ', POS) + box(b'\xa9xyz', POS + b'\0' * 3))))
    out('iso-top-level-udta', assemble(top_extra=box(b'udta', xyz + ut(b'\xa9mak', b'A'))))
    out('iso-top-level-meta', assemble(top_extra=plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1), ('com.apple.quicktime.content.identifier', SN, 1)])))
    out('iso-qt-plain-meta', assemble(moov_meta=plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1)]).replace(b'meta', b'meta', 1)))
    # QuickTime's own meta box has no version/flags word
    pm = plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1), ('com.apple.quicktime.serialnumber', SN, 1)])
    out('iso-qt-nofullbox-meta', assemble(moov_meta=pm))
    out('iso-meta-in-trak', assemble([fx.track(1, b'meta', fx.entry_box(b'fdsc'), [b'x' * 40])], trak_extra={1: plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1)])}))
    out('iso-meta-twice', assemble(moov_meta=plain_meta([('com.apple.quicktime.model', b'Model', 1)]) + plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1)])))
    out('iso-itunes-freeform', assemble(udta=box(b'udta', box(b'meta', b'\0\0\0\0' + box(b'hdlr', b'\0' * 24) + box(b'ilst',
        box(b'----', box(b'mean', b'\0\0\0\0com.apple.iTunes') + box(b'name', b'\0\0\0\0LOCATION') + box(b'data', struct.pack('>II', 1, 0) + POS))
        + box(b'----', box(b'mean', b'\0\0\0\0com.example') + box(b'name', b'\0\0\0\0SERIALNUMBER') + box(b'data', struct.pack('>II', 1, 0) + SN)))))))
    xml = (XMP_HEAD + '<rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="47,37.2307N" exif:GPSLongitude="122,20.9587W"/>' + XMP_TAIL)
    out('iso-meta-xml-box', assemble(moov_meta=box(b'meta', b'\0\0\0\0' + box(b'hdlr', b'\0' * 24) + box(b'xml ', b'\0\0\0\0' + xml.encode()))))
    out('iso-xmp-udta-xmp_', assemble(udta=box(b'udta', box(b'XMP_', xml.encode()))))
    xu = xmp_uuid(xml)
    out('iso-xmp-uuid-in-moov', assemble(moov_extra=xu))
    out('iso-xmp-uuid-in-trak', assemble([fx.track(1, b'meta', fx.entry_box(b'fdsc'), [b'x' * 40])], trak_extra={1: xu}))
    out('iso-freetext', assemble(udta=box(b'udta', ut(b'\xa9cmt', b'shot at 47.620512,-122.349312') + ut(b'\xa9des', b'N 47 37 14.8 W 122 20 57.5') + ut(b'\xa9nam', b'ZQ9X7R5T3M1K'))))
    # trailing data after the last box (some phones append vendor trailers)
    out('iso-trailer', assemble(top_extra=b'SEFHtrailer lat=' + LAT.encode() + b' lon=' + LON.encode() + b' sn=' + SN))
    out('iso-sony-rtmd-unknown', assemble([fx.track(1, b'meta', fx.entry_box(b'rtmd'), [POS + SN + struct.pack('>dd', 47.620512, -122.349312)])]))
    out('iso-gps-atom-dashcam', assemble(top_extra=box(b'gps ', b'\0\0\0\0$GPRMC,123519,A,4737.2307,N,12220.9587,W,022.4,084.4,230394,003.1,W*6A')))


def keys_case():
    items = [
        ('COM.APPLE.QUICKTIME.LOCATION.ISO6709', POS, 1),
        ('com.apple.quicktime.location.name', b'Seattle', 1),
        ('com.example.GPSLatitude', LAT.encode(), 1),
        ('com.example.geo-location', POS, 1),
        ('com.example.lat', LAT.encode(), 1),
        ('com.example.lon', LON.encode(), 1),
        ('com.example.position', POS, 1),
        ('com.example.coordinates', POS, 1),
        ('com.example.latitude.float', struct.pack('>d', 47.620512), 23),
        ('com.example.devicesn', SN, 1),
        ('com.example.sn', SN, 1),
        ('com.example.serialnumber', SN, 1),
        ('com.example.imei', b'356938035643809', 1),
        ('com.example.camera.identifier', SN, 1),
        ('com.apple.quicktime.camera.identifier', SN, 1),
        ('com.example.unit_id', SN, 1),
        ('com.example.hardware.id', SN, 1),
        ('com.example.mac_address', b'AA:BB:CC:DD:EE:FF', 1),
        ('com.example.owner', OWNER, 1),
        ('com.example.UUID', SN, 1),
        ('com.example.gnss', POS, 1),
        ('com.example.place', b'Space Needle', 1),
        ('com.example.model', b'Keep Model', 1),
    ]
    out('iso-keys-variants', assemble(moov_meta=plain_meta(items)))
    out('iso-keys-binary-values', assemble(moov_meta=plain_meta([('com.apple.quicktime.location.ISO6709', struct.pack('>ii', 476205120, -1223493120), 77),
                                                              ('com.apple.quicktime.serialnumber', SN.hex().encode(), 1)])))


def xmp_cases():
    ns = ('xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:aux="http://ns.adobe.com/exif/1.0/aux/" xmlns:exifEX="http://cipa.jp/exif/1.0/" '
          'xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:Iptc4xmpExt="http://iptc.org/std/Iptc4xmpExt/2008-02-29/" '
          'xmlns:Iptc4xmpCore="http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/"')

    def put(name, body, **kw):
        out(name, assemble(top_extra=xmp_uuid(XMP_HEAD + body + XMP_TAIL, **kw)))

    put('xmp-aliased-ns', '<rdf:Description rdf:about="" xmlns:q="http://ns.adobe.com/exif/1.0/" xmlns:z="http://ns.adobe.com/exif/1.0/aux/" '
        'q:GPSLatitude="47,37.2307N" q:GPSLongitude="122,20.9587W" q:BodySerialNumber="ZQ9X7R5T3M1K"><z:SerialNumber>ZQ9X7R5T3M1K</z:SerialNumber></rdf:Description>')
    put('xmp-exifex-serials', f'<rdf:Description rdf:about="" {ns} exifEX:BodySerialNumber="ZQ9X7R5T3M1K" exifEX:LensSerialNumber="LSN-ZQ9X7R5T3M1K" '
        f'aux:LensSerialNumber="LSN2-ZQ9X7R5T3M1K" aux:SerialNumber="ZQ9X7R5T3M1K" exifEX:CameraOwnerName="OWNR-ALPHA-7731" aux:OwnerName="OWNR-ALPHA-7731" '
        f'aux:ImageNumber="ZQ9X7R5T3M1K" xmp:Identifier="ZQ9X7R5T3M1K"/>')
    put('xmp-iptc-struct', f'<rdf:Description rdf:about="" {ns}><Iptc4xmpExt:LocationShown><rdf:Bag><rdf:li rdf:parseType="Resource">'
        '<Iptc4xmpExt:City>Seattle</Iptc4xmpExt:City><Iptc4xmpExt:GPSLatitude>47.620512</Iptc4xmpExt:GPSLatitude></rdf:li></rdf:Bag></Iptc4xmpExt:LocationShown>'
        '<photoshop:City>Seattle</photoshop:City><photoshop:Country>US</photoshop:Country><Iptc4xmpCore:Location>Needle</Iptc4xmpCore:Location>'
        '<Iptc4xmpCore:CountryCode>US</Iptc4xmpCore:CountryCode><photoshop:State>WA</photoshop:State></rdf:Description>')
    put('xmp-entities', f'<rdf:Description rdf:about="" {ns} exif:GPSLatitude="&#52;7,37.2307N" exif:BodySerialNumber="ZQ9X7R5T3M&#49;K">'
        '<exif:GPSLongitude><![CDATA[122,20.9587W]]></exif:GPSLongitude></rdf:Description>')
    put('xmp-two-descriptions', f'<rdf:Description rdf:about="" {ns} dc:format="video/mp4"/><rdf:Description rdf:about="" {ns} exif:GPSLatitude="47,37.2307N"/>'
        f'<rdf:Description rdf:about="" {ns} aux:SerialNumber="ZQ9X7R5T3M1K"/>')
    put('xmp-freetext', f'<rdf:Description rdf:about="" {ns}><dc:description><rdf:Alt><rdf:li xml:lang="x-default">at 47.620512, -122.349312 sn ZQ9X7R5T3M1K</rdf:li></rdf:Alt></dc:description>'
        '<dc:subject><rdf:Bag><rdf:li>47.620512,-122.349312</rdf:li></rdf:Bag></dc:subject></rdf:Description>')
    put('xmp-geo-words', f'<rdf:Description rdf:about="" {ns} xmp:Geo="47.620512,-122.349312" xmp:Lat="47.620512" xmp:Position="47.620512" xmp:Place="Seattle" xmp:DeviceSN="ZQ9X7R5T3M1K"/>')
    # a packet that is not UTF-8
    xml16 = ('\ufeff<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>' + XMP_HEAD + f'<rdf:Description rdf:about="" {ns} exif:GPSLatitude="47,37.2307N" exif:BodySerialNumber="ZQ9X7R5T3M1K"/>'
             + XMP_TAIL + '<?xpacket end="w"?>').encode('utf-16-le')
    out('xmp-utf16', assemble(top_extra=box(b'uuid', bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC') + xml16)))
    # a uuid XMP whose packet has no xpacket wrapper
    nowrap = (XMP_HEAD + f'<rdf:Description rdf:about="" {ns} exif:GPSLatitude="47,37.2307N" exif:BodySerialNumber="ZQ9X7R5T3M1K"/>' + XMP_TAIL).encode()
    out('xmp-nowrapper', assemble(top_extra=box(b'uuid', bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC') + nowrap)))
    # second XMP packet in the same box after padding
    p1 = xmp_uuid(XMP_HEAD + '<rdf:Description rdf:about="" ' + ns + '/>' + XMP_TAIL, total=600)
    p2 = (XMP_HEAD + f'<rdf:Description rdf:about="" {ns} exif:GPSLatitude="47,37.2307N"/>' + XMP_TAIL).encode()
    out('xmp-second-packet', assemble(top_extra=p1[:-0] + box(b'uuid', bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC') + b'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>' + p2 + b'<?xpacket end="w"?>')))


def exif_cases():
    def atom(name, tiffbytes, kind=b'QVMI', pre=b'Exif\0\0'):
        out(name, assemble(udta=box(b'udta', box(kind, pre + tiffbytes))))

    atom('exif-le', tiff('II'))
    atom('exif-be', tiff('MM'))
    atom('exif-be-ifd1-gps', tiff('MM', ifd1_gps=True))
    atom('exif-gps-from-exif-ifd', tiff('II', gps=False, gps_from_exif=True))
    atom('exif-inline-serial', tiff('MM', serial_inline=True))
    atom('exif-nowrapper', tiff('II'), pre=b'')
    atom('exif-wrapper-8', tiff('MM'), pre=b'\0' * 8)
    atom('exif-in-RMKN', tiff('MM'), kind=b'RMKN')
    atom('exif-in-unknown-atom', tiff('MM'), kind=b'CNTH')
    # Canon-style uuid box with a TIFF block
    out('exif-uuid-canon', assemble(top_extra=box(b'uuid', bytes.fromhex('85c0b687820f11e0811100241d36c4a1')[:16] + box(b'CMT1', tiff('II')))))


def telemetry_cases():
    g0, g1 = fx.gpmf_sample(0), fx.gpmf_sample(1)
    two = [fx.track(1, b'meta', fx.entry_box(b'gpmd'), [g0, g1]), fx.track(2, b'meta', fx.entry_box(b'gpmd'), [g1, g0])]
    out('tele-two-gpmd', assemble(two))
    gps9 = struct.pack('>llllllllHH', 476205120, -1223493120, 184500, 640, 0, 0, 0, 0, 0, 0)
    scal = klv(b'SCAL', b'l', 4, 9, struct.pack('>9l', 10000000, 10000000, 1000, 1000, 1000, 1, 1, 1, 1)) if False else b''
    s9 = nested(b'DEVC', text(b'DVNM', b'Synth'), text(b'MUID', b'MUIDSERIAL000042'), nested(b'STRM', klv(b'GPS9', b'?', 34, 1, gps9 + b'\0' * 0), text(b'GPSU', b'240101120000.000')))
    out('tele-gps9', assemble([fx.track(1, b'meta', fx.entry_box(b'gpmd'), [s9])]))
    # GPS5 deeper: DEVC > STRM > nested again; key repeated at top level of the sample
    deep = nested(b'DEVC', nested(b'STRM', nested(b'STRM', klv(b'GPS5', b'l', 20, 1, struct.pack('>5l', 476205120, -1223493120, 184500, 0, 0)))))
    out('tele-gpmf-deep', assemble([fx.track(1, b'meta', fx.entry_box(b'gpmd'), [deep])]))
    # gpmd samples with garbage after the first DEVC and a second DEVC
    out('tele-gpmf-two-devc', assemble([fx.track(1, b'meta', fx.entry_box(b'gpmd'), [g0 + b'\0' * 12 + g1])]))
    # GPMF in track whose handler says something else / sample entry gpmd but with extra boxes
    out('tele-camm-all', assemble([fx.track(1, b'meta', fx.entry_box(b'camm'), [fx.camm_sample(6), fx.camm_sample(5), fx.camm_sample(1)]),
                                   fx.track(2, b'meta', fx.entry_box(b'camm'), [fx.camm_sample(6)])]))
    # camm type 4 (position) plus a sample holding two packets (not allowed, but readers may concatenate)
    pos4 = struct.pack('<HH', 0, 4) + struct.pack('<fff', 47.620512, -122.349312, 184.5)
    out('tele-camm-double', assemble([fx.track(1, b'meta', fx.entry_box(b'camm'), [fx.camm_sample(5) + fx.camm_sample(6), pos4])]))
    keys = [(1, 'com.apple.quicktime.location.ISO6709', 1), (2, 'COM.APPLE.QUICKTIME.LOCATION.NAME', 1), (3, 'com.apple.quicktime.serialnumber', 1),
            (4, 'com.example.lat', 1), (5, 'com.example.model', 1)]
    ment = fx.entry_box(b'mebx', box(b'keys', b''.join(fx.mebx_key(*k) for k in keys)))
    sample = fx.mebx_item(1, POS) + fx.mebx_item(2, b'Seattle') + fx.mebx_item(3, SN) + fx.mebx_item(4, LAT.encode()) + fx.mebx_item(5, b'ModelX')
    out('tele-mebx-variants', assemble([fx.track(1, b'meta', ment, [sample])]))
    # a data track the strip knows nothing about that carries the secret
    out('tele-unknown-track', assemble([fx.track(1, b'meta', fx.entry_box(b'fdsc'), [b'hdr' + POS]), fx.track(2, b'meta', fx.entry_box(b'xxxx'), [struct.pack('>dd', 47.620512, -122.349312)])],
                                       udta=box(b'udta', ut(b'\xa9xyz', POS))))


def text_cases():
    samples = {
        'plain-decimal': '47.620512, -122.349312',
        'labelled': 'lat: 47.620512 lon: -122.349312',
        'dms-symbols': "47\u00b037'14.8\"N 122\u00b020'57.5\"W",
        'dms-words': 'N 47 37 14.8 W 122 20 57',
        'euro-comma': '47,620512 -122,349312',
        'iso6709': '+47.620512-122.349312/',
        'gps-paren': 'GPS(47.620512,-122.349312,18)',
        'plus-code': '84VVJM5F+C2',
        'utm': '10T 549848 5273437',
        'mgrs': '10TET4984873437',
        'labelled-serial': 'SN: ZQ9X7R5T3M1K',
        'bare-serial': 'camera ZQ9X7R5T3M1K',
        'serial-eq': 'serial=ZQ9X7R5T3M1K',
        'serial-lower': 'Serial No. ZQ9X7R5T3M1K',
        'fullwidth': '\uff14\uff17.620512, -122.349312',
        'lat-only-3dec': '47.620 ',
        'dec-4': '47.6205 -122.3493',
        'dec-spaces': '47.620512   -122.349312',
        'dec-newline': '47.620512\n-122.349312',
        'dec-with-tab': '47.620512\t-122.349312',
        'what3words': '///filled.count.soap',
        'address': '400 Broad St Seattle WA 98109',
        'degrees-decimal': '47.620512\u00b0 N, 122.349312\u00b0 W',
        'degrees-decimal-2': '47.620512\u00b0N 122.349312\u00b0W',
        'lat-long-words': 'latitude 47.620512 longitude 122.349312',
        'lng-colon-nospace': 'Lat:47.620512,Lng:-122.349312',
        'geo-uri': 'geo:47.620512,-122.349312',
        'maps-url': 'https://maps.example/?q=47.620512,-122.349312',
    }
    tx = lambda s: struct.pack('>H', len(s.encode('utf-8'))) + s.encode('utf-8')
    names = list(samples)
    entry = fx.entry_box(b'tx3g', b'\0' * 4 + b'\0' * 8 + b'\0' * 8 + b'\0' * 13)
    out('text-tx3g-patterns', assemble([fx.track(1, b'sbtl', entry, [tx(samples[n]) for n in names])]))
    with open(os.path.join(sys.argv[1], 'text-tx3g-patterns.names'), 'w') as f:
        f.write('\n'.join(names))
    # UTF-16 with BOM (3GPP TS 26.245 allows it)
    u16 = lambda s: struct.pack('>H', len(s.encode('utf-16-be')) + 2) + b'\xfe\xff' + s.encode('utf-16-be')
    out('text-tx3g-utf16', assemble([fx.track(1, b'sbtl', entry, [u16('47.620512, -122.349312'), u16('lat: 47.620512 lon: -122.349312'), u16('SN: ZQ9X7R5T3M1K')])]))
    wv = lambda s: box(b'vttc', box(b'payl', s.encode()))
    out('text-wvtt', assemble([fx.track(1, b'sbtl', fx.entry_box(b'wvtt', box(b'vttC', b'WEBVTT')), [wv('lat: 47.620512 lon: -122.349312'), wv('47.620512, -122.349312'), wv('SN: ZQ9X7R5T3M1K')])]))
    ttml = lambda s: ('<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="0s" end="1s">' + s + '</p></div></body></tt>').encode()
    out('text-stpp', assemble([fx.track(1, b'subt', fx.entry_box(b'stpp', b'http://www.w3.org/ns/ttml\0\0\0'), [ttml('47.620512, -122.349312'), ttml('lat: 47.620512'), ttml('&#52;7.620512, -122.349312')])]))
    # a text track whose sample entry is not a known text one (qt text)
    out('text-qt-text', assemble([fx.track(1, b'text', fx.entry_box(b'text', b'\0' * 43), [tx('lat: 47.620512 lon: -122.349312')])]))
    # text secret split across two samples
    out('text-split-samples', assemble([fx.track(1, b'sbtl', entry, [tx('lat: 47.62'), tx('0512 lon: -122.349312')])]))


def frag_case():
    """A fragmented file: the movie box holds an empty gpmd and camm track and a udta; the fragments hold the samples, and a traf holds a udta and a meta."""
    g = fx.gpmf_sample(0)
    c = fx.camm_sample(6)

    def trak(tid, entry):
        t = fx.track(tid, b'meta', entry, [b''])
        tkhd, mdhd, hdlr, dinf, stsd, stts, stsc, stsz = t['parts']
        stts = full(b'stts', 0, 0, struct.pack('>I', 0))
        stsc = full(b'stsc', 0, 0, struct.pack('>I', 0))
        stsz = full(b'stsz', 0, 0, struct.pack('>II', 0, 0))
        stco = full(b'stco', 0, 0, struct.pack('>I', 0))
        return box(b'trak', tkhd + box(b'mdia', mdhd + hdlr + box(b'minf', full(b'nmhd', 0, 0) + dinf + box(b'stbl', stsd + stts + stsc + stsz + stco))))

    ftyp = box(b'ftyp', b'iso5' + struct.pack('>I', 512) + b'iso5iso6mp41')
    mvhd = full(b'mvhd', 0, 0, struct.pack('>IIII', 0, 0, 1000, 0) + struct.pack('>IH', 0x00010000, 0x0100) + b'\0' * 10
                + struct.pack('>9I', 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000) + b'\0' * 24 + struct.pack('>I', 3))
    trex = lambda tid: full(b'trex', 0, 0, struct.pack('>IIIII', tid, 1, 0, 0, 0))
    moov = box(b'moov', mvhd + trak(1, fx.entry_box(b'gpmd')) + trak(2, fx.entry_box(b'camm')) + box(b'mvex', trex(1) + trex(2)) + box(b'udta', ut(b'\xa9xyz', POS)))

    def traf(tid, size, data_offset, extra=b''):
        tfhd = full(b'tfhd', 0, 0x020000, struct.pack('>I', tid))  # default-base-is-moof
        tfdt = full(b'tfdt', 1, 0, struct.pack('>Q', 0))
        trun = full(b'trun', 0, 0x000201, struct.pack('>Ii', 1, data_offset) + struct.pack('>I', size))
        return box(b'traf', tfhd + tfdt + trun + extra)

    extra1 = box(b'udta', ut(b'\xa9xyz', POS)) + plain_meta([('com.apple.quicktime.location.ISO6709', POS, 1)])
    mfhd = full(b'mfhd', 0, 0, struct.pack('>I', 1))
    sizes = [len(g), len(c)]
    # moof size is fixed by content; compute twice
    def moof(off):
        return box(b'moof', mfhd + traf(1, sizes[0], off, extra1) + traf(2, sizes[1], off + sizes[0]))
    m = moof(0)
    m = moof(len(m) + 8)
    return ftyp + moov + m + box(b'mdat', g + c)


def uuid_free_cases():
    out('iso-keys-and-free-text-after', assemble(moov_meta=plain_meta([('com.apple.quicktime.model', b'ModelX', 1)])))


# ---------------------------------------------------------------------------------------------------------------- non-ISO
def run(*a, **kw):
    return subprocess.run(a, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kw)


def media_cases(root):
    d = root
    P = lambda n: os.path.join(d, n)
    # WAV with LIST INFO, bext, id3 chunk
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.2', '-c:a', 'pcm_s16le', P('base.wav'))
    wav = open(P('base.wav'), 'rb').read()

    def chunk(cid, data):
        return cid + struct.pack('<I', len(data)) + data + (b'\0' if len(data) % 2 else b'')

    def riff_add(wavbytes, chunks):
        body = wavbytes[12:] + b''.join(chunks)
        return b'RIFF' + struct.pack('<I', 4 + len(body)) + b'WAVE' + body

    def info(*pairs):
        return chunk(b'LIST', b'INFO' + b''.join(chunk(k, v + b'\0') for k, v in pairs))

    id3 = b'ID3\x04\0\0'

    def id3frame(fid, data):
        sz = len(data)
        return fid + bytes([(sz >> 21) & 0x7f, (sz >> 14) & 0x7f, (sz >> 7) & 0x7f, sz & 0x7f]) + b'\0\0' + data

    def id3tag(*frames):
        body = b''.join(frames) + b'\0' * 64
        sz = len(body)
        return id3 + bytes([(sz >> 21) & 0x7f, (sz >> 14) & 0x7f, (sz >> 7) & 0x7f, sz & 0x7f]) + body

    txxx = lambda desc, val: id3frame(b'TXXX', b'\x03' + desc.encode() + b'\0' + val.encode())
    frames = [txxx('LOCATION', POS.decode()), txxx('SERIALNUMBER', SN.decode()), txxx('GPS', LAT), txxx('Lat', LAT), txxx('DEVICE', SN.decode()), txxx('Place', 'Seattle'),
              id3frame(b'TOWN', b'\x03' + OWNER), id3frame(b'GEOB', b'\x03text/plain\0loc.txt\0pos\0' + POS), id3frame(b'COMM', b'\x03eng\0\0' + b'at ' + LAT.encode() + b',' + LON.encode().replace(b'-', b'-')),
              id3frame(b'PRIV', b'com.example\0' + SN), id3frame(b'UFID', b'owner.example\0' + SN), id3frame(b'TSRC', b'\x03' + SN)]
    tag = id3tag(*frames)
    open(P('wav-id3-info.wav'), 'wb').write(riff_add(wav, [info((b'IARL', POS), (b'ICMT', b'at ' + LAT.encode()), (b'ISRC', SN), (b'IENG', OWNER)), chunk(b'id3 ', tag),
                                                       chunk(b'bext', (b'Desc' + b'\0' * 252) + b'Orig' + b'\0' * 28 + (b'ORIGREF-' + SN).ljust(32, b'\0') + b'\0' * 10 + b'\0' * 10 + b'\0' * 8 + b'\0' * 2 + b'\0' * 4 + b'\0' * 190)]))
    # MP3 with ID3v2.4 + ID3v1 + APE
    mp3 = subprocess.run(['ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.5', '-c:a', 'libmp3lame', '-f', 'mp3', '-id3v2_version', '0', '-write_id3v1', '0', '-'], check=True, capture_output=True).stdout
    v1 = b'TAG' + b'T'.ljust(30, b'\0') + b'A'.ljust(30, b'\0') + b'B'.ljust(30, b'\0') + b'2020' + (b'at ' + LAT.encode() + b',' + LON.encode()).ljust(28, b'\0')[:28] + b'\0\x01\x00'
    open(P('mp3-id3v2-v1.mp3'), 'wb').write(tag + mp3 + v1)
    # FLAC / Ogg Vorbis / chained Ogg / Opus
    meta_args = ['-metadata', 'LOCATION=' + POS.decode(), '-metadata', 'SERIALNUMBER=' + SN.decode(), '-metadata', 'GPS=' + LAT, '-metadata', 'OWNER=' + OWNER.decode(),
                 '-metadata', 'COMMENT=at ' + LAT + ',' + LON, '-metadata', 'DEVICEID=' + SN.decode(), '-metadata', 'TITLE=keep']
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.3', *meta_args, P('flac-vc.flac'))
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.3', '-c:a', 'libvorbis', *meta_args, P('a.ogg'))
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.3', '-c:a', 'libvorbis', '-metadata', 'LOCATION=' + POS.decode(), '-metadata', 'SERIAL=' + SN.decode(), P('b.ogg'))
    open(P('ogg-chained.ogg'), 'wb').write(open(P('a.ogg'), 'rb').read() + open(P('b.ogg'), 'rb').read())
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'sine=d=0.3', '-c:a', 'libopus', *meta_args, P('opus-vc.opus'))
    # Matroska with global tags, track-level tags and subtitle
    open(P('sub.srt'), 'w').write('1\n00:00:00,000 --> 00:00:01,000\nlat: 47.620512 lon: -122.349312\n')
    tags_xml = ('<?xml version="1.0"?><Tags>'
                '<Tag><Targets/><Simple><Name>LOCATION</Name><String>' + POS.decode() + '</String></Simple><Simple><Name>SERIAL_NUMBER</Name><String>' + SN.decode() + '</String></Simple>'
                '<Simple><Name>RECORDING_LOCATION</Name><String>Seattle</String></Simple><Simple><Name>GPS</Name><String>' + LAT + '</String>'
                '<Simple><Name>nested</Name><String>' + SN.decode() + '</String></Simple></Simple>'
                '<Simple><Name>Lat</Name><String>' + LAT + '</String></Simple><Simple><Name>DeviceSN</Name><String>' + SN.decode() + '</String></Simple>'
                '<Simple><Name>COMMENT</Name><String>at ' + LAT + ',' + LON + '</String></Simple></Tag>'
                '<Tag><Targets><TrackUID>1</TrackUID></Targets><Simple><Name>LOCATION</Name><String>' + POS.decode() + '</String></Simple></Tag></Tags>')
    open(P('tags.xml'), 'w').write(tags_xml)
    run('ffmpeg', '-y', '-f', 'lavfi', '-i', 'testsrc2=d=0.5:s=64x48:r=10', '-f', 'lavfi', '-i', 'sine=d=0.5', '-c:v', 'libx264', '-c:a', 'aac', P('base.mp4'))
    run('mkvmerge', '-o', P('mkv-tags.mkv'), '--title', 'T ' + LAT, '--track-name', '0:SN ' + SN.decode(), '--global-tags', P('tags.xml'), P('base.mp4'), P('sub.srt'))
    run('mkvmerge', '-o', P('mkv-base.mkv'), P('base.mp4'))
    # Tags at the end of the segment with no SeekHead entry: append a Tags element and grow the Segment size field
    mkv = bytearray(open(P('mkv-base.mkv'), 'rb').read())
    i = mkv.index(b'\x18\x53\x80\x67')  # Segment id
    lenbyte = mkv[i + 4]
    width = 1
    while not (lenbyte & (0x80 >> (width - 1))):
        width += 1
    size = int.from_bytes(mkv[i + 4:i + 4 + width], 'big') & ((1 << (7 * width)) - 1)
    ebml = lambda eid, payload: eid + (0x10000000000000 | len(payload)).to_bytes(8, 'big')[1:].rjust(8, b'\x01')[:0] + b'' if False else eid
    def vint(n):
        return (0x0100000000000000 | n).to_bytes(8, 'big')
    def el(eid, payload):
        return eid + vint(len(payload)) + payload
    stag = lambda n, v: el(b'\x67\xC8', el(b'\x45\xA3', n.encode()) + el(b'\x44\x87', v.encode()))
    tags = el(b'\x12\x54\xC3\x67', el(b'\x73\x73', el(b'\x63\xC0', b'') + stag('LOCATION', POS.decode()) + stag('SERIAL_NUMBER', SN.decode())))
    mkv += tags
    new = size + len(tags)
    mkv[i + 4:i + 4 + width] = ((1 << (7 * width)) | new).to_bytes(width, 'big') if new < (1 << (7 * width)) - 1 else mkv[i + 4:i + 4 + width]
    open(P('mkv-tags-at-end.mkv'), 'wb').write(bytes(mkv))
    for n in ('base.wav', 'a.ogg', 'b.ogg', 'sub.srt', 'tags.xml', 'base.mp4', 'mkv-base.mkv'):
        os.remove(P(n))


def main():
    root = sys.argv[1]
    os.makedirs(root, exist_ok=True)
    iso_cases()
    keys_case()
    xmp_cases()
    exif_cases()
    telemetry_cases()
    text_cases()
    out('frag-udta-meta-in-traf', frag_case())
    for name, data in OUT.items():
        open(os.path.join(root, name + '.mp4' if not name.startswith('frag') else os.path.join(root, name + '.mp4'), ), 'wb').write(data) if False else open(os.path.join(root, name + '.mp4'), 'wb').write(data)
    if '--no-ffmpeg' not in sys.argv:
        media_cases(root)
    print(len(OUT), 'iso files')


if __name__ == '__main__':
    main()

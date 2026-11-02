#!/usr/bin/env python3
"""Writes the small fact-source fixtures under testdata/facts: a TIFF/Exif block in both byte orders, a Sony clip XML, a DJI SRT and an XMP sidecar.
Every value is invented. Usage: tools/gen-facts-fixtures.py [out-dir]   (default testdata/facts beside this script's repository)."""
import os
import struct
import sys

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "testdata", "facts")
os.makedirs(out, exist_ok=True)


def tiff(big):
    e = ">" if big else "<"
    pool = bytearray()
    base = 62  # header 8 + IFD0 (2 + 4 entries * 12 + 4)

    def put(b):
        off = base + len(pool)
        pool.extend(b)
        if len(pool) % 2:
            pool.append(0)
        return off

    def rat(*pairs):
        return b"".join(struct.pack(e + "II", n, d) for n, d in pairs)

    make = put(b"Acme\0")
    model = put(b"Cam 9\0")
    dto = put(b"2023:04:05 10:11:12\0")
    serial = put(b"SN12345\0")
    lat = put(rat((47, 1), (22, 1), (3000, 100)))
    lon = put(rat((8, 1), (30, 1), (0, 1)))
    alt = put(rat((4500, 10)))
    ts = put(rat((9, 1), (11, 1), (12, 1)))
    ds = put(b"2023:04:05\0")
    off = put(b"+02:00\0")
    exif_at = base + len(pool)
    exif_len = 2 + 3 * 12 + 4
    gps_at = exif_at + exif_len

    def entry(tag, typ, count, value):
        return struct.pack(e + "HHII", tag, typ, count, value)

    def inline(text):
        return struct.unpack(e + "I", (text + b"\0\0\0\0")[:4])[0]

    ifd0 = struct.pack(e + "H", 4) + entry(0x010F, 2, 5, make) + entry(0x0110, 2, 6, model) + entry(0x8769, 4, 1, exif_at) + entry(0x8825, 4, 1, gps_at) + struct.pack(e + "I", 0)
    exif = struct.pack(e + "H", 3) + entry(0x9003, 2, 20, dto) + entry(0x9011, 2, 7, off) + entry(0xA431, 2, 8, serial) + struct.pack(e + "I", 0)
    gps = (struct.pack(e + "H", 9) + entry(1, 2, 2, inline(b"N\0")) + entry(2, 5, 3, lat) + entry(3, 2, 2, inline(b"E\0")) + entry(4, 5, 3, lon) + entry(5, 1, 1, 0)
           + entry(6, 5, 1, alt) + entry(7, 5, 3, ts) + entry(9, 2, 2, inline(b"A\0")) + entry(0x1D, 2, 11, ds) + struct.pack(e + "I", 0))
    head = (b"MM\0*" if big else b"II*\0") + struct.pack(e + "I", 8)
    return head + ifd0 + bytes(pool) + exif + gps


for big, name in ((False, "exif-le.tiff"), (True, "exif-be.tiff")):
    with open(os.path.join(out, name), "wb") as f:
        f.write(tiff(big))

with open(os.path.join(out, "sony-clip.xml"), "w", newline="\n") as f:
    f.write('''<?xml version="1.0" encoding="UTF-8"?>
<NonRealTimeMeta xmlns="urn:schemas-professionalDisc:nonRealTimeMeta:ver.2.00" lastUpdate="2016-01-03T06:12:00-04:00">
 <Duration value="120"/>
 <LtcChangeTable tcFps="24" halfStep="false"><LtcChange frameCount="0" value="00000000" status="increment"/></LtcChangeTable>
 <CreationDate value="2016-01-03T06:12:00-04:00"/>
 <Device manufacturer="Sony" modelName="FDR-AX100" serialNo="1234567890"/>
</NonRealTimeMeta>
''')

with open(os.path.join(out, "dji.srt"), "w", newline="\n") as f:
    f.write('''1
00:00:00,000 --> 00:00:00,033
<font size="36">SrtCnt : 1, DiffTime : 33ms
2023-02-24 08:47:37.832
[iso : 100] [shutter : 1/250.0] [fnum : 170] [ev : 0] [ct : 4730] [color_md : default] [focal_len : 240] [latitude: 12.345678] [longitude: 56.789012] [rel_alt: 0.500 abs_alt: 101.250] </font>

2
00:00:00,033 --> 00:00:00,066
<font size="36">SrtCnt : 2, DiffTime : 33ms
2023-02-24 08:47:37.862
[iso : 100] [latitude: 12.345678] [longitude: 56.789012] [rel_alt: 0.500 abs_alt: 101.250] </font>
''')

with open(os.path.join(out, "sidecar.xmp"), "w", newline="\n") as f:
    f.write('''<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/" xmlns:aux="http://ns.adobe.com/exif/1.0/aux/"
  exif:DateTimeOriginal="2020-06-01T12:00:00+09:00" exif:GPSLatitude="35,40.5N" exif:GPSLongitude="139,45.0E" tiff:Make="Canon" tiff:Model="EOS R5" aux:SerialNumber="0123"/>
</rdf:RDF></x:xmpmeta>
<?xpacket end="w"?>
''')
print("wrote fixtures to", out)

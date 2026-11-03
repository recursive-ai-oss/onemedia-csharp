#!/usr/bin/env python3
"""Writes testdata/metadata/vendor_camera_atoms.mov: tags_udta.mov with a second user-data box that holds invented camera atoms in the
layouts of Nikon (NCDT), Nikon Coolpix (TAGS), Olympus (TAGS), Panasonic (PANA) and Canon (CNMN) movies.

  gen-vendor-camera-fixture.py [SOURCE] [OUT]
Only invented names are used. The file seeds the tag fuzz target and is a sample for the vendor atom readers.
"""
import os, struct, sys

here = os.path.dirname(os.path.abspath(__file__))
src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, '..', 'testdata', 'metadata', 'tags_udta.mov')
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, '..', 'testdata', 'metadata', 'vendor_camera_atoms.mov')


def box(t, *parts):
    body = b''.join(parts)
    return struct.pack('>I4s', 8 + len(body), t) + body


def text(s, n):
    return s.encode('latin1').ljust(n, b'\0')


def nikon_record(tag, s, count):
    return struct.pack('>IHH', tag, 2, count) + s.encode('latin1').ljust(max(4, count), b'\0')


ncdt = box(b'NCDT', box(b'NCHD', bytes(10)),
           box(b'NCTG', nikon_record(1, 'NIKON', 6), nikon_record(2, 'Z9000', 6), nikon_record(3, 'Z9000 Ver.1.0', 14)),
           box(b'NCTH', bytes(64)))
coolpix = bytearray(233)
coolpix[0:24] = text('NIKON DIGITAL CAMERA', 24)
coolpix[24:38] = text('L99    ', 14)
coolpix[175:191] = b'COOLPIX L99 V1.0'
coolpix[191] = 1
prms = bytearray(300)
prms[0:4] = b'OLYM'
prms[4:18] = b'Digital Camera'
prms[18:44] = text('OLYMPUS DIGITAL CAMERA', 26)
prms[44:52] = text('D4586', 8)
olympus = bytes(12) + box(b'OLYM', box(b'prms', bytes(prms)))
pana = bytearray(200)
pana[12:28] = text('DC-X1', 16)
udta = box(b'udta', ncdt, box(b'TAGS', bytes(coolpix)), box(b'TAGS', olympus), box(b'PANA', bytes(pana)), box(b'CNMN', text('Canon EOS Q1', 64)))

data = open(src, 'rb').read()
pos = 0
res = bytearray()
while pos + 8 <= len(data):
    size, typ = struct.unpack('>I4s', data[pos:pos + 8])
    if typ == b'moov':
        res += struct.pack('>I4s', size + len(udta), typ) + data[pos + 8:pos + size] + udta
    else:
        res += data[pos:pos + size]
    pos += size
open(out, 'wb').write(res)
print('wrote', out, len(res))

# testdata/avi/nikon_ncdt.avi: tagged.avi with a LIST ncdt after the header list, holding the Nikon AVI chunk (16-bit tag, 16-bit size, text).
src_avi = os.path.join(here, '..', 'testdata', 'avi', 'tagged.avi')
out_avi = os.path.join(here, '..', 'testdata', 'avi', 'nikon_ncdt.avi')


def rec(tag, s):
    b = s.encode('latin1') + b'\0'
    return struct.pack('<HH', tag, len(b)) + b


def riff(cid, payload):
    return cid + struct.pack('<I', len(payload)) + payload + (b'\0' if len(payload) & 1 else b'')


nctg = riff(b'nctg', rec(3, 'NIKON CORPORATION') + rec(4, 'NIKON D0') + rec(5, 'A1.00') + rec(5, 'B1.00'))
ncdt = riff(b'LIST', b'ncdt' + riff(b'ncvr', bytes(18)) + nctg)
avi = open(src_avi, 'rb').read()
assert avi[:4] == b'RIFF' and avi[8:12] == b'AVI '
pos = 12
while pos + 8 <= len(avi):
    cid, size = struct.unpack('<4sI', avi[pos:pos + 8])
    end = pos + 8 + size + (size & 1)
    if cid == b'LIST' and avi[pos + 8:pos + 12] == b'hdrl':
        res = avi[:pos] + avi[pos:end] + ncdt + avi[end:]
        res = res[:4] + struct.pack('<I', len(res) - 8) + res[8:]
        open(out_avi, 'wb').write(res)
        print('wrote', out_avi, len(res))
        break
    pos = end

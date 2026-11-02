#!/usr/bin/env python3
"""Independent byte search for the strip review. usage: search.py <original> <stripped> [--json]
Looks for the invented position and serial numbers of gen-adversarial.py in every encoding listed below, in both files, and prints the forms found
in the original and the forms still present in the stripped copy. Written for the review; does not use the strip's own verifier."""
import base64
import struct
import sys

LAT, LON, ALT = 47.620512, -122.349312, 184.5
SECRETS = {'sn': b'ZQ9X7R5T3M1K', 'owner': b'OWNR-ALPHA-7731', 'imei': b'356938035643809'}


def forms():
    f = {}
    for k, v in SECRETS.items():
        f[k + ':ascii'] = v
        f[k + ':lower'] = v.lower()
        f[k + ':utf16le'] = v.decode().encode('utf-16-le')
        f[k + ':utf16be'] = v.decode().encode('utf-16-be')
        f[k + ':hex'] = v.hex().encode()
        f[k + ':HEX'] = v.hex().upper().encode()
        f[k + ':b64'] = base64.b64encode(v)[:-2] if len(v) % 3 else base64.b64encode(v)
    f['sn:short'] = b'M1K'  # inline Exif serial keeps only a prefix; see exif-inline-serial
    f['sn:prefix3'] = b'ZQ9'
    for d in (4, 5, 6):
        f['lat:text%d' % d] = ('%.*f' % (d, LAT)).encode()
        f['lon:text%d' % d] = ('%.*f' % (d, abs(LON))).encode()
    f['lat:text-eu'] = b'47,620512'
    f['lon:text-eu'] = b'122,349312'
    for nm, v in (('lat', LAT), ('lon', LON), ('lon+', -LON), ('alt', ALT)):
        f[nm + ':f64le'] = struct.pack('<d', v)
        f[nm + ':f64be'] = struct.pack('>d', v)
        f[nm + ':f32le'] = struct.pack('<f', v)
        f[nm + ':f32be'] = struct.pack('>f', v)
    for scale in (10000000, 1000000):
        for nm, v in (('lat', LAT), ('lon', LON)):
            f['%s:i32be%d' % (nm, scale)] = struct.pack('>i', round(v * scale))
            f['%s:i32le%d' % (nm, scale)] = struct.pack('<i', round(v * scale))
    f['lat:fix1616'] = struct.pack('>i', int(LAT * 65536))
    f['lon:fix1616'] = struct.pack('>i', int(LON * 65536))
    f['lat:dms'] = b'47\xb037'
    f['lat:dms-sym'] = '47\u00b037'.encode()
    f['dms:rational-be'] = struct.pack('>6I', 47, 1, 37, 1, 1234, 100)
    f['dms:rational-le'] = struct.pack('<6I', 47, 1, 37, 1, 1234, 100)
    f['lon:rational-be'] = struct.pack('>6I', 122, 1, 20, 1, 5753, 100)
    f['lon:rational-le'] = struct.pack('<6I', 122, 1, 20, 1, 5753, 100)
    f['pos:dm'] = b'47,37.2307'
    f['pos:dm2'] = b'122,20.9587'
    f['pos:nmea'] = b'4737.2307'
    f['pos:nmea-lon'] = b'12220.9587'
    f['pos:iso'] = b'+47.620512'
    f['pos:dec4-only'] = b'47.6205'
    f['pos:plus'] = b'84VVJM5F'
    f['pos:utm'] = b'549848'
    f['pos:mgrs'] = b'10TET4984873437'
    f['pos:fullwidth'] = '\uff14\uff17'.encode()
    return f


def found(data, fs):
    return sorted(k for k, v in fs.items() if v and v in data)


def main():
    a = open(sys.argv[1], 'rb').read()
    b = open(sys.argv[2], 'rb').read()
    fs = forms()
    fa = found(a, fs)
    fb = found(b, fs)
    print('IN  :', ' '.join(fa) or '-')
    print('LEFT:', ' '.join(k for k in fb if k in fa) or '-')
    print('NEW :', ' '.join(k for k in fb if k not in fa) or '-')


if __name__ == '__main__':
    main()

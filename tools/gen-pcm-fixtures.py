#!/usr/bin/env python3
"""Writes small WAVE, RF64, AIFF, AIFF-C and CAF files with chunks that ffmpeg cannot write (iXML, axml, cart, smpl, cue/adtl, inst,
AIFF markers and comments, CAF info/chan/packet tables), and damaged variants (streaming sizes, cut data, odd padding).

    gen-pcm-fixtures.py OUTDIR

The sound is a generated sine tone; the output is deterministic. Pure standard library.
"""
import math
import os
import struct
import sys

RATE = 8000


def tone(frames=2000, channels=1, rate=RATE, amp=12000):
    out = bytearray()
    for i in range(frames):
        v = int(amp * math.sin(2 * math.pi * 440 * i / rate))
        out += struct.pack('<h', v) * channels
    return bytes(out)


def chunk(cid, data, big=False):
    pad = b'\0' if len(data) & 1 else b''
    return cid + struct.pack('>I' if big else '<I', len(data)) + data + pad


def fmt_pcm(channels=1, rate=RATE, bits=16):
    return struct.pack('<HHIIHH', 1, channels, rate, rate * channels * bits // 8, channels * bits // 8, bits)


def fmt_ext(channels, rate, bits, valid, mask, tag):
    base = struct.pack('<HHIIHH', 0xFFFE, channels, rate, rate * channels * bits // 8, channels * bits // 8, bits)
    guid = struct.pack('<I', tag) + bytes.fromhex('000010008000 00aa00389b71'.replace(' ', ''))
    return base + struct.pack('<HHI', 22, valid, mask) + guid


def riff(chunks, form=b'WAVE', magic=b'RIFF'):
    body = form + b''.join(chunks)
    return magic + struct.pack('<I', len(body)) + body


def fixed(text, n):
    b = text.encode('latin-1')
    return b[:n].ljust(n, b'\0')


def bext(version=2):
    d = fixed('Scene 12, take 3', 256) + fixed('OneMedia fixtures', 32) + fixed('REF-0001', 32) + fixed('2020-01-31', 10) + fixed('14:03:59', 8)
    d += struct.pack('<II', 172800000 & 0xFFFFFFFF, 0) + struct.pack('<H', version)
    d += (bytes(range(1, 65)) if version >= 1 else bytes(64))
    d += struct.pack('<hhhhh', -2300, 700, -150, -1800, -2100) if version >= 2 else bytes(10)
    d += bytes(180)
    d += b'A=PCM,F=8000,W=16,M=mono,T=OneMedia\r\n'
    return d


def ixml():
    return (b'<?xml version="1.0" encoding="UTF-8"?><BWFXML><IXML_VERSION>1.5</IXML_VERSION><PROJECT>Fixture project</PROJECT>'
            b'<SCENE>12</SCENE><TAKE>3</TAKE><TAPE>A001</TAPE><NOTE>Wind noise &amp; birds</NOTE>'
            b'<SPEED><NOTE>unused</NOTE><TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_LO>172800000</TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_LO></SPEED>'
            b'<TRACK_LIST><TRACK_COUNT>1</TRACK_COUNT><TRACK><CHANNEL_INDEX>1</CHANNEL_INDEX><NAME>Boom</NAME></TRACK></TRACK_LIST></BWFXML>\0\0')


def cart():
    d = fixed('0101', 4) + fixed('Cart title', 64) + fixed('Cart artist', 64) + fixed('CUT01', 64) + fixed('CLIENT', 64) + fixed('NEWS', 64)
    d += fixed('Spot', 64) + fixed('out cue text', 64) + fixed('2020/01/31', 10) + fixed('00:00:00', 8) + fixed('2020/02/29', 10) + fixed('23:59:59', 8)
    d += fixed('OneMedia', 64) + fixed('1.0', 64) + fixed('user', 64) + struct.pack('<i', -2300)
    d += struct.pack('<II', 0x20202020, 12345) * 8 + bytes(276) + fixed('https://example.org/cart', 1024) + b'free text\r\n'
    return d


def info_list(items):
    return chunk(b'LIST', b'INFO' + b''.join(chunk(k, v.encode('utf-8') + b'\0') for k, v in items))


def cue_chunks():
    cues = [(1, 0, 1000), (2, 500, 1500)]
    cue = struct.pack('<I', len(cues)) + b''.join(struct.pack('<II4sIII', i, p, b'data', 0, 0, o) for i, p, o in cues)
    adtl = b'adtl' + chunk(b'labl', struct.pack('<I', 1) + b'Intro\0') + chunk(b'note', struct.pack('<I', 2) + b'Remember\0') \
        + chunk(b'ltxt', struct.pack('<II4sHHHH', 2, 100, b'rgn ', 0, 0, 0, 0) + b'Region\0')
    smpl = struct.pack('<IIIIIIII', 0, 0, 125000, 60, 0, 0, 0, 1) + struct.pack('<I', 0) + struct.pack('<IIIIII', 1, 0, 100, 900, 0, 0)
    inst = bytes([60, 0xF6, 3, 36, 84, 1, 127])
    return [chunk(b'cue ', cue), chunk(b'LIST', adtl), chunk(b'smpl', smpl), chunk(b'inst', inst)]


def ext80(rate):
    e = 16383 + int(math.floor(math.log2(rate)))
    m = int(rate * (1 << (63 - int(math.floor(math.log2(rate))))))
    return struct.pack('>HQ', e, m)


def aiff(chunks, form=b'AIFF'):
    body = form + b''.join(chunks)
    return b'FORM' + struct.pack('>I', len(body)) + body


def comm(channels, frames, bits, rate, comp=None, name=b''):
    d = struct.pack('>hIh', channels, frames, bits) + ext80(rate)
    if comp:
        d += comp + bytes([len(name)]) + name
        if (len(name) + 1) & 1:
            d += b'\0'
    return chunk(b'COMM', d, True)


def be16(frames, channels=1):
    out = bytearray()
    for i in range(frames):
        v = int(12000 * math.sin(2 * math.pi * 440 * i / RATE))
        out += struct.pack('>h', v) * channels
    return bytes(out)


def caf(chunks):
    return b'caff' + struct.pack('>HH', 1, 0) + b''.join(chunks)


def cchunk(t, data, size=None):
    return t + struct.pack('>q', len(data) if size is None else size) + data


def cdesc(rate, fid, flags, bpp, fpp, ch, bits):
    return cchunk(b'desc', struct.pack('>d4sIIIII', rate, fid, flags, bpp, fpp, ch, bits))


def varint(n):
    out = [n & 0x7F]
    n >>= 7
    while n:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    return bytes(reversed(out))


def main(out):
    os.makedirs(out, exist_ok=True)
    pcm = tone()

    def w(name, data):
        with open(os.path.join(out, name), 'wb') as f:
            f.write(data)

    fmt = chunk(b'fmt ', fmt_pcm())
    data = chunk(b'data', pcm)
    w('bwf-ixml-cart.wav', riff([fmt, chunk(b'bext', bext(2)), chunk(b'iXML', ixml()), chunk(b'axml', b'<ebuCoreMain/>'), chunk(b'cart', cart()),
                                   info_list([(b'INAM', 'Fixture \u00e9'), (b'IART', 'Test Artist'), (b'ICMT', 'A comment'), (b'ICRD', '2020-05-17'), (b'ISFT', 'gen-pcm-fixtures'), (b'ITRK', '3/12')]),
                                   chunk(b'id3 ', b'ID3\x04\x00\x00\x00\x00\x00\x00'), data]))
    w('bwf-v0.wav', riff([fmt, chunk(b'bext', bext(0)), data]))
    w('cue-smpl.wav', riff([fmt, chunk(b'fact', struct.pack('<I', 2000))] + cue_chunks() + [data]))
    w('odd-padding.wav', riff([fmt, chunk(b'junk', b'abc'), chunk(b'zzzz', b'x'), chunk(b'JUNK', bytes(27)), chunk(b'data', pcm[:-1] if len(pcm) % 2 == 0 else pcm), chunk(b'LIST', b'INFOINAM\x03\x00\x00\x00ab\0\0')]))
    w('ext-mask-6ch.wav', riff([chunk(b'fmt ', fmt_ext(6, RATE, 16, 16, 0x3F, 1)), chunk(b'data', tone(500, 6))]))
    w('ext-valid20.wav', riff([chunk(b'fmt ', fmt_ext(2, RATE, 24, 20, 0x3, 1)), chunk(b'data', bytes(300))]))
    w('ext-unknown-sub.wav', riff([chunk(b'fmt ', fmt_ext(2, RATE, 16, 16, 0x3, 0x1234)), chunk(b'data', bytes(400))]))
    # Streaming writers leave the sizes open.
    body = b'WAVE' + fmt + b'data' + struct.pack('<I', 0xFFFFFFFF) + pcm
    w('stream-ffffffff.wav', b'RIFF' + struct.pack('<I', 0xFFFFFFFF) + body)
    body = b'WAVE' + fmt + b'data' + struct.pack('<I', 0) + pcm
    w('stream-zero.wav', b'RIFF' + struct.pack('<I', 0) + body)
    # Cut data: the data chunk promises more than the file holds.
    full = riff([fmt, data])
    w('truncated-data.wav', full[:len(full) - 1001])
    w('truncated-header.wav', full[:30])
    w('no-data.wav', riff([fmt]))
    w('no-fmt.wav', riff([data]))
    w('zero-channels.wav', riff([chunk(b'fmt ', fmt_pcm(0)), data]))
    w('huge-chunk-size.wav', b'RIFF' + struct.pack('<I', 4000) + b'WAVE' + fmt + b'LIST' + struct.pack('<I', 0x7FFFFFF0) + b'INFO')
    # RF64 with the sizes in ds64.
    ds64 = struct.pack('<QQQI', 0, len(pcm), len(pcm) // 2, 1) + b'fact' + struct.pack('<Q', 4)
    rbody = b'WAVE' + chunk(b'ds64', ds64) + fmt + b'data' + struct.pack('<I', 0xFFFFFFFF) + pcm
    w('rf64-small.wav', b'RF64' + struct.pack('<I', 0xFFFFFFFF) + rbody)
    w('bw64-small.wav', b'BW64' + struct.pack('<I', 0xFFFFFFFF) + rbody)
    # AIFF and AIFF-C.
    ssnd = chunk(b'SSND', struct.pack('>II', 0, 0) + be16(2000), True)
    texts = [chunk(b'NAME', b'Fixture name', True), chunk(b'AUTH', b'Test Author', True), chunk(b'(c) ', b'(c) 2020 Test', True), chunk(b'ANNO', b'First note', True), chunk(b'ANNO', b'Second note', True)]
    comt = chunk(b'COMT', struct.pack('>H', 1) + struct.pack('>IhH', 3000000000, 0, 7) + b'Comment' + b'\0', True)
    mark = chunk(b'MARK', struct.pack('>H', 2) + struct.pack('>HI', 1, 100) + bytes([5]) + b'Start' + struct.pack('>HI', 2, 900) + bytes([4]) + b'End\0', True)
    inst_a = chunk(b'INST', bytes([60, 0, 0, 36, 84, 1, 127]) + bytes(14), True)
    w('aiff-text-mark.aiff', aiff([comm(1, 2000, 16, RATE)] + texts + [comt, mark, inst_a, chunk(b'APPL', b'test', True), ssnd]))
    w('aiff-ssnd-offset.aiff', aiff([comm(1, 1000, 16, RATE), chunk(b'SSND', struct.pack('>II', 6, 0) + b'\xaa' * 6 + be16(1000), True)]))
    w('aiff-id3.aiff', aiff([comm(1, 2000, 16, RATE), chunk(b'ID3 ', b'ID3\x04\x00\x00\x00\x00\x00\x00', True), ssnd]))
    w('aifc-sowt.aifc', aiff([chunk(b'FVER', struct.pack('>I', 0xA2805140), True), comm(1, 2000, 16, RATE, b'sowt', b'little-endian'), chunk(b'SSND', struct.pack('>II', 0, 0) + pcm, True)], b'AIFC'))
    w('aifc-fl32.aifc', aiff([chunk(b'FVER', struct.pack('>I', 0xA2805140), True), comm(1, 100, 32, 44100, b'fl32', b'32-bit float'), chunk(b'SSND', struct.pack('>II', 0, 0) + bytes(400), True)], b'AIFC'))
    w('aiff-count-short.aiff', aiff([comm(1, 500, 16, RATE), ssnd]))
    w('aiff-no-comm.aiff', aiff([ssnd]))
    w('aiff-odd-rates.aiff', aiff([comm(2, 100, 24, 96000), chunk(b'SSND', struct.pack('>II', 0, 0) + bytes(600), True)]))
    # CAF.
    info = struct.pack('>I', 3) + b'title\0Fixture title\0artist\0Fixture artist\0recorded date\0' + b'2020-01-31T14:03:59\0'
    chan = cchunk(b'chan', struct.pack('>III', 100 << 16 | 1, 0, 0))
    cpcm = tone()
    w('caf-info-chan.caf', caf([cdesc(8000.0, b'lpcm', 2, 2, 1, 1, 16), chan, cchunk(b'info', info), cchunk(b'free', bytes(10)), cchunk(b'data', struct.pack('>I', 0) + cpcm)]))
    w('caf-data-unknown-size.caf', caf([cdesc(8000.0, b'lpcm', 2, 2, 1, 1, 16), cchunk(b'data', struct.pack('>I', 0) + cpcm, -1)]))
    sizes = [100, 200, 50, 300, 20, 400, 128, 129]
    paktbody = struct.pack('>qqii', len(sizes), len(sizes) * 1024 - 100, 64, 36) + b''.join(varint(s) for s in sizes)
    w('caf-pakt-variable.caf', caf([cdesc(44100.0, b'aac ', 0, 0, 1024, 2, 0), cchunk(b'pakt', paktbody), cchunk(b'kuki', b'\x03\x80\x80\x80\x22\x00\x00\x00\x04\x80\x80\x80\x14\x40\x15\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x05\x80\x80\x80\x02\x12\x10\x06\x01\x02'), cchunk(b'data', struct.pack('>I', 0) + bytes(sum(sizes)), -1)]))
    w('caf-float64-be.caf', caf([cdesc(48000.0, b'lpcm', 1, 16, 1, 2, 64), cchunk(b'data', struct.pack('>I', 0) + bytes(16 * 100))]))
    w('caf-no-desc.caf', caf([cchunk(b'data', struct.pack('>I', 0) + cpcm)]))
    w('caf-pakt-missing.caf', caf([cdesc(44100.0, b'aac ', 0, 0, 1024, 2, 0), cchunk(b'data', struct.pack('>I', 0) + bytes(100))]))
    w('caf-cut.caf', (caf([cdesc(8000.0, b'lpcm', 2, 2, 1, 1, 16), cchunk(b'data', struct.pack('>I', 0) + cpcm)]))[:200])


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])

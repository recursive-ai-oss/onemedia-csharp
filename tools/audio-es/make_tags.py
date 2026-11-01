#!/usr/bin/env python3
"""Writes tagged variants of an MP3 for the tag readers' tests: ID3v2.2/2.3/2.4 in many shapes, ID3v1, APEv1/v2, Lyrics3.

usage: make_tags.py INPUT.mp3 OUTDIR

INPUT must be a plain MP3 without tags. Every output is INPUT's audio with one tag layout around it; the layouts cover the features the
readers must handle (unsynchronisation, extended headers, footers, compressed and grouped frames, all text encodings, pictures, chapters,
private frames, padding, several tags in a row, trailing tags in each order). Written with the Python standard library only.
"""
import struct, sys, zlib, os

def synchsafe(n):
    return bytes([(n >> 21) & 0x7F, (n >> 14) & 0x7F, (n >> 7) & 0x7F, n & 0x7F])

def unsync(data):
    out = bytearray()
    for i, b in enumerate(data):
        out.append(b)
        if b == 0xFF and (i + 1 == len(data) or data[i + 1] >= 0xE0 or data[i + 1] == 0):
            out.append(0)
    return bytes(out)

def text_bytes(s, enc):
    if enc == 0: return s.encode('latin-1')
    if enc == 1: return b'\xff\xfe' + s.encode('utf-16-le')
    if enc == 2: return s.encode('utf-16-be')
    return s.encode('utf-8')

def term(enc): return b'\0\0' if enc in (1, 2) else b'\0'

def frame(ver, fid, data, flags=0):
    if ver == 2:
        return fid.encode() + struct.pack('>I', len(data))[1:] + data
    size = synchsafe(len(data)) if ver == 4 else struct.pack('>I', len(data))
    return fid.encode() + size + struct.pack('>H', flags) + data

def text_frame(ver, fid, *values, enc=0):
    body = bytes([enc]) + term(enc).join(text_bytes(v, enc) for v in values)
    return frame(ver, fid, body)

def txxx(ver, desc, value, enc=0):
    return frame(ver, 'TXX' if ver == 2 else 'TXXX', bytes([enc]) + text_bytes(desc, enc) + term(enc) + text_bytes(value, enc))

def comm(ver, text, desc='', lang='eng', enc=0, fid=None):
    fid = fid or ('COM' if ver == 2 else 'COMM')
    return frame(ver, fid, bytes([enc]) + lang.encode() + text_bytes(desc, enc) + term(enc) + text_bytes(text, enc))

PNG = (b'\x89PNG\r\n\x1a\n' + struct.pack('>I', 13) + b'IHDR' + struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)
       + struct.pack('>I', zlib.crc32(b'IHDR' + struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)))
       + struct.pack('>I', 12) + b'IDAT' + zlib.compress(b'\0\xff\0\0')[:8] + b'\0\0\0\0' + struct.pack('>I', 0) + b'IEND' + b'\xaeB`\x82')

def apic(ver, picture=PNG, ptype=3, desc='cover', mime='image/png', enc=0):
    if ver == 2:
        return frame(2, 'PIC', bytes([enc]) + b'PNG' + bytes([ptype]) + text_bytes(desc, enc) + term(enc) + picture)
    return frame(ver, 'APIC', bytes([enc]) + mime.encode() + b'\0' + bytes([ptype]) + text_bytes(desc, enc) + term(enc) + picture)

def basic_frames(ver, enc=0):
    t = lambda a, b, v: text_frame(ver, a if ver != 2 else b, v, enc=enc)
    f = [t('TIT2', 'TT2', 'Deposition, day 2'), t('TPE1', 'TP1', 'Ada Lovelace'), t('TALB', 'TAL', 'Notes'),
         t('TPE2', 'TP2', 'Various Artists'), t('TCOM', 'TCM', 'Charles Babbage'), t('TCON', 'TCO', '(17)Rock'),
         t('TRCK', 'TRK', '3/12'), t('TPOS', 'TPA', '1/2'), t('TCOP', 'TCR', '2020 Example Ltd'), t('TENC', 'TEN', 'tester'),
         t('TSSE', 'TSS', 'Lavf test')]
    f.append(t('TDRC', 'TYE', '2019-05-17T10:30:00') if ver == 4 else t('TYER', 'TYE', '2019'))
    if ver == 3:
        f += [text_frame(3, 'TDAT', '1705', enc=enc), text_frame(3, 'TIME', '1030', enc=enc)]
    f += [comm(ver, 'A comment', enc=enc), comm(ver, 'Lyrics line one\nline two', fid='ULT' if ver == 2 else 'USLT', enc=enc)]
    f += [txxx(ver, 'REPLAYGAIN_TRACK_GAIN', '-6.50 dB', enc), txxx(ver, 'REPLAYGAIN_TRACK_PEAK', '0.988525', enc)]
    return f

def id3v2(ver, frames, flags=0, padding=0, ext=b'', footer=False):
    body = ext + b''.join(frames) + b'\0' * padding
    if flags & 0x80 and ver < 4:
        body = unsync(body)
    head = b'ID3' + bytes([ver, 0, flags]) + synchsafe(len(body))
    return head + body + (b'3DI' + bytes([ver, 0, flags]) + synchsafe(len(body)) if footer else b'')

def id3v1(title='Title', artist='Artist', album='Album', year='2019', comment='Comment', track=7, genre=17):
    f = lambda s, n: s.encode('latin-1')[:n].ljust(n, b'\0')
    return b'TAG' + f(title, 30) + f(artist, 30) + f(album, 30) + f(year, 4) + f(comment, 28) + b'\0' + bytes([track, genre])

def ape_item(key, value, flags=0):
    v = value if isinstance(value, bytes) else value.encode()
    return struct.pack('<II', len(v), flags) + key.encode() + b'\0' + v

def ape(items, version=2000, header=True):
    body = b''.join(items)
    size = len(body) + 32
    def block(is_header):
        flags = (0x80000000 if header and version == 2000 else 0) | (0x20000000 if is_header else 0)
        return b'APETAGEX' + struct.pack('<IIII', version, size, len(items), flags) + b'\0' * 8
    return (block(True) if header and version == 2000 else b'') + body + block(False)

def lyrics3v2():
    fields = b'LYR' + b'00011' + b'Hello lyrics' + b'ETT' + b'00005' + b'Title' + b'EAR' + b'00006' + b'Artist'
    inner = b'LYRICSBEGIN' + fields
    return inner + b'%06d' % len(inner) + b'LYRICS200'

def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    audio = open(src, 'rb').read()
    def w(name, head, *tails):
        open(os.path.join(out, name), 'wb').write((head or b'') + audio + b''.join(tails))
    w('id3v22-basic.mp3', id3v2(2, basic_frames(2)  + [apic(2)]))
    w('id3v23-basic.mp3', id3v2(3, basic_frames(3) + [apic(3)]))
    w('id3v24-basic.mp3', id3v2(4, basic_frames(4) + [apic(4)]))
    w('id3v23-utf16.mp3', id3v2(3, basic_frames(3, enc=1)))
    w('id3v24-utf16be.mp3', id3v2(4, basic_frames(4, enc=2)))
    w('id3v24-utf8.mp3', id3v2(4, basic_frames(4, enc=3)))
    w('id3v24-multivalue.mp3', id3v2(4, [text_frame(4, 'TPE1', 'First', 'Second', 'Third', enc=3), text_frame(4, 'TCON', 'Rock', 'Jazz', enc=3), text_frame(4, 'TIT2', 'Naïve café', enc=3)]))
    w('id3v23-unsync.mp3', id3v2(3, [text_frame(3, 'TIT2', 'Sync\xff\xe0test'), apic(3, picture=b'\xff\xd8\xff\xe0' + b'\xff\x00\xff\xe1' * 20)], flags=0x80))
    pic = b'\x00image/jpeg\0\x03cover\0' + b'\xff\xd8\xff\xe0' + b'\xff\xe1' * 20
    w('id3v24-unsync.mp3', id3v2(4, [frame(4, 'TIT2', unsync(b'\x03Sync\xff\xe0'), flags=0x0002), frame(4, 'APIC', unsync(pic), flags=0x0002)], flags=0x80))
    w('id3v24-footer.mp3', id3v2(4, basic_frames(4), footer=True, flags=0x10))
    w('id3v23-extheader.mp3', id3v2(3, basic_frames(3), flags=0x40, ext=struct.pack('>IHI', 6, 0, 0)))
    w('id3v24-extheader.mp3', id3v2(4, basic_frames(4), flags=0x40, ext=synchsafe(6) + b'\x01\x00'))
    w('id3v24-padding.mp3', id3v2(4, basic_frames(4), padding=1024))
    comp = zlib.compress(b'\x00' + 'Compressed title'.encode())
    w('id3v23-compressed.mp3', id3v2(3, [frame(3, 'TIT2', struct.pack('>I', 17) + comp, flags=0x0080), text_frame(3, 'TPE1', 'Plain')]))
    w('id3v24-grouped-dli.mp3', id3v2(4, [frame(4, 'TIT2', b'\x05' + synchsafe(6) + b'\x03Title', flags=0x0041), text_frame(4, 'TPE1', 'Plain', enc=3)]))
    chap = lambda eid, start, end, title: frame(4, 'CHAP', eid.encode() + b'\0' + struct.pack('>IIII', start, end, 0xFFFFFFFF, 0xFFFFFFFF) + text_frame(4, 'TIT2', title, enc=3))
    w('id3v24-chapters.mp3', id3v2(4, [chap('ch1', 0, 500, 'Intro'), chap('ch2', 500, 900, 'Main'), frame(4, 'CTOC', b'toc\0' + bytes([3, 2]) + b'ch1\0ch2\0' + text_frame(4, 'TIT2', 'Contents', enc=3))]))
    w('id3v24-misc-frames.mp3', id3v2(4, [frame(4, 'POPM', b'user@example.com\0' + bytes([196]) + struct.pack('>I', 12)), frame(4, 'PCNT', struct.pack('>I', 42)),
        frame(4, 'UFID', b'http://musicbrainz.org\0' + b'0123456789abcdef'), frame(4, 'PRIV', b'com.example\0' + b'\x01\x02\x03'),
        frame(4, 'GEOB', b'\x00application/octet-stream\0name.bin\0object desc\0' + b'\xde\xad\xbe\xef'), frame(4, 'WXXX', b'\x00homepage\0http://example.com/'),
        frame(4, 'WOAR', b'http://example.com/artist'), frame(4, 'ZZZZ', b'unknown frame bytes'), text_frame(4, 'TIT2', 'After unknown', enc=3)]))
    w('id3v24-two-tags.mp3', id3v2(4, [text_frame(4, 'TIT2', 'First tag', enc=3)]) + id3v2(3, [text_frame(3, 'TPE1', 'Second tag')]))
    w('id3v1-only.mp3', None, id3v1())
    w('id3v11-track.mp3', None, id3v1(track=9))
    w('id3v24-and-v1.mp3', id3v2(4, basic_frames(4)), id3v1())
    w('id3v23-and-v1.mp3', id3v2(3, basic_frames(3)), id3v1(title='Different v1 title'))
    items = [ape_item('Title', 'Ape Title'), ape_item('Artist', 'Ape Artist'), ape_item('Album', 'Ape Album'), ape_item('Year', '2018'),
             ape_item('Track', '4/10'), ape_item('Disc', '1/2'), ape_item('Genre', 'Rock\0Jazz'), ape_item('Comment', 'Ape comment'),
             ape_item('REPLAYGAIN_TRACK_GAIN', '-7.00 dB'), ape_item('Lyrics', 'Ape lyrics'),
             ape_item('Cover Art (Front)', b'cover.png\0' + PNG, flags=2)]
    w('apev2-only.mp3', None, ape(items))
    w('apev2-footer-only.mp3', None, ape(items, header=False))
    w('apev1-only.mp3', None, ape(items[:8], version=1000, header=False))
    w('apev2-and-v1.mp3', None, ape(items), id3v1())
    w('id3v24-apev2-v1.mp3', id3v2(4, basic_frames(4)), ape(items), id3v1())
    w('lyrics3v2-and-v1.mp3', None, lyrics3v2(), id3v1())
    w('lyrics3v1.mp3', None, b'LYRICSBEGIN' + b'Old style lyrics' + b'LYRICSEND')
    w('apev2-lyrics3-v1.mp3', None, lyrics3v2(), ape(items[:3]), id3v1())

main()

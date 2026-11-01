#!/usr/bin/env python3
"""Lists the top-level objects of an ASF file and the objects inside its Header Object and Header Extension.

This is how the object GUIDs used by the reader were collected: by looking at what real files contain, with the
position of each object (child of the header, child of the header extension, first object after the header) and
the sizes as the only guide. Nothing here reads any specification.

    dump-objects.py FILE...     prints offset, size and GUID (as written in the file, and in text form) per object
"""
import struct
import sys
import uuid


def guid_text(b):
    return str(uuid.UUID(bytes_le=bytes(b))).upper()


def objects(f, start, end, depth, out):
    pos = start
    while pos + 24 <= end:
        f.seek(pos)
        head = f.read(24)
        if len(head) < 24:
            break
        guid, size = head[:16], struct.unpack("<Q", head[16:])[0]
        out.append((depth, pos, size, guid_text(guid)))
        if size < 24:
            break
        if depth == 0 and guid_text(guid) == "75B22630-668E-11CF-A6D9-00AA0062CE6C":
            objects(f, pos + 30, min(pos + size, end), 1, out)
        elif depth == 1 and guid_text(guid) == "5FBF03B5-A92E-11CF-8EE3-00C00C205365":
            objects(f, pos + 24 + 22, min(pos + size, end), 2, out)
        if depth == 0 and guid_text(guid) == "75B22636-668E-11CF-A6D9-00AA0062CE6C":
            # The Data Object's packets are not objects; the next object follows the packets.
            pass
        pos += size


def main():
    for path in sys.argv[1:]:
        print(path)
        out = []
        with open(path, "rb") as f:
            f.seek(0, 2)
            n = f.tell()
            objects(f, 0, n, 0, out)
        for depth, pos, size, g in out:
            print("  " * (depth + 1) + f"{pos:>10} {size:>10} {g}")


if __name__ == "__main__":
    main()

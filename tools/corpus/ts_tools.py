#!/usr/bin/env python3
"""Makes damaged or re-packaged copies of MPEG transport streams for tests.

usage: ts_tools.py COMMAND [options] INPUT OUTPUT

  parity204    append 16 bytes after every 188-byte packet (the layout with Reed-Solomon parity; the bytes are zero)
  drop         remove packets of one PID so its continuity counter jumps: --pid N|auto-video --every K --count C
  duplicate    repeat packets of one PID right after themselves: same options as drop
  errorflag    set transport_error_indicator on packets of one PID: same options as drop
  truncate     cut the file: --bytes N (negative: remove that many from the end)
  garbage      insert N bytes of 0xA5 between two packets: --at FRACTION --bytes N

Input is a plain 188-byte transport stream. The first video PID is taken from the PMT's stream types.
"""
import argparse
import sys

P = 188


def pid_of(pkt):
    return ((pkt[1] & 0x1F) << 8) | pkt[2]


def find_video_pid(data):
    # Read PAT then PMT sections (single packet each in files written by these tools) and pick the first video stream_type.
    pmt_pids = set()
    for i in range(0, min(len(data), P * 400), P):
        pkt = data[i:i + P]
        if pid_of(pkt) == 0 and pkt[1] & 0x40:
            sec = pkt[5:]
            n = ((sec[1] & 0xF) << 8) | sec[2]
            for k in range(8, 3 + n - 4, 4):
                prog = (sec[k] << 8) | sec[k + 1]
                if prog:
                    pmt_pids.add(((sec[k + 2] & 0x1F) << 8) | sec[k + 3])
            break
    for i in range(0, min(len(data), P * 400), P):
        pkt = data[i:i + P]
        if pid_of(pkt) in pmt_pids and pkt[1] & 0x40:
            sec = pkt[5:]
            n = ((sec[1] & 0xF) << 8) | sec[2]
            pil = ((sec[10] & 0xF) << 8) | sec[11]
            k = 12 + pil
            while k + 5 <= 3 + n - 4:
                st = sec[k]
                pid = ((sec[k + 1] & 0x1F) << 8) | sec[k + 2]
                il = ((sec[k + 3] & 0xF) << 8) | sec[k + 4]
                if st in (0x01, 0x02, 0x1B, 0x24, 0x33, 0x10):
                    return pid
                k += 5 + il
    raise SystemExit("no video PID found")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("command")
    ap.add_argument("--pid", default="auto-video")
    ap.add_argument("--every", type=int, default=40)
    ap.add_argument("--count", type=int, default=5)
    ap.add_argument("--bytes", type=int, default=0)
    ap.add_argument("--at", type=float, default=0.5)
    ap.add_argument("input")
    ap.add_argument("output")
    a = ap.parse_args()
    data = open(a.input, "rb").read()
    out = bytearray()
    cmd = a.command
    if cmd == "parity204":
        for i in range(0, len(data) - P + 1, P):
            out += data[i:i + P] + bytes(16)
    elif cmd in ("drop", "duplicate", "errorflag"):
        pid = find_video_pid(data) if a.pid == "auto-video" else int(a.pid)
        seen = done = 0
        for i in range(0, len(data) - P + 1, P):
            pkt = bytearray(data[i:i + P])
            hit = False
            if pid_of(pkt) == pid and pkt[3] & 0x10 and not pkt[1] & 0x40:
                seen += 1
                hit = seen % a.every == 0 and done < a.count
            if hit:
                done += 1
                if cmd == "drop":
                    continue
                if cmd == "duplicate":
                    out += pkt
                else:
                    pkt[1] |= 0x80
            out += pkt
    elif cmd == "truncate":
        n = a.bytes
        out = bytearray(data[:n] if n >= 0 else data[:len(data) + n])
    elif cmd == "garbage":
        cut = int(len(data) * a.at) // P * P
        out = bytearray(data[:cut] + bytes([0xA5]) * a.bytes + data[cut:])
    else:
        sys.exit("unknown command " + cmd)
    open(a.output, "wb").write(out)


main()

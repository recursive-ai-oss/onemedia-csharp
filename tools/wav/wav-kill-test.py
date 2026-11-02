#!/usr/bin/env python3
"""Kill test for the atomic WAVE writer: start `WavCheck big --atomic`, kill -9 it at a random moment, and check the target.

Each round runs against a target that either does not exist or holds an earlier complete file. After the kill the target must be absent
(first case), unchanged (second case), or, if the writer finished before the signal, a complete, valid file of the expected length. A temporary file
may be left behind; it must never be the target.

Usage: wav-kill-test.py --check "dotnet /path/OneMedia.WavCheck.dll" --dir DIR [--rounds 300] [--gib 0.3] [--ffprobe PATH]
"""
import argparse
import hashlib
import os
import random
import shlex
import signal
import subprocess
import sys
import time


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", required=True)
    ap.add_argument("--dir", required=True)
    ap.add_argument("--rounds", type=int, default=300)
    ap.add_argument("--gib", type=float, default=0.3)
    ap.add_argument("--ffprobe", default="ffprobe")
    a = ap.parse_args()
    os.makedirs(a.dir, exist_ok=True)
    target = os.path.join(a.dir, "out.wav")
    cmd = shlex.split(a.check) + ["big", target, str(a.gib), "--atomic"]
    # The expected file length: header 48 + fmt 40 + data header 8 + data, data a whole number of 6-byte frames per 1 MiB packet.
    ref = os.path.join(a.dir, "reference.wav")
    subprocess.run(shlex.split(a.check) + ["big", ref, str(a.gib), "--atomic"], check=True, capture_output=True)
    expected_len = os.path.getsize(ref)
    expected_sha = sha(ref)
    os.unlink(ref)
    finished = killed = bad = kept = absent = 0
    for i in range(a.rounds):
        have_old = i % 2 == 1
        if have_old:
            with open(target, "wb") as f:
                f.write(b"old complete file " * 1000)
            old_sha = sha(target)
        elif os.path.exists(target):
            os.unlink(target)
        p = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        time.sleep(random.uniform(0.15, 1.2))
        if p.poll() is None:
            os.killpg(p.pid, signal.SIGKILL)
            killed += 1
        else:
            finished += 1
        p.wait()
        if os.path.exists(target):
            if have_old and sha(target) == old_sha:
                kept += 1
            elif os.path.getsize(target) == expected_len and sha(target) == expected_sha:
                pass  # the writer finished before the signal
            else:
                bad += 1
                print("round", i, "target is neither the old file nor a complete new one, size", os.path.getsize(target))
        else:
            if have_old:
                bad += 1
                print("round", i, "the old target disappeared")
            else:
                absent += 1
        for f in os.listdir(a.dir):
            if f != "out.wav":
                os.unlink(os.path.join(a.dir, f))  # the temporary files of killed writers
    print("rounds", a.rounds, "killed", killed, "finished-before-kill", finished, "old-kept", kept, "target-absent", absent, "corrupt", bad)
    if os.path.exists(target):
        os.unlink(target)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

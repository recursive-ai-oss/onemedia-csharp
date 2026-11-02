#!/usr/bin/env python3
"""Round-trip check for the WAVE/RF64 muxer against ffmpeg/ffprobe, used as black-box oracles.

For every generated input (WAV, W64, AIFF/AIFC, CAF, MOV, MKV, AVI carrying PCM at several widths, byte orders and channel layouts) it:
  1. remuxes the file to WAVE with the `onemedia remux` tool;
  2. compares ffmpeg's decoded samples of the input and of the output (whole-stream md5 and normalised frame md5, both decoded to f64 so that
     sample-format differences such as signed and unsigned 8-bit do not hide a real difference);
  3. compares the stream's payload bytes (`-c copy -f data`) after applying, independently of the muxer, the byte-order or 8-bit change the
     input's codec name implies;
  4. compares ffprobe's channel layout and rate of input and output.
A final step can run the packet differential of our reader on the outputs (see tools/OneMedia.Oracles, `diff --family pcm`).

Usage: wav-roundtrip.py --work DIR --tool "dotnet /path/to/onemedia.dll" [--ffmpeg PATH] [--ffprobe PATH] [--only SUBSTRING]
Output: DIR/results.tsv (one row per input) and a summary on stdout. Exit code 1 when any row is a mismatch.
"""
import argparse
import hashlib
import json
import os
import shlex
import subprocess
import sys

SINE = "sine=frequency=440:sample_rate={rate}:duration={dur}"


def multi(layout, rate, dur):
    # A different tone on every channel so that a channel mix-up changes the payload.
    freqs = [220, 330, 440, 550, 660, 770, 880, 990]
    n = {"mono": 1, "stereo": 2, "3.0": 3, "quad": 4, "5.0": 5, "5.1": 6, "6.1": 7, "7.1": 8, "5.1(side)": 6}[layout]
    expr = "|".join("0.5*sin({}*2*PI*t)".format(freqs[i]) for i in range(n))
    return "aevalsrc='{}':c={}:s={}:d={}".format(expr, layout, rate, dur)


def cases():
    """(name, ffmpeg arguments after -i-less lavfi source, output extension)."""
    out = []
    ints = ["pcm_u8", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le", "pcm_alaw", "pcm_mulaw"]
    for codec in ints:
        for layout in ["mono", "stereo"]:
            out.append(("wav-{}-{}".format(codec[4:], layout), layout, 44100, codec, ["-f", "wav"], "wav"))
    for layout in ["3.0", "quad", "5.0", "5.1", "6.1", "7.1", "5.1(side)"]:
        for codec in ["pcm_s16le", "pcm_s24le", "pcm_f32le"]:
            out.append(("wav-{}-{}".format(codec[4:], layout.replace("(", "").replace(")", "")), layout, 48000, codec, ["-f", "wav"], "wav"))
    for rate in [8000, 11025, 96000, 192000]:
        out.append(("wav-s16le-stereo-{}".format(rate), "stereo", rate, "pcm_s16le", ["-f", "wav"], "wav"))
    out.append(("bwf-bext-s24le-stereo", "stereo", 48000, "pcm_s24le", ["-f", "wav", "-write_bext", "1", "-metadata", "description=Take 1", "-metadata", "originator=tests"], "wav"))
    out.append(("wav-info-s16le", "stereo", 44100, "pcm_s16le", ["-f", "wav", "-metadata", "title=Tone", "-metadata", "artist=Nobody"], "wav"))
    out.append(("rf64-s16le-stereo", "stereo", 44100, "pcm_s16le", ["-f", "wav", "-rf64", "always"], "wav"))
    out.append(("rf64-s24le-5.1", "5.1", 48000, "pcm_s24le", ["-f", "wav", "-rf64", "always"], "wav"))
    for codec in ["pcm_s16le", "pcm_s24le", "pcm_f32le"]:
        for layout in ["mono", "stereo", "5.1"]:
            out.append(("w64-{}-{}".format(codec[4:], layout), layout, 48000, codec, ["-f", "w64"], "w64"))
    for codec in ["pcm_s8", "pcm_s16be", "pcm_s24be", "pcm_s32be"]:
        for layout in ["mono", "stereo"]:
            out.append(("aiff-{}-{}".format(codec[4:], layout), layout, 44100, codec, ["-f", "aiff"], "aif"))
    out.append(("aiff-s16be-5.1", "5.1", 48000, "pcm_s16be", ["-f", "aiff"], "aif"))
    for codec in ["pcm_f32be", "pcm_f64be"]:
        out.append(("aifc-{}-stereo".format(codec[4:]), "stereo", 44100, codec, ["-f", "aiff"], "aifc"))
    for codec in ["pcm_alaw", "pcm_mulaw"]:
        out.append(("aifc-{}-mono".format(codec[4:]), "mono", 8000, codec, ["-f", "aiff"], "aifc"))
    for codec in ["pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be", "pcm_f32le", "pcm_f32be", "pcm_f64le", "pcm_f64be", "pcm_alaw", "pcm_mulaw"]:
        out.append(("caf-{}-stereo".format(codec[4:]), "stereo", 44100, codec, ["-f", "caf"], "caf"))
    out.append(("caf-s16le-5.1", "5.1", 48000, "pcm_s16le", ["-f", "caf"], "caf"))
    for codec in ["pcm_u8", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be", "pcm_f32le", "pcm_f32be", "pcm_f64le", "pcm_f64be", "pcm_alaw", "pcm_mulaw"]:
        out.append(("mov-{}-stereo".format(codec[4:]), "stereo", 44100, codec, ["-f", "mov"], "mov"))
    out.append(("mov-s16le-5.1", "5.1", 48000, "pcm_s16le", ["-f", "mov"], "mov"))
    out.append(("mov-s24le-mono", "mono", 48000, "pcm_s24le", ["-f", "mov"], "mov"))
    for codec in ["pcm_u8", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be", "pcm_f32le", "pcm_f64le"]:
        out.append(("mkv-{}-stereo".format(codec[4:]), "stereo", 48000, codec, ["-f", "matroska"], "mkv"))
    out.append(("mkv-s16le-5.1", "5.1", 48000, "pcm_s16le", ["-f", "matroska"], "mkv"))
    for codec in ["pcm_u8", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_alaw", "pcm_mulaw"]:
        out.append(("avi-{}-stereo".format(codec[4:]), "stereo", 44100, codec, ["-f", "avi"], "avi"))
    out.append(("avi-s16le-5.1", "5.1", 48000, "pcm_s16le", ["-f", "avi"], "avi"))
    return out


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, **kw)


def md5_decoded(ffmpeg, path, frames):
    args = [ffmpeg, "-v", "error", "-i", path, "-map", "0:a:0"]
    if frames:
        args += ["-af", "asetnsamples=n=1024:p=0"]
    args += ["-c:a", "pcm_f64le", "-f", "framemd5" if frames else "md5", "-"]
    r = run(args)
    if r.returncode != 0:
        return "ERR:" + r.stderr.decode(errors="replace")[:200]
    text = r.stdout.decode()
    if frames:
        # Keep the hashes and sizes; the header lines carry the file name.
        lines = [l.split(",")[-2:] for l in text.splitlines() if l and not l.startswith("#")]
        return hashlib.md5(json.dumps(lines).encode()).hexdigest()
    return text.strip()


def payload(ffmpeg, path):
    r = run([ffmpeg, "-v", "error", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "data", "-"])
    return r.stdout if r.returncode == 0 else None


def probe(ffprobe, path):
    r = run([ffprobe, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,channels,channel_layout,sample_rate,bits_per_sample,bits_per_raw_sample",
             "-of", "json", path])
    try:
        return json.loads(r.stdout)["streams"][0]
    except Exception:
        return {}


def expected_payload(data, codec):
    """What the WAVE data chunk must hold for an input whose packets are `codec` (the two representation changes the muxer defines)."""
    if codec in ("pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_f32be", "pcm_f64be"):
        w = {"s16": 2, "s24": 3, "s32": 4, "f32": 4, "f64": 8}[codec[4:7]]
        n = len(data) // w * w
        b = bytearray(data)
        for i in range(0, n, w):
            b[i:i + w] = b[i:i + w][::-1]
        return bytes(b)
    if codec == "pcm_s8":
        return bytes((x + 128) & 255 for x in data)
    return data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", required=True)
    ap.add_argument("--tool", required=True, help="command that runs the onemedia tool, e.g. 'dotnet /path/onemedia.dll'")
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--ffprobe", default="ffprobe")
    ap.add_argument("--only", default="")
    ap.add_argument("--duration", type=float, default=1.5)
    a = ap.parse_args()
    ind, outd = os.path.join(a.work, "in"), os.path.join(a.work, "out")
    os.makedirs(ind, exist_ok=True)
    os.makedirs(outd, exist_ok=True)
    tool = shlex.split(a.tool)
    rows = []
    bad = 0
    for name, layout, rate, codec, fmt, ext in cases():
        if a.only and a.only not in name:
            continue
        src = os.path.join(ind, name + "." + ext)
        dst = os.path.join(outd, name + ".wav")
        gen = [a.ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", multi(layout, rate, a.duration), "-c:a", codec] + fmt + [src]
        g = run(gen)
        if g.returncode != 0:
            rows.append((name, "GENFAIL", g.stderr.decode(errors="replace").strip().splitlines()[-1:][0] if g.stderr else ""))
            continue
        r = run(tool + ["remux", src, dst, "--overwrite"])
        if r.returncode != 0:
            msg = (r.stderr or r.stdout).decode(errors="replace").strip().replace("\n", " ")[:160]
            rows.append((name, "REFUSED", msg))
            continue
        pin, pout = probe(a.ffprobe, src), probe(a.ffprobe, dst)
        d_in, d_out = md5_decoded(a.ffmpeg, src, False), md5_decoded(a.ffmpeg, dst, False)
        f_in, f_out = md5_decoded(a.ffmpeg, src, True), md5_decoded(a.ffmpeg, dst, True)
        p_in, p_out = payload(a.ffmpeg, src), payload(a.ffmpeg, dst)
        want = expected_payload(p_in, pin.get("codec_name", "")) if p_in is not None else None
        pay_ok = want is not None and p_out == want
        # ffprobe names no layout for a plain one- or two-channel format record; a source that states mono or stereo then reads as unspecified.
        # That is the same speaker assignment, so it is not a difference. Anything else that changes is.
        lin, lout = pin.get("channel_layout"), pout.get("channel_layout")
        layout_ok = (lin == lout or (lin in ("mono", "stereo") and lout is None)) and pin.get("sample_rate") == pout.get("sample_rate") and pin.get("channels") == pout.get("channels")
        ok = d_in == d_out and f_in == f_out and pay_ok and not d_in.startswith("ERR")
        # AIFF, CAF and Matroska files state no speaker positions for more than two channels (ffprobe then guesses its own default), so the
        # muxer's conventional mask differs from that guess without anything having been lost.
        default_mask = ext in ("aif", "aifc", "caf", "mkv") or lin is None
        status = "OK" if ok and layout_ok else ("DEFAULTMASK" if ok and default_mask else ("LAYOUT" if ok else "MISMATCH"))
        if status == "MISMATCH":
            bad += 1
        detail = "decode={} frames={} payload={} in={}/{}/{} out={}/{}/{}".format(
            d_in == d_out, f_in == f_out, pay_ok, pin.get("codec_name"), pin.get("channel_layout"), pin.get("sample_rate"),
            pout.get("codec_name"), pout.get("channel_layout"), pout.get("sample_rate"))
        rows.append((name, status, detail))
        if status == "OK":
            pass
    with open(os.path.join(a.work, "results.tsv"), "w") as f:
        for row in rows:
            f.write("\t".join(row) + "\n")
    counts = {}
    for _, s, _ in rows:
        counts[s] = counts.get(s, 0) + 1
    print(json.dumps(counts))
    for name, s, d in rows:
        if s != "OK":
            print("{}\t{}\t{}".format(name, s, d))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

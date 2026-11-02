# Reconstructing cut-off MP4, MOV and 3GP recordings

A phone or camera that loses power mid-recording leaves a file with `ftyp` and a media data box (`mdat`) but no usable movie box
(`moov`): the sample tables lived only in memory, or at the end of the file. `IsoReconstructor` (namespace
`RecursiveAI.OneMedia.Repair.Reconstruction`) walks the media data, splits it into samples from the samples' own syntax, rebuilds the
timing and writes a **new** file with a fresh `moov` at the end. Nothing is decoded: framing uses NAL unit and slice headers, OBU headers
and audio frame headers only. The input is never modified, and a report says what was recovered, how far to trust each track and
which input ranges were used or skipped.

## Through `Media.Repair`

`Media.Repair(input, output, reference)` and `onemedia repair <in> <out> [--reference <file>]` send an MP4/MOV/3GP whose movie box is missing or cut off
here. The common `RepairReport` carries an item `Rebuilt moov`, one `Dropped` item per skipped range, one `RepairTrackReport` per track (samples,
partial samples dropped, confidence, the track's input ranges), the reconstruction's notes as warnings and the whole `ReconstructionReport` in
`RepairReport.Reconstruction`. Status is `Repaired`, or `PartiallyRepaired` when a track came back empty or under 90 % of the media data was
framed; `Unrepairable` (no output) when nothing could be framed or under 2 % of the media data could (typically codec setup that matched by coincidence, or a reference from another recording), with the reason as a warning. The core package has no AAC probe: pass
`AacReconstructionProbe.Options()` from the audio package as the `reconstruction` argument (the tool does) to recover AAC tracks.

Index repair of intact files and truncated Matroska/AVI are not part of this; see the repair notes of those formats.

## Inputs it accepts

| Input | How it is found |
|---|---|
| `ftyp`, `mdat` (size field larger than the file, or 0 meaning "to the end"), `moov` lost with the tail | box chain walk |
| `mdat` size field correct but the `moov` after it cut off or incomplete | box chain walk; bytes after the box are reported as `AfterMediaData` and not scanned |
| `moov` first, zeroed or damaged; chain broken before `mdat` | search for an `mdat` box header in the first 64 MiB |
| no `mdat` header at all | bytes after the file type box are scanned as media data (only if more than 4 KiB), with a note |
| nothing that looks like media data | `ReconstructionException` with `ReconstructionFailure.NoMediaData` |

## With and without a reference file

A **reference** is a healthy file of the same device and settings. It supplies, per track: the sample entry (`avcC`, `hvcC`, `av1C`, `esds`,
`damr`, PCM layout, colour/`pasp`/`clap`/`fiel` boxes), the timescale, the usual sample duration, encoder priming, a bound on plausible sample
sizes (16 times the largest of the first 400 samples seen, at least 1 MiB), and for PCM the usual packet size. The reference's chunk
interleave pattern is **not** used; the walker is driven by what the bytes are, so a recording whose chunking differs from the reference
still works.

**Without a reference**, the codec setup must come from the stream:

- Video: SPS/PPS (H.264), VPS/SPS/PPS (HEVC) or a sequence header (AV1) found in the first 8 MiB of media data. Many cameras repeat
  parameter sets before every key frame; encoders that write MP4 (x264, x265 and most phone encoders) keep them only in `avcC`/`hvcC`, which
  is in the lost `moov`. Then no video track can be set up and the report says a reference file is needed.
- Audio: AMR (narrow- and wideband), MPEG audio layers I–III and AC-3/E-AC-3 are found by their frame headers and accepted when six frames
  in a row chain correctly. AAC is found through the `.Audio` adapter, which tries AAC-LC AudioSpecificConfigs at 12 sample rates and 1–2
  channels against the data.

| Without a reference | Result |
|---|---|
| H.264/HEVC/AV1 with in-band parameter sets | High confidence if the stream carries its timing (VUI / AV1 timing info), else timing measured against audio (Medium) or assumed 30 fps (Low) |
| AMR, MP3, AC-3 | recovered, setup `Guessed`, confidence Low |
| AAC | recovered with a guessed AudioSpecificConfig; the frames parse under it but the true sample rate cannot be told from them; Low |
| PCM | **not recoverable**: PCM has no framing, so no byte of it can be told from other data without knowing its channel count, bit depth and packet size. The track is not listed; video, if found, still recovers, and the report note says the unframed bytes belong to a track whose framing is unknown |
| video with parameter sets only in `avcC`/`hvcC` | not recoverable without a reference |

## Walking the media data

The scanner (`MediaDataScanner`) keeps a position and, at each position, asks the known tracks' framers, video first, then audio, whether a
whole sample starts there. Reading goes through a window, so memory does not grow with the file; only the sample list does (about 24 bytes a
sample).

### Video

- **H.264 / HEVC, length-prefixed** (the NAL length size comes from the reference or is found in the stream): an access unit is a run of NAL units
  starting at a position where a length prefix gives a NAL that passes the checks below, and ending where the next access unit begins
  (a first slice of a new picture) or at the end of the data. Access unit delimiters, SEI, parameter sets and filler are kept in the sample.
- **AV1**: a temporal unit is a run of OBUs (with size fields) up to the next temporal delimiter.

NAL validity checks (ITU-T H.264 §7.3.1.2, §7.4.1; H.265 §7.3.1.2):

1. forbidden_zero_bit is 0; nal_ref_idc/nal_unit_type combinations the standard allows (for example nal_ref_idc is 0 for SEI/AUD, non-zero for
   IDR); HEVC temporal id plus one is non-zero and nuh_layer_id within bounds.
2. A NAL's payload holds no `00 00 00`, `00 00 01` or `00 00 02` (emulation prevention, H.264 §7.4.1), which stray data almost always does.
3. The first slice header is parsed with range checks (H.264 §7.3.3: first_mb_in_slice, slice_type, pic_parameter_set_id known, frame_num within its
   bits, IDR gives frame_num 0, picture order count LSB, reference list modification and marking commands, cabac_init_idc, QP between 0 and 51,
   deblocking fields; H.265 §7.3.6: slice type, IRAP pictures are I). Parameter sets learned from the reference or the stream supply the field
   widths.
4. frame_num continues the last reference picture's (H.264 §7.4.3: it increments by one after a reference picture, modulo the maximum).
5. After a gap no track explains, a video access unit is accepted only if the next one follows within 256 KiB with a continuing frame_num (or
   the data ends); a non-video sample needs a chain of two samples, each of which may follow up to 32 bytes of something unframed (the
   tiny samples of a descriptor or timecode track that sits between audio frames, such as GoPro's 16-byte `fdsc` items). A candidate shorter than 16 bytes (a silent audio frame, which parses by chance inside other data far more easily) needs a chain of four. When the same gap has followed three samples in a row, the next sample is looked for where that gap ends before every position is tried, so bytes inside a regular small item that parse as a tiny sample are not taken for one.

The last access unit is accepted when its NAL chain ends exactly at the end of the file and it passes the same checks as any other (a later missing slice of the
same picture cannot be told). One that would run past the end is dropped and reported as `PartialSample`.

### Audio

| Codec | Sample | Checks |
|---|---|---|
| AMR-NB/WB | one frame with its table-of-contents byte | RFC 4867 §5.3: F=0, defined frame type, Q=1, padding bits 0 |
| MP3 (and MP2 with a reference) | one frame | header parse (ISO/IEC 11172-3 / 13818-3), sample rate and layer equal the track's, frame length from the header |
| AC-3 / E-AC-3 | one sync frame | ETSI TS 102 366 §5.3 sync word, frame size from the header, sample rate equal |
| AAC | one raw_data_block | `AacFrameProbe` (OneAudio 1.7) through `RecursiveAI.OneMedia.Audio`; without the adapter an AAC reference track is dropped with a note |
| PCM | a fixed run | bytes between other tracks' samples, cut at the reference's usual packet size; no check is possible, confidence Medium |
| GoPro `gpmd` | a KLV block | GPMF key/size structure |

Without a reference an audio track is **discovered** at a sample boundary when the candidate framer chains six samples in a row with no bytes
between them (allowing gaps here let guessed AAC setups "find" a track inside PCM and video, so a recording that interleaves a small unknown track
with its sound keeps its video but loses the sound without a reference). Discovery (no gaps allowed) tries MPEG audio layer III (layers I and II have no sample entry in the ISO muxer), AC-3, AMR-NB, AMR-WB, then the adapter's guesses, at positions where the previous sample ended.

### Interleave and gaps

At a position where several framers accept, the first in the order video, then audio wins. Bytes no track explains are skipped one position at a
time until a framer finds a sample that is confirmed by what follows it (see rule 5 above); the skipped stretch is reported as `Unframed`. More than 2 % unframed bytes caps every track's
confidence at Medium and adds a note (another track whose framing is unknown, or damage).

## Timing

- **Reference:** the reference track's timescale and the median duration of its first samples. Video decode times step by that duration.
- **Stream:** frame rate from the SPS VUI timing info (H.264, HEVC) or the AV1 timing info.
- **Audio clock:** else the slope of audio time against video sample number (least squares over the last three quarters of the data), snapped to
  a common rate (23.976, 24, 25, 29.97, 30, 50, 59.94, 60, 15, 12, 10, 20, 48, 100, 120 ...) within 6 %. Confidence Medium.
- **Assumed:** else `ReconstructionOptions.AssumedFrameRate`, default 30/1. Confidence Low.
- **Audio:** cumulative samples at the sample-rate timescale (frame length from the codec: 160 for AMR-NB, 320 for AMR-WB, 1152 or 576 for MPEG
  audio, 1536 for AC-3, 1024 for AAC, the packet size for PCM). Priming comes from the reference.
- **Picture order (H.264 picture_order_cnt_type 0, HEVC):** POC is reconstructed from the slice headers (H.264 §8.2.1, H.265 §8.3.1), restarted at
  each IDR; the step between display positions is the gcd of the gaps between the sorted counts of each coded video sequence (differences between decode-order neighbours can all be multiples of the real step in a deep reference pyramid). Presentation times follow, decode times are in file order, and the
  reorder delay is written as an edit list (the video track's priming), the way ffmpeg and x264 write B-frame files. Field pictures and POC type 1
  keep decode order with a note; AV1 temporal units are already in output order.

## The output

Written through `IsoMuxer` with the layout "movie box last", the original major brand, minor version and compatible brands, and the sample entries
from the reference or the stream. The media data is copied once, sample by sample, without re-encoding. A track that recovered no sample is not
written. The output opens in any reader; it is not a byte copy of the input and does not try to be.

`ReconstructionReport` lists, per track: samples, payload bytes, duration, **confidence**, **setup source** (`Reference`, `InStream`, `Guessed`)
and notes; and per file: the scanned span, `UsedRanges`, up to 1000 `SkippedRanges` (reason `Unframed`, `PartialSample`, `AfterMediaData`), their
total count, `RecoveredFraction` and notes.

| Confidence | Meaning |
|---|---|
| High | framing validated sample by sample, setup from a reference or parameter sets in the stream, timing from the reference or the stream's own timing info, no large unframed span |
| Medium | timing estimated (audio clock), picture order unknown, PCM, or more than 2 % unframed bytes |
| Low | setup guessed (AAC ASC, audio without a reference) or frame rate assumed |
| None | nothing recovered |

## Limits

The `MediaLimits` object passes through: `MaxPacketBytes` bounds a sample, `MaxSampleTableEntries` bounds samples per track, `MaxTracks` and
`MaxAllocationBytes` apply as in the muxer. Reference parsing reads the first 4000 packets at most. Cancellation is checked at every sample. A walk
over data no framer accepts costs time proportional to the data times the checks for one failing position (the framers reject within a few
bytes), and the window is a fixed size.

## Known gaps

- Fragmented MP4 (`moof` boxes) and files whose `mdat` is split into several boxes: only the first `mdat` is walked. Fragmented files lose only the
  last fragment and are the business of index repair.
- A reference track whose codec has no framer here (VP9, ProRes, ALAC, Opus in MP4 and so on) is left out with a note.
- MBAFF/field streams: access units are cut correctly, but output order is not worked out (decode order with a note).
- HEVC open-GOP leading pictures and dependent slice segments follow the slice-header rules above; real-device files with exotic structure are not
  yet in the test set (the device corpus would add real interleave patterns, field/MBAFF H.264, HEVC from phones, and dashcam streams with unusual
  metadata tracks).
- `tmcd` timecode tracks (4-byte samples), GoPro `fdsc` (16-byte samples) and other tracks without content to check are skipped and show
  up as `Unframed` ranges; they push the recovered fraction down by their share of the media data (about 1 % in the GoPro samples).
- GoPro `gpmd` samples that hold more than one top-level `DEVC` item (the hero6 file with a Bluetooth remote) are cut into one sample per item,
  so the rebuilt track has more, shorter samples than the original (their bytes are the original's).
- AAC with more than two channels: OneAudio's frame probe does not take multichannel configurations, so such a track is dropped with a note
  and its bytes stay unframed (observed on a 5.1 Blender clip: 14 % of its media data).
- AAC without a reference: the sample rate (and so the time of every audio sample) is a guess from the frames, 44.1 kHz first; a 48 kHz recording
  rebuilt this way has the right bytes and wrong audio times, which is why its confidence is Low. A reference removes the guess.
- Irregular sample durations of the original (a first picture held for several frames, repeated-frame durations in `stts`) cannot be told from the
  stream: video times use one frame period, so later samples can be offset from the original by those holds while bytes, keyframes and order are
  equal (observed on a Blender clip: sample 0 lasts 11 frames and two later samples 2 frames each).
- Dependence on a reference for PCM and for video without in-band parameter sets is by design (see above).

## Checks

`tools/reconstruct-fixtures.py` makes cut-off copies of healthy files: 11 cut points between 25 % and 95 % of the media data (with a few bytes of
jitter so cuts rarely land on a sample boundary), every third with the `mdat` size field zeroed, and for moov-first files copies with the `moov`
zeroed. `tools/reconstruct-sweep.py` reconstructs each with and without a reference, compares the packets with the healthy original's (bytes,
keyframe flag, decode and presentation times within one frame), decodes the result to null with ffmpeg and runs `MP4Box -info`.
`tools/run-reconstruct-checks.sh` runs the sets on a build machine. Numbers are in the commit that last changed this file's tables.

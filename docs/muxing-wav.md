# Writing WAVE, Broadcast Wave and RF64

`RecursiveAI.OneMedia.Riff.WavMuxer` (`src/OneMedia/Riff/Mux/`) writes one audio track as RIFF WAVE, Broadcast Wave (BWF) or RF64/BW64. It follows the
`IMuxer` contract in `muxing.md`; this page is normative for what is specific to WAVE. Specifications: Microsoft's WAVE and WAVE_FORMAT_EXTENSIBLE
documentation, EBU Tech 3285 (BWF) with its supplements, EBU Tech 3306 (RF64), ITU-R BS.2088 (BW64), the iXML specification and the AES46 `cart`
layout as ExifTool's RIFF tag documentation lists it. Nothing is decoded, converted between widths or rates, or encoded.

## What is written

```
RIFF <size> WAVE
JUNK 28 bytes           reserves the room of ds64 (RF64 from the start: ds64 itself)
fmt                     16, 18 or 40 bytes, or the source's own record
fact                    for every format whose format tag is not 1 (integer PCM): the frame count
bext, cart, iXML, LIST INFO, other chunks (from WavMuxOptions), JUNK padding
data <size> samples [pad byte]
chunks marked AfterData
```

Chunk sizes and the RIFF size are patched when `Finish` is called, so the destination must seek. A destination that cannot is refused by the constructor
with `MuxRefusalReason.OutputNotSuitable` (a streamed WAVE has no honest size to state). `WavMuxer.CreateFile(path)` writes under a temporary name next
to the target, flushes it to disk and renames it at `Finish`: the target name never shows a partly written file, a muxer disposed early deletes its
temporary file, and an earlier killed writer's leftover is removed by the next write to the same target.

## Layouts and the 4 GiB limit

| `WavLayout` | Header | Past 4 GiB − 9 bytes |
|---|---|---|
| `Auto` (default) | `RIFF`, `JUNK` reservation | at `Finish` the header is rewritten in place as RF64: `RIFF`→`RF64`, `JUNK`→`ds64`, RIFF size and `data` size → `0xFFFFFFFF`; no sample byte moves |
| `RiffOnly` | `RIFF`, no reservation | the packet that would cross the limit is refused (`LimitExceeded`) before any of its bytes are written |
| `Rf64` | `RF64`, `ds64` | RF64 whatever the size |
| `Bw64` | `BW64`, `ds64` | as `Rf64` (ITU-R BS.2088) |

`ds64` holds the 64-bit RIFF size (file length − 8), data size and frame count; its table of oversized chunks is empty (no chunk the muxer writes is larger
than 4 GiB − 1). Tech 3306 v2 now defers to BS.2088, so the field layout was checked against files written by ffmpeg (`-rf64 always`) and read back by
ffprobe and MediaInfo (see Results).

## The format record

| Source of the track | Written |
|---|---|
| a `fmt ` or `waveformatex` configuration that parses and names the track's codec (WAVE, W64, AVI) | the record **byte for byte**: PCM of any width, float, A-law, µ-law, ADPCM, MP3, AC-3, ADTS AAC, any tag. The packets are the data chunk's bytes |
| other PCM (AIFF, CAF, MOV, Matroska, FLV, ASF without a record) | described from the layout the source states (below), WAVEFORMATEX or WAVE_FORMAT_EXTENSIBLE |
| any other codec without a record | refused, `UnsupportedCodec`, naming the missing record |

A described record is `WAVEFORMATEX` (16 bytes for tag 1, 18 with `cbSize` 0 for others) for integer PCM of 8 or 16 bits, float and G.711 with one or two
channels. It is `WAVE_FORMAT_EXTENSIBLE` (40 bytes: valid bits, channel mask, sub-format GUID = tag + Microsoft's base GUID) for more than two channels,
integers wider than 16 bits, samples with fewer valid bits than their container, or a channel mask that is not the default of the channel count.

Channel masks: the source's own mask when it comes in a `fmt ` record; else `WavTrackOptions.ChannelMask`; else the conventional mask of the count
(1 front centre, 2 left and right, 3 FL FR FC, 4 quad, 5 five, 6 5.1, 7 6.1, 8 7.1 as `WavMuxer.DefaultChannelMask`; 0 beyond 8). The report notes
every mask assigned this way. AIFF, CAF and Matroska files say nothing a reader here uses, so their 5.1 files come out with the 5.1 mask.

## The two representation changes

WAVE stores samples little-endian and 8-bit samples unsigned. A source layout that differs gets exactly these changes and nothing else:

- big-endian 16, 24, 32-bit integers and 32, 64-bit floats (AIFF, AIFF-C `twos`/`in24`/`in32`/`fl32`/`fl64`, CAF, QuickTime `twos`/`in24`/`in32`/`fl32`/`fl64`
  without an `enda` atom, Matroska `A_PCM/INT/BIG`, FLV `pcm_s16be`) are byte-swapped sample by sample (samples split across two packets are handled);
- signed 8-bit integers (AIFF, CAF) are offset by 128.

Both are noted in `MuxReport.Notes`. Any other layout change (width, rate, integer↔float) is refused as `UnsupportedCodec`. A packet that ends inside a
sample of a converted stream leaves those last bytes as they were and says so.

Layout resolution for tracks without a record: `WavTrackOptions.Pcm`; else the codec tag (`sowt`, `twos`, `in24`, `in32`, `fl32`, `fl64`, `raw `, `alaw`,
`ulaw`, `ipcm`/`fpcm` with `pcmC`, Matroska `A_PCM/…`, `pcm_s16be`, WAVE tags `0x0003`/`0x0006`/`0x0007`); else signed little-endian at the declared bit
depth (16 when undeclared; 8 is unsigned). A QuickTime `in24`/`in32`/`fl32`/`fl64` entry that carries an `enda` atom with a non-zero value is little-endian;
the remux hook reads it (ffmpeg writes little-endian 24 and 32-bit PCM that way).

## Metadata chunks

`WavMuxOptions` carries `Bext` (`BextChunk`), `Cart` (`CartChunk`), `IXml` (text, checked well-formed with a DTD-free `XmlReader`), `Info`
(`LIST INFO` entries, UTF-8 with a terminating zero), `Chunks` (any other chunk, before or after the data) and `PaddingBytes` (a `JUNK` chunk in front of
`data` that the in-place tag editor can use later).

- `bext`: 602 fixed bytes plus coding history; ASCII fields that must fit (never truncated); version 0, 1 with a UMID, 2 with loudness; unset loudness
  fields hold `0x7FFF`.
- `cart`: the 2048 fixed bytes and the free text; fields as listed in ExifTool's RIFF documentation.
- A text that is not ASCII or does not fit is an `ArgumentException` from the constructor, before anything is written.

## Remux

`Remuxer.Remux(input, output, RemuxFormat.Wav | Rf64)` and the path overloads (`.wav .wave .bwf` → `Wav`, `.rf64` → `Rf64`; CLI `onemedia remux in out.wav`).
Only the first audio track can be carried; video, subtitles and further audio tracks are reported as lost (`UnsupportedCodec` / `NotRepresentable`).
The path overloads write under a temporary name (as `CreateFile`). From a RIFF or RF64 input every chunk except `fmt `, `data`, `fact`, `ds64` and
padding travels unchanged (`bext`, `iXML`, `axml`, `cart`, `LIST INFO`, `id3 `, `cue `, `smpl`, `inst`, unknown ones; one larger than `MaxTagBytes` is
reported); for other inputs the common tags go through the tag writer into `LIST INFO` inside 16 KiB of padding, written in place on the temporary file
(a larger set falls back to a rewrite). Chapters of non-WAVE inputs, pictures the tag writer cannot place and tags with no common field are listed as losses.
The stream overloads write no tags.

## Times

A WAVE file has no timestamps. The muxer refuses decreasing decode times (`NonMonotonicDts`) and notes a start offset and any jumps of the source's times
against the sample count (one tick of the packet's own clock is tolerated); it does not refuse a packet with no time.

## Limits and cost

Memory is constant (a 64 KiB scratch for byte swapping). `MediaLimits.MaxPacketBytes` bounds a packet, `MaxTagBytes` each metadata chunk.

## Deviations and gaps

- AIFF/CAF channel layouts (`chan`) are not read, so a layout other than the default for the count is lost (the report says a mask was assigned).
- PCM in MPEG-TS/PS (LPCM) is not carried: the TS and PS readers expose no PCM codec.
- ffprobe reports no channel layout for a plain one- or two-channel `fmt` record; a source that stated mono or stereo then reads as unspecified.

## Results

Oracles: ffmpeg and ffprobe 7.0.2 (static release build), MediaInfo 26.10, ExifTool 13.60; run as binaries only.

**Round trip on generated files.** 113 files (WAV, BWF, RF64, W64, AIFF/AIFC, CAF, MOV, MKV and AVI carrying unsigned 8-bit, signed 16/24/32-bit, float 32/64, A-law and mu-law, 1 to 8 channels, 8 to 192 kHz) were remuxed to WAVE with `onemedia remux`. ffmpeg's decoded samples (whole-stream md5 and normalised frame md5) and the stream bytes (`-c copy -f data`, after applying independently the byte swap or 8-bit offset the input's codec implies) are equal for 102; 11 differ only in the channel mask, because their sources state no layout and the muxer assigns the conventional one. No file differs otherwise. Our reader and ffprobe list the same packets on all 113 outputs (1,307 packets).

**Corpus.** 217 PCM-family corpus files under 300 MB: 158 equal, 14 layout or default-mask only, 36 refused (compressed codecs the muxer does not carry, such as ALAC, Opus, IMA4, GSM and G.72x without a format record, and damaged inputs), 8 differences. All 8 are on the input side (ffmpeg cannot read the input or reads it differently), with our output readable by ffmpeg and ffprobe and holding the bytes our reader found:

| File | What ffmpeg does with the input | Our output |
|---|---|---|
| `ext-unknown-sub.wav` (extensible, unknown sub-format) | reads it, but cannot decode the sub-format (0 bytes decoded) | same packets (400 bytes) as the input |
| `huge-chunk-size.wav`, `no-data.wav` | "no 'data' tag found", input rejected | a valid, empty WAVE |
| `bad_length.aifc` | input rejected | a valid WAVE with the samples our reader found |
| `Fnonull.aif`, `Ptjunk.aif` | input rejected (0 bytes) | 9 samples; for `Ptjunk` the SSND offset (5) is skipped and the COMM frame count (9) is honoured, as AIFF defines |
| `Poffset.aif` | reads 0 bytes (honours the COMM frame count, ours uses the SSND size) | 15 bytes |
| `voxware.wav` (a codec ffmpeg does not decode) | 0 bytes decoded | the same 2,592 packet bytes as ffprobe lists for the input |

**Metadata.** `bext` (Description, Originator, OriginatorReference, date, time, TimeReference, CodingHistory), iXML (project, scene, take) and `LIST INFO` (INAM, IART, ICMT, ICRD, ISFT) written by `WavMuxer` read back identically in ExifTool and MediaInfo. Neither shows `cart` (ExifTool lists it as an unknown binary block), so `cart` is checked only by our own reader.

**Large file.** A 4,939,212,494-byte file (24-bit, 48 kHz, stereo) is written as RF64 in 2.8 s with a peak resident set of 35 MB; ffprobe reports 17,150.043021 s, MediaInfo 17,150,043 ms and ExifTool 4:45:50. The packet differential on it lists 200,978 equal packets.

**Kill test.** `tools/wav/wav-kill-test.py`: 300 rounds of `kill -9` at a random moment while a 1.5 GiB file was being written, half over an absent target and half over an earlier complete file. 300 killed, 150 old files kept untouched, 150 targets absent, 0 corrupt.

**Fuzzing.** Target `wavmux` (a track description, options and a run of packets with arbitrary times and lengths; accepted output must read back with the samples it was given): 600 s, 18,752,708 executions, 31,202 per second, 227 corpus entries, no crash.

**MOV/MP4 PCM remux (shared code).** The WAVE work changed how PCM sample entries are read for ISO remux (`enda` marks little-endian 24/32-bit entries; 8-bit `sowt`/`twos` entries are signed bytes). `tools/wav/iso-pcm-regress.py` writes 62 files with ffmpeg (u8, s8, s16, s24, s32, f32, f64, A-law, mu-law, both byte orders, mono, stereo and 5.1, MOV and MP4), remuxes each to MP4, MOV and Matroska, and compares ffmpeg's decoded samples of input and output. Before the change: 71 equal, 30 different, 85 refused. After: 83 equal, 0 different, 103 refused; no row got worse. The refusals are existing limits: the ISO muxer has no sample entry for companded PCM, 8-bit signed PCM or little-endian 24/32-bit and float in QuickTime, and the Matroska muxer does not write PCM at all. The existing remux differential (`tools/run-remux-diff.sh`) on the 83 written files (50 MP4, 33 MOV): 3,652 packets, 0 differences from ffprobe, 0 new MP4Box or ffmpeg messages.

**Write speed (indicative; shared worker under heavy load, so not a published row).** A 288,000,102-byte 24-bit stereo WAV remuxed to WAV, warm page cache, three runs: `onemedia remux` 0.33, 0.35 and 0.35 s including process start; `ffmpeg -c copy` 0.14, 0.26 and 0.24 s. Peak resident set 36 to 37 MB. A big-endian AIFF of the same size is written as WAVE with the byte swap in 0.75 s; ffmpeg cannot copy big-endian PCM into WAVE (it refuses the codec), so there is no row to compare.

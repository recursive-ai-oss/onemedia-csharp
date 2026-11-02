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

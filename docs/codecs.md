# Codec header layer

Behaviour of `RecursiveAI.OneMedia.Codecs.*`: header parsing and repackaging only. No sample is decoded or encoded.

## Rules every parser follows

- Input is a `ReadOnlySpan<byte>`. A parser returns `false` (or a status) for truncated, reserved or out-of-range data. It never throws and, for the header parsers listed in the allocation gate below, never allocates.
- Loops are bounded by constants of the syntax (for example at most 64 short-term reference picture sets) or by the input length. The record parsers that build lists (`avcC`, `hvcC`, `vvcC`) take the `MediaLimits` of the caller where a count could be large.
- Emulation-prevention bytes (0x03 after two zero bytes) are skipped while reading, so a NAL unit is parsed as it sits in a stream or sample. `EmulationPrevention.Remove` is for callers that need the unescaped bytes (SEI payloads).
- Parsed records keep slices of the memory they were parsed from. Writers take an `IBufferWriter<byte>` or a destination span and report the size needed when it is too small.

## What each family provides

| Family | Parsed | Built / converted |
|---|---|---|
| H.264 | NAL header, SPS (profile, level, chroma, bit depth, size with cropping, VUI: SAR, colour, timing, reorder), PPS essentials, SEI framing, `avcC` | `avcC` from Annex B; Annex B ↔ length-prefixed |
| H.265 | VPS/SPS/PPS essentials (profile-tier-level, size with conformance window, chroma, bit depth, VUI colour/timing/min spatial segmentation), SEI framing, `hvcC` | `hvcC` from Annex B; Annex B ↔ length-prefixed |
| H.266 | SPS (profile/tier/level, size, chroma, bit depth unless subpicture information is present), `vvcC` | `vvcC` write; Annex B ↔ length-prefixed. Provisional: no reference stream was available |
| AV1 | OBU header, sequence header, `av1C`, low-overhead / Annex B detection | `av1C` from a sequence header |
| VP8 / VP9 | VP8 key frame header; VP9 uncompressed header, superframe index, `vpcC`, WebM feature list | `vpcC` from a key frame; `vpcC` ↔ feature list |
| MPEG-2 video | Sequence header and extension, GOP and picture headers, start codes | |
| AAC | AudioSpecificConfig (explicit and backward-compatible SBR/PS, program config element), ADTS header, `esds` | ADTS → raw + config; raw + config → ADTS; `esds` write |
| Opus | OpusHead, `dOps`, TOC duration | OpusHead ↔ `dOps` |
| Vorbis | identification header, setup header modes, block sizes | Xiph lacing split and build |
| FLAC | STREAMINFO, metadata block headers, frame header with CRC-8 | `dfLa` ↔ Matroska CodecPrivate |
| AC-3 / E-AC-3 | syncframe header | `dac3`, `dec3` from a first frame |
| MPEG audio | frame header (Layers I–III, MPEG-1/2/2.5), Xing/Info/VBRI with the LAME delay and padding | |
| AMR | storage magic, frame TOC size, `damr` | |

`CodecParameterReader` turns a track's configuration (or first sample) into a `CodecParameters` summary with the RFC 6381 string; `CodecConfigMapper` maps Matroska CodecPrivate to the ISO configuration box and back.

`MediaTrack.Rfc6381Codec()` adds the codecs that have a string without a configuration record: VP8 is `vp8`, and the ISO timed-text sample entries are named by their sample entry code as RFC 6381 §3.3 prescribes for the ISO base media file family (`tx3g`, `wvtt`, `stpp`). The TTML profile suffix of `stpp` (`.ttml.im1t`) is not derived, since the sample entry's namespace and schema strings are not read. There is no registered string for QuickTime `text`, the Matroska text codecs (SRT, SSA/ASS, WebVTT, USF) or PGS bitmap subtitles, so the method returns null for them.

## Choices where the specifications or the oracles settle a detail

- **Annex B start codes.** From length-prefixed samples, H.264 units get a four-byte start code when first in the sample or when they are SPS/PPS, a three-byte one otherwise; H.265 and H.266 units always get four bytes. Parameter sets stored in the sample entry are put before the first random access picture of a sample that carries none. This is the layout the common tools write (observed with ffmpeg's `h264_mp4toannexb`/`hevc_mp4toannexb` on streams with several slices per picture and with access unit delimiters), and it makes the output byte-comparable.
- **`avcC` tail.** The chroma format and bit depth bytes follow the parameter sets for every profile whose SPS has `chroma_format_idc` (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135). Files written by common tools do this for 4:4:4 as well; older text lists only 100, 110, 122 and 144.
- **`hvcC`.** `parallelismType` is written as 0 (unknown), as files from common tools do even when wavefront processing is enabled. `array_completeness` is a parameter of `TryCreateFromAnnexB`; the common writers store 0, the specification asks for 1 when the arrays are complete. SEI units seen before the first slice are stored in prefix/suffix SEI arrays.
- **`esds`.** Written with one-byte descriptor sizes, ES_ID 0, and the bit rates the caller passes. The common writers use four-byte sizes, ES_ID 1 and their measured rates. Readers agree on object type, stream type and decoder-specific info, which is what the tests compare.
- **VP9 CodecPrivate.** The common Matroska writers omit it. The mapper then derives `vpcC` from the first key frame (level estimated from picture size alone, so frame rate and bit rate limits are not considered), and writes the WebM feature list for ISO → Matroska.
- **MPEG audio in ISO.** `esds` with object type 0x6B (MPEG-1) or 0x69 (MPEG-2/2.5) and no decoder-specific info.
- **AAC.** `OutputSamplingFrequency` is the SBR extension rate when SBR is signalled, doubling the core rate if the extension rate is absent. ffprobe reports the core rate for implicitly signalled streams; compare `CoreSampleRate` there.
- **Channel counts.** AAC configuration 0 takes the count from the program config element; AC-3 counts the LFE channel.

## Allocation gate

Per call, 0 bytes: H.264/H.265 SPS, AV1 sequence header, AudioSpecificConfig, ADTS header, VP9 frame header, MPEG-2 sequence header, AC-3 syncframe, MPEG audio header, Opus TOC, Annex B ↔ length-prefixed into a caller buffer. Static lookup tables of multi-byte values are kept in static arrays, because a span literal of `int` or `ushort` allocates each time it is read.

## Differential method

`tools/codecs/make-streams.sh` generates the streams from ffmpeg's own test sources; the tests under `tests/OneMedia.Tests/Codecs` compare parsed fields with ffprobe, RFC 6381 strings with MP4Box, and repackaged bytes with ffmpeg's bitstream filters and `-c copy` remuxes. Set `ONEMEDIA_CODEC_STREAMS` (and `ONEMEDIA_FFPROBE`, `ONEMEDIA_FFMPEG`, `ONEMEDIA_MP4BOX`) to run them. Documented deviations: the `esds` encoding, the missing VP9 CodecPrivate, `array_completeness` above.

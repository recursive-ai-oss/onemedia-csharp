# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0-preview.3] - 2026-11-01

Fixes from a review of the second preview.

### Fixed

- **A second read of a `MediaFile` returned nothing without an error.** After `media.Packets(...)`, `Decode()` produced no audio; decoding one track and then another did the same, on MP4 and Matroska. Every read after the first now starts again from the beginning of the file (`Packets`, `PacketsAsync` and `Decode`, in any order). Where that is impossible, it throws instead of returning nothing: `MediaReadStateException` with `Reason` `CannotRewind` when the source cannot seek, and `Superseded` when a reader that is still alive is overtaken by a newer read of the same file. Seeking before the start of an MP4 now lands on the first sample.
- `Decode` throws `UnsupportedAudioFormatException`, as documented, for every AAC track whose configuration signals SBR or PS. OneAudio reported explicit signalling written one bit short (as MP4Box writes it) as an invalid configuration; the adapter now says what the track signals.
- The refusal message and the reports name the signalling correctly: explicit (audio object type 5/29 first) or backward-compatible (extension after the core configuration). Before, a backward-compatible track was described as explicit.
- `SbrImplied` said "SBR" for a plain AAC-LC track whose container declares twice its rate even when the stream has no SBR (a Matroska test file's commentary track). The container's rate is a hint, not evidence; it is now named and documented as one.

### Added

- `MediaReadStateException` and `MediaReadStateReason`.
- `CodecParameters.SbrSignalling` (`SbrSignalling`: `None`, `Explicit`, `BackwardCompatible`, `ContainerRate`), `AudioDecodeReport.Signalling` and `MediaTrackPacketSource.SbrSignalling`.
- `CodecParameters.ContainerDeclaresDoubleRate`, the new name of `SbrImplied`.

### Changed

- `CodecParameters.SbrImplied` is `[Obsolete]` and forwards to `ContainerDeclaresDoubleRate`. `onemedia info` prints `container declares twice the core rate` for it and `SBR, explicit signalling` or `SBR, backward-compatible signalling` for signalled tracks.
- `RecursiveAI.OneMedia.Audio` depends on `RecursiveAI.OneAudio` 1.5.0.
- Audio adapter: Matroska `DiscardPadding` on any block (not only the last) is passed to OneAudio as `PacketView.DiscardEndFrames`, for Opus, Vorbis, AAC, FLAC and AMR, converted at the rate OneAudio decodes (the core rate for HE-AAC). Vorbis end padding is now applied. `AudioSeamLimitations.MidStreamDiscardPadding` and `EndPaddingNotApplied` are marked `[Obsolete]` and never set; nothing is left unapplied except padding that asks to cut the start of a block (`LeadingDiscardPadding`).
- AMR samples that hold several frames (3GP with `damr` frames-per-sample above 1) decode through the adapter, since OneAudio 1.5.0 splits them by their table of contents.

## [0.1.0-preview.2] - 2026-11-01

Fixes from a review of the first preview. No new formats or features.

### Changed

- **Breaking (preview):** `Fact<T>.Value` no longer returns `default` for a fact the file does not state; it throws `InvalidOperationException`. Before, `media.Location.Value` read 0,0 (a real place) for a file with no location, and every other fact had the same trap. Check `HasValue`, or use the new `TryGetValue`. The type of `Value` is now `T` (it was `T?`), and `ToString()` returns the value as text or `none`.
- `onemedia info` prints `none` for a fact the file does not state (`location: none`), and `--json` writes `null` for it; before, the line or property was missing.
- `MediaTrackPacketSource` for an HE-AAC track reports the rate and channel count OneAudio actually decodes (the core coder's), not the output rate the file declares, so frame counts and priming are counted in the frames that are decoded.
- The README, `PACKAGE.md` and package descriptions say that subtitles are planned and not in this preview; the scope line is unchanged.
- `THIRD-PARTY-NOTICES.md` now says where the test media come from (all generated here from synthetic signals) and the README points to it by its real name.

### Added

- `Fact<T>.TryGetValue`.
- HE-AAC is reported instead of passing as plain AAC. `CodecParameters.SbrPresent`, `PsPresent` and `SbrImplied`, and `MediaTrack.DescribeCodec()`, give the signalled output rate (`SampleRate`), the core rate (`CoreSampleRate`) and whether SBR or parametric stereo is signalled, explicitly or by the backward-compatible extension. `SbrImplied` marks a plain AAC-LC configuration in a container that declares twice its rate (SBR signalled only inside the stream). `onemedia info` shows it on the track line.
- `RecursiveAI.OneMedia.Audio`: `AudioDecodeReport`, `MediaFile.DescribeDecode()` and `MediaFile.Decode(out AudioDecodeReport, ...)` say which rate and channel count the PCM really has, which rate and channels the file signals, and `AudioSeamLimitations.SbrNotApplied` / `PsNotApplied`. OneAudio 1.4.0 has no SBR or PS decoder: a track whose configuration signals SBR or PS is refused with a typed OneAudio exception, and one that signals it only in the stream decodes as the core coder (for example 22.05 kHz where the file says 44.1 kHz). `docs/audio-adapter.md` describes both.
- `MediaAudioExtensions.SbrDataIgnored(decoder)` surfaces OneAudio's `AacDecoder.SbrDataIgnored`: SBR data found inside a plain-LC stream, known only after reading.
- Test files for HE-AAC signalling in `testdata/heaac`, made by `tools/gen-heaac-testdata.sh`.

### Fixed

- An AAC AudioSpecificConfig with explicit SBR signalling that ends one bit before the end of its GASpecificConfig (as MP4Box writes it) is read instead of rejected.

## [0.1.0-preview.1] - 2026-11-01

First preview: reading MP4/MOV/3GP (including fragmented MP4) and Matroska/WebM, with native tags, a common tag view and basic capture facts. No writing or tag editing yet.

### Added

- ASF/WMV/WMA reading (`RecursiveAI.OneMedia.Asf`, `docs/asf.md`): header objects as a range tree, packets in file order with exact presentation times, keyframe seeks, audio de-scrambling, forward-only and ranged sources, `Protected` for DRM files (`MediaFile.IsProtected`), and native `asf` tags (Content Description, extended attributes, Metadata and Metadata Library, WM/Picture) in the common view. Content Description's description is `Description`. Tags and packets agree with ffprobe, ExifTool, MediaInfo and ATL.NET on the corpus; WMA audio does not decode, MP3 in ASF does through `.Audio`.
- New codec ids for ASF: `Mp2`, `WmaV1`, `WmaV2`, `WmaPro`, `WmaLossless`, `WmaVoice`, `Mpeg4`, `Wmv1`, `Wmv2`, `Wmv3`, `Vc1`.
- MPEG transport streams (188, 192 M2TS/MTS and 204-byte packets, PSI, service names, AVCHD camera data), program streams and VOB (MPEG-1 and MPEG-2, DVD streams), and elementary video: Annex B H.264/HEVC/VVC, MPEG-1/2 video and AV1 OBU, plus IVF. Access-unit, audio-frame and PES packets with exact 90 kHz times, field-picture and repeated-field timing, key frames from the bitstream, damage handling with typed defects, and a fixed-size probe (1.58 MB read from a 2.37 GB transport stream, 0 bytes allocated per packet). Details and every difference from ffprobe 7.0.2 in `docs/mpeg.md`.
- Audio elementary streams: MPEG audio (MP1/MP2/MP3, MPEG-2.5; Xing, Info, LAME and VBRI tags, free-format frames, resynchronisation with typed defects), ADTS AAC, AC-3/E-AC-3 and AMR/AMR-WB files, with `OpenAsync` over ranged sources and the MP3 encoder delay and padding as priming and end trim. `media.Decode()` plays them through OneAudio with ffmpeg's length.
- `Tags.Id3` (ID3v1/1.1, ID3v2.2–2.4 with unsynchronisation, extended header, footer, compressed frames; public `Id3Tag.Read` for other containers), `Tags.Ape` (APEv1/v2) and Lyrics3 as native tags with common-view rows, including ReplayGain and cover art.
- `MediaFile.Tags`, `CaptureTime`, `Location`, `Device`, `Rotation`, `Native[...]` and `ReadMetadataAsync`, and tags and facts in `onemedia info` (text and `--json`).
- `MediaFile.ContainerDuration`, `TracksDuration` and `ExactDuration`; `Duration` is the container's figure unless a track without an edit list is longer.
- Matroska: zlib-compressed frames are inflated on read; `OutputSamplingFrequency` is the audio rate; `V_MS/VFW/FOURCC` tracks are named by their FourCC.
- ISO: QuickTime packetised audio (IMA 4:1 and similar) is joined per chunk; the QuickTime sound layout is read from the data; AMR is mono at its codec rate; QuickTime `text` entries are timed text; `tapt`, `gmhd`, `hinf` and `hnti` are containers; a video size of 0 is taken from the sequence parameter set.

- Repository skeleton: core package `RecursiveAI.OneMedia`, audio adapter `RecursiveAI.OneMedia.Audio`, the `onemedia` tool, tests, NativeAOT and browser-WebAssembly smoke apps.
- Byte sources: memory, file, seekable stream, caller-supplied ranged reads and forward-only streams, plus an observer hook for counting reads.
- `MediaTime` (exact rational time with explicit rounding), `MediaLimits`, typed defects and exceptions.
- Content-based format sniffing and an explicit format registry; sniffers for ISO base media files, EBML (Matroska/WebM) and the other container and stream signatures.
- The track and packet model, demuxer interfaces with an allocation-free pull path, and the structure-node contract.
- `Media.Open` / `MediaFile` happy path, the `onemedia info` command, and the audio adapter: tracks as OneAudio packet sources, OneAudio container registrations for MP4 and Matroska, and `Decode()`.

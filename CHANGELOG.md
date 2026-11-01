# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
- Test files for HE-AAC signalling in `testdata/heaac`, made by `tools/gen-heaac-testdata.sh`.

### Fixed

- An AAC AudioSpecificConfig with explicit SBR signalling that ends one bit before the end of its GASpecificConfig (as MP4Box writes it) is read instead of rejected.

## [0.1.0-preview.1] - 2026-11-01

First preview: reading MP4/MOV/3GP (including fragmented MP4) and Matroska/WebM, with native tags, a common tag view and basic capture facts. No writing or tag editing yet.

### Added

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

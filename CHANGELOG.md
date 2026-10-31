# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `MediaFile.Tags`, `CaptureTime`, `Location`, `Device`, `Rotation`, `Native[...]` and `ReadMetadataAsync`, and tags and facts in `onemedia info` (text and `--json`).
- `MediaFile.ContainerDuration`, `TracksDuration` and `ExactDuration`; `Duration` follows the longest track.
- Matroska: zlib-compressed frames are inflated on read; `OutputSamplingFrequency` is the audio rate; `V_MS/VFW/FOURCC` tracks are named by their FourCC.
- ISO: QuickTime packetised audio (IMA 4:1 and similar) is joined per chunk; the QuickTime sound layout is read from the data; AMR is mono at its codec rate; QuickTime `text` entries are timed text; `tapt`, `gmhd`, `hinf` and `hnti` are containers; a video size of 0 is taken from the sequence parameter set.

- Repository skeleton: core package `RecursiveAI.OneMedia`, audio adapter `RecursiveAI.OneMedia.Audio`, the `onemedia` tool, tests, NativeAOT and browser-WebAssembly smoke apps.
- Byte sources: memory, file, seekable stream, caller-supplied ranged reads and forward-only streams, plus an observer hook for counting reads.
- `MediaTime` (exact rational time with explicit rounding), `MediaLimits`, typed defects and exceptions.
- Content-based format sniffing and an explicit format registry; sniffers for ISO base media files, EBML (Matroska/WebM) and the other container and stream signatures.
- The track and packet model, demuxer interfaces with an allocation-free pull path, and the structure-node contract.
- `Media.Open` / `MediaFile` happy path, the `onemedia info` command, and the audio adapter: tracks as OneAudio packet sources, OneAudio container registrations for MP4 and Matroska, and `Decode()`.

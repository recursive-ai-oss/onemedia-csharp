# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Repository skeleton: core package `RecursiveAI.OneMedia`, audio adapter `RecursiveAI.OneMedia.Audio`, the `onemedia` tool, tests, NativeAOT and browser-WebAssembly smoke apps.
- Byte sources: memory, file, seekable stream, caller-supplied ranged reads and forward-only streams, plus an observer hook for counting reads.
- `MediaTime` (exact rational time with explicit rounding), `MediaLimits`, typed defects and exceptions.
- Content-based format sniffing and an explicit format registry; sniffers for ISO base media files, EBML (Matroska/WebM) and the other container and stream signatures.
- The track and packet model, demuxer interfaces with an allocation-free pull path, and the structure-node contract.

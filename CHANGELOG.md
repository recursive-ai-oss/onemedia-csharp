# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Tag writing (`TagEdits`, `TagWriter`, `TagWriteReport`, `docs/tag-writing.md`): common-view and native edits for MP4/MOV/3GP, Matroska/WebM, MP3 (ID3v2/ID3v1/APEv2), FLAC, Ogg Vorbis/Opus/FLAC, WAV/RF64 and AIFF, ASF/WMA/WMV (Content Description, Extended Content Description), XMP packets in `uuid` boxes and iXML elements by path, with a write-back report; setters on `CommonTags`, `MediaFile.Save/SaveAs/SaveInPlace`, `onemedia tags`; `EbmlEditor.ReplaceWithRun`; `Id3Genres.Number`; ReplayGain mapping rows for Vorbis, iTunes and Matroska.

- Browser playability (`RecursiveAI.OneMedia.Playback`, `docs/playability.md`): `MediaFile.CanPlay(profile)` and `Media.CanPlay(path, profile)` return a verdict per mode (a plain URL, MediaSource) with typed blockers (container, codec, profile, level, bit depth, chroma, `hvc1`/`hev1` form, segmentation, encryption), each marked as fixable by a remux, an audio transcode, or a video blocker; plus warnings for hardware-dependent codecs, a movie box at the end and incomplete codec strings. Built-in profiles as static data: `Chrome`, `Chromium`, `Firefox`, `Safari`, `WebKitLinux`, `IosSafari`, `AndroidChrome`, `GenericMp4`; the first five were checked by playing a 75-file matrix in the real browsers (`tools/playback/`).
- `IsoBmffDemuxer.MediaDataPrecedesMovie` and `IsoBmffAsyncDemuxer.FileType`/`MediaDataPrecedesMovie`.

### Changed

- `MediaTrack.Rfc6381Codec()` names the sample entry for `hev1` (HEVC) and `avc3` (H.264) tracks, returns `vp9` for a VP9 track whose level is unknown (browsers reject `vp09.PP.00.DD`), and `vp9` for one without a configuration.

## [0.2.0-preview.1] - 2026-11-01

Every other reader: ASF/WMV/WMA, MPEG-TS/M2TS, PS/VOB, elementary video and audio streams, MP3 with ID3 and APE, Ogg, FLAC, FLV, AVI/OpenDML, DV, WAV/BWF/RF64/W64, AIFF/AIFC and CAF, with their native tags in the common view. The format readers are read-only. Structure writers and the safe writer come as a first preview (below); tag editing, muxing and remux, subtitles, repair and packaging are planned and not in this release. `RecursiveAI.OneMedia.Audio` moves to OneAudio 1.6.1, which decodes HE-AAC at the full output rate.

Known gaps: DV audio inside DV frames is not de-shuffled, so DV type 1 and raw DV have no audio track; `Decode()` does not play PCM, ADPCM, WMA, AC-3/E-AC-3 or ALAC tracks (it names the codec in an `UnsupportedAudioFormatException`); AVCHD CPI/MPL sidecars are listed, not decoded.

### Added

- ASF/WMV/WMA reading (`RecursiveAI.OneMedia.Asf`, `docs/asf.md`): header objects as a range tree, packets in file order with exact presentation times, keyframe seeks, audio de-scrambling, forward-only and ranged sources, `Protected` for DRM files (`MediaFile.IsProtected`), and native `asf` tags (Content Description, extended attributes, Metadata and Metadata Library, WM/Picture) in the common view. Content Description's description is `Description`. Tags and packets agree with ffprobe, ExifTool, MediaInfo and ATL.NET on the corpus; WMA audio does not decode, MP3 in ASF does through `.Audio`.
- New codec ids for ASF: `Mp2`, `WmaV1`, `WmaV2`, `WmaPro`, `WmaLossless`, `WmaVoice`, `Mpeg4`, `Wmv1`, `Wmv2`, `Wmv3`, `Vc1`.
- MPEG transport streams (188, 192 M2TS/MTS and 204-byte packets, PSI, service names, AVCHD camera data), program streams and VOB (MPEG-1 and MPEG-2, DVD streams), and elementary video: Annex B H.264/HEVC/VVC, MPEG-1/2 video and AV1 OBU, plus IVF. Access-unit, audio-frame and PES packets with exact 90 kHz times, field-picture and repeated-field timing, key frames from the bitstream, damage handling with typed defects, and a fixed-size probe (1.58 MB read from a 2.37 GB transport stream, 0 bytes allocated per packet). Details and every difference from ffprobe 7.0.2 in `docs/mpeg.md`.
- Audio elementary streams: MPEG audio (MP1/MP2/MP3, MPEG-2.5; Xing, Info, LAME and VBRI tags, free-format frames, resynchronisation with typed defects), ADTS AAC, AC-3/E-AC-3 and AMR/AMR-WB files, with `OpenAsync` over ranged sources and the MP3 encoder delay and padding as priming and end trim. `media.Decode()` plays them through OneAudio with ffmpeg's length.
- `Tags.Id3` (ID3v1/1.1, ID3v2.2–2.4 with unsynchronisation, extended header, footer, compressed frames; public `Id3Tag.Read` for other containers), `Tags.Ape` (APEv1/v2) and Lyrics3 as native tags with common-view rows, including ReplayGain and cover art.
- ID3v2 tags embedded in other containers are read as `id3v2` native tags: an ASF `ID3` attribute (the attribute stays as a binary tag) and `ID32` boxes in an MP4 `meta` box.
- Ogg (Vorbis, Opus, FLAC, Theora, Speex; all logical streams, chained files, forward-only and ranged sources), native FLAC, and FLV (`RecursiveAI.OneMedia.Ogg`, `.Flac`, `.Flv`, `Tags.VorbisComment`; `docs/ogg.md`, `docs/flac.md`, `docs/flv.md`, `docs/vorbis-comment.md`): packets, seeks, Vorbis comments and FLAC blocks as native tags in the common view, `onMetaData` as `flv` tags.
- WAVE (WAV, BWF, RF64, Wave64), AIFF/AIFC and CAF reading (`RecursiveAI.OneMedia.Riff`, `.Aiff`, `.Caf`, `Tags.Riff`; `docs/pcm-files.md`): PCM and compressed-in-WAVE packetization, `LIST INFO`, `bext`, `iXML`, `axml`, `cart`, AIFF text chunks and CAF `info` as native tags, cue points as chapters.
- ID3v2 tags in a WAVE or AIFF `id3 `/`ID3 ` chunk and in front of a FLAC or Ogg stream are read as `id3v2` native tags beside the raw block. In FLAC and Ogg the stream's Vorbis comments come before ID3 in the common view; in WAVE and AIFF ID3 comes before `LIST INFO`. A FLAC or Ogg file behind an ID3 tag longer than the sniffed head now opens as its own format on a seekable source.
- `TrackEditInfo.PacketTimesIncludePriming`: set for Ogg Opus, whose packet times already have the pre-skip taken off (RFC 7845 §4.4), so `MediaTrack.PresentationTime` subtracts it once.
- One codec id per codec across WAVE, AVI and ASF: ADPCM (Microsoft, IMA, G.726) now has the same neutral id in every container.
- AVI: `RecursiveAI.OneMedia.Avi` reads AVI and OpenDML (`AVIX`, super and standard indexes, `idx1`, index-less files by a bounded `movi` scan), DV in AVI (type 1 and type 2) and `LIST INFO`/`IDIT` tags; raw DV (`.dv`) through `RecursiveAI.OneMedia.Dv`; key frames from the index or the picture header; the reader registers with `AddOneMedia()` in the audio adapter. DV audio inside frames is not de-shuffled (no audio track for DV type 1 and raw DV). See `docs/avi.md` and `docs/dv.md`. AVI `LIST INFO` and `IDIT` are read by the same `Tags.Riff` reader as WAVE, and AVI shares the WAVE reader's chunk walker, so AVI defect codes are the shared `riff.*` codes (`riff.size-exceeds-file`, `riff.size-exceeds-parent`, `riff.unbounded-size`, `riff.trailing`).

- Structure writers and the safe writer, a preview (`docs/writer.md`); no tag-editing API sits on them yet: `EditPlan`, `SafeWriter` (`Write`, `SaveAs`, `Save` as temp + fsync + atomic rename, `SaveInPlace` within padding or `PaddingRequiredException`), `WriteReport`, and the editors `BoxEditor` (`BoxBuilder`), `EbmlEditor` (`EbmlWriter`) and `ChunkEditor` (`ChunkBuilder`) for ISO boxes, EBML and RIFF/RF64/Wave64/AIFF. Untouched files come back byte for byte; edits change only the edited ranges and keep media payload as copied ranges. Growing a header area moves later bytes and the editors follow: `stco` → `co64` promotion, `tfhd`/`sidx`/`tfra`, Matroska `SeekHead`/`Cues`/cluster positions and CRC-32, RIFF/RF64 sizes and plain WAVE → RF64 at 4 GiB. An outer RIFF size that overshoots the file is corrected once content moves; appends after a chunk or box with no stated end are refused.

- `MediaTrack.PresentationTime(...)`: a packet's time on the presentation timeline with the edit list, Matroska codec delay and start offset applied, in `MediaTime`.
- `MediaTrack.Rfc6381Codec()`: the track's RFC 6381 `codecs` string, now also `vp8` and the ISO timed-text codes `tx3g`, `wvtt` and `stpp`; codecs with no registered string return null.
- `Media.Open(Stream)` accepts a stream that cannot seek (new `MediaFile.Open(IForwardMediaSource)` and `MediaFormatRegistry.Open(IForwardMediaSource)`): formats readable front to back open and read once, a format that needs to seek throws `UnsupportedMediaException`, a second read throws `MediaReadStateException` (`CannotRewind`).
- `MediaReadStateReason.AsynchronousSource`; `AudioSeamLimitations.SbrPossiblyNotApplied`, `AudioDecodeReport.IsPossiblyCoreOnly` and `AudioDecodeReport.AfterReading(decoder)`.
- iTunes `©cpy` atoms in `ilst` feed `CommonTags.Copyright` (after `cprt`); `docs/tag-mapping.md` regenerated.
- `onemedia info`: track names (also in `--json`), every fact candidate with its source, reduced frame rates, clearer HE-AAC wording.

### Fixed

- `SafeWriter.Save` on Windows: the writer's own source handle is closed before the rename, and a target another handle keeps open raises `FileInUseException` with the original untouched instead of an unspecified `UnauthorizedAccessException`.

- Format sniffing: an Annex B head that ended right after a start code made the H.265 check read one byte past the end (found by fuzzing the registry).
- CAF with variable-size packets opened from a stream that cannot seek delivered no packets, because the packet table had gone by before the audio; the table is kept, and a file whose table is not before the audio is refused with `UnsupportedMediaException`.

- `onemedia info` printed Matroska frame rates unreduced (`200000000/8333333 fps`); they are now reduced or snapped to the nearest broadcast rate.
- Raw AV1 streams: an OBU whose size field was close to `int.MaxValue` overflowed the end-of-OBU test and threw `ArgumentOutOfRangeException`; it is now a malformed unit.

### Changed

- Reading tags allocates far less: an ID3 tag no longer clears a 64 KiB window for a few hundred bytes (an MP3 tag read went from about 150 KB to 20 KB allocated), the common tag view no longer allocates a list per mapping row, and an Ogg Vorbis file reads its setup header only when packet durations are asked for.
- `RecursiveAI.OneMedia.Audio` uses OneAudio 1.6.1, which decodes HE-AAC v1/v2 (SBR and baseline PS) at the full output rate. `Decode()` on HE-AAC no longer throws for SBR/PS signalling, and `MediaTrackPacketSource.SampleRate`/`Channels`, priming, `DiscardEndFrames`, lengths and seeking are counted at the output rate and channels. `Decode(out AudioDecodeReport)` reports the decoder's own rate and channels, with the file's declaration in `SignalledSampleRate`/`SignalledChannels` when they differ (a container hint of SBR that the stream does not bear out decodes at the core rate). OneAudio still refuses 64-96 kHz with bs_start_freq other than 9, dual-mono SBR, unrestricted PS and multichannel AAC with its typed unsupported error.
- `AudioSeamLimitations.SbrNotApplied` / `AudioDecodeReport.IsCoreOnly` now mean only SBR that first appears after OneAudio's first 256 access units and was skipped; `AudioDecodeReport.AfterReading` also refreshes rate and channels from the decoder. New `AudioDecodeReport.SbrActive` and `PsActive` report what OneAudio's AAC decoder runs.
- `onemedia info` no longer says "SBR not decoded" for HE-AAC tracks.
- `Packets()` and the `.Audio` decode calls on a file opened with `OpenAsync` throw `MediaReadStateException` (`AsynchronousSource`, still an `InvalidOperationException`) instead of a plain `InvalidOperationException`.

### Deprecated

- `AudioSeamLimitations.PsNotApplied`, `AudioSeamLimitations.SbrPossiblyNotApplied` and `AudioDecodeReport.IsPossiblyCoreOnly`: never set or true with OneAudio 1.6.1.

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

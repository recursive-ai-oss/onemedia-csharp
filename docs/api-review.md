# API review before 1.0

The public surface was listed from the built assemblies with `tools/OneMedia.ApiList` (`OneMedia.ApiList [--lib=<dir>] RecursiveAI.OneMedia.dll RecursiveAI.OneMedia.Audio.dll`): 743 public types and about 7,200 members in the core package, 15 types in the audio package. The tool prints one line per type and member and then flags mutable fields and public setters, `Get*` methods, `TimeSpan` and floating-point members, debug- or test-sounding names and types in the root namespace. Every public member already has an XML doc comment (the build fails on a missing one). The rest of this page records what the review found, what was changed, and how the open items were decided.

## Changed in this review

| Change | Package-validation note |
|---|---|
| `track.Decode()` in `RecursiveAI.OneMedia.Audio`: decodes an audio track obtained from the file that opened it, so `media.Audio!.Decode()` works. One line over `media.Decode(track)`; a track that did not come from a `MediaFile` throws `ArgumentException`. | additive |
| `ToString()` on the report types (`RemuxReport`, `RepairReport`, `MakePlayableReport`, `ClipReport`, `MuxReport`, `ReconstructionReport`, `PackagingReport`, `SegmenterReport`, `FaststartReport`, `StripReport`, `ConversionReport`, `TagWriteReport`, `CaptureTimeReport`, `WriteReport` and their per-track and per-system parts) print one short line, such as `Remuxed: 3 tracks, 2 losses (see Losses)`. The data stays in the properties. | none (same signature) |
| `TagWriteReport.Write` and `CaptureTimeReport.Write` lose their public setters. The save methods fill the property in; a caller assigning it had no meaning, and `StripReport.Write` already worked this way. | removal, recorded in `src/OneMedia/CompatibilitySuppressions.xml` (preview to 1.0) |

## Findings and recommendations

Each item is marked **keep**, **internal** (make internal before 1.0) or **rename** (a breaking change). Items marked *done* were applied; the last section gives the outcome of each open item.

### (a) Members that look accidental

| Item | Recommendation |
|---|---|
| `TagWriteReport.Write`, `CaptureTimeReport.Write` public setters | internal, *done* |
| No mutable public fields, no `Debug*`, `*ForTest*` or `*Hook*` names, no test-only members found | nothing to do |
| `RemuxSession.IsoOptions` (settable callback, named for ISO but also used for WAV PCM options; used only by the remuxer and the segmenter) | internal, *done* (round 2) |
| Public settable properties elsewhere are option objects and edit builders by design (`CommonTags`, `EvidenceSet`, `BoxEditor`/`EbmlEditor`/`ChunkEditor.RewriteHeaders` and `GrowthPadding`, `MediaFile.Resolver`, `MediaFile.UseFileSystemTime`) | keep |
| About 45 public types are never named outside their own file (layer readers such as `CafReader`, `OggPages`, `AiffCommon`, `SamplerLoop`, the per-container audio formats in `.Audio`, extension holders). They are spec-layer pieces or registry entries reached through the registry or returned from public members | keep |

### (b) Naming and consistency

| Item | Recommendation |
|---|---|
| `Get*` methods: `GetSize()` on the four decoder-configuration records (`avcC`, `hvcC`, `vvcC`, `av1C`), `VorbisIdentificationHeader.GetHeaderType`, `VorbisModes.GetOutputSamples`, `VorbisXiphLacing.GetSize`, `HlsAttributeList.Get(name)`. Everything else uses properties and `Try*` | keep all of them, including `HlsAttributeList.Get` (decided) |
| `TimeSpan` appears only in the happy path (`Media.Trim`, `Media.Split`, `MediaFile.Duration`) and in `MediaTime.FromTimeSpan`/`ToTimeSpan` | keep |
| `double` for media time: `PackagedTrack.DurationSeconds` (packaging report) and `CammPacket.GpsSecondsToUtcTicks(double)` (a spec value). Other `double` members are display or sensor values (frame rate, latitude, gains) | `PackagedTrack.DurationSeconds` is now `Duration`, a `MediaTime`, *done* (round 2) |
| Happy-path calls that read or write files take no `CancellationToken`: `Remux`, `Faststart`, `MakePlayable`, `SetCaptureTime`, `PackageHls`, `PackageDash`, `StripLocation`, `StripDeviceIds`, `Repair`, `Concat`, `CanPlay`. `Trim`, `Split`, `SplitBySize` and the `OpenAsync` family take one. The layer calls beneath most of them already do | added to each, *done* (round 2) |
| Async twins exist where a source can answer late (`OpenAsync`, the four asynchronous demuxers, `SubtitleTrack.ReadAsync`, `Subtitles.ReadAsync/WriteAsync/ConvertAsync`). Path-based editing calls (`Remux`, `Trim`, `Repair`, `MakePlayable`, `PackageHls`) are synchronous only, which is by design: they work on local files | keep; revisit after 1.0 if remote output is wanted |
| Subtitle types live in `RecursiveAI.OneMedia.Captions`, not `.Subtitles`, because the static class `Subtitles` already owns that name in the root namespace | keep |

### (c) Happy-path calls with behaviour or options of their own

| Item | Recommendation |
|---|---|
| `Media.Trim`/`Split`/`SplitBySize` choose between the ISO and Matroska cutters inline (two lambdas) and `Media.Split` converts times itself | moved into the editing layer (internal), *done* (round 2) |
| `Media.Trim` has a `TrimMode mode = FrameAccurate` parameter | keep: it is the layer's own choice, not an option object |
| `Media.CanPlay` opens and disposes the file (three lines) | keep |
| `Media.PackageHls`/`PackageDash` build a `PackagingOptions` with only the format set | keep |
| `Subtitles.Read` decides by content whether a file is a bitmap subtitle that needs its sibling file | keep: documented behaviour of the layer, in an internal helper |
| `Media.Repair(input, output, reference = null)`, `Media.Remux(input, output, format)` | keep |

### (d) Root-namespace types that belong in a spec namespace

The root holds 75 types. About half are the neutral model and belong there (`MediaTime`, `MediaTrack`, `PacketInfo`, `PacketView`, `OwnedPacket`, `MediaLimits`, defects and exceptions, the demuxer and source interfaces, `MediaFormats`).

| Item | Recommendation |
|---|---|
| Writing layer: `SafeWriter`, `EditPlan`, `WriteChange`, `WriteMode`, `WriteReport`, `OutputPiece`, `OutputPieceKind`, `IPieceGenerator`, `PaddingRequiredException` (their source folder is already `Writing`) | moved to `RecursiveAI.OneMedia.Writing`, *done* (round 2) |
| Muxing: `IMuxer`, `MuxerExtensions`, `MuxReport`, `TrackMuxReport`, `MuxRefusedException`, `MuxRefusalReason` | keep: neutral model |
| Sources: `MemoryMediaSource`, `FileMediaSource`, `StreamMediaSource`, `StreamForwardSource`, `RangedMediaSource`, `ObservedMediaSource`, `ObservedForwardSource`, `MediaSourceExtensions` | keep in the root as the neutral bottom layer (decided) |
| `AudioTrackInfo`, `VideoTrackInfo`, `ByteRange`, `CodecId`, `CodecConfiguration`, `TrackEditInfo` | keep: part of the track model |

## Outcomes of the follow-up round

Each open item was decided and applied or kept. Removals and changes against 0.8.0-preview.1 are in `src/OneMedia/CompatibilitySuppressions.xml`, each with a reason, and in the CHANGELOG.

| # | Item | Outcome |
|---|---|---|
| 1 | `CancellationToken` on the happy-path file calls | **Done.** `Media.Open` (path, stream, memory), `OpenGrowing`, `CanPlay`, `Remux` (both), `Faststart`, `MakePlayable`, `SetCaptureTime`, `PackageHls`, `PackageDash`, `StripLocation`, `StripDeviceIds`, `Repair`, and `Subtitles.Read`, `Write`, `Convert` take a trailing `CancellationToken cancellationToken = default` and pass it to the layer call. `Media.Concat(params string[])` cannot take a trailing token, so it gains an overload `Concat(output, IReadOnlyList<string>, CancellationToken)`. The sync `Subtitles` calls check the token before they start (the layer reads a file in one step). Internal callers that open files now forward their token too (the analyzer flagged each). |
| 2 | `PackagedTrack.DurationSeconds` | **Done.** Replaced by `Duration`, a `MediaTime` in the track's timescale. |
| 3 | A namespace for the safe-writer types | **Done.** `SafeWriter`, `EditPlan`, `WriteReport`, `WriteChange`, `WriteMode`, `OutputPiece`, `OutputPieceKind`, `IPieceGenerator` and `PaddingRequiredException` are in `RecursiveAI.OneMedia.Writing`. `FileInUseException` and the source types stay in `RecursiveAI.OneMedia`. |
| 4 | `RemuxSession.IsoOptions` | **Done.** Internal. |
| 5 | `HlsAttributeList.Get` | **Kept** as a method. |
| 6 | Cutter dispatch inside `Media.Trim`/`Split`/`SplitBySize` | **Done.** The choice between the ISO and Matroska cutters is in the editing layer (internal `EditFiles.Trim`/`Split`/`SplitBySize`); each `Media.*` call is one expression. |

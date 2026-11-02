# Facts: evidence, resolver, device profiles, sidecars

A fact is a list of readings (candidates) plus a decision about which one to trust. The readers collect candidates; a resolver orders and explains
them. Nothing is dropped: every candidate keeps its source, raw value, interpretation, time-zone certainty and byte range.

```csharp
using var media = Media.Open("clip.mov");
media.AddSidecarsBesideFile();                       // optional: sidecars in the file's directory count as evidence
media.CaptureTime.Best;                              // the winner
media.CaptureTime.Resolution.Reason;                 // "mdta:com.apple.quicktime.creationdate on tier 1 (OffsetBearingVendor), ahead of 3 other reading(s) ..."
media.CaptureTime.Candidates;                        // every reading, best first
media.Location; media.Device.Make; media.Device.Model; media.Device.Serial; media.Rotation;
```

## Types

| Type | Role |
|---|---|
| `FactCandidate<T>` | one reading: value, `Source` (`system:key`), `RawValue`, `Interpretation`, `Certainty`, `Range` |
| `Fact<T>` | the candidates, best first, plus `Resolution` (`Rule`, `Reason`, `Resolver`) |
| `FactEvidence` | everything a resolver may weigh: container candidates, sidecar and Exif readings, attached telemetry, file name, file-system time |
| `IFactResolver` | `MediaFacts Resolve(FactEvidence)` |
| `DefaultFactResolver` | the documented default below |
| `DeviceProfileTable` / `DeviceProfile` | data rows that reinterpret time fields for a device |
| `EvidenceSet` | candidates from one extra place (a sidecar, an Exif block) |

Time-zone certainty is per source: `Offset` (text with `+hh:mm`), `Utc` (a `Z` suffix, a GPS time, a binary header read as UTC), `Local` (known local
time of an unknown zone; a device profile sets it), `Unknown` (wall-clock reading with no zone).

## Default capture-time precedence

The resolver orders candidates by tier, then by the order the readers produced them (which is stable and documented per format). Lower tier wins.

| Tier | Name | What lands here |
|---|---|---|
| 1 | `OffsetBearingVendor` | a time written as text with an offset or `Z` by a vendor tag, XMP, Exif (`OffsetTimeOriginal`, GPS date and time) or a sidecar: Apple `creationdate`, Sony XML `CreationDate`, MDPM with a zone |
| 2 | `TelemetryGps` | the first valid GPS fix with a UTC time in an attached telemetry channel, moved back by the fix's position on the media timeline (start of recording) |
| 3 | `ContainerLocal` | wall-clock times with no zone (`©day`, `IDIT`, `bext`, DV, XMP/Exif without a zone, DJI SRT), and binary header times a device profile marks as local |
| 4 | `HeaderUtc` | binary header times read as UTC: `mvhd`, Matroska `DateUTC`, ASF file properties |
| 5 | `FileName` | `YYYYMMDD_HHMMSS` and `YYYY-MM-DD HH.MM.SS` patterns (no zone; Pixel `PXL_` names are UTC but the name does not say so) |
| 6 | `FileSystem` | last-write time (when the file was saved, not recorded) |

File name and file-system candidates exist only when the caller supplies them (`FactEvidence.FileName`, `FileSystemTimeUtc`; the happy path's
`AddSidecarsBesideFile` turns them on). Telemetry GPS samples count only with a fix of 2D or 3D and a UTC year of 2000 or later (a receiver that has not
locked reports its epoch). `Resolution.Reason` names the winner's tier, how many readings it beat, the runner-up, and the gap when both have a known instant and differ by more than 2 s.

## Location, device, the rest

- **Location:** explicit position tags (`location.ISO6709`, `©xyz`, `loci`, XMP GPS, Exif GPS, sidecar positions) in reader order, then the first valid fix
  of each telemetry GPS channel. A fix of 0,0 and fixes with no quality are skipped.
- **Make, model, software, serial, orientation, duration, frame size:** reader order; sidecars and Exif follow the container's own tags. `Serial` is the
  per-unit device identifier (Exif `BodySerialNumber`, XMP `aux:SerialNumber`/`exifEX:BodySerialNumber`, Sony XML `serialNo`; `4294967295` is "none").
- The default resolver never merges or averages. If two sources disagree the winner is chosen by the rules above and the others stay listed.

## Device profiles

A profile row is matched on the resolved make and model (`Make` exact, case-insensitive; `ModelPrefix` prefix, case-insensitive; a row naming only a model
matches when the file stores no maker; `SoftwareShapes` match a firmware string by shape, `A` a letter, `9` a digit). The first matching row wins. Today a row decides one thing: whether binary header times hold local time
(`HeaderTimeIsLocal`). Every row records its `Basis` (`ObservedInFiles` or `Documented`) and `Evidence`.

| Row | Effect | Basis | What settled it |
|---|---|---|---|
| `gopro` (model `HERO…`), `gopro-max` (model `GoPro…`), `gopro-make` (maker `GoPro`), `gopro-firmware` (the `FIRM` atom's firmware string, shape `HD6.01.01.60.00`, `H19.03.01.30.00`, `FS1.04.01.70.00`) | header times are local | observed | HERO5, HERO6 and MAX sample files: `mvhd` equals the GPMF GPS UTC time minus a whole-hour offset (HERO6 11:27:32 vs 19:27:58 UTC; HERO5 10:31:43 vs 17:31:03; MAX 15:45:32 vs 23:45:14). The movie files store no maker or model (the model lives in the GPMF track), but all ten sample files (HERO5–8, MAX, Fusion) carry the firmware atom, which the facts read as a Software candidate |
| `apple` | header times are UTC | documented | the QuickTime header is UTC by specification; Apple writes `creationdate` with an offset (no iPhone file was available) |
| `android` (maker Google), `samsung` | header times are UTC | documented | Android's `com.android.*` keys; Samsung's `samsung.android.utc_offset` key is listed in ExifTool's QuickTime tag documentation (no sample file was available) |

DJI, Sony, Canon and Panasonic have no row: no sample file or document available to this build says anything about their header times that the defaults
do not already assume. Sony and DJI sidecars carry their own times (below); Panasonic and Sony AVCHD times come from MDPM with their own zone field.
Evidence beats a profile: when attached GPS telemetry shows the header time is already UTC (within ten minutes of the first fix's UTC time), a "header is local" row is not applied and the reason says so. In the sample set this matters for one clip: `karma.mp4` has the HERO5 firmware family but its header equals GPS UTC; without GPS telemetry the firmware row would misread it as local. Two other files (HERO7, Fusion) have header clocks that were never set (2016, 2017 against GPS dates of 2019 and 2018), so no header time is trustworthy there; the GPS candidate is the only good one. Add a row only with the evidence that settles it. Rows are data: pass a different `DeviceProfileTable` to `DefaultFactResolver`.

## Embedded Exif

`Exif.ExifBlock` reads a TIFF/Exif block (CIPA DC-008): IFD0, IFD1, the Exif, GPS and Interoperability IFDs, SubIFDs. Entries keep their raw bytes;
maker notes and thumbnails are not interpreted. Limits: 16 directories, 4096 entries per directory, 20,000 entries, block ≤ `MaxTagBytes`; loops, values
outside the block and truncated directories are defects (`exif.loop`, `exif.truncated`, `exif.limit`), never exceptions. A TIFF header may sit up to 16
bytes into the data (container wrappers). In ISO files the reader looks at the user-data atoms `QVMI` (Casio), `MVTG` (FujiFilm), `RMKN` (Ricoh) and
`SIGM` (Sigma), which ExifTool's QuickTime documentation lists as Exif-structured. Exif facts: `DateTimeOriginal` (+`OffsetTimeOriginal`, `SubSecTime`),
`DateTimeDigitized`, GPS date and time (UTC), GPS position and altitude (status `V` = void is skipped), `Make`, `Model`, `Software`, `BodySerialNumber`.
No file in the corpus carried an Exif block in a video container; the atoms above are implemented from documentation and crafted blocks only.

## Sidecars

`Sidecars.Read` detects by content, never by extension, and reads at most `MaxTagBytes` (DJI SRT: the first 64 KiB).

| Sidecar | Detected by | Evidence |
|---|---|---|
| XMP (`.xmp`) | `xmpmeta`, `xpacket` or the XMP namespace | the same mapping as XMP inside a movie |
| Sony clip XML (`CLIP/C0001M01.XML`) | root `NonRealTimeMeta` | `CreationDate/@value` (offset-bearing), `Device/@manufacturer`, `@modelName`, `@serialNo` |
| DJI SRT | cue text with `[latitude…]`, `[iso…]`, `SrtCnt` or `FrameCnt` | frame 1 wall-clock time minus its cue start (no zone, tier 3), first non-zero `[latitude]`/`[longitude]` with `abs_alt`; the older `GPS (a, b, c)` layout is a defect (its coordinate order was not settled) |

Sidecar candidates use sources prefixed `sidecar:` and their byte ranges point into the sidecar file. AVCHD CPI/MPL files are not read.

**Lookup rule (only `MediaFile.AddSidecarsBesideFile`, the CLI `info` unless `--no-sidecars`):** in the media file's directory, names compared ignoring case:
`stem.xmp`, `name.ext.xmp`, `stemM01.XML`, `stem.srt`. Other directories are not searched, and a found file still has to be recognised by content.
Passing a sidecar yourself: `media.AddSidecar(path)` or `media.AddEvidence(Sidecars.Read(bytes))`.

## Replacing the resolver

```csharp
media.Resolver = new MyResolver();                         // IFactResolver; applies to every fact property from the next read
media.Resolver = new DefaultFactResolver(myProfiles);      // same rules, your device rows
var facts = new MyResolver().Resolve(FactEvidence.FromContainer(media.Metadata.Facts));   // or without a MediaFile
```

A resolver receives `FactEvidence` and returns `MediaFacts` built with `Fact.Create(candidates, resolution)`. It must keep every candidate; it decides only the
order and the `FactResolution` that explains it. Keep it deterministic: the same evidence gives the same facts.

## Telemetry as evidence

Decoded `TelemetryChannel`s (model in `Metadata/Telemetry.cs`, decoders in `Metadata/Telemetry/`) are evidence once they have been read: `media.Telemetry`
(which decodes the file's GPMF, CAMM and `mebx` tracks into memory) or `media.AttachTelemetry(channels)` for channels decoded or filtered elsewhere. Reading
the facts never decodes telemetry on its own, so a facts-only open stays structural; read `media.Telemetry` first (CLI: `info --telemetry`) when GPS evidence is wanted.
Only `Gps` channels matter to the resolver: `GpsSample.GpsTime` + `Fix` give the tier-2 time and the position candidate. Decoders must set `Fix` to 2D/3D for
formats that carry no quality indicator, or their fixes are ignored.

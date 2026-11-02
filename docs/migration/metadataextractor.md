# From MetadataExtractor

Docs read: MetadataExtractor README for the .NET port (main branch, read 2026-11-02; the README shows package 2.9.1, the bench
comparator is 2.9.3). Source was not read.

MetadataExtractor returns a flat sequence of `Directory` objects, each with `Tag`s. OneMedia answers the questions those tags are
usually read for (when, where, what device) as facts with their evidence, and keeps the raw tags under `media.Native`.

| MetadataExtractor | OneMedia |
|---|---|
| `ImageMetadataReader.ReadMetadata(path)` | `Media.Open(path)` |
| `QuickTimeMovieHeaderDirectory` created/modified | `media.CaptureTime.Candidates` (source `mvhd`, certainty `Unknown` unless a profile says otherwise) |
| `QuickTimeMetadataHeaderDirectory` `TagCreationDate` with offset (`TryGetDateTimeOffset`) | `media.CaptureTime.Value.ToDateTimeOffset()`; offset kept in `MediaDateTime.OffsetMinutes` and `Certainty` |
| GPS in `QuickTimeMetadataHeaderDirectory` / Exif GPS directory | `media.Location` (`Fact<GeoLocation>`: latitude, longitude, altitude) |
| `Make`/`Model` tags | `media.Device.Make`, `Model`, `Software`, `Serial` |
| movie dimensions, frame rate | `media.Video.Video`: `Width`, `Height`, `FrameRateNumerator/Denominator`, `RotationDegrees` |
| `directory.Tags`, `tag.Name`, `tag.Description` | `media.Native[system, key]`, `media.Native.Find(system, key)` (`NativeTag`) |
| `directory.HasError`, `Errors` | `media.Defects` (typed, with byte offsets) |
| Exif/XMP/IPTC in JPEG, PNG, RAW | out of scope: OneMedia reads video and audio containers (and the Exif blocks they embed) |

```csharp
using MediaFile media = Media.Open("located.mp4");
Fact<MediaDateTime> captured = media.CaptureTime;
foreach (FactCandidate<MediaDateTime> c in captured.Candidates)
    Console.WriteLine($"{c.Source} {c.RawValue} {c.Interpretation} {c.Certainty}");
NativeTag? tag = media.Native["QuickTime", "©xyz"];
if (media.Location.TryGetValue(out GeoLocation where)) Console.WriteLine($"{where.Latitude}, {where.Longitude}");
```

## Differences that matter

- **Candidates, not a single field.** A file often carries several times (container header, vendor tag, telemetry, file name). They
  are listed with source and time-zone certainty; `media.CaptureTime.Resolution` says which won and why. The resolver is replaceable
  (`media.Resolver`).
- **No local-time guesswork.** An offset-less time is reported with certainty `Unknown`; it is not converted with the machine's zone.
- **Telemetry** (GoPro GPMF, CAMM, DJI, `mebx`) decodes to typed samples: `media.Telemetry`.
- **Lazy and range-based.** A metadata read touches the structure ranges only; a 4 GB `moov`-last MP4 costs a seek, not a scan.
- **Writable**, losslessly (see the TagLib# page); MetadataExtractor is read-only.

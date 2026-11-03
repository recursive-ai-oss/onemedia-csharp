# From MetadataExtractor

Basis: the MetadataExtractor .NET README (https://github.com/drewnoakes/metadata-extractor-dotnet, main branch, fetched 2026-11-02; it shows
package 2.9.1, the bench comparator is 2.9.3). Only members the README names are mapped. Source was not read.

MetadataExtractor returns a flat sequence of `Directory` objects, each with `Tag`s. OneMedia answers the questions those tags are
usually read for (when, where, what device) as facts with their evidence, and keeps the raw tags under `media.Native`.

| MetadataExtractor | OneMedia |
|---|---|
| `ImageMetadataReader.ReadMetadata(path)` | `Media.Open(path)` |
| movie-header dates in the QuickTime/MP4 directories | `media.CaptureTime.Candidates` (source `mvhd`, certainty `Utc`, as the specification defines it; a device profile can mark a camera's header time as local) |
| `QuickTimeMetadataHeaderDirectory` `TagCreationDate` with offset (`TryGetDateTimeOffset`) | `media.CaptureTime.Value.ToDateTimeOffset()` (null unless the zone is known); offset kept in `MediaDateTime.OffsetMinutes` and `Certainty` |
| QuickTime location dates and the location tags in the directories | `media.Location` (`Fact<GeoLocation>`: latitude, longitude, altitude) |
| Exif IFD0 `Make`/`Model` (the README's example) | `media.Device.Make`, `Model`, `Software`, `Serial` |
| movie dimensions, frame rate | `media.Video`: `Width`, `Height`, `FrameRate`, `RotationDegrees` (exact rate: `media.Video.Video`) |
| `directory.Tags`, `tag.Name`, `tag.Description` | `media.Native[system, key]`, `media.Native.Find(system, key)` (`NativeTag`) |
| errors a directory reports | `media.Defects` (typed, with byte offsets) |
| Exif/XMP/IPTC in JPEG, PNG, RAW | out of scope: OneMedia reads video and audio containers (and the Exif blocks they embed) |

```csharp
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

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
- **No local-time guesswork.** A wall-clock time with no offset (a vendor tag, say) is reported with certainty `Unknown`; it is not converted with the machine's zone. The movie header's own times are `Utc`.
- **Telemetry** (GoPro GPMF, CAMM, DJI, `mebx`) decodes to typed samples: `media.Telemetry`.
- **Lazy and range-based.** A metadata read touches the structure ranges only; a 4 GB `moov`-last MP4 costs a seek, not a scan.
- **Writable**, losslessly (see the TagLib# page); the MetadataExtractor README describes a library for reading metadata.

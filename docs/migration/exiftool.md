# From ExifTool

Basis: the ExifTool application documentation (https://exiftool.org/exiftool_pod.html) and the QuickTime, GPS and Composite tag-name pages
(https://exiftool.org/TagNames/), fetched 2026-11-02; each flag and tag below was found on them. ExifTool 13.60 is the version run as an
oracle in OneMedia's tests. The Perl source and modules were not read. ExifTool is a command-line tool; OneMedia is a library, so the map is
flag to call.

| ExifTool | OneMedia |
|---|---|
| `exiftool file` | `Media.Open(path)`; CLI: `onemedia info file`, `onemedia tags file` |
| `-Make -Model` | `media.Device.Make`, `media.Device.Model` (`Fact<string>`) |
| `-CreateDate`, `-DateTimeOriginal`, and the `QuickTimeUTC` API option (`-api QuickTimeUTC=1`; documented on the QuickTime page) | `media.CaptureTime` (candidates with source and time-zone certainty, plus the resolved value) |
| `-GPSPosition` (a Composite tag) | `media.Location` |
| `-Rotation` (QuickTime tag, also a writable Composite tag) | `media.Rotation` |
| `-G1 -a -s` (all tags with their group) | `media.Native[system, key]`, `media.Native.Find(system, key)`; the system names the group |
| `-ee` (embedded telemetry: GPMF, CAMM, `mebx`) | `media.Telemetry` (typed samples), `media.OpenTelemetry(track)` |
| `-Title=x -Artist=y` | `media.Tags.Title = "x"; media.Tags.Artist = "y"; media.SaveAs(out)` |
| `-CreateDate=...` on a video | `Media.SetCaptureTime(in, out, DateTimeOffset)` (writes every date field the file has, with a report) |
| `-GPS:all=` (group delete, note 4 of the application docs) | `Media.StripLocation(in, out)` |
| `-SerialNumber=` and other device tags | `Media.StripDeviceIds(in, out)` |
| `-overwrite_original` | `media.Save()` (atomic replace); the default is a new file via `SaveAs` |
| `-overwrite_original_in_place` (copies the temp file over the original) | no equivalent: `media.SaveInPlace()` is different, it writes only within existing padding and throws `PaddingRequiredException` otherwise |
| `-json`, `-csv` | typed objects; serialise what you need |
| the `Warning` and `Error` tags (`-a` shows duplicates) | `media.Defects` |
| `-ee` on still images, MakerNotes, IPTC, XMP in JPEG | out of scope |

```csharp
using RecursiveAI.OneMedia;
using (MediaFile media = Media.Open("located.mp4"))
    _ = media.Native["QuickTime", "©xyz"];
Media.SetCaptureTime("in.mp4", "dated.mp4", new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.FromHours(2)));
Media.StripLocation("located.mp4", "nolocation.mp4");
Media.StripDeviceIds("located.mp4", "nodevice.mp4");
```

## Differences that matter

- **A resolver instead of a flag.** ExifTool shows each date tag and leaves the choice to you (or to `-api QuickTimeUTC`). OneMedia
  lists the candidates and picks one by a documented precedence (docs/facts.md); `media.Resolver` can be replaced.
- **Writes are reported.** `SetCaptureTime` and `Save*` return what was written and what could not be represented; they do not
  change media bytes (the per-track payload hash stays the same).
- **Strip is documented, not exhaustive.** It removes the documented location and device fields in every system and telemetry track;
  its report names the vendor boxes, chunks, tracks and trailers it has no rule for; it does not search free text (docs/strip.md).
- **Parity is checked, not assumed.** Reads are compared against ExifTool 13.60 on the test corpus; docs/facts.md lists the
  differences that remain.

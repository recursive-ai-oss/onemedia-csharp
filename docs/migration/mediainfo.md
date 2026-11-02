# From MediaInfo

Basis: black-box run of the MediaInfo 26.10 CLI (MediaInfoLib v26.10, 2026-11-02): `mediainfo --help` (confirms `--Full`, `--Output=JSON`,
`--Inform=`, `--Info-Parameters`) and `mediainfo --Info-Parameters`, from which every field name below was checked in its stream section
(General, Video, Audio, Text, Menu). The library source was not read; MediaInfo is BSD-2 and is not a dependency.

MediaInfo prints one flat report of "General", "Video", "Audio", "Text" and "Menu" streams, with every value a string. OneMedia gives typed
values; there is no text report to parse.

| MediaInfo | OneMedia |
|---|---|
| `mediainfo file` | `Media.Open(path)`; CLI: `onemedia info file` |
| General: `Format`, `Duration` | `media.Format`, `media.Duration` / `media.ExactDuration` |
| `Encoded_Date`, `Tagged_Date`, `Recorded_Date` (General) | `media.CaptureTime.Candidates`, `media.Facts`, `media.Native[...]` |
| General: `Title`, `Performer`, `Album`, `Track/Position`, `Cover_Data` | `media.Tags.Title`, `Artist`, `Album`… |
| Video: `Format`, `Width`, `Height`, `FrameRate`/`FrameRate_Num`/`FrameRate_Den`, `Rotation` | `media.Video.Codec`, `.Video.Value.Width`, `.Height`, `.FrameRateNumerator/Denominator`, `.RotationDegrees` |
| `CodecID` | `track.Rfc6381Codec()`, `track.CodecTag` |
| Audio: `Format`, `Channel(s)`, `SamplingRate`, `BitRate` | `media.Audio.Codec`, `.Audio.Value.Channels`, `.SampleRate`, `.BitRate` |
| Text streams, Menu streams | `media.Subtitles` (cues via `Document.Cues`), chapters through the native layer |
| `--Output=JSON`, `--Full` | typed objects; the CLI's `info` command prints a report |
| Inform templates | not provided: write the projection you want in C# |

```csharp
using MediaFile media = Media.Open("in.mp4");
MediaTrack? video = media.Video;
string line = $"{media.Format} {media.Duration} {video?.Codec} {video?.Video?.Width}x{video?.Video?.Height}";
```

## Differences that matter

- **Frame rate is a rational** (`FrameRateNumerator/Denominator`), 0/0 when the container does not say; packet timestamps are exact
  `MediaTime`. No "29.970 fps" strings.
- **Duration** has three honest answers: `ContainerDuration` (what the header says), `TracksDuration` (from tracks) and `ExactDuration`
  (the best of them); growing and truncated files say so rather than guessing.
- **Defects** are a typed list with offsets (`media.Defects`).
- **I/O**: opening and probing read structure ranges only; docs/io-results.txt gives bytes and range counts next to
  MediaInfo's for the same files.
- MediaInfo reads some formats OneMedia does not (and the reverse); check `docs/` per format before relying on parity.

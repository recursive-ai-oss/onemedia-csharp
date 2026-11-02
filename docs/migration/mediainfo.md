# From MediaInfo

Docs read: MediaInfo CLI documentation and the output-parameter naming used by `--Output=JSON` / `--Full` (MediaInfo 26.10 CLI, the version
run as an oracle in OneMedia's tests). The library source was not read; MediaInfo is BSD-2 and is not a dependency.

MediaInfo prints one flat report of "General", "Video", "Audio", "Text" and "Menu" streams, with every value a string. OneMedia gives typed
values; there is no text report to parse.

| MediaInfo | OneMedia |
|---|---|
| `mediainfo file` / `MediaInfo.Open(path)` | `Media.Open(path)`; CLI: `onemedia info file` |
| General: Format, Duration | `media.Format`, `media.Duration` / `media.ExactDuration` |
| General: Encoded date, Tagged date | `media.CaptureTime.Candidates`, `media.Facts`, `media.Native[...]` |
| General: title/performer/album… | `media.Tags.Title`, `Artist`, `Album`… |
| Video: Format, Width, Height, Frame rate, Rotation | `media.Video.Codec`, `.Video.Value.Width`, `.Height`, `.FrameRateNumerator/Denominator`, `.RotationDegrees` |
| Video: Codec ID (`avc1.…`) | `track.Rfc6381Codec()`, `track.CodecTag` |
| Audio: Format, Channel(s), Sampling rate, Bit rate | `media.Audio.Codec`, `.Audio.Value.Channels`, `.SampleRate`, `.BitRate` |
| Text streams, Menu (chapters) | `media.Subtitles` (cues via `Document.Cues`), chapters through the native layer |
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
- **Defects** are a typed list with offsets (`media.Defects`), not a "Conformance" text field.
- **I/O**: opening and probing read structure ranges only; docs/io-results.txt gives bytes and range counts next to
  MediaInfo's for the same files.
- MediaInfo reads some formats OneMedia does not (and the reverse); check `docs/` per format before relying on parity.

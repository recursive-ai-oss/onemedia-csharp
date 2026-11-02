# From FFMpegCore, ffprobe and ffmpeg

Docs read: FFMpegCore README (main branch, read 2026-11-02; it states CI against ffmpeg 8.1) and the ffprobe/ffmpeg command-line
documentation (ffmpeg 7.0.2 and 8.x user docs). Source of neither was read.

FFMpegCore builds `ffmpeg` command lines and parses `ffprobe` output. OneMedia does the container work in-process and has no
subprocess, so there is no binary to locate, no argument builder and no temp-file folder to configure.

## Probing

| Theirs | OneMedia |
|---|---|
| `FFProbe.Analyse(path)` / `AnalyseAsync` | `Media.Open(path)` / `Media.OpenAsync(path)` |
| `ffprobe -show_format` | `media.Format`, `media.ExactDuration`, `media.Tags`, `media.Defects` |
| `ffprobe -show_streams` | `media.Tracks` (kind, codec, `Rfc6381Codec()`, language, disposition, edit info) |
| `ffprobe -show_packets` | `media.Packets(track)`: pts, dts, duration, keyframe flag, size, source byte range |
| any raw tag or box value | `media.Native[system, key]` |

```csharp
using var media = Media.Open("in.mp4");
string line = $"{media.Format}, {media.ExactDuration}, {media.Tracks.Count} tracks";
foreach (MediaTrack track in media.Tracks)
    line += $"; #{track.Index} {track.Kind} {track.Codec} {track.Rfc6381Codec()} {track.Language}";
```

```csharp
using var media = Media.Open("in.mp4");
foreach (OwnedPacket packet in media.Packets(media.Video!))
{
    using (packet) { if (packet.Info.IsKeyframe) { /* packet.Info.Pts, packet.Info.Dts, packet.Data */ } }
}
```

Times are `MediaTime` (integer ticks plus timescale), not floating seconds; `media.Duration` gives a `TimeSpan?` for convenience.
For allocation-free loops use the demuxer's `TryReadPacket` into a caller buffer.

## Operations

| Theirs | OneMedia |
|---|---|
| `ffmpeg -i in -c copy out.mkv` | `Media.Remux("in.mp4", "out.mkv")` returns a `RemuxReport` (`HasLosses`, dropped tracks) |
| `FFMpeg.SubVideo(in, out, from, to)`, `-ss -to -c copy` | `Media.Trim(in, out, from, to)`; `TrimMode.FrameAccurate` (default, edit list hides pre-roll) or keyframe |
| `-movflags +faststart`, `.WithFastStart()` | `Media.Faststart(in, out)` |
| `FFMpeg.Join(out, parts...)` | `Media.Concat(out, parts...)` (mismatched configurations are refused or reported) |
| `-metadata title=...` | `media.Tags.Title = ...; media.SaveAs(out)` |
| `-map_metadata -1`, strip location | `Media.StripLocation(in, out)`, `Media.StripDeviceIds(in, out)` |
| `-f hls` / `-f dash` | `Media.PackageHls(in, dir)` / `Media.PackageDash(in, dir)` |
| `-c:s webvtt` on a sidecar | `Subtitles.Convert("a.srt", "a.vtt")` returns a `ConversionReport` |
| make it play in a browser | `Media.CanPlay(path, BrowserProfiles.Chrome)`, `Media.MakePlayable(in, out, profile)` |
| `FFMpeg.ExtractAudio` to PCM/WAV | `media.Decode()` (package `RecursiveAI.OneMedia.Audio`) gives decoded audio through OneAudio |
| `FFMpeg.Snapshot`, `GifSnapshot`, scaling, filters, `-c:v libx264` | out of scope: OneMedia decodes and encodes no video |

```csharp
RemuxReport remux = Media.Remux("in.mp4", "out.mkv");
var clip = Media.Trim("in.mp4", "clip.mp4", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
Media.Faststart("in.mp4", "fast.mp4");
using (MediaFile media = Media.Open("in.mp4")) { media.Tags.Title = "Hello"; media.SaveAs("titled.mp4"); }
Media.PackageHls("in.mp4", "hls");
Subtitles.Convert("sample.srt", "migrated.vtt");
```

## Differences that matter

- Copy-only. Where `-c copy` would fail or quietly drop a stream, the report lists it (`RemuxReport.HasLosses`, `LostMediaTracks`).
- Payload bytes are copied, never re-serialised; trims cut with edit lists, so a frame-accurate cut keeps the pre-roll frames hidden
  in the file.
- Operations are verbs on paths and return reports; there is no builder and no process, so nothing to cancel by killing a child.
- Malformed input raises typed exceptions (`TryParse` variants beside them) or lands in `media.Defects`; there is no stderr to scrape.

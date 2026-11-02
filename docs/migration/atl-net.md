# From ATL.NET

Docs read: ATL.NET wiki "Usage / Code snippets" page (edited 2025-08-24, read 2026-11-02) and the package version 7.19.0 used as a bench
comparator through its public API only. Source was not read.

ATL's `Track` merges properties and tags into one object whose fields you set and `Save()`. OneMedia separates the file (`MediaFile`),
its tracks (`media.Tracks`) and its tags (`media.Tags`, `media.Native`).

| ATL.NET | OneMedia |
|---|---|
| `new Track(path)` | `Media.Open(path)` |
| `track.Title`, `Artist`, `Album`, `Genre`, `Comment`, `Composer` | same names on `media.Tags` |
| `track.Year` | `media.Tags.Date` (string as stored); `media.CaptureTime` for video |
| `track.TrackNumber`, `DiscNumber` | `media.Tags.TrackNumber`, `DiscNumber` (`int?`) |
| `track.Duration` (seconds) | `media.Duration` (`TimeSpan?`), `media.ExactDuration` |
| `track.Bitrate`, `ChannelsArrangement`, `SampleRate` | `media.Audio.BitRate`, `media.Audio.Audio.Value.Channels`, `.SampleRate` |
| `track.AdditionalFields["key"]` | `media.Native[system, key]`, `media.Native.Find(system, key)` (system named, e.g. `QuickTime`, `ID3v2`, `RIFF`) |
| `track.EmbeddedPictures` | `media.Tags.Pictures`; `SetPicture`, `RemovePictures` |
| `track.Lyrics`, `track.Chapters` | `media.Tags.Lyrics`; chapters through the native layer |
| `track.Save()` | `media.Save()`, `SaveAs(path)`, `SaveInPlace()` |
| `Settings.ID3v2_tagSubVersion = 3` | not a global switch; the file's ID3v2 version is kept, a new tag is 2.4, or 2.3 through the writer option `Id3v2Version` (docs/tag-writing.md) |

```csharp
using MediaFile media = Media.Open("aac.m4a");
MediaTrack audio = media.Audio!;
string summary = $"{media.Tags.Title} / {media.Tags.Album} / {audio.Audio!.Value.SampleRate} Hz / {audio.BitRate} bit/s / {media.Duration}";
```

```csharp
using MediaFile media = Media.Open("atl.m4a");
media.Tags.Title = "Edited";
try { media.SaveInPlace(); }
catch (PaddingRequiredException) { media.Save(); }   // no room in the file's padding: atomic replace
```

## Differences that matter

- **No global settings.** ATL's behaviour is steered by static `Settings`; OneMedia has none. Limits are one `MediaLimits` object you pass in.
- **Namespaced native keys.** ATL flattens custom fields into one dictionary with prefixes (`bext.`, `info.`); OneMedia keeps
  each system separate, so `ID3v2/TXXX` and `QuickTime/©day` never collide.
- **Saving is explicit about where bytes go**: new file, atomic replace, or existing padding (see the TagLib# page).
- **Video is first-class**: the same call opens MP4, MKV, AVI, TS and the audio formats ATL covers.
- **Decoding** is not ATL's job either; `media.Decode()` in `RecursiveAI.OneMedia.Audio` hands the audio track to OneAudio.

# From TagLib#

Docs read: TagLibSharp 2.3.0 public API documentation and README names only (the library is LGPL; its source was not read and it is
not referenced by any shipped OneMedia package).

TagLib# opens a file as a `TagLib.File`, exposes one merged `Tag` plus format-specific tags, and `Save()`s. OneMedia's `media.Tags` is a
view over every native tag system in the file (ilst, ID3v2, Vorbis comments, RIFF INFO, Matroska tags…); the native values stay available
and are written back losslessly.

| TagLib# | OneMedia |
|---|---|
| `TagLib.File.Create(path)` | `Media.Open(path)` (content-sniffed, never by extension) |
| `file.Tag.Title`, `Artist`, `Album`, `Genre`, `Comment`… | `media.Tags.Title`, `Artist`, `Album`, `Genre`, `Comment`… |
| `file.Tag.Track` | `media.Tags.TrackNumber` (`int?`) |
| `file.Tag.Pictures` | `media.Tags.Pictures` (`TagPicture`: type, MIME, `ReadData()`) |
| `file.Tag.Pictures = ...` | `media.Tags.SetPicture(bytes, mime, PictureType.FrontCover)`, `RemovePictures()` |
| `file.Properties.Duration` | `media.Duration` (`TimeSpan?`) or `media.ExactDuration` (`MediaTime`) |
| `file.Properties.AudioBitrate`, `AudioSampleRate`, `AudioChannels` | `media.Audio.BitRate`, `media.Audio.Audio.Value.SampleRate`, `.Channels` |
| `file.GetTag(TagTypes.Id3v2)` | `media.Native["ID3v2", "TIT2"]`, `media.Native.Find(system, key)` |
| `file.Save()` | `media.Save()` (atomic replace), `media.SaveAs(path)`, `media.SaveInPlace()` |
| `file.Dispose()` | `media.Dispose()` |

```csharp
using MediaFile media = Media.Open("in.mp4");
string? title = media.Tags.Title;
media.Tags.Title = "New title";
media.Tags.Artist = "New artist";
media.Tags.TrackNumber = 3;
TagWriteReport report = media.SaveAs("tagged.mp4");
```

```csharp
using MediaFile media = Media.Open("in.mp4");
media.Tags.SetPicture(jpegBytes, "image/jpeg", PictureType.FrontCover);
media.SaveAs("cover.mp4");
```

## Differences that matter

- **Edits are staged.** Setting a property records an edit on `media.Tags.Pending`; nothing changes on disk until `Save`, `SaveAs` or
  `SaveInPlace`. Reading the property afterwards returns the staged value.
- **Reports.** Each save returns a `TagWriteReport`: which systems were written, what could not be represented, warnings. TagLib#'s
  `Save()` returns nothing.
- **Padding.** `SaveInPlace()` writes only into padding that already exists and throws `PaddingRequiredException` (with the bytes
  needed) otherwise. `Save()` rewrites through a temp file and an atomic rename; media payload bytes are copied unchanged, and the
  per-track payload hash is the same before and after.
- **Every system present is written** (or the one you choose), not just the primary tag.
- **Unknown frames and atoms survive** an edit byte for byte.
- Corrupt tags raise typed defects on `media.Defects` instead of a `CorruptFileException` that hides the rest of the file.

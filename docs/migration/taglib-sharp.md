# From TagLib#

Basis: black-box use of the public TagLibSharp 2.3.0 API exactly as the benchmark project calls it (`File.Create`, `Tag.Title`,
`Performers`/`FirstPerformer`, `Album`, `AlbumArtists`, `Composers`, `FirstGenre`, `Comment`, `Year`, `Pictures`, `Properties.Duration`,
`Save`), 2026-11-02. The repository was not opened, no documentation page was fetched, and TagLib# is LGPL and used only in the bench
project. Properties not listed here are not mapped.

TagLib# opens a file as a `TagLib.File`, exposes one merged `Tag` plus format-specific tags, and `Save()`s. OneMedia's `media.Tags` is a
view over every native tag system in the file (ilst, ID3v2, Vorbis comments, RIFF INFO, Matroska tags…); the native values stay available
and are written back losslessly.

| TagLib# | OneMedia |
|---|---|
| `TagLib.File.Create(path)` | `Media.Open(path)` (content-sniffed, never by extension) |
| `file.Tag.Title`, `Album`, `Comment` | `media.Tags.Title`, `Album`, `Comment` |
| `file.Tag.FirstPerformer`, `Performers` | `media.Tags.Artist` (the artist field; multiple values through `media.Tags.Values(CommonField.Artist)`) |
| `file.Tag.AlbumArtists` | `media.Tags.AlbumArtist` |
| `file.Tag.Composers` | `media.Tags.Composer` |
| `file.Tag.FirstGenre` | `media.Tags.Genre` |
| `file.Tag.Year` | `media.Tags.Date` (string as stored) |
| `file.Tag.Pictures` | `media.Tags.Pictures` (`TagPicture`: type, MIME, `ReadData()`); writing: `SetPicture(bytes, mime, PictureType.FrontCover)`, `RemovePictures()` |
| `file.Properties.Duration` | `media.Duration` (`TimeSpan?`) or `media.ExactDuration` (`MediaTime`) |
| `file.Properties.Codecs` | `media.Tracks` (kind, codec, `Rfc6381Codec()`) |
| a format-specific tag object | `media.Native[system, key]`, `media.Native.Find(system, key)` |
| `file.Save()` | `media.Save()` (atomic replace), `media.SaveAs(path)`, `media.SaveInPlace()` |
| `file.Dispose()` | `media.Dispose()` |

```csharp
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;

using MediaFile media = Media.Open("in.mp4");
string? title = media.Tags.Title;
media.Tags.Title = "New title";
media.Tags.Artist = "New artist";
media.Tags.TrackNumber = 3;   // TagLib#'s track number is not mapped on this page; this is OneMedia's
TagWriteReport report = media.SaveAs("tagged.mp4");
```

```csharp
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags;

byte[] jpegBytes = File.ReadAllBytes("cover.jpg");
using MediaFile media = Media.Open("in.mp4");
media.Tags.SetPicture(jpegBytes, "image/jpeg", PictureType.FrontCover);
media.SaveAs("cover.mp4");
```

## Differences that matter

- **Edits are staged.** Setting a property records an edit on `media.Tags.Pending`; nothing changes on disk until `Save`, `SaveAs` or
  `SaveInPlace`. Reading the property afterwards returns the staged value.
- **Reports.** Each save returns a `TagWriteReport`: which systems were written, what could not be represented, warnings. TagLib#'s
  `Save()` returns nothing.
- **Padding.** `SaveInPlace()` writes only into padding that already exists and throws `PaddingRequiredException` (namespace `RecursiveAI.OneMedia.Writing`, with the bytes
  needed) otherwise. `Save()` rewrites through a temp file and an atomic rename; media payload bytes are copied unchanged, and the
  per-track payload hash is the same before and after.
- **Every system present is written** (or the one you choose), not just the primary tag.
- **Unknown frames and atoms survive** an edit byte for byte.
- Corrupt tags raise typed defects on `media.Defects` instead of a `CorruptFileException` that hides the rest of the file.

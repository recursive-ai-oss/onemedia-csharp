# Migrating to OneMedia

One page per tool. Each maps that tool's common tasks to OneMedia calls and says where behaviour differs. There are no shims and no
look-alike classes: OneMedia's calls are shaped by the file formats, not by another library's API.

| Coming from | Page |
|---|---|
| FFMpegCore, ffprobe, ffmpeg (`-c copy` work) | [ffmpeg-ffprobe.md](ffmpeg-ffprobe.md) |
| TagLib# | [taglib-sharp.md](taglib-sharp.md) |
| ATL.NET | [atl-net.md](atl-net.md) |
| MetadataExtractor | [metadataextractor.md](metadataextractor.md) |
| MediaInfo | [mediainfo.md](mediainfo.md) |
| ExifTool | [exiftool.md](exiftool.md) |

Every OneMedia snippet on these pages is compiled and run by `samples/OneMedia.Samples` (`Migration.cs`). The happy-path tasks of the
README are in `Tasks.cs` there, one or two lines each.

What is the same everywhere:

- **No decoding.** OneMedia reads and writes containers and tags. Anything that needs pixels or audio samples (thumbnails, scaling,
  re-encoding, filters) stays with the tool that did it; `RecursiveAI.OneMedia.Audio` decodes audio through OneAudio.
- **Typed defects, not silent repair.** A damaged file opens when it safely can and lists what is wrong on `media.Defects`, with byte offsets.
- **Evidence, not one guess.** Capture time, location and device are lists of candidates (source, raw value, interpretation, time-zone
  certainty) with a resolved value: `media.CaptureTime.Candidates`, `media.CaptureTime.Value`.
- **Writes never touch media bytes.** Tag edits go to a new file (`SaveAs`), an atomic replace (`Save`) or existing padding (`SaveInPlace`,
  which throws `PaddingRequiredException` rather than rewriting the file). The result is a report of what was written and what was refused.

# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers with no dependency beyond the .NET base class library. Subtitles are planned and not in this preview. Unaffiliated with other products or companies named OneMedia.

**Preview (`0.1.0-preview.2`): reads MP4/MOV/3GP (including fragmented MP4) and Matroska/WebM.** Packets with exact timestamps, keyframe flags and source byte ranges; codec header parsing (H.264, HEVC, AV1, VP8/VP9, AAC, Opus, Vorbis, FLAC, AC-3 and more); native tags, a common tag view and basic capture facts with their evidence; zero-allocation packet pull and structural-only probing of huge or remote files. Writing, tag editing and the other containers are planned and not in this release.

```csharp
using var media = Media.Open("clip.mov");
Console.WriteLine($"{media.Duration} {media.Video?.Codec} {media.Tags.Title} {media.CaptureTime}");
```

See the [README](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/README.md) for the support table and how each claim was checked, and the [changelog](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/CHANGELOG.md).

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.

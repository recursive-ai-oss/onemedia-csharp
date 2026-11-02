# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers with no dependency beyond the .NET base class library. Unaffiliated with other products or companies named OneMedia.

**Preview (`0.6.0-preview.1`): reads MP4/MOV/3GP (including fragmented MP4), Matroska/WebM, AVI/OpenDML and DV, ASF/WMV/WMA, MPEG-TS/M2TS, PS/VOB and elementary streams, MP3/ADTS/AC-3/AMR files, Ogg, FLAC, FLV, WAV/BWF/RF64/W64, AIFF/AIFC and CAF; writes MP4/MOV/3GP/fragmented MP4/CMAF, Matroska/WebM, MPEG-TS/M2TS and WAV/BWF/RF64; remuxes, trims, splits and concatenates without re-encoding; edits tags losslessly; makes files playable in browsers; strips location and device identifiers (the fields listed in its documentation; it does not search free text, unknown vendor atoms or trailers); writes capture dates; and repairs damaged files and rebuilds cut-off MP4/MOV/3GP recordings into a new file with a report.** Packets with exact timestamps, keyframe flags and source byte ranges; codec header parsing (H.264, HEVC, AV1, VP8/VP9, AAC, Opus, Vorbis, FLAC, AC-3 and more); native tags (ID3, APE, Vorbis comments, RIFF INFO, ASF and more) and a common tag view; zero-allocation packet pull and structural-only probing of huge or remote files.

**Previews, subject to change (subtitle parse and convert speed is published; the other previews have no speed rows yet):** subtitles and captions (SRT, WebVTT, ASS, TTML, SBV, LRC, container text tracks, CEA-608/708, SCC, teletext, bitmap subtitles), capture facts as evidence, telemetry and timed events. Facts and date writes were checked against ExifTool and MediaInfo on published samples and generated files, not on a corpus of device recordings. The CTA-608/708 and SCTE 35 standards were not available, so those parts rest on public regulations and black-box oracles. Repair is checked on generated files, published samples and 10 GoPro clips, not on a corpus of device recordings. HLS/DASH packaging and HLS/DASH sources are planned and not in this release.

```csharp
using var media = Media.Open("clip.mov");
Console.WriteLine($"{media.Duration} {media.Video?.Codec} {media.Tags.Title} {media.CaptureTime}");
```

See the [README](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/README.md) for the support table and how each claim was checked, and the [changelog](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/CHANGELOG.md).

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.

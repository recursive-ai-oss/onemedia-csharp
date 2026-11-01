# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers, with no dependency beyond the .NET base class library. This preview reads containers and metadata; subtitles are planned and not in it yet. OneMedia is unaffiliated with other products or companies named OneMedia.

The goal is the depth of libavformat, GPAC, mkvtoolnix, MediaInfo and ExifTool in one library you can call from C#: demux and mux, lossless tag editing, evidence-backed capture time and place, subtitles and captions, repair, and HLS/DASH packaging. It never decodes or encodes an audio or video sample; audio decoding is handed to [OneAudio](https://github.com/recursive-ai-oss/oneaudio-csharp) through the `RecursiveAI.OneMedia.Audio` adapter.

## Status

**Preview, version `0.2.0-preview.2`: reading every container below (MP4/MOV/3GP, Matroska/WebM, MPEG-TS/PS and elementary streams, ASF, AVI, DV, Ogg, FLAC, FLV, MP3/ADTS/AC-3/AMR, WAV/AIFF/CAF).** Structure writers (preview) are included; tag editing, muxing and remux, subtitles, repair and packaging are planned and not in this release. The table below lists what works and how it was checked; gaps are listed, not hidden.

```csharp
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Audio;   // package RecursiveAI.OneMedia.Audio

using var media = Media.Open("clip.mov");
Console.WriteLine($"{media.Duration} {media.Video?.Codec} {media.Audio?.Codec}");
Console.WriteLine($"{media.Tags.Title} {media.CaptureTime}");     // a fact prints its best value, or "none"
if (media.Location.TryGetValue(out var place))                      // absent facts have no value: no 0,0 stand-in
    Console.WriteLine($"{place.Latitude}, {place.Longitude}");
foreach (var candidate in media.CaptureTime.Candidates) Console.WriteLine(candidate);

using var pcm = media.Decode();   // the audio track, decoded by OneAudio
```

`onemedia info <file>` (or `--json`) prints tracks, codecs (RFC 6381 strings), tags and facts with their candidate sources.

### Support (0.2)

| Format | Read | Write | Tag edit | Packets vs ffprobe 7.0.2 | Probe + metadata I/O (local reads), ours vs ffprobe / MediaInfo / ExifTool | Documented deviations |
|---|---|---|---|---|---|---|
| MP4, MOV, 3GP/3G2, M4A/M4V, fragmented MP4 | ✓ demux, codec headers, native tags (`udta`, `ilst`, `mdta`, 3GPP, XMP, Nero chapters), common tags, basic facts | — | — | 121 corpus files, 744,306 packets, 0 unexplained | 4.38 GB file with the index at the end: 165,681 B in 34 reads vs 1,741,689 B / 32 (ffprobe), 23,628,223 B / 138 (MediaInfo), 1,103,868 B / 139 (ExifTool) | [docs/isobmff.md](docs/isobmff.md): edit-list repeats and drops, last-packet durations, negative composition offsets, fragmented durations are structural (no fragment scan) |
| Matroska, WebM (MKV, MKA, MK3D) | ✓ demux (seekable and live/forward-only), codec headers, tags with targets, attachments, basic facts | — | — | 92 corpus files, 464,934 packets, 0 unexplained | 4.45 GB file: 69,806 B in 20 reads vs 4,877,532 B / 16 (ffprobe), 72,168,087 B / 493 (MediaInfo), 79,600 B / 11 (ExifTool) | [docs/matroska-differential.md](docs/matroska-differential.md): attachments are not streams, RealAudio is not de-interleaved, RealVideo timestamps as stored |
| MPEG-TS, M2TS/MTS (AVCHD), PS/VOB, elementary video (Annex B, MPEG-1/2, AV1 OBU), IVF | ✓ demux (seekable and forward-only), PSI/SDT/EIT names, AVCHD MDPM, codec headers | — | — | 149 corpus files, 54,785 packets, 0 unexplained (SDT-stripped copies for the pinned ffprobe) | 2.37 GB transport stream: 1,576,976 B in 4 reads vs 5,460,112 B / 170 (ffprobe), 17,402,896 B / 266 (MediaInfo), 509,424 B / 64 (ExifTool) | [docs/mpeg.md](docs/mpeg.md): leading/trailing fragments, damaged-packet policy, DTS/Opus-in-TS not framed, AVCHD CPI/MPL sidecars listed not decoded |
| ASF, WMV, WMA | ✓ demux (seekable, forward-only), native `asf` tags in the common view, cover art, DRM files reported `Protected` (metadata only) | — | — | 55 corpus files, 82,454 packets, 0 unexplained (50 equal; 5 DRM files not demuxed, as designed) | 2.33 GB WMV: 36,950 B in 3 reads vs 5,079,040 B / 155 (ffprobe), 3,204,929 B / 51 (MediaInfo), 41,324 B / 8 (ExifTool). Media Foundation and Shell check on Windows 11 (18 ASF files): streams 62/67 agree (all 26 files, MP4 included), durations 24/25 exact, protection 18/18, samples 20/26 ASF streams equal with each difference explained ([docs/asf.md](docs/asf.md)) | [docs/asf.md](docs/asf.md): written clean-room from public descriptions and observed files; WMA audio does not decode |
| MP3 (ID3v1/v2, APE, Lyrics3), ADTS AAC, AC-3/E-AC-3, AMR/AMR-WB files | ✓ demux, Xing/Info/LAME tags, priming and padding, native tags in the common view | — | — | 134 corpus files, 10,544,449 packets, 0 unexplained (2 free-format MP3 that ffprobe cannot open) | 2.2 GB MP3: 151,783 B in 12 reads vs 163,968 B / 5 (ffprobe), 147,616 B / 4 (MediaInfo), 96,159 B / 16 (ExifTool) | [docs/audio-es.md](docs/audio-es.md): duration of streams without a frame count is an estimate; AC-3/E-AC-3 do not decode through `.Audio` |
| Ogg (Vorbis, Opus, FLAC, Theora, Speex), native FLAC | ✓ demux of all logical streams, chained files, Vorbis comments and FLAC blocks as tags, pictures | — | — | 72 corpus files, 246,857 packets, 0 unexplained | 2.2 GB FLAC: 69,666 B in 3 reads vs 229,376 B / 7 (ffprobe), 65,696 B / 2 (MediaInfo), 38,783 B / 8 (ExifTool) | [docs/ogg.md](docs/ogg.md), [docs/flac.md](docs/flac.md), [docs/vorbis-comment.md](docs/vorbis-comment.md) |
| FLV | ✓ demux (Enhanced FLV codecs), `onMetaData` tags | — | — | 30 corpus files, 217,868 packets, 0 unexplained | 3.63 GB file: 69,632 B in 2 reads vs 2,228,087 B / 47 (ffprobe), 5,830,707 B / 115 (MediaInfo), 30,448 B / 5 (ExifTool) | [docs/flv.md](docs/flv.md) |
| AVI (OpenDML), DV | ✓ demux of AVI, OpenDML, DV in AVI and raw DV; `LIST INFO` and `IDIT` tags | — | — | AVI 70 files, 164,648 packets, 0 unexplained; DV 14 files, 5,073 packets (ffprobe lists the audio inside DV frames as extra streams) | 4.65 GB OpenDML file: 4,821 B in 40 reads vs 1,308,840 B / 29 (ffprobe), 11,416,591 B / 176 (MediaInfo), 71,408 B / 11 (ExifTool) | [docs/avi.md](docs/avi.md), [docs/dv.md](docs/dv.md): DV audio is not de-shuffled, so DV type 1 and raw DV have no audio track |
| WAV/BWF/RF64/W64, AIFF/AIFC, CAF | ✓ demux (PCM and compressed), `LIST INFO`, `bext`, iXML, ID3 chunks, cue points | — | — | 225 corpus files, 2,204,392 packets, 0 unexplained (222 equal; 3 NIST SPHERE files named `.wav` are not RIFF and not read) | 4.4 GB RF64 file: 4,216 B in 9 reads vs 884,816 B / 27 (ffprobe), 1,048,576 B / 16 (MediaInfo), 22,256 B / 5 (ExifTool) | [docs/pcm-files.md](docs/pcm-files.md): one AIFF-C layout (`digit0v0.aiff`) is misread as empty; `.Audio` does not decode PCM or ADPCM |

Bytes and request counts for every corpus file of every family, over HTTP range requests and local reads, are in [docs/io-results.txt](docs/io-results.txt) (method in [docs/harness.md](docs/harness.md)). Audio tracks of MP4/3GP equal OneAudio's own MP4 reader packet for packet (250,469 packets over 64 files). Tags and facts agree with ExifTool 13.60 and MediaInfo 26.10 on 116 files, with every difference explained in [docs/tags.md](docs/tags.md); the field mapping is [docs/tag-mapping.md](docs/tag-mapping.md). The audio adapter is described in [docs/audio-adapter.md](docs/audio-adapter.md), the comparison method in [docs/harness.md](docs/harness.md), and the benchmarks in [docs/bench.md](docs/bench.md).

**Speed** against the fastest managed library per task (ratio = OneMedia time ÷ theirs, steady state; lower is better). Full tables, allocations, memory, first-call times and the native tools' times are in [docs/bench-results](docs/bench-results); the method is in [docs/bench.md](docs/bench.md).

| Task | Compared with | x64 (Ryzen 9 7950X3D) geomean / worst file | arm64 (Apple M2) geomean / worst file |
|---|---|---|---|
| MP4 demux, every packet | SharpMP4 | 0.10× / 0.21× | 0.09× / 0.20× |
| MP4 audio track only | OneAudio `Mp4PacketSource` | 0.53× / 0.82× | 0.49× / 0.61× |
| MP4 probe (tracks, duration) | MetadataExtractor, TagLib#, ATL.NET | 0.10× / 0.41× | 0.12× / 0.27× |
| Matroska demux | Matroska (NEbml) | 0.26× / 0.31× | 0.25× / 0.41× |
| Matroska probe | NEbml element reader | 0.68× / 0.79× | 0.59× / 0.78× |
| MP3 tags (ID3v1/v2, APE) | TagLib#, ATL.NET | 0.50× / 0.63× | 0.63× / 1.04× |
| FLAC and Ogg tags | TagLib#, ATL.NET | 0.56× / 0.76× | 0.73× / 0.91× |
| ASF/WMA tags | TagLib#, ATL.NET | 0.60× / 0.75× | 0.74× / 0.93× |
| WAV/AIFF/CAF open and tags | TagLib#, ATL.NET | 0.50× / 0.70× | 0.59× / 0.88× |
| AVI probe | MetadataExtractor, TagLib# | 0.82× / 0.84× | 0.78× / 0.81× |

The probe and tag rows were measured again for 0.2 ([x64](docs/bench-results/0.2-x64.md), [arm64](docs/bench-results/0.2-arm64.md)); the demux rows are from the first preview's run. ATL.NET cannot read AVI and TagLib# cannot read CAF, so those files have no ratio against them. Demux of the 0.2 containers has no managed comparator.

The first open in a process includes JIT time: about 17–23 ms for MP4 and 12–16 ms for Matroska, against 10–14 ms and about 3 ms for those libraries.

Every parser is fuzzed; damaged input is reported as typed defects with byte offsets and never surfaces as an untyped exception. Reading pulls packets with zero allocations per packet and constant memory at any file size.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneMedia` | the library; depends on nothing but .NET |
| `RecursiveAI.OneMedia.Audio` | adapter that hands audio tracks to OneAudio; depends on the core package and `RecursiveAI.OneAudio` |
| `RecursiveAI.OneMedia.Tool` | the `onemedia` command-line tool (`dotnet tool install`) |

Target framework is `net10.0`. The library is NativeAOT, trimming and browser-WebAssembly clean.

## Building

```
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

To run part of the suite, use the runner's own filters, for example `dotnet test --project tests/OneMedia.Tests -c Release --filter-class '*XiphFlvTests'` (also `--filter-method`, `--filter-namespace` with a full name, `--filter-query`). The VSTest-style `--filter` is not understood by this runner: `dotnet test` then reports "Handshake failures" and exit code 5 with zero tests.

Build output goes to `artifacts/` in the repository root (ignored by git). To build somewhere else, set `ONEMEDIA_SCRATCH` to a directory; output then goes to `$ONEMEDIA_SCRATCH/artifacts`. Test files are written under the system temporary directory (`TMPDIR`).

`smoke/OneMedia.AotSmoke` is published with NativeAOT and fails on any trim or AOT warning; `smoke/OneMedia.WasmSmoke` runs the same checks under the browser-wasm runtime (`wasm-tools` workload; run the built `AppBundle/main.mjs` with node). `tools/` holds the differential and I/O harness and the corpus scripts, `fuzz/` the fuzz targets and `bench/` the benchmarks; none of them ship.

## Contributing notes

- Clean room: the code is written from published specifications and from observing the behaviour of other tools as black boxes. The source of other media libraries and tools is not consulted.
- Every public type and member has an XML doc comment; warnings are errors.
- `unsafe` code, `Unsafe.*`, `MemoryMarshal` and intrinsics are not used.

## Licence

`MIT OR Apache-2.0`, at your option. Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`. Test media provenance and third-party notices are in `THIRD-PARTY-NOTICES.md`.

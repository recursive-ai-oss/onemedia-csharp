# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers, with no dependency beyond the .NET base class library. This preview reads containers and metadata; subtitles are planned and not in it yet. OneMedia is unaffiliated with other products or companies named OneMedia.

The goal is the depth of libavformat, GPAC, mkvtoolnix, MediaInfo and ExifTool in one library you can call from C#: demux and mux, lossless tag editing, evidence-backed capture time and place, subtitles and captions, repair, and HLS/DASH packaging. It never decodes or encodes an audio or video sample; audio decoding is handed to [OneAudio](https://github.com/recursive-ai-oss/oneaudio-csharp) through the `RecursiveAI.OneMedia.Audio` adapter.

## Status

**Preview, version `0.1.0-preview.3`: reading MP4/MOV/3GP (including fragmented MP4) and Matroska/WebM.** Writing, tag editing, every other container, subtitles, repair and packaging are planned and not in this release. The table below lists what works and how it was checked; gaps are listed, not hidden.

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

### Support (0.1)

| Format | Read | Write | Tag edit | Packets vs ffprobe 7.0.2 | Probe + metadata I/O (local reads), ours vs ffprobe / MediaInfo / ExifTool | Documented deviations |
|---|---|---|---|---|---|---|
| MP4, MOV, 3GP/3G2, M4A/M4V, fragmented MP4 | ✓ demux, codec headers, native tags (`udta`, `ilst`, `mdta`, 3GPP, XMP, Nero chapters), common tags, basic facts | — | — | 121 corpus files, 744,306 packets, 0 unexplained | 4.38 GB file with the index at the end: 165,681 B in 34 reads vs 1,741,689 B / 32 (ffprobe), 23,628,223 B / 138 (MediaInfo), 1,103,868 B / 139 (ExifTool) | [docs/isobmff.md](docs/isobmff.md): edit-list repeats and drops, last-packet durations, negative composition offsets, fragmented durations are structural (no fragment scan) |
| Matroska, WebM (MKV, MKA, MK3D) | ✓ demux (seekable and live/forward-only), codec headers, tags with targets, attachments, basic facts | — | — | 92 corpus files, 464,934 packets, 0 unexplained | 4.45 GB file: 69,806 B in 20 reads vs 4,877,532 B / 16 (ffprobe), 72,168,087 B / 493 (MediaInfo), 79,600 B / 11 (ExifTool) | [docs/matroska-differential.md](docs/matroska-differential.md): attachments are not streams, RealAudio is not de-interleaved, RealVideo timestamps as stored |
| MPEG-TS, M2TS/MTS (AVCHD), PS/VOB, elementary video (Annex B, MPEG-1/2, AV1 OBU), IVF | ✓ demux (seekable and forward-only), PSI/SDT/EIT names, AVCHD MDPM, codec headers | — | — | 149 corpus files, 54,785 packets, 0 unexplained (SDT-stripped copies for the pinned ffprobe) | 2.37 GB transport stream: 1,576,976 B in 4 reads vs 5,460,112 B / 170 (ffprobe), 17,402,896 B / 266 (MediaInfo), 509,424 B / 64 (ExifTool) | [docs/mpeg.md](docs/mpeg.md): leading/trailing fragments, damaged-packet policy, DTS/Opus-in-TS not framed, AVCHD CPI/MPL sidecars listed not decoded |

Bytes and request counts for all 213 corpus files, over HTTP range requests and local reads, are in [docs/io-results.txt](docs/io-results.txt) (method in [docs/harness.md](docs/harness.md)). Audio tracks of MP4/3GP equal OneAudio's own MP4 reader packet for packet (250,469 packets over 64 files). Tags and facts agree with ExifTool 13.60 and MediaInfo 26.10 on 116 files, with every difference explained in [docs/tags.md](docs/tags.md); the field mapping is [docs/tag-mapping.md](docs/tag-mapping.md). The audio adapter is described in [docs/audio-adapter.md](docs/audio-adapter.md), the comparison method in [docs/harness.md](docs/harness.md), and the benchmarks in [docs/bench.md](docs/bench.md).

**Speed** against the fastest managed library per task (ratio = OneMedia time ÷ theirs, steady state; lower is better). Full tables, allocations, memory, first-call times and the native tools' times are in [docs/bench-results](docs/bench-results); the method is in [docs/bench.md](docs/bench.md).

| Task | Compared with | x64 (Ryzen 9 7950X3D) geomean / worst file | arm64 (Apple M2) geomean / worst file |
|---|---|---|---|
| MP4 demux, every packet | SharpMP4 | 0.10× / 0.21× | 0.09× / 0.20× |
| MP4 audio track only | OneAudio `Mp4PacketSource` | 0.53× / 0.82× | 0.49× / 0.61× |
| MP4 probe (tracks, duration) | MetadataExtractor, TagLib#, ATL.NET | 0.10× / 0.40× | 0.12× / 0.28× |
| Matroska demux | Matroska (NEbml) | 0.26× / 0.31× | 0.25× / 0.41× |
| Matroska probe | NEbml element reader | 0.69× / 0.83× | 0.60× / 0.76× |

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

Build output goes to `artifacts/` in the repository root (ignored by git). To build somewhere else, set `ONEMEDIA_SCRATCH` to a directory; output then goes to `$ONEMEDIA_SCRATCH/artifacts`. Test files are written under the system temporary directory (`TMPDIR`).

`smoke/OneMedia.AotSmoke` is published with NativeAOT and fails on any trim or AOT warning; `smoke/OneMedia.WasmSmoke` runs the same checks under the browser-wasm runtime (`wasm-tools` workload; run the built `AppBundle/main.mjs` with node). `tools/` holds the differential and I/O harness and the corpus scripts, `fuzz/` the fuzz targets and `bench/` the benchmarks; none of them ship.

## Contributing notes

- Clean room: the code is written from published specifications and from observing the behaviour of other tools as black boxes. The source of other media libraries and tools is not consulted.
- Every public type and member has an XML doc comment; warnings are errors.
- `unsafe` code, `Unsafe.*`, `MemoryMarshal` and intrinsics are not used.

## Licence

`MIT OR Apache-2.0`, at your option. Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`. Test media provenance and third-party notices are in `THIRD-PARTY-NOTICES.md`.

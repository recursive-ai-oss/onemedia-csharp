# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for reading media containers, with no dependency beyond the .NET base class library. OneMedia is unaffiliated with other products or companies named OneMedia.

The goal is the depth of libavformat, GPAC, mkvtoolnix, MediaInfo and ExifTool in one library you can call from C#: demux and mux, lossless tag editing, evidence-backed capture time and place, subtitles and captions, repair, and HLS/DASH packaging. It never decodes or encodes an audio or video sample; audio decoding is handed to [OneAudio](https://github.com/recursive-ai-oss/oneaudio-csharp) through the `RecursiveAI.OneMedia.Audio` adapter.

## Status

**Preview, version `0.1.0-preview.1`. Nothing here reads a media file yet.** What exists is the foundation the readers are being built on:

- sources: files, streams, memory, caller-supplied ranged reads (HTTP range, blob storage) and forward-only streams, with a hook for counting bytes and range requests;
- `MediaTime`: exact rational time with explicit rounding, no floating point;
- `MediaLimits` and typed defects and exceptions, so damaged or hostile input is reported with byte offsets and never surfaces as an untyped exception;
- content-based format sniffing and an explicit format registry (no reflection, so unused formats trim away);
- the neutral track and packet model, with an allocation-free pull path and pooled owned packets;
- the `onemedia` tool, which for now only names the container format of a file (`onemedia info <file>`).

Reading MP4/MOV/3GP and Matroska/WebM is in progress; every other format and every operation in the list at the top is planned and not started. This README will say so until it is no longer true.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneMedia` | the library; depends on nothing but .NET |
| `RecursiveAI.OneMedia.Audio` | adapter that hands audio tracks to OneAudio; depends on the core package and `RecursiveAI.OneAudio` |
| `RecursiveAI.OneMedia.Tool` | the `onemedia` command-line tool (`dotnet tool install`) |

Target framework is `net10.0`. The library is built to be NativeAOT, trimming and browser-WebAssembly clean.

## Building

```
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

Build output goes to `artifacts/` in the repository root (ignored by git). To build somewhere else, set `ONEMEDIA_SCRATCH` to a directory; output then goes to `$ONEMEDIA_SCRATCH/artifacts`. Test files are written under the system temporary directory (`TMPDIR`).

`smoke/OneMedia.AotSmoke` is published with NativeAOT in CI and fails on any trim or AOT warning; `smoke/OneMedia.WasmSmoke` runs the same checks under the browser-wasm runtime (`wasm-tools` workload; run the built `AppBundle/main.mjs` with node). `tools/`, `fuzz/` and `bench/` hold the differential harness, fuzz targets and benchmarks; they are placeholders for now.

## Contributing notes

- Clean room: the code is written from published specifications and from observing the behaviour of other tools as black boxes. The source of other media libraries and tools is not consulted.
- Every public type and member has an XML doc comment; warnings are errors.
- `unsafe` code, `Unsafe.*`, `MemoryMarshal` and intrinsics are not used outside bulk-scan hot paths, and none exists yet.

## Licence

`MIT OR Apache-2.0`, at your option. Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`.
 EOF
sed -i 's/^ EOF$//' README.md
cat > PACKAGE.md <<'EOF'
# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers with no dependency beyond the .NET base class library. Unaffiliated with other products or companies named OneMedia.

**Preview (`0.1.0-preview.1`):** this release contains the foundation only: byte sources (file, stream, memory, ranged, forward-only), exact rational `MediaTime`, resource limits, typed defects and exceptions, content-based format sniffing with an explicit registry, and the track and packet model. Reading MP4/MOV/3GP and Matroska/WebM is in progress; no container can be opened yet.

See the [repository](https://github.com/recursive-ai-oss/onemedia-csharp) for the plan, the [README](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/README.md) for the current status, and the [changelog](https://github.com/recursive-ai-oss/onemedia-csharp/blob/main/CHANGELOG.md).

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.

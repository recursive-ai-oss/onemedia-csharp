# WAVE, AIFF and CAF files

How `RecursiveAI.OneMedia.Riff`, `.Aiff` and `.Caf` read audio files made of one run of sample data. Specifications: Microsoft's RIFF and
WAVE_FORMAT_EXTENSIBLE documentation, EBU Tech 3285 (BWF) and Tech 3306 (RF64/BW64), the iXML specification, Apple's AIFF/AIFF-C and Core Audio
Format specifications, and the Wave64 description (the layout was checked against files written by ffmpeg and libsndfile, since Sony's PDF is no longer
online). ExifTool's RIFF and AIFF tag pages are read as tables of facts.

## Layers

| Layer | Types | Notes |
|---|---|---|
| Chunk structure | `ChunkFile`, `ChunkNode`, `ChunkLayout` | Little-endian RIFF, RF64/BW64 (with `ds64`), Wave64 (GUID chunks, 8-byte alignment, sizes that include the 24-byte header) and big-endian IFF `FORM`. Lazy tree of ranges; unknown chunks are kept. CAF has its own tiny reader (`CafReader`, `CafChunk`). |
| Format model | `WaveFormat` (+ `WaveCodecs`), `WaveChunkReader` (cue/adtl, smpl, inst), `AiffCommon`, `CafAudioDescription`, `CafPacketTableHeader` | `fmt ` incl. WAVE_FORMAT_EXTENSIBLE with channel mask and sub-format GUID; `COMM` with the 80-bit rate; `desc`, `pakt`, `kuki`, `chan`, `info`. |
| Neutral demux | `WavDemuxer`, `AiffDemuxer`, `CafDemuxer` (sync, seekable and forward-only), `AudioFileAsyncDemuxer` (ranged) | One audio track, timescale = sample rate, every packet a keyframe. |
| Tags | `RiffTagReader` → `ChunkTagSet` | `LIST INFO`, `bext` v0–2, `iXML`, `axml`, `cart`, ID3 chunks (raw until the ID3 layer is wired), AIFF `NAME AUTH (c)  ANNO COMT`, CAF `info`. |

## Packets

PCM is cut into fixed packets with exact times (`pts` = frames before the packet, in units of 1/sample rate). The sizes are those the reference tool
(ffprobe) uses, found by experiment on 219 files, so that packet lists can be compared byte for byte:

| Data | Packet |
|---|---|
| WAVE/W64 PCM, float, G.711 | the largest power of two of frames not above `rate × 4096 / 44100` (500 Hz → 32, 8 kHz → 512, 44.1 kHz → 4096, 192 kHz → 16384), at least 1 |
| AIFF/AIFF-C and CAF PCM | 4096 bytes rounded down to whole frames |
| MS ADPCM, IMA ADPCM (WAVE) | as many whole blocks as fit the PCM frame count above, at least one |
| GSM 06.10 | one block (WAVE 65 bytes/320 frames; AIFF-C `GSM ` 33 bytes/160 frames) |
| `ima4` (AIFF-C, CAF) | one 34-byte-per-channel block of 64 frames |
| MP3, MP2, AC-3, ADTS in WAVE | one frame, found from its header; bytes between frames are skipped and reported (`audio.resync`) |
| CAF with a packet table | the packet table's entries, read from the file as the demuxer goes (never held) |
| any other codec | up to 4096 bytes (512 for G.726), each with its share of the stated frame count |

- **The last packet takes every byte that is left**, including a tail shorter than a frame or block (ffprobe delivers it; `audio.partial-unit` notes it).
- **AIFF**: the sound data is what `SSND` holds after its offset field; the frame count in `COMM` is not used to cut it (`aiff.frame-count` notes a
  mismatch). A `COMM` of an AIFC form that stops after the 18 fixed bytes means no compression.
- **WAVE `data` of size 0 followed by sample data** (a streaming writer that never came back) runs to the end of the file (`wav.data-size-zero`); a size
  of 0xFFFFFFFF, or an outer RIFF size of 0, means "to the end" as well (`riff.unbounded-size`).
- **A chunk that fits the file but ends after a too-small outer size** is read in full (`riff.size-exceeds-parent`); libsndfile's Wave64 files do this.
- **CAF** `data` with size −1 runs to the end of the file. Priming and remainder frames of the packet table are reported on the track, not applied.
- `fact` counts are used for duration unless they are more than twice what the data holds (`wav.fact-count`).
- Text: UTF-8 when valid, else Latin-1 (`riff.text-latin1`); `INFO` strings end at the first NUL.

## Defects

`riff.*` (chunk structure): `truncated-header`, `trailing`, `size-exceeds-file`, `size-exceeds-parent`, `unbounded-size`, `w64-size`, `ds64-short`,
`bext-short`, `cart-short`, `smpl-short`, `smpl-loops`, `cue-count`, `ixml-large`, `ixml-xml`, `text-latin1`. `wav.*`: `no-data`, `block-align`,
`data-size-zero`, `fact-count`. `aiff.*`: `no-data`, `frame-count`, `comt`. `caf.*`: `no-data`, `no-pakt`, `pakt-short`, `pakt-varint`, `size`,
`size-exceeds-file`, `truncated-header`. `audio.*`: `partial-unit`, `resync`, `truncated-data`. Every one carries the byte offset of the chunk or value.
Typed exceptions (`MediaFormatException`) are raised only when no audio can be described: no `fmt `/`COMM`/`desc`, zero channels or rate.

## Reading from ranged sources

`WavFileFormat`, `AiffFileFormat` and `CafFileFormat` implement `OpenAsync`. The synchronous reader runs over a cache of fetched ranges; a read of bytes not
yet fetched stops the step, the range is fetched (16 KiB blocks for headers and tags, 64 KiB blocks for sample data, two blocks kept), and the step runs
again. Opening a file therefore costs one request per distant chunk (typically 2–3: the head, and the tail where `LIST`/`iXML`/`id3 ` chunks sit), and
playing costs one request per 64 KiB; memory is constant. Tags (`ReadMetadataAsync`) fetch each tag chunk whole, bounded by `MaxTagBytes`.

## Tags

Native systems: `riff-info`, `bext`, `ixml`, `axml`, `cart`, `aiff`, `caf-info`, `id3-chunk` (raw bytes with ranges). The common view maps through
`TagMappingTable` (generated page: `tag-mapping.md`). The BWF origination date and time is a capture-time candidate with the zone unknown (rank 35,
below offset-bearing vendor times). iXML is read with `XmlReader` (no DTD, no entity expansion) and keeps its whole payload as the range.

## Fidelity

Unedited input is never modified, and all chunks, known or not, stay reachable through `Chunks` with their ranges. Packets carry the source range of the
bytes delivered. See the tables below for how packets, durations and tags compare with ffprobe, MediaInfo and ExifTool.

## Differential results

RESULTS_PLACEHOLDER

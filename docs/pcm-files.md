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
| Tags | `RiffTagReader` → `ChunkTagSet` | `LIST INFO`, `bext` v0–2, `iXML`, `axml`, `cart`, ID3 chunks (kept raw, and their frames read as `id3v2` tags), AIFF `NAME AUTH (c)  ANNO COMT`, CAF `info`. |

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
- `fact` counts are used for duration unless they differ from what the data holds by more than a factor of two (`wav.fact-count`).
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

Native systems: `riff-info`, `bext`, `ixml`, `axml`, `cart`, `aiff`, `caf-info`, `id3-chunk` (raw bytes with ranges, beside the `id3v2` frames read from them; a chunk that holds no tag, or a tag longer than its chunk, is a defect and stays raw). The common view maps through
`TagMappingTable` (generated page: `tag-mapping.md`). The BWF origination date and time is a capture-time candidate with the zone unknown (rank 35,
below offset-bearing vendor times). iXML is read with `XmlReader` (no DTD, no entity expansion) and keeps its whole payload as the range.

## Fidelity

Unedited input is never modified, and all chunks, known or not, stay reachable through `Chunks` with their ranges. Packets carry the source range of the
bytes delivered. See the tables below for how packets, durations and tags compare with ffprobe, MediaInfo and ExifTool.

## Differential results

Method: `OneMedia.Oracles diff --family pcm` against ffprobe 7.0.2 (static build), packet by packet (track, pts, dts, duration, size, file position, key flag,
SHA-256 of the payload). Corpus: 65 files made by ffmpeg 7.0.2 from a sine input (PCM s8 to f64, extensible 5.1/7.1, µ-law, A-law, MS and IMA ADPCM, MP3 and
AC-3 in WAVE, RF64, Wave64, AIFF, AIFF-C, CAF with PCM/ALAC/Opus/IMA4, tagged variants), 32 crafted files (`tools/gen-pcm-fixtures.py`: iXML, cart, smpl, cue,
streaming sizes, damage) and 122 published samples (libsndfile test files, Core Audio samples, ffmpeg's sample collection).

| Result | Files |
|---|---|
| Packets equal ffprobe's, no exception needed | the rest |
| Equal after a documented exception (`tools/OneMedia.Oracles/exceptions.tsv`) | see below |
| ffprobe reports no streams (we read or reject, no comparison) | 14 |
| Not RIFF at all (NIST SPHERE files named `.wav`) | 3 |

**Exceptions**, each from an experiment on the worker:

- *CAF with a packet table*: ffprobe prints N/A for the duration; we report the table's frames per packet.
- *Last packet of a block-coded WAVE stream*: ffprobe prints N/A timestamps.
- *VOXware, TrueSpeech*: ffprobe cuts them with its own frame parser (27 and 64-byte packets); we deliver 4096-byte packets (bytes identical).
- *OKI ADPCM, G.726 in WAVE and AIFF-C*: the same data and packet bytes; ffprobe's parser counts more frames than the header implies.
- *Four libsndfile AIFF-C test files* (`Fnonull`, `Pmiscck`, `Poffset`, `Ptjunk`): ffprobe delivers no packets (still none after removing the odd chunk, the FVER chunk or the SSND offset, so the
  cause is not one of those); the sound data is intact and we deliver it.
- *Files ffprobe rejects* (`ffprobe reports no streams`): COMM missing, bad channel count or rate, pakt missing, chunk sizes past the file; we report a defect or raise a typed exception.
- *NIST SPHERE* (`sw1038t_short.wav`, `swb.wav`, `telephone.wav`): ffprobe reads them; the container is not RIFF and not in scope.

**Larger than 4 GB** (generated, `tools/corpus/generate.sh` with `LARGE=1`): RF64 4.4 GB, Wave64 4.4 GB, CAF 4.4 GB, AIFF 2.3 GB. Packets equal ffprobe's on all four (2,179,689
packets), 0 bytes allocated per packet in steady state, peak working set 38–41 MB. Probe plus metadata read of the RF64 file: 20,480 bytes in 3 range requests
(ffprobe: 1,032,192 B in 2; MediaInfo 344,064 B in 1); the same file read from disk: 4,216 B in 9 reads (ffprobe 884,816 B in 27; MediaInfo 1,048,576 B in 16; ExifTool 22,256 B in 5).

**Tags** against ExifTool 13.60 and MediaInfo 26.10 on 219 files (`meta`): see `tags.md` for the method. Differences are explained: MediaInfo's CAF durations are
wrong for PCM (ffprobe and we agree); ExifTool computes ADPCM durations from the average data rate; both show mojibake for UTF-8 in INFO strings where we decode UTF-8;
the `id3 ` chunk of AIFF/WAVE was kept as raw bytes when this comparison was made; its frames are read now (artist, album, genre were "ours-missing" then). In the common view ID3 comes before `LIST INFO`. ATL.NET (7.19, `bench tagcompare`):
99 of 120 field comparisons equal; the others are ATL's file-name title fallback, its `0001-01-01` placeholder date, ID3 chunks, joined AIFF comments, and CAF comments it does not read.

## Durations against ExifTool 13.60 and MediaInfo 26.10, field by field

`meta --family pcm` on the final build: 223 files; the Duration fact differs in 49 rows (ffprobe 7.0.2 `format=duration` added as a third opinion). Every row, grouped by cause:

| Cause | Files | Ours | ExifTool | MediaInfo | ffprobe |
|---|---|---|---|---|---|
| MediaInfo (and ExifTool, which prints nothing) misread CAF: they take the packet-table or `data` size without the frame size | 12 generated CAF PCM/IMA4 files, `audio1`, `testcaf3`, `waterfall`, `oboe-*`, `caf-float*`, `caf-pcm32`, `over4gb.caf` (e.g. `caf-pcm_s16le-stereo48k` ours 3.000) | = ffprobe | none | 0.085–1.396 | = ours |
| ExifTool takes ADPCM duration from the average data rate and ignores the block frame count | `wav-adpcm_ima_wav-*`, `wav-adpcm_ms-*` (ours 3.000; ET 4.192 / 8.384), `aif-ima-adpcm`, `dvi_adpcm` (ET 0.774), `o2_dvi`, `msadpcm`, `wav-ms-adpcm` | = ffprobe | wrong | mostly = ours | = ours |
| ExifTool/MediaInfo read the AIFF-C `ima4` frame count from COMM, which counts blocks, not frames | `aifc-ima4-stereo` (ours 3.001; both 0.047) | = ffprobe | 0.047 | 0.047 | = ours |
| Both tools add the encoder delay or count MPEG frames differently | `wav-mp3` (3.025 vs 3.030), `wav-ac3` (3.000 vs 3.008) | frame walk = ffprobe | +0.005 | +0.005 | = ours |
| GSM 06.10 in AIFF-C: ffprobe's duration is wrong (272 s, 5.88 s); ours agrees with both tools | `aif-gsm610` (1.712), `o2_gsm` (0.040) | 1.712 / 0.040 | 1.701 / 0.037 | same | 272.1 / 5.88 |
| G.726 (4 bits per sample in the header): ours and MediaInfo use the header; ffprobe and ExifTool use 3 bits | `wav-g721` | 0.854 | 1.709 | 0.854 | 1.709 |
| `fact` chunk says 1.0 s, data holds 12.5 s | `try1.wav` | 12.485 after this round's fact check (was 1.000) | 104.064 (average-rate) | 1.000 | 12.485 |
| Stated frame count in COMM smaller than the sound data | `aiff-count-short` (the sound data decides: 0.250) | 0.250 | 0.063 | 0.062 | 0.0625 (ffprobe plays the stated count when it is shorter) |
| Streaming sizes | `stream-ffffffff` (ours 0.250 = ffprobe; ET 268435.456, MI 1.15e15 read the size literally), `stream-zero` (ours 0.250 = ffprobe, ET 0.253, MI 0.000) | = ffprobe | wrong | wrong | = ours |
| Truncated data | `truncated-data.wav` (0.187 = MediaInfo = ffprobe; ExifTool trusts the header, 0.250) | = ffprobe | 0.250 | 0.187 | = ours |
| No usable data chunk | `huge-chunk-size`, `no-data` (ours 0, ExifTool 0.002–0.003, others nothing) | 0 | tiny | none | none |
| Wave64 MS ADPCM: ffprobe prints N/A; our block arithmetic gives 1.723, MediaInfo 1.769 | `w64-ms-adpcm` | 1.723 | none | 1.769 | N/A |
| Wave64 files whose first outer size is too small | `w64-alaw`, `w64-gsm610`, `w64-ima-adpcm`, `w64-pcmu8`, `w64-ulaw` | = ffprobe | none | within 0.04 | = ours |
| **Ours is wrong:** an AIFF-C whose `COMM` size omits the compression type bytes (`digit0v0.aiff`): the stray bytes shift the next chunk header, so no sound data is found and the duration is 0 (ffprobe, ExifTool and MediaInfo all give 0.560) | `digit0v0.aiff` | 0 | 0.560 | 0.560 | 0.560 |
| Garbage frame count (0xFFFFFFF6 frames) | `bad_length.aifc` (ours 0; ET and MI 194,783 s; ffprobe rejects) | 0 | wrong | wrong | rejects |

`digit0v0.aiff` is a known gap, not an exception: a resynchronising chunk search for AIFF (scanning for `SSND` after a chunk whose size runs past its parent) would fix it, and is left as a follow-up.

**ATL.NET** (final build): 119 of 120 field comparisons agree or are explained as above. The `tagged-aiff.aiff` Title difference is ATL's: the file's `NAME` chunk is 10 bytes, `Tone test` plus a NUL pad inside the size, and ATL keeps the NUL (`Tone test `) where we, ExifTool and MediaInfo end the string at the NUL.

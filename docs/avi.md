# AVI (RIFF), OpenDML and DV-in-AVI

How `RecursiveAI.OneMedia.Avi` reads AVI files. Documents: the Microsoft AVI RIFF file reference (field layouts of `AVIMAINHEADER`,
`AVISTREAMHEADER`, `AVIOLDINDEX`), the OpenDML AVI File Format Extensions 1.02 (super index, standard index, `AVIX`, `dmlh`, `vprp`), the
Microsoft `BITMAPINFOHEADER` and `WAVEFORMATEX`/`WAVEFORMAT_EXTENSIBLE` documentation, and the RIFF INFO list names as listed in ExifTool's
tag documentation. The Microsoft reference is licensed CC BY 4.0 (© Microsoft; see THIRD-PARTY-NOTICES.md); no text is copied. Everything the documents leave open was settled by files written by other muxers and read by an independent tool (stated
where it matters). Raw DV and the DV frame layout are in [dv.md](dv.md).

## Layers

| Layer | Types | Notes |
|---|---|---|
| RIFF structure | `RiffChunk` (`Riff` namespace), a thin AVI view over the shared `ChunkNode` walker | Lazy tree of byte ranges: `RIFF`/`LIST` containers with children read on first use, data chunks as ranges. Odd sizes are padded to even offsets (§ RIFF File Format). Sizes past the end of the file are clamped. `JUNK` and every unknown chunk is kept as a node. |
| Format model | `AviMainHeader`, `AviStreamHeader`, `BitmapInfoHeader`, `WaveFormat`, `VideoPropertiesHeader`, `OpenDmlHeader`, `SuperIndexEntry`, `AviStreamInfo`, `AviHeaderList` | Parsed from `hdrl` by `AviHeaderParser`. The raw `strf` bytes are kept on the stream info; `strn` names and `strd` data are ranges. |
| Index | internal `Idx1Cursor`, `OdmlCursor` | Forward cursors that read a block of entries at a time; nothing is loaded whole. |
| Neutral demux | `AviDemuxer` (seekable, forward-only and ranged), `AviFormat` | Tracks, packets, seeking. |
| Metadata | `Tags.Riff.RiffTagReader` (shared with WAVE) | `LIST INFO`, `IDIT`, and for DV type 1 the DV packs of the first frame. |

## What is read

- `RIFF 'AVI '` followed by any number of `RIFF 'AVIX'` extension chunks (OpenDML §"Increased File Size Limits"). The `movi` lists of all of them
  are one packet sequence. A chunk after the data that is neither `AVIX` nor `idx1` ends reading with an `Info` defect.
- `hdrl`: `avih`, one `strl` per stream (`strh`, `strf`, optional `strn`, `strd`, `indx`, `vprp`), and `odml`/`dmlh`. Streams are numbered in
  header-list order; chunk ids `NNxx` address them by the two decimal digits.
- `movi`: stream chunks directly in the list or inside `rec ` lists (entered). Chunks `NNdc`, `NNdb`, `NNwb`, `NNtx` and any other
  two-letter code are packets of stream NN, except palette changes `NNpc` (skipped; the palette change is not a packet) and OpenDML `ixNN`
  index chunks, `JUNK` and other non-stream chunks (skipped).
- Index: OpenDML super index (`indx`) with standard indexes (`ix##`, with the 32-bit "delta frame" flag in bit 31 of the size and the base offset
  of the index) is preferred for the streams that have one; otherwise `idx1`. `idx1` offsets are taken relative to the `movi` list type, as the
  reference states; some writers store absolute offsets or offsets from the `LIST` header, so the first four entries are checked against the
  chunk ids they point at and the base that matches is used (candidates: `movi`+8, 0, `movi`). If none matches the index is ignored with
  `avi.idx1-offsets`.
- Sizes: the `movi` size is trusted only as far as the file goes; stream chunks continuing after a too-small `movi` size are read on
  (`avi.movi-size-too-small`), which is how files cut and re-headed by capture tools look.

## Tracks and time

- Codec: video from the `BITMAPINFOHEADER` compression field (fourcc), falling back to the stream header's handler; audio from the
  `wFormatTag` (`WAVE_FORMAT_EXTENSIBLE` resolves through its sub-format GUID). Names follow the common codec names (`mpeg4`, `msmpeg4v3`,
  `h264`, `mjpeg`, `dvvideo`, `rawvideo`, `pcm`, `mp3`, `ac3`, `aac`, …); an unknown code gives an unknown codec with the original code kept
  as the track's tag. The configuration is the whole `strf` (`bitmapinfoheader` or `waveformatex`, AAC extra data as `asc`, DV type 1 as `dvinfo`).
- Time base: the stream header's `dwRate/dwScale` reduced by their gcd; one tick lasts `scale/rate` seconds, a time is `ticks × scale` in a
  time scale of `rate`. Exact, no floating point.
- Packet clocks: a stream with a sample size (`dwSampleSize` ≠ 0: PCM and other constant-rate audio) counts bytes and divides, so a chunk
  starts at `bytes so far / sampleSize` samples and lasts `size / sampleSize`; any other stream counts chunks (VBR MP3 and AAC, video).
  `dwStart` is added to the first time of a stream (the stream starts that late). Pts equals Dts: the file lists chunks in decoding order
  and states no presentation order.
- A chunk of size 0 is not a packet. In a video stream it still advances the clock by one frame (a dropped frame keeps the following times
  right); in an audio stream it does not (observed with files written by other muxers: their audio times continue unbroken).
- Duration of a track: the stream header's `dwLength` in its time base; the file's: the longest track, else `avih`'s frame count.
- Key frames: from the index (`AVIIF_KEYFRAME` / the delta bit). A chunk the index does not describe, a file without an index and a forward-only
  source use the picture header of the chunk (read from the first 16 KiB; no sample is decoded): MPEG-4 Part 2 `vop_coding_type` of the first
  VOP is I; H.264 contains an IDR slice NAL unit; MPEG-1/2 `picture_coding_type` is I. All other codecs (intra-only codecs and codecs not
  inspected) report every chunk as a key frame.
- DivX packed bitstream (a P-VOP and the following B-VOP in one chunk, with an empty N-VOP chunk later) is delivered as stored; nothing is
  split, merged or dropped. Chunks of size 0 are not packets, which is where the N-VOP placeholders go.
- Seeking walks chunk headers from the start of the movie (small reads, no payloads) to find the key frame of the first video stream and set
  every clock; with an index-less file the picture header of the video chunks decides.

## Damaged and unaligned chunks

- A chunk position that holds no printable identifier (or an unknown chunk whose size runs past the end of the file) is either an odd-length chunk written without its pad byte (the header then starts one byte
  earlier) or damage. On a seekable source the reader first tries one byte back, then scans forward up to 1 MiB for a data chunk of a described
  stream that is followed by another header, and continues there with an `avi.resync` defect at the offset where the header was expected.
  Forward-only sources do not resync. Observed on the published `invalid_unaligned`, `mp_seek_failed` and `a.avi` samples.
- An `idx1` whose first entries are `rec ` groups still matches its offsets: a `rec ` entry points at the `LIST` header of the group.
- The frame rate of a DV type 1 stream is 30000/1001 or 25 when the stream header is within 0.1% of it (writers state 333666/10000000).

## Metadata

`LIST INFO` entries become native tags in system `riff-info` (four-character id, text decoded as UTF-8 when valid, otherwise Latin-1); they map
into the common view through the table rows in [tag-mapping.md](tag-mapping.md) (`INAM` title, `IART` artist, `IPRD` album, `ICMT` comment,
`ICRD` date, `IGNR` genre, `ICOP` copyright, `ISFT` encoder, …). `IDIT` (the creation date as `ctime` text such as
`Wed Jan 02 02:03:55 1990`, or an ISO date) and `ICRD` are capture-time candidates (zone unknown). The Riff tag layer proper is shared with
WAV: one reader (`Tags.Riff.RiffTagReader`) serves both containers, and `IDIT` is read through it as a loose chunk.

## Defects

All carry a byte offset; reading continues where it safely can.

| Code | Kind | Meaning | What happens |
|---|---|---|---|
| `riff.truncated-header` | `Truncated` | The file ends inside a chunk or list header | The list ends. |
| `riff.truncated-data` | `Truncated` | A chunk's data ends early | The present bytes are used. |
| `riff.size-exceeds-file` | `InvalidSize` | A chunk or list size runs past the end of the file | Clamped to the file. |
| `riff.size-exceeds-parent` | `InvalidSize` | A chunk or list ends after the list that holds it does | Read in full when the file has the bytes, else clamped. |
| `riff.unbounded-size` | `InvalidSize` | A size of 0xFFFFFFFF, or an outer chunk with no usable size | Taken to run to the end of the parent (or the file). |
| `riff.trailing` | `UnaccountedBytes` | Fewer bytes at the end of a list than a chunk header needs | Reported, not read. |
| `avi.missing-avih`, `avi.short-avih`, `avi.stream-count` | `MissingRequired`/`InvalidSize`/`Inconsistent` | Main header missing, short or disagreeing with the stream lists | Stream lists decide. |
| `avi.missing-strh`, `avi.short-strh`, `avi.missing-strf`, `avi.strf-too-large` | `MissingRequired`/`InvalidSize`/`LimitReached` | Stream header problems | A stream without `strh` is ignored; an oversized `strf` is cut at the limit. |
| `avi.short-indx`, `avi.indx-not-super`, `avi.indx-entry-count`, `avi.indx-offset` | various | Super index problems | The entries that fit are used. |
| `avi.ix-missing`, `avi.ix-type`, `avi.ix-entry-count` | `Inconsistent`/`Unsupported`/`InvalidValue` | A standard index the super index names is absent or malformed | That index is skipped. |
| `avi.idx1-offsets` | `Inconsistent` | `idx1` entries do not point at the chunks they name | The index is not used. |
| `avi.missing-movi` | `MissingRequired` (Error) | No `movi` list | No packets. |
| `avi.movi-size-too-small`, `avi.avix-truncated`, `avi.trailing-riff` | `SizeExceedsParent`/`SizeExceedsFile`/`UnaccountedBytes` | List sizes disagree with the data | Reading continues as described above. |
| `avi.unknown-stream` | `Inconsistent` | A chunk names a stream the header list lacks | Such chunks are skipped (reported once). |
| `avi.truncated-header`, `avi.truncated-chunk` | `Truncated` | The file ends inside a chunk header or a chunk | The partial chunk is delivered with the `Corrupt` flag. |

A file that is not RIFF/AVI, or has no `hdrl` list, raises `MediaFormatException` (`TryOpen` returns the defect).

## Reading cost

`Open` reads the RIFF header, `hdrl` (parsed from one block of up to 1 MiB, so a few kilobytes in practice) and the `movi` list header. It does
not read `idx1` or any `ix##`; index entries are read in blocks of 1,024 entries (16 KiB) only when packets are delivered, so `idx1` at the
end of the file costs a seek and one block read for the first chunks, not a scan. The probe and metadata read touch `hdrl`, the
`LIST INFO` and `IDIT` chunks (which writers put right after `hdrl` or after `movi`), and for DV type 1 the first video frame's header
blocks.

Measured over HTTP range requests on 70 files (probe plus metadata, see [io-results.txt](io-results.txt)): a median of 114,509 B in 9 requests and at most 273,716 B in 13 for every file with a `movi` list, whatever its size; the OpenDML files of 1.29 GB and 4.65 GB take about 172 KB in 9 to 10 requests (ffprobe 1.67 MB / 11 and 3.35 MB / 23, MediaInfo 11 MB / 173 and 11.4 MB / 176 through `strace`, ExifTool 47 KB / 8 and 71 KB / 11). Opening reads the first 32 KiB (more when the header list is longer) into a front cache, then serves the many small reads of the header list from it and later reads through a 32 KiB read-ahead window; a probe is 10 to 50 reads of the file instead of about 100 one-chunk reads. Before that change the same probe fetched 4,559 B (median) in 32 requests; fewer round trips cost more bytes, and a smaller window (8 KiB gave 45 KB in 10 requests) is a follow-up. Opening also no longer walks the first 64 video chunks of a file whose video track is not H.264 or HEVC (DV-in-AVI is the common case): that walk made one 4 KiB read per chunk for nothing. The one file that needs a scan is a sample with no `movi` list: the bounded search for stream chunks (4 MiB at most) read 75 KB in 451 requests. Raw DV reads 64 KiB (the first frames' header blocks) in 3 requests.

## Deviations from ffprobe 7.0.2

`tools/OneMedia.Oracles/exceptions.tsv` scopes each of these; every other packet field (size, payload hash, pts, dts, duration, key flag,
position) and every track field agrees on the corpus: 70 files (published samples plus files written by ffmpeg from generated input, including
OpenDML files of 1.29 GB and 4.65 GB), 164,648 ffprobe packets, no unexplained difference.

| Class | ffprobe | OneMedia | Why |
|---|---|---|---|
| `no-presentation-order` | prints no `pts` for streams whose codec reorders (MPEG-4 B-frames, H.264) | `pts` = decode-order position = `dts` | An AVI index lists chunks in decoding order only; nothing in the container states a presentation time. |
| `variable-frame-audio` | no duration for FLAC chunks | stream rate/scale per chunk | FLAC frames vary in length and the container states no per-chunk duration. |
| `dv-embedded-audio` | synthesizes audio streams from DV (type 1 AVI and raw DV) | the video track only; audio parameters are in metadata | DV audio is shuffled across the frame's DIF blocks and needs a de-shuffling decoder step; see [dv.md](dv.md). |
| `subtitle-events` | for text streams (`ass`) emits the script header and each event as its own packet, timed from the script | the chunk bytes at the chunk's clock | A chunk of a text stream is opaque to the container; splitting scripts into events is the subtitle layer's job. |
| `damaged-resync` | skips stretches of garbage in `movi` by its own rules | skips them by the rule in *Damaged and unaligned chunks* | After a damaged stretch the two readers can continue at different chunk headers: `mp_seek_failed.avi` lists 4 video chunks ffprobe does not, `a.avi` (a sample with a deliberately damaged video stream) lists 364 video packets to ffprobe's 367 with a different packet near each stretch. Scoped by file. |
| `truncated-last-chunk` | an empty packet for the cut chunk | the present bytes with `Corrupt` | The reader is lossless: bytes that exist are returned and flagged. |

## Header comparison (`infocheck`) against ffprobe 7.0.2

70 files, 0 unexplained. Classes printed as `explained`: `truncated-estimate` (a file cut short: ffprobe estimates from the data present, ours is the length the headers state; recognised by `riff.size-exceeds-file` or `riff.truncated-data`), `audio-stream-duration` (ffprobe gives MP3/MP2/AC-3 in AVI no duration and takes the video's), `dv-embedded-audio` and `mjpeg-frame-size`. One file is scoped in `exceptions.tsv`: `verona60avi56k.avi`, whose audio stream header states 30,864 s of samples for a 1.9 MB file; ours is the longest stream header, ffprobe's the video's.

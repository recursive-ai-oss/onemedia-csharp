# Audio elementary streams and their tags

MP3 (and MP2/MP1), ADTS AAC, AC-3 and E-AC-3, and AMR-NB/AMR-WB files, plus the tag systems that travel with them: ID3v1/1.1, ID3v2.2–2.4,
APEv2 and Lyrics3. `RecursiveAI.OneMedia.AudioStreams` holds the readers, `RecursiveAI.OneMedia.Tags.Id3`, `.Ape` and `.Lyrics3` the tag layers.
Reading only; writing tags is a later feature, and the models keep byte ranges so it can round-trip them.

## What a reader gives

One track and one packet per frame, with the frame's bytes exactly as stored (an ADTS packet keeps its header). Packets are keyframes. Times are
exact in the stream's sample rate (`MediaTime` ticks of 1/sample-rate): packet *n* of an MPEG-1 Layer III stream starts at *n* × 1152 ticks.

| Format | Packet | Samples per packet | Track description |
|---|---|---|---|
| MPEG audio | one frame (Layer I 384, Layer II 1152, Layer III 1152 or 576 samples) | by version and layer | codec `mp3` (Layer III), `mp2`, `mp1`; rate, channels, bit rate from the header |
| ADTS | one `adts_frame` including its 7- or 9-byte header | 1024 | codec `aac`; `Configuration` = AudioSpecificConfig (format `asc`) built from the first header |
| AC-3 | one syncframe | 1536 | codec `ac3` |
| E-AC-3 | one access unit: an independent frame with the dependent frames and further-substream frames that follow it | 256 × blocks | codec `eac3` |
| AMR | one frame: table-of-contents byte plus speech bits | 160 (NB, 8 kHz) or 320 (WB, 16 kHz) | codec `amr-nb` / `amr-wb`; `Configuration` is a synthesised `damr` body |

All readers work over seekable, forward-only and ranged sources (`Open`, `Open(IForwardMediaSource)`, `OpenAsync`). A leading ID3v2 tag is skipped by
every one of them. A forward-only source cannot look behind a tag of more than about 200 KiB and cannot see the tail.

## Duration and timing

- **MPEG audio with a Xing, Info or VBRI frame** (the first frame, no audio): the frame is not a packet. Its frame count gives the duration exactly
  and costs no scan. The LAME extension carries the encoder delay *d* and padding *p* (12 bits each). The track states
  `Edit.Priming = d + 529` and `Edit.EndPadding = max(0, p − 529)` samples, and `Duration` is the presented length: frames × samples per frame − priming − padding.
  529 is the reference decoder's own delay (528 + 1 samples), which LAME documents as part of the gapless arithmetic. The first frame is kept as
  `Configuration` (format `mpeg-audio-info-frame`) so the audio adapter can hand it to OneAudio's MP3 reader.
- **Without a count** (a plain CBR file, an ADTS or AC-3 file, AMR): `Duration` is an estimate, `IsDurationEstimated` is true. It is the audio's
  byte length times the samples-per-byte of the first (up to 128) units found in the first 16 KiB. Exact for constant bit rate, approximate for VBR.
  No scan, no extra reads. The audio's length excludes leading and trailing tags.
- **AC-3/E-AC-3** times are exact in the stream's rate. ffprobe reports a 1/90000 base, which cannot hold 1536/44100 s, so its times round; the
  differential allows one tick there.

## Resynchronisation and defects

A frame is accepted when its header is valid and either another frame of the same stream (the same stream signature: version, layer and rate for MPEG audio;
the fixed header fields for ADTS; sample rate and bitstream class for AC-3/E-AC-3) starts right after it, or the data ends there. Bytes between accepted frames are **skipped and reported** (`audio.junk`, a warning at the offset of the first skipped byte,
one per run of junk). A change of stream parameters in the middle is treated as junk, not as a new stream. A last frame that is cut off is delivered
with `PacketAttributes.Corrupt` and reported once (`audio.truncated-frame`).

**Deviation from ffprobe (junk).** ffprobe folds the skipped bytes into the next packet, so its packet is longer than the frame and starts
earlier; ours is the frame and the junk is reported. Packet counts agree; sizes and positions differ for the packets after junk. Damaged files
live in the corpus' `_defects` folder, which sweeps skip.

**Free format** (MPEG audio with bit-rate index 0, ISO/IEC 11172-3 §2.4.2.3): the frame length is the distance to the next header with the same
stable fields, confirmed by the header after that; the length found is tried first for the following frames, so each costs one comparison.
ffprobe 7 and 8 cannot open such files ("Failed to find two consecutive MPEG audio frames"), so they have no differential; the reader is checked
against the constant-bit-rate file the free-format file was made from (same frames, headers rewritten), see the results.

**AMR** is strict: a table-of-contents byte must have its padding bits (`0x83`) clear and an `FT` that is a speech mode or `NO_DATA`; anything
else is not a frame. The `#!AMR\n`, `#!AMR-WB\n` magic and the multichannel forms (`#!AMR_MC1.0\n`, `#!AMR-WB_MC1.0\n` with a channel-description word) are read; a
multichannel file's frames hold one TOC per channel and are delivered whole.

**AC-3 / E-AC-3** syncframes are found by `0x0B77` plus a valid header (`fscod`, `frmsizecod` or `frmsiz`, `bsid` ≤ 10 for AC-3, 11–16 for E-AC-3).
A dependent frame (`strmtyp` 1) or a frame of a further substream belongs to the access unit that precedes it (ETSI TS 102 366, Annex E).

## Tags at the ends

At open, a seekable source is asked for the last 128 bytes (ID3v1), the 32 bytes before that (an APEv2 footer), the 15 before that (Lyrics3), and a
`3DI` footer, up to six rounds, so the usual orders (`audio, Lyrics3, APE, ID3v1` and `audio, APE, ID3v1`) are found with a handful of small reads.
Those blocks are removed from the audio (`AudioEnd` is where they start). An ID3v2 tag in the middle of a stream (a spliced file) is skipped and
reported as `audio.inner-id3` (info). An ID3v2 tag appended after the audio is only found when it has a footer.

## Seeking

There is no index in these formats. Reading remembers the offset and time of every 256th unit; `Seek` jumps to the last remembered unit at or before
the target, then walks headers (no payload decoding, only header parses over the buffered bytes) to the target. The cost is at most 256 units from
a place already read, or the whole distance the first time a position beyond what was read is asked for. `Seek` lands on a frame boundary:
`PreviousKeyframe` on the frame containing the target, `NextKeyframe` on the first frame starting at or after it. Forward-only sources cannot seek.

## ID3v2

- **Header** (ID3v2.4 §3.1): `ID3`, version and revision (never 0xFF), flags, 28-bit synchsafe size. A footer (`3DI`) is accounted for. The tag
  extends over header + body (+ footer); `Padding` is the run of zero bytes after the last frame.
- **Versions.** 2.2 has 3-character frame ids and 24-bit sizes; 2.3 has 4-character ids and plain big-endian sizes; 2.4 has synchsafe sizes. Frame ids
  are kept as stored (a 2.2 tag keeps `TT2`); the common-view mapping knows all three spellings.
- **Unsynchronisation.** A tag-wide flag (2.2/2.3: the whole body; 2.4: set only when every frame is) is undone by reading the tag whole (bounded by
  `MaxTagBytes`) and serving values from memory. A 2.4 frame-level flag is undone for that frame. Byte ranges still point into the file.
- **Extended header** (2.3: size, flags, padding size, optional CRC; 2.4: synchsafe size, flag bytes, optional CRC/restrictions) is skipped and its range kept.
- **Frame flags.** Compression (2.3: zlib with a 4-byte decompressed size; 2.4: via the data length indicator) is inflated within `MaxTagBytes`; a zlib
  failure is `id3.compressed-data` and the frame stays opaque. Encrypted frames and frames with a group id are **exposed, never decrypted**: the frame
  has `Flags`, `EncryptionMethod`, `GroupId` and its `DataRange`, and a binary `NativeTag`.
- **Text frames** decode all four encodings (ISO-8859-1, UTF-16 with BOM, UTF-16BE, UTF-8). 2.2/2.3 tags that name encoding 3 are read as UTF-8 and reported
  (`id3.text-encoding`). Multiple values (2.4 nul-separated) are kept in `Id3Frame.Values`; `NativeTag.Text` joins them with `"; "`.
- **Frames understood** beyond text: `TXXX`, `WXXX`, `COMM`, `USLT`, `SYLT`, `APIC`/`PIC` (cover art, MIME, picture type, lazy value), `POPM`, `UFID`,
  `PRIV`, `GEOB`, `CHAP`/`CTOC` (chapters with sub-frames, `Id3Tag.Chapters()`). ReplayGain is read from `TXXX` frames.
  Any other frame is a binary `NativeTag` with its range, so nothing is lost. Native keys are the stored frame id, plus `:description` for the frames that have one
  (`TXXX:REPLAYGAIN_TRACK_GAIN`, `COMM:desc` when the description is non-empty).
- **Dates.** `TDRC`/`TYER`+`TDAT`+`TIME` give the date string; `TDRC` is also a capture-time candidate (timezone `Unknown`).
- **Entry points for other containers.** `Id3Tag.Read(IMediaSource, offset, …)` reads a tag that starts at `ID3` anywhere in a source (a WAV `id3 ` chunk, an AIFF `ID3 ` chunk, a FLAC prefix);
  `Id3Tag.Read(ReadOnlyMemory<byte>, baseOffset, …)` reads one from memory (an MP4 `ID32` payload after its 6-byte header). `Id3Tag.TryRead` returns false instead of throwing for a non-tag.

## ID3v1, APEv2, Lyrics3

- **ID3v1 / 1.1**: `TAG` + 30/30/30/4/30 bytes + genre; a zero byte at offset 125 with a non-zero track byte makes it 1.1. The enhanced `TAG+` block before it is kept as `Extended`.
  Genre numbers 0–191 (ID3v1 and Winamp extensions) resolve through `Id3Genre`. Text is ISO-8859-1.
- **APEv2.** 32-byte header and footer (`APETAGEX`, version 1000/2000, tag size including the footer and items but not the header, item count, flags). Item: value size, flags,
  key (2–255 characters of 0x20–0x7E, zero-terminated), value. Item flag bits 1–2: 0 UTF-8 text, 1 binary, 2 locator, 3 reserved; bit 0 read-only.
  Global flags: bit 31 header present, bit 30 the footer flag, bit 29 "this is the header". A tag is found from its footer at the end (header optional) or from a header at the start.
  Text values with several entries are zero-separated and are joined with `"; "`. Cover art (`Cover Art (Front)`, …) is a binary item: file name, zero byte, image; it becomes an image tag with MIME sniffed from the image.
  Keys are compared case-insensitively by the common view; the file keeps their case.
  *Sources and the one conflict.* Two public descriptions of the APEv2 layout were compared item by item (the mutagen-specs page and the Hydrogenaudio wiki pages, archived copies): layout, sizes, item
  format, key alphabet and item-flag bits agree. They disagree on bit 30 (mutagen-specs: 1 = *no* footer; Hydrogenaudio: 1 = footer). Real files settle it. ExifTool 13.60 cannot write APE tags
  ("isn't writable"), so the writers observed are mp3gain 1.6.2 (MP3, `mp3gain -s a`) and WavPack 5.9.0 (`wvtag`, a `.wv` file): both write a header with flags `0xA0000000` and a footer with
  `0x80000000`. With a footer present, bit 30 is clear, which is the mutagen-specs reading ("1 = no footer"); the Hydrogenaudio reading would have set it. ExifTool reads the mp3gain file
  (`Replaygain Track Gain`, `Mp 3gain Minmax`, …) and so do we (fixture `testdata/audio-es/ape-mp3gain.mp3`, a test). We follow mutagen-specs; the bit is used only to decide whether a tag read from its header has a footer.
  A key with characters outside 0x20–0x7E is kept and reported (`ape.key-chars`); a key shorter than two characters or unterminated stops the tag (`ape.key`).
- **Lyrics3.** v2.00 (`LYRICSBEGIN` … 6-digit size … `LYRICS200`; the size counts `LYRICSBEGIN` and the fields) is split into its three-letter fields with five-digit sizes; v1 (`LYRICSBEGIN` … `LYRICSEND`, ≤ 5100 bytes) is one Latin-1 text item `LYRICS`.
  Both are native tags in system `lyrics3` and are kept out of the audio. The common view takes `ETT`/`EAR`/`EAL` as title/artist/album and `LYR` (or the v1 block) as lyrics, after ID3v2 and APE.

## Defect catalogue

| Code | Severity | Meaning |
|---|---|---|
| `audio.junk` | warning | bytes between frames, before the first frame or at the end that are not a frame were skipped |
| `audio.truncated-frame` | error | the data ends inside the last frame; the part present is delivered, marked corrupt |
| `audio.inner-id3` | info | an ID3v2 tag sits between frames and was skipped |
| `id3.header`, `id3.header-flags` | warning | unusable or reserved bits in the tag header |
| `id3.size-exceeds-file` | error | the declared tag size runs past the end of the data |
| `id3.extended-header`, `id3.footer` | warning | malformed extended header or footer |
| `id3.frame-id` | warning | a frame id with characters other than A–Z, 0–9 — the rest of the tag is not read |
| `id3.frame-size`, `id3.frame-size-not-synchsafe` | warning | a frame size reaching past the tag; a 2.4 size that is not synchsafe and was read as plain big-endian because that fits |
| `id3.truncated`, `id3.empty-frame` | warning | the tag ends inside a frame; a frame with no data |
| `id3.padding` | warning | non-zero bytes inside what looked like padding |
| `id3.text-encoding`, `id3.utf16-bom`, `id3.description` | warning | encoding byte not defined for this version; UTF-16 without usable BOM; a description that is not terminated |
| `id3.apic`, `id3.picture-type`, `id3.chap`, `id3.ctoc` | warning | malformed picture, chapter or table-of-contents frame |
| `id3.compressed-data`, `id3.compressed-length`, `id3.encrypted` | warning | a compressed frame that does not inflate or has an unusable length; an encrypted frame kept opaque |
| `id3.v22-compression` | warning | the 2.2 compression bit (no scheme exists): frames are not read |
| `id3.value-too-large`, `id3.limit` | warning | a value over `MaxTagBytes` is kept undecoded; a count limit was reached |
| `ape.header`, `ape.size`, `ape.item-count`, `ape.item-size`, `ape.trailing` | warning/error | footer announces a missing header; tag larger than the file; fewer items than declared; an item reaching past the tag; bytes after the last item |
| `ape.key`, `ape.key-chars`, `ape.utf8` | error/warning | unusable key (stops the tag); non-printable key (kept); a text value that is not UTF-8 |
| `lyrics3.field` | warning | a Lyrics3 field with an unusable size: the rest of the block is not split |

A typed exception (`MediaFormatException`) is raised only when no frame can be found at all (`MissingRequired`) or the AMR magic is wrong. Limits that apply: `MaxTagBytes`
(tag size, decoded and inflated frames), `MaxElementCount` (frames and items), `MaxAttachmentBytes` (pictures when read), `MaxPacketBytes`.

## Common view

`TagMappingTable` has rows for `id3v2` (all three spellings), `id3v1`, `ape` and `lyrics3`; the generated table is in [tag-mapping.md](tag-mapping.md). Title, artist, album, album artist,
composer, genre (ID3 `TCON` references and ID3v1 numbers resolve to names), date, comment, lyrics, track and disc number/total (`3/12` splits), ReplayGain track/album gain and peak, encoder, and cover art.
The first system with a value wins in the order ID3v2, APE, ID3v1.

## Ranged sources and I/O

`OpenAsync` fetches the first 256 KiB and the last 16 KiB of the source, opens the same synchronous reader over those bytes and, when a read falls outside what was fetched, fetches the missing window
(256 KiB at least) and retries. Memory stays at five windows however large the source is. A normal file opens in three requests (the format sniff, the head, the tail); packets are pulled in 256 KiB steps.
Large tags (cover art) are fetched when a value is read. The measured bytes and range counts, next to ffprobe, MediaInfo and ExifTool, are in the results below.

## The audio adapter (`RecursiveAI.OneMedia.Audio`)

`media.Decode()` works for all of them: AAC from ADTS gets the AudioSpecificConfig and the packets without their ADTS headers; AMR gets a `damr` configuration; MP3 goes to OneAudio's MP3 reader as a byte
stream that starts with the Xing/Info frame, so OneAudio's own gapless handling applies and the decoded length of a LAME file equals ffmpeg's. The adapter does not rebuild gapless data.

## Differential method

`tools/audio-es/run-all.sh` runs on the worker: `generate.sh` writes the corpus (ffmpeg 7.0.2 static build for the MP3/MP2/AAC/AC-3/E-AC-3/AMR files; `make_tags.py` for ID3v2.2/2.3/2.4/APE/Lyrics3 layouts that no
encoder writes; mp3gain and id3lib's id3v2 as independent writers of APEv2 and ID3v1/ID3v2 (ExifTool cannot write MP3 files)), then compares packets (`diff`), stream facts (`infocheck`), decoded length (`audiodecode`), tags (`tagcompare` in the bench project) and I/O (`io`).
Documented differences from ffprobe, each with its rule in `tools/OneMedia.Oracles/exceptions.tsv`:

| Difference | Reason |
|---|---|
| ID3/APE pictures are a video stream in ffprobe | pictures are tags here |
| ffprobe's duration for a file without a frame count | both sides estimate; they must agree within 2% (bytes over bit rate, with the tags weighed differently) |
| AC-3/E-AC-3 times differ by up to one tick per packet | ffprobe truncates every packet duration to its 1/90000 base and adds them up, so its times drift from the exact n × 1536 / rate (rule `drift=1`) |
| duration of a file with a LAME/Info tag | ffprobe counts every frame; ours is the gapless length (frames minus the stated encoder delay and padding); the check allows exactly that difference |
| duration of an AMR file | ffprobe divides the file size by the bit rate of the first frame header and counts the magic bytes; ours counts frames (6% allowed) |
| an APEv1, footer-only APEv2 or Lyrics3 block at the end with no ID3v1 behind it | ffprobe lists it as one more packet; ours ends at the last frame and reports the tag |
| packets after junk | ffprobe folds junk into the next packet |
| free-format MP3 | ffprobe cannot open it |

## Results (worker, Linux x64; oracles ffprobe/ffmpeg 7.0.2, ExifTool 13.60, MediaInfo 26.10)

- **Packets vs ffprobe 7.0.2** (`diff`): 132 generated files (MP3/MP2 including MPEG-2.5, CBR/ABR/VBR, with and without Xing/Info/LAME and with ID3v1/v2.2/2.3/2.4, APEv1/v2 and Lyrics3 tags, 2 free-format;
  ADTS LC and Main in 8–96 kHz and up to 6 channels; AC-3 and E-AC-3; AMR-NB and -WB at every mode). 130 equal (size, hash, pts, dts, duration, key flag); the 2 others are the free-format files, which ffprobe cannot open.
  The free-format files give the same packets as their constant-bit-rate twins (116 packets / 48,483 bytes and 117 / 24,450). Every difference seen on the way is a rule in `exceptions.tsv` (table above).
- **Stream facts vs ffprobe** (`infocheck`): 129 files, 0 differences (free-format files skipped).
- **Decoded length** (`media.Decode()` through the adapter, against `ffmpeg -f s16le`): 107 files with a decoder in OneAudio (MP3 with and without LAME gapless data, ADTS LC, AMR-NB/-WB), all equal to the frame.
  OneAudio's own reader on the whole file differs where the file has no Info frame (it drops 529 frames of decoder delay that ffmpeg keeps) and where a block that is not ID3v1 trails the audio (APEv1, footer-only APEv2,
  Lyrics3: it decodes the tag bytes); through the adapter neither happens. AAC with more than two channels and AC-3/E-AC-3 are not decoded by OneAudio.
- **Tags** (`tagcompare`, 69 files, 497 field readings, ExifTool 13.60, ATL.NET 7.19.0, TagLib# 2.3.0): ours agree with all three on 404 readings. The rest, each explained: ATL.NET puts the file name in Title when there is no tag
  (41); TagLib# returns nothing for frames with a data-length indicator (ExifTool and ours read them); ATL.NET shows compressed frame bytes
  and does not undo v2.3 unsynchronisation inside frames; ID3v2.4 `TDRC` is kept as the ISO 8601 string where ExifTool rewrites it as `2019:05:17 10:30:00` and ATL.NET/TagLib# keep the year; with an ID3v1 and an APE tag present
  the APE value wins (TagLib#'s order; ID3v1 truncates at 30 characters).
  Writers behind the fixtures: ffmpeg 7.0.2 (ID3v2.3/2.4, Info), our generator for layouts no encoder writes, mp3gain 1.6.2 (APEv2), id3lib's `id3v2` 0.1.12 (ID3v2.3, ID3v1). ExifTool cannot write MP3 files.
- **I/O, probe plus metadata** (`io`; bytes / read calls; HTTP = ranged requests to the local range server, strace = `read`/`pread64`): the 2.2 GB MP3 with and without Xing frame: ours 282,624 B / 4 requests over HTTP and
  151,783 B / 12 reads locally; ffprobe 360,576 B / 3 and 163,968 B / 5; MediaInfo 180,384–196,768 B / 3 and 147,616 B / 4; ExifTool 96,159 B / 16. Without a Xing frame the duration is the documented estimate and nothing is scanned.
  Short files are read whole by every tool (the clips are 3–60 s); the tables are in `docs/io-results.txt` style in the worker's results directory.
- **Allocation, pull path** (Release, a second full pass after `Seek` to the start, the 2.2 GB file included): 0 bytes over 5,263,848 packets; MP3, AAC, AC-3 and AMR 0 B per packet. The first pass allocates the seek index
  (two lists, one entry per 256 packets, 512 KiB at 2.2 GB). Peak resident memory of the whole 2.2 GB pull: 35 MB.
- **Fuzz** (SharpFuzz under libFuzzer, 601 s each, one process, 0 crashes): `id3` 4,013,702 runs (one crash found earlier, a zlib failure inside a compressed frame, fixed and kept as `testdata/fuzz/id3/`), `ape` 12,973,686,
  `mpa-es` 4,049,038, `adts-ac3-amr-es` 5,742,026.
- **Release suite** (worker, `dotnet test -c Release`): 403 tests pass, none skipped. **NativeAOT** publish of the smoke app (`linux-x64`, warnings as errors): 0 trim or AOT warnings, the app runs and its audio-stream checks
  pass. **WebAssembly** smoke app under node: 0 failures.

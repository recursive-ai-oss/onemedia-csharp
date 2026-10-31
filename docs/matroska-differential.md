# Matroska and WebM: reading, and how it is checked

How `RecursiveAI.OneMedia.Matroska` reads these files and how the result is compared with other implementations. Specifications: RFC 8794
(EBML), RFC 9559 (Matroska); section numbers are in the code comments. WebM is Matroska with `DocType` `webm`; both go through the same reader.

## Layers

| Layer | Types | Notes |
|---|---|---|
| EBML primitives | `Ebml`, `EbmlCrc32` | Variable-length IDs and sizes (RFC 8794 §4), integers, floats (decoded with integer arithmetic), CRC-32. |
| Structure | `EbmlDocument`, `EbmlElement` (an `IStructureNode`) | Lazy tree of byte ranges. Children are read on first use; payloads are read on request and never kept. |
| Format model | `MatroskaHeader`, `MatroskaInfo`, `MatroskaTrackEntry` and friends, `MatroskaSeekEntry`, `MatroskaCuePoint` | Decoded from the element payloads by tolerant in-memory parsers. Chapters, Tags and Attachments are kept as ranges. |
| Neutral demux | `MatroskaDemuxer` (seekable and forward-only), `MatroskaAsyncDemuxer` (ranged), `MatroskaFormat` | One async-first reader drives both; the synchronous demuxer completes its `ValueTask`s synchronously. |

## Reading rules

- **Open** reads the EBML header, the Segment header, then the SeekHead (and chained SeekHeads) and follows it to Info, Tracks and Cues. With no SeekHead the
  reader walks the Segment's children; it stops at the first Cluster once Tracks has been seen. When Tracks only appears after the Clusters and there
  is no SeekHead, it walks past every Cluster with one 512-byte read of each Cluster header: this is the documented cost of that layout (see
  "Open cost" below). A forward-only source needs Tracks before the first Cluster (`MediaFormatException`, kind `InvalidOrder`).
- **Unknown sizes** are allowed for Segment and Cluster only (RFC 8794 §6.2). An unknown-size Cluster ends at the next element that belongs to the
  Segment level; an unknown-size element anywhere else is a defect and is cut at its parent's end.
- **Time.** `TimestampScale` that divides 10⁹ gives a track time base of `1/(10⁹ / scale)`; otherwise ticks are nanoseconds (`units × scale`, exact).
  Float `Duration` and `SamplingFrequency` are converted with integer arithmetic. No floating point reaches a packet time.
- **CodecDelay** is subtracted from block timestamps (RFC 9559 §11.2 Opus mapping) and also exposed exactly as the track's priming. A block's
  `DiscardPadding` is attached to the packet (positive: the end of the last frame of the block; negative: the start of the first frame).
- **Duration of a packet:** `BlockDuration`, else `DefaultDuration` (× frames in the lace), else unknown. A lace's total is `BlockDuration` or
  `n × DefaultDuration`; frames are split by cumulative floor so the parts add up to the total.
- **DTS** is unknown for video (Matroska stores presentation times only) and equals the PTS for audio, subtitles and data.
- **Keyframe** is the SimpleBlock flag, or a BlockGroup without `ReferenceBlock`. The `invisible` flag becomes `Discard`, `discardable` becomes `Disposable`.
- **Source range** is the frame's bytes in the file; for laced blocks, the frame's own bytes. When header stripping
  (`ContentCompAlgo` 3) or zlib (`ContentCompAlgo` 0) is in use, the packet is delivered from a buffer (the stripped prefix put back, or the frame
  inflated with the BCL's `ZLibStream`, never past `MaxPacketBytes`) and the source range still names the stored bytes. A frame that is not valid
  zlib is delivered as stored with an `InvalidValue` warning. Other compression (bzlib, lzo) and several stacked encodings are reported as an
  `Unsupported` defect and the frames are delivered as stored. Undoing content compression is container framing, not sample decoding.
- **Encryption** (`ContentEncryption`) marks the track `IsProtected`; no key handling.
- **Seeking** by time goes to the Cues entry at or before (or after, for `NextKeyframe`) the target, using the first enabled video track that has cues, otherwise the
  first track with cues. Without Cues the reader walks Cluster headers (one small read per Cluster) from the start; it lands on a Cluster start.
- **Resync after damage.** When a header under the Segment or inside a Cluster is not valid EBML, or a size runs past its parent, the reader scans
  forward for the next Segment-level element whose header and size are sound, records a defect with the offset, and continues. The lazy tree does the
  same for junk between the Segment's children.

## Defects

Every defect carries the byte offset of the element concerned. Kinds used (the message names the element):

| Kind | Severity | When |
|---|---|---|
| `InvalidSize` | Error | A header is not valid EBML; an unknown size where not allowed. |
| `SizeExceedsParent` / `SizeExceedsFile` | Error / Warning | An element is longer than what contains it, or than the file; it is cut there. |
| `Truncated` | Error / Warning | The data ends inside an element or a block; a partial block is dropped. |
| `UnaccountedBytes` | Error | Bytes that are not an element, skipped to the next sound element; or nothing sound follows. |
| `ChecksumMismatch` | Warning | A `CRC-32` element does not match its siblings' bytes. |
| `InvalidValue` | Warning | An integer or float element that cannot be read; TimestampScale 0; a negative lace size. The default is used. |
| `MissingRequired` | Error / Warning | A TrackEntry without TrackNumber, a CuePoint without CueTime, a BlockGroup without Block, a Segment without Tracks (throws). |
| `Duplicate` | Warning | Two tracks with one TrackNumber; blocks go to the first. |
| `Inconsistent` | Warning | A block for an undeclared track (skipped); a SeekHead entry that points at another element; a lace whose sizes do not add up. |
| `InvalidOrder` | Error | No Segment after the header (scanned for); first Cluster before Tracks on a forward-only source (throws). |
| `Unsupported` | Warning | Unknown DocType, a higher `DocTypeReadVersion`, frame compression this reader does not undo. |

Damaged input never raises anything but `MediaException` types. Truncation at every byte offset and four byte-value corruptions at every offset of a
crafted file are unit tests; a truncation sweep over a 13 312-byte WebM (every cut point) gave 9 586 clean opens and 3 727 typed exceptions, no others.

## Differential method

`tools/OneMedia.MatroskaDiff` (a test tool; not shipped) compares our packets with `ffprobe -show_packets -show_entries
packet=stream_index,pts,dts,duration,size,pos,flags` and the element structure and track headers with `mkvinfo`. The oracles run as released
binaries only (versions in the results table below). `tools/matroska/run-matroska-diff.sh` runs the whole set;
`tools/matroska/make-fixtures.sh` writes the synthetic fixtures (ffmpeg- and mkvmerge-written files from generated input) and
`tools/matroska/run-matroska-big.sh` the large-file checks.

For every stream the packets are compared in order: size, payload checksum (ffprobe's `-show_data_hash adler32` against the Adler-32 of the bytes we deliver), PTS, duration, key flag, and position. Each difference is in an explained class or it is
unexplained; the tool exits non-zero on any unexplained difference.

### Explained classes

"Follows the spec" says which side matches RFC 9559.

| Class | What differs | Follows the spec |
|---|---|---|
| `duration-from-codec` | ffprobe fills in a duration the file does not store (an audio packet without `BlockDuration` or `DefaultDuration`) by looking at the codec's frame length. Ours stays unknown. | Both: the file stores none; ffprobe derives one. Deriving it needs the codec layer. |
| `lace-shared-pts` | Frames of a lace with neither `BlockDuration` nor `DefaultDuration`: ours all carry the block's timestamp (unknown duration); ffprobe spaces them by codec frame length. | Ours (§10.2: a lace has one timestamp). |
| `inexact-timebase` | `TimestampScale` does not divide 10⁹: ours are exact nanosecond ticks, ffprobe rounds to an integer time base, so values are within one tick. | Ours (exact). |
| `last-block-duration-differs` | The last block's `BlockDuration` is shorter than the codec's frame; ffprobe reports the frame length, ours the stored value. | Ours (the file's value). |
| `realvideo-timestamps` | RealVideo (`V_REAL/RV40`): ffprobe takes frame times from the RealVideo stream (blocks at 80 and 81 ms are reported at 40 and 80); ours is the Block time. | Ours (the container states it; the stream's own times are codec work). |
| `realaudio-deinterleave` | RealAudio (`cook`, `ra`) blocks hold interleaved sub-packets; ffprobe de-interleaves them into the codec frames (280 bytes where the block is 1400). Ours delivers the stored block. | Ours (the bytes in the file); de-interleaving is codec work. |
| `webvtt-block-repacked` | The WebVTT-in-Matroska block (`BlockAdditions` with settings and the cue text): ffprobe repacks it as a `webvtt` packet. Ours is the block's bytes. | Ours (the bytes in the file). |
| `pos-queue` | `pos` for packets ffprobe holds back when it interleaves audio with video: it reports an earlier position than the block's own. For video, `pos` equals the Block's start, which is the frame's `SourceRange.Offset` minus the 4-byte block header (one-byte track number). | The position of the block in the file is ours; ffprobe's value is a queue artefact. |

### mkvinfo

mkvinfo (`-a -P -z -v -v`) lists elements with offset, header size and data size. Every element mkvinfo lists is found in our tree at the same offset with the same sizes
(`differing=0`); the tree may list more (it also lists children that mkvinfo hides at the verbosity used). On the damaged published files the lists
differ in one way: test7 contains an element at 451451 whose size runs past its cluster. The tree cuts it at the cluster's end and keeps its bytes as a
range; mkvinfo descends into the cut-off payload and lists the elements it sees there, which we do not. Track headers (number, UID, type, codec, language,
flags, default duration, video/audio fields) are compared field by field.

## Open cost (I/O)

| Layout | Reads at open |
|---|---|
| SeekHead + Cues (any writer that wrote them) | The first window of the file (32 KiB); Cues are read on the first seek. |
| Live (unknown sizes, no SeekHead, Tracks before Clusters) | The first window. |
| No SeekHead, Tracks after the Clusters | One 512-byte read per Cluster header until Tracks is found: bytes = clusters × 512. Documented cost; there is no way to find a trailing Tracks without walking. |

## Allocations and memory

`TryReadPacket` allocates nothing in Release builds: payloads are read into a reused buffer (initial 1 MiB, doubled only when a larger block arrives, bounded by
`MediaLimits.MaxPacketBytes`). Debug builds' async state machines allocate, so the zero-allocation test is skipped there.

Open-time `ToArray()` copies are of CodecPrivate, the SegmentUUID, compression settings and the header-stripping prefix. Each is a slice of a master element
(Info, Tracks, SeekHead, Cues) that was itself checked against `MediaLimits.MaxAllocationBytes` before it was read, so the copies are bounded by that limit and by the number
of tracks: the memory taken at open does not grow with the size of the file.

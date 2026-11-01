# Ogg (RFC 3533)

`RecursiveAI.OneMedia.Ogg`: `OggDemuxer` (seekable and forward-only sources), `OggAsyncDemuxer` (ranged sources), `OggFormat` (registry), the page reader
and the logical-stream model (`OggLogicalBitstream`, `OggSkeleton`). Opening is asynchronous at the bottom (`SequentialInput` over a seekable, ranged or
forward-only source); the synchronous demuxer completes the same code without waiting.

## Structure

- A page is `OggS`, version 0, 27 fixed bytes, the lacing table and a body of at most 255 × 255 bytes (RFC 3533 §6). The CRC-32 (polynomial 0x04C11DB7,
  no reflection, no final XOR) is checked on every page. A page that fails the check is still used and reported (`ChecksumMismatch`, code
  `ogg.crc`); bytes that are not a page are skipped up to the next `OggS` that has a plausible header (`ogg.skipped`, `UnaccountedBytes`).
- A packet that spans pages is assembled from the lacing values; a continued-packet flag with nothing to continue, a sequence-number gap, a page after the
  end-of-stream page and pages with no first page each raise a defect (`ogg.continuation-orphan`, `ogg.continuation-missing`, `ogg.sequence-gap`, `ogg.after-eos`, `ogg.orphan`) and reading goes on.
- Logical bitstreams are told apart by serial number. Multiplexed streams become one track each in the order of their first pages. A chained file
  (a first page that follows the last page of every earlier stream) appends the tracks of the next link as the reader reaches it; times continue after the
  earlier links, so the timeline is the concatenation.
- Streams are recognised by their first packet: `\x01vorbis`, `OpusHead`, `\x7fFLAC`, `\x80theora`, `Speex   `, `fishead`/`fisbone` (Skeleton). Skeleton is read as
  metadata (`OggDemuxer.Skeleton`: version, bones with name and language, the raw index packets) and its names and languages feed the tracks. Any other
  stream is kept as a `Data` track with unknown times; its packets are delivered as they are.
- Limits: header packets and packet assembly are bounded by `MediaLimits.MaxPacketBytes` and `MaxAllocationBytes`; the number of streams by
  `MaxElementCount`.

## Time

Times come from granule positions (RFC 3533 §6, RFC 7845 §4, Vorbis I §A.2, Theora §A.2.3, Ogg FLAC mapping):

| Stream | Time base | Rule |
|---|---|---|
| Vorbis | sample rate | The end of the packet that finishes a page is the page's granule. Earlier packets are counted back by their durations (a quarter of the previous plus a quarter of the own block size; the block size comes from the mode in the setup header). The first packet counts as half its block, so a stream starts before 0 (pts −128 for a 256-sample short block), as ffprobe shows. |
| Opus | 48 kHz | `pts = granule − pre-skip` (RFC 7845 §4.4). Packet duration from the TOC byte (`OpusToc.TryGetSamples`). The pre-skip is also on the track as the priming time. |
| FLAC | sample rate | Granule counts samples; the duration of a packet is the block size of its frame header. |
| Theora | frame rate numerator (a tick is one frame period) | The granule is `(keyframe number << shift) + frames since the keyframe`. From version 3.2.1 the numbers count from 1, so their sum is the number of frames up to and including the packet; earlier versions add one. The first frame is pts 0. Keyframe = bit 6 of the first byte clear. |
| Speex | sample rate | Frames per packet × frame size from the header. |

On the last page of a stream, a granule below the packets' total shortens the last packet: its duration is reduced and `PacketInfo.DiscardPadding` holds the cut
(RFC 7845 §4.5; Vorbis I §A.2). A granule that disagrees with the packet durations elsewhere is a defect (`ogg.granule-mismatch`) and the granule wins.

`Duration` of a seekable file: a tail read of at most 72 KiB finds the last page of each stream, and the longest stream's end is the duration (Opus
without the pre-skip). It is unknown for a forward-only source and for a chained file, where the later links' ends are not known without reading
their pages. This is the only read that is not a header: a seek, not a scan.

Seeking bisects on page granules of the first audio stream (the first enabled stream otherwise). For Theora the seek lands on the page before the keyframe
that governs the target and drops packets until a keyframe.

## Tags

Every stream with a comment header gives one `VorbisCommentBlock` (see [vorbis-comment.md](vorbis-comment.md)). The first stream with comments is the
file's tags (track id 0), the others carry their serial number. An ID3v2 tag in front of the first page is skipped and exposed as the raw binary tag
`id3:ID3v2` (`RawId3Prefix`). Facts: the duration, the Theora picture size, `DATE` and `ENCODER` as evidence.

## I/O

Opening reads the pages of the header packets (one or two reads of 64 KiB) and one tail read of at most 72 KiB; tag values are in the packets already read.

## Differences from `ffprobe -show_packets` (7.0.2)

Checked on the generated files, the Xiph/Blender samples and the OneAudio-readable files (see the results in the unit report).

- **`pos`.** ffprobe prints the offset of the page that ends the packet; ours is the range of the packet's own payload, which can start on an earlier page.
- **Chained files.** ffprobe 7.0.2 lists only the first link's packets; ours also lists the later links' tracks and packets. `count` is excepted.
- **Duration of Opus.** ffprobe takes the last granule as the duration (it includes the pre-skip, 6.5 ms for 312 samples); ours is the playable length.
- **Duration of chained files** is unknown here, known to ffprobe.

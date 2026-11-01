# Writing Matroska and WebM

`RecursiveAI.OneMedia.Matroska.MatroskaMuxer` writes Matroska and WebM files from packets in decode order. It implements `IMuxer` (declare tracks with `AddTrack`, give packets with `WritePacket`, end with `Finish`, which returns a `MuxReport`). Payload bytes are copied to the output unchanged. Specifications: RFC 8794 (EBML), RFC 9559 (Matroska), the Matroska codec mapping and the WebM container guidelines; section numbers are in the code comments.

## Layout

Seekable output (the default):

```
EBML header | Segment (size patched at Finish)
  SeekHead (Info, Tracks, Cues, two spare slots as Void)
  Info (TimestampScale, Duration, MuxingApp, WritingApp, SegmentUUID [not in WebM])
  Tracks
  Void (ReserveBytes, default 2048: room for tags added in place later)
  Cluster ...
  Cues
```

- The header is written when the first Cluster is closed, so a track's `DefaultDuration` can be stated when the first packets show one (the same duration on every packet but the last, at least three packets). Otherwise `DefaultDuration` comes from the declared frame rate (video) or a codec's fixed frame length (AAC, AC-3 …).
- The Segment is first written with an unknown size; `Finish` seeks back and patches the SeekHead (Cues position), Info (Duration, a fixed 8-byte float) and the Segment size. A file cut short stays readable.
- A packet is a **SimpleBlock** unless it needs a **BlockGroup**: a duration the track's `DefaultDuration` does not give (`BlockDuration`), a `DiscardPadding`, or a subtitle. A non-key BlockGroup carries a `ReferenceBlock`, because a BlockGroup without one is a key frame (RFC 9559 §10.3).
- Packets of one audio track that share a timestamp and state no duration (the frames of a laced block in the file they came from) are written back as one **Xiph-laced** block; written as separate blocks they would overlap. Lacing happens only in seekable mode, and the track's `FlagLacing` is left at its default for audio (video is `FlagLacing 0`).
- **Clusters** start on a video key frame (with no video, on a packet of the first track) once the current one is at least `MinClusterDuration` (2 s) long or holds `MaxClusterBytes` (4 MiB). They are cut inside a group of pictures only when block offsets would overflow 16 bits (about 32 s at 1 ms units) or the payload passes four times `MaxClusterBytes`. The Cluster Timestamp is the earliest block time, so offsets are small.
- **Cues** (RFC 9559 §11): one per video key frame (no video: one per Cluster) with `CueClusterPosition` (from the Segment data start) and `CueRelativePosition` (from the Cluster data start).
- **CRC-32** (RFC 8794 §11.3.1), when `Crc32` is set: first child of SeekHead, Info, Tracks, Cues and every Cluster.

## Live mode

`MatroskaMuxOptions.Live` writes to a stream that cannot seek: the Segment and every Cluster have an unknown size (RFC 8794 §6.2), each block is written when it arrives (two writes: block header, payload), and there are no Cues, Duration, SeekHead patch, lacing or CRC. `DefaultDuration` comes only from the declared frame rate or a codec's frame length; any other packet with a duration is a BlockGroup. Read back with the forward-only reader and with ffprobe.

## Memory and allocations

- Seekable mode holds at most one Cluster's payload (`MaxClusterBytes`, up to four times that inside a long group of pictures, plus one packet) and the Cue list: `MatroskaMuxer.CueBytesPerPoint` = 24 bytes in memory per cue point, about 25 bytes when written.
- Live mode holds no payload.
- After the first Cluster the write path allocates nothing per packet (tests measure 0 bytes over 20,000 live packets and under 4 KiB over 10,000 seekable ones, which is the Cue list doubling).

## Tracks

CodecID and CodecPrivate come from the neutral track and the codec layer:

| Source | CodecID | CodecPrivate |
|---|---|---|
| H.264 / HEVC / VVC | `V_MPEG4/ISO/AVC` / `V_MPEGH/ISO/HEVC` / `V_MPEGI/ISO/VVC` | `avcC` / `hvcC` / `vvcC` as stored (Annex B tracks are refused) |
| AV1, VP9, VP8 | `V_AV1`, `V_VP9`, `V_VP8` | `av1C`; VP9: `vpcC` → Matroska VP9 features (when present); none for VP8 |
| MPEG-1/2 video, MJPEG, MPEG-4 ASP, Theora | `V_MPEG1`, `V_MPEG2`, `V_MJPEG`, `V_MPEG4/ISO/ASP`, `V_THEORA` | MPEG-4: the decoder-specific info of the `esds` |
| AAC | `A_AAC` | AudioSpecificConfig (`esds` unwrapped); implicit SBR (CodecID ending `/SBR`) writes half the output rate as `SamplingFrequency` |
| Opus | `A_OPUS` | `OpusHead` (`dOps` converted); `CodecDelay` = pre-skip, `SeekPreRoll` 80 ms |
| Vorbis, FLAC | `A_VORBIS`, `A_FLAC` | Xiph-laced headers; `fLaC` + metadata blocks (`dfLa` converted) |
| MP3, MP2, AC-3, E-AC-3 | `A_MPEG/L3`, `A_MPEG/L2`, `A_AC3`, `A_EAC3` | none |
| SubRip, ASS | `S_TEXT/UTF8`, `S_TEXT/ASS` | ASS header |
| A track read from Matroska | its own CodecID (`CodecTag`) | its CodecPrivate unchanged; `CodecDelay` restated; BitDepth kept |
| `bitmapinfoheader` / `waveformatex` (from Matroska VFW/ACM or AVI) | `V_MS/VFW/FOURCC` / `A_MS/ACM` | the structure |

Also written: track number (index + 1), UID, flags (`FlagDefault` only when not default, forced, hearing/visual impaired, original, commentary, enabled), name, language (`Language` for three lowercase letters, otherwise `und` plus `LanguageBCP47`), pixel size, display size from the pixel aspect ratio, `Colour` (matrix, transfer, primaries, range) when the codec headers state it, channels, sampling frequency, bit depth for FLAC, PCM wrappers and tracks read from Matroska.

**WebM** (`MatroskaProfile.WebM`) takes VP8, VP9, AV1, Vorbis and Opus; any other track is refused with `MuxRefusalReason.UnsupportedCodec` naming the CodecID. mkvalidator 0.6.0, the validator used here, rejects `SegmentUUID` and the whole `Colour` element in its webm profile (the WebM guidelines list `Colour`), so the WebM profile leaves both out.

Refusals are typed `MuxRefusedException` with a `MuxRefusalReason`, raised before anything of the offending track or packet is written: `UnsupportedCodec`, `MissingConfiguration` (no CodecPrivate where the codec needs it), `InvalidConfiguration` (setup bytes in a form that cannot be repackaged), `NonMonotonicDts`, `MissingTimestamp`, `InvalidTrack`, `InvalidState`, `LimitExceeded`, `OutputNotSuitable`.

## Time

Block timestamps are in `TimestampScale` units (default 1 ms; a Matroska source whose tracks all count 1,000–10,000 ticks per second keeps its time base). Times are rescaled exactly with nearest rounding; `MuxReport.Notes` says when any rounding happened. Matroska stores no decode time: blocks are written in the order given, with presentation times, so a B-frame stream needs no extra field. DTS only gates the order (a decrease within a track is refused).

Tracks whose packet times already exclude `CodecDelay` (`TrackEditInfo.PacketTimesIncludePriming`: Matroska and Ogg Opus) get it added back to the Block timestamp; an ISO Opus track (packet times start at 0, the pre-skip is discarded by the decoder) is written with its times as they are, and `CodecDelay` states the pre-skip. AAC priming from an edit list is not carried (Matroska readers do not apply `CodecDelay` to AAC consistently).

A packet with an unknown duration stays unknown (no `BlockDuration`): the neutral contract says to derive it from the next decode time, but a lace member or a subtitle has no next time to derive from.

## Not carried

Rotation (`ProjectionPoseRoll`), interlacing and field order, stereo mode, content encoding/compression (frames are written plain), attachments, chapters and tags (tag writing is separate), `BlockAdditions`, CodecState, and the discardable flag on a packet that needs a BlockGroup.

## How it is checked

`tools/OneMedia.MatroskaMuxCheck` copy-muxes a file with our reader and muxer, then runs `ffprobe -show_packets -show_data_hash adler32` on the output and compares, per stream, size, payload hash, presentation time, duration and key flag with what our reader found in the input; for a Matroska input it also compares ffprobe's listing of the input and of the output field by field (decode times included). It then reads the output back with our own reader (flags included). `tools/matroska/run-mux-checks.sh` runs a list of files and adds mkvalidator, mkvinfo and `ffmpeg -f null`. Every difference is one of these explained classes, counted per file:

| Class | What differs | Why |
|---|---|---|
| `lace-shared-pts` | Frames of one laced block carry one timestamp in our reader | A lace has one timestamp; ffprobe spaces the frames by the codec's frame length, both for the input and for our re-laced output |
| `rounded-to-the-output-time-base` | Times differ by under half an output unit | The source's TimestampScale is finer or does not divide a second; ffprobe's own integer time base for such a scale is approximate (1e-4) |
| `pts-as-ffprobe-gives-for-the-input` / `duration-as-ffprobe-gives-for-the-input` | Our reader and ffprobe disagree, but ffprobe says the same for input and output | A documented reader/ffprobe difference (`docs/matroska-differential.md`) |
| `pts-not-stated-by-ffprobe-for-the-input` | ffprobe gives no presentation time for packets of the input (MPEG-4 with B-frames) and does for the output | ffprobe derives them from our explicit block times; ffmpeg's null muxer then warns about the dts it generates |
| `duration-from-default-duration` | ffprobe shows a duration for the output where the input stated none | We state a `DefaultDuration` the input lacked (learned or from the codec) |
| `ffprobe-splits-blocks(-of-the-input-too)` | ffprobe lists more packets than blocks | RealAudio de-interleaving, identical for the input |
| invisible flag | ffprobe shows no `D` for a block's invisible flag | Checked with our reader instead: the flag survives |

The results table with file and packet counts is at the end of the unit's report; rerun with `tools/matroska/run-mux-checks.sh <work-dir> <file-list>`.

# ISO base media files (MP4, MOV, 3GP, fragmented MP4, CMAF)

How `RecursiveAI.OneMedia.IsoBmff` reads these files. Specifications: ISO/IEC 14496-12 (clause numbers in the code comments), the QuickTime File
Format specification for the atom differences, 3GPP TS 26.244 for 3GP. Codec configuration boxes (`avcC`, `hvcC`, `esds`, …) are carried as opaque
bytes; they are parsed by the codec layer.

## Layers

| Layer | Types | Notes |
|---|---|---|
| Box structure | `BoxReader`, `BoxHeader`, `BoxNode`, `BoxTree` | Lazy tree of byte ranges. Children are read on first use. No payload bytes are kept. |
| Format model | `FileTypeBox`, `MovieReader` → `IsoMovie`, `IsoTrack`, `SampleEntry`, `EditListEntry`, `TrackExtends` | Headers, edit lists, sample descriptions. Sample tables are located, not read. |
| Neutral demux | `IsoBmffDemuxer` (seekable and forward-only), `IsoBmffAsyncDemuxer` (ranged), `IsoBmffFormat` | Tracks, packets, seeking. |

## Box rules

- 32-bit size, 64-bit `largesize` (size field 1), size 0 ("to the end of the enclosing range"), and `uuid` boxes with their 16-byte user type are read
  (§4.2). Unknown and vendor boxes are kept as ranges.
- Containers: `moov trak mdia minf stbl edts dinf mvex moof traf mfra udta tref sinf schi meta ilst stsd dref` and the item boxes inside `ilst`.
  `stsd` and `dref` start with version, flags and an entry count (8 bytes) before their children. `meta` is a full box in ISO files and a plain
  container in QuickTime files; the reader tells them apart by looking for `hdlr` right after the header.
- A QuickTime list may end with a 32-bit zero, and writers pad with zero bytes: reported as an `Info` defect (`UnaccountedBytes`), nothing more.

### Defects

All carry the byte offset of the box (or entry) they concern. Reading continues where it safely can.

| Code | Kind | Meaning | What happens |
|---|---|---|---|
| `isobmff.size-below-header` | `InvalidSize` | Declared size smaller than the header | The box list ends there. |
| `isobmff.size-huge` | `InvalidSize` | 64-bit size above 2^63-1 | The box list ends there. |
| `isobmff.truncated` | `SizeExceedsFile` | Top-level box runs past the end of the file | The box is clamped to the file. |
| `isobmff.size-exceeds-parent` | `SizeExceedsParent` | Nested box runs past its parent | The box is clamped to the parent. |
| — | `Truncated` | Fewer than 8 bytes left, a cut-off 64-bit size or `uuid` type, or a source that ends inside a box | The list ends. |
| `isobmff.no-moov` | `MissingRequired` (exception) | No movie box in the file | `MediaFormatException`; `TryOpen` returns the defect instead. |
| `isobmff.no-mvhd`, `isobmff.track-incomplete`, `isobmff.no-stbl` | `MissingRequired` | A required box is missing | A track without track header, media header or handler is skipped. |
| `isobmff.table-count`, `isobmff.elst-count`, `isobmff.trun-count` | `Truncated` | A table declares more entries than its box holds | The count is clamped to what the box holds. |
| `isobmff.stts-short`, `isobmff.stsc-short`, `isobmff.stsc-order`, `isobmff.chunk-missing` | `Inconsistent` | Sample tables disagree (for example the time table covers fewer samples than the size table) | The track ends at the last sample the tables describe. Reported once per track. |
| `isobmff.sample-past-eof` | `Truncated` | A sample's bytes lie beyond the end of the file | The sample is not returned. Reported once per track. |
| `isobmff.sample-limit` | `LimitReached` | A sample is larger than `MaxPacketBytes` | The sample is skipped. |
| `isobmff.short-read` | `Truncated` | The source returned fewer bytes than the sample size | The sample is skipped. |
| `isobmff.traf-unknown-track`, `isobmff.traf-no-tfhd` | `Inconsistent`, `MissingRequired` | A track fragment names a track the movie does not declare, or has no header | The track fragment is skipped. |
| `isobmff.forward-order` | `InvalidOrder` | Forward-only source: a sample lies before the current position | The sample is skipped. |
| `isobmff.entry-large` | `LimitReached` | A sample entry is larger than 1 MiB | Only its first mebibyte is read. |
| `isobmff.limit` | `LimitReached` | Depth, element count, allocation or sample-table limit | The box list or fragment walk ends. |

Limits used: `MaxStructureDepth`, `MaxElementCount`, `MaxTracks`, `MaxSampleTableEntries`, `MaxAllocationBytes` (the movie box is only read whole
for ranged and forward-only sources; each movie fragment box is read whole), `MaxPacketBytes`, `MaxTagBytes` (the gapless tag), `MaxDefects`.
Every table's declared entry count is checked against the box size and the limit before anything is read.

## Time

Packet times are on the track's **media timeline** in the track's timescale, exact: `Dts` is the running sum of the decoding-time-to-sample
deltas (`stts`) or the fragment's base decode time plus the running sum of run durations; `Pts = Dts + composition offset`. Composition
offsets (`ctts` version 0 and 1, `trun` version 0 and 1) are read as signed 32-bit values; a version-0 value above 2^31 is not credible for a
real file and old QuickTime writers store negative offsets that way. `Duration` is the table's duration for the sample.

Edit lists are **not applied** to packet times. They are reported in `MediaTrack.Edit`:

- `Edits`: every entry (`MediaStart` unknown for an empty edit, `Duration` in the movie timescale);
- `StartTime`: the total of leading empty edits (movie timescale);
- `Priming`: the media time at which the first non-empty edit starts, in the track timescale (§8.6.6). For AAC this is the encoder delay the player
  skips. When the movie carries an iTunes `iTunSMPB` tag that parses (three hexadecimal fields: delay, padding, valid frames), it replaces
  `Priming` and `EndPadding` for the first audio track, because the tag states encoder delay and padding exactly where the edit list rounds;
- `EndPadding`: for a single non-empty edit, the media after the end of the edit.

Presentation time on the edited timeline of a packet is `Pts - Priming + StartTime` (rescaled to a common scale). A packet that ends at or before
the first edit's start (`Pts + Duration <= media_time`) carries `PacketAttributes.Discard`: it is decoded but never shown.

## Sample tables without loading them

`SampleCursor` walks `stts`, `ctts`, `stss`, `stsz`/`stz2`, `stsc`, `stco`/`co64`, and `sdtp` together in sample order through 4 KiB windows. Memory
is constant whatever the table length, and moving to the next sample costs no allocation. A seek positions each table directly (binary search in
`stss`, run walk in `stts`/`ctts`/`stsc`, direct indexing in the fixed-size tables). Keyframe seeking: the sync sample at or before (or after) the target; the
other tracks land on their last sync sample not after the primary track's landing time. The primary track is the first enabled video track, else
the first enabled track.

Keyframes: samples listed in `stss`; every sample when `stss` is absent. Repeated `stss` entries are tolerated. `Disposable`: `sdtp`
`sample_is_depended_on == 2`. Sample group boxes (`sgpd`, `sbgp`) are kept as ranges and do not change packet flags.

## Packet order

Across the enabled tracks, the packet with the lowest source offset comes next. For interleaved files this is the order the data is stored in, so
a plain read is sequential; for a file whose tracks are stored one after the other (all video, then all audio), it is the same storage order.
Within a track packets are in decode order. A disabled track costs nothing: its cursor is never advanced and its payload is never read.

Payload reads go through a window that starts at 4 KiB and doubles (to 64 KiB) while reads continue where the last one ended. After a jump (over
the chunks of a switched-off track, say) the first window is as long as the run that just ended, rounded up by an eighth and to 4 KiB, because the
next chunk of an interleaved track is about as long as the last: a chunk then costs one read instead of a climb from 4 KiB. Samples over 32 KiB
bypass the window.

## Fragmented files

`mvex`/`trex` mark a file as fragmented. Packets of the movie box's own tables come first (usually none: `empty_moov`), then each `moof` in file
order. A fragment is held in memory only while it is read. Defaults inherit per §8.8.7 and §8.8.8: `tfhd`, then `trex`; `first_sample_flags`
override the first sample; the base data offset is the explicit `base_data_offset`, else the start of the `moof` (`default-base-is-moof` and the
common case), else the end of the previous track fragment's data; a `trun` without `data_offset` continues after the previous run. A missing
`tfdt` continues the track's previous decode time; for the first fragment that is the end of the samples the movie box itself describes (a hybrid
file, zero for `empty_moov`). Non-sync samples have bit 16 of the sample flags set.

Seeking in a fragmented file whose movie box holds no samples picks the first enabled video track (else the first enabled track) and scans movie
fragment headers (never media data) until the target is passed, then reloads the landing fragment. The scan starts from an index when there is
one, so the cost is a few header reads instead of one per fragment:

1. the movie fragment random access box (`mfra`, §8.8.9), found through the `mfro` box that closes the file: two reads at the first seek (16 bytes,
   then the box), used when its `tfra` for the track has an entry at or before the target;
2. else the segment index boxes (`sidx`, §8.16.3) between `moov` and the first fragment, read at open, when they are not hierarchical.

The index only names where to start: the scan begins one entry before the best one, and an index is ignored (scan from the first fragment) if its offset
is not a `moof` or that fragment does not state a `tfdt` for every track fragment, because the running decode times are unknown without the earlier
fragments. A file with neither index, or only per-segment `sidx` boxes (CMAF/DASH live segments), pays the full header scan.

### What a probe reads

- Seekable file: the registry's first look at the file (one read of 4 KiB, to recognise it) is handed to the reader, which starts from those bytes
  instead of reading them again. After that the read-ahead window (32 KiB) serves the start of each top-level box on the path to `moov`
  (`ftyp`, then `mdat` is skipped by size, then `moov`), and the boxes inside `moov` except the table entries. With `moov` at the end this is
  one seek, not a scan of `mdat`.
- A movie box of at most 128 KiB is then read once, whole, into a pooled buffer; it is parsed from there and the sample-table windows below read
  from it too, so a small file opens with a read for the head, one for the box header and one for the box. A larger movie box is read through the
  windows as before, so the buffer never grows with the file. Reads outside the box go to the file.
- Table windows: one 4 KiB window per table when a packet cursor is created (at open); from the buffered movie box when there is one.
- Fragmented file: `moov`, then the box headers up to the first `moof` or `mdat`, to find `sidx` (served by the same read-ahead window as `moov`).
  The total duration is `mvhd`, else `mehd`, else the sum of `sidx` durations; when none states it, `Duration` is unknown. Knowing it exactly would
  take one read per fragment (what `ffprobe` does), so the probe does not. The `mfra` box at the end is read only on the first seek.
- Ranged sources (`IsoBmffAsyncDemuxer`): `moov` is fetched in one request and parsed from memory; each packet is one request, each fragment one
  request for its header box.

Measured numbers are in the unit report.

## Forward-only sources

`IsoBmffDemuxer.Open(IForwardMediaSource)` reads boxes in order: `moov` must come before the media data (fragmented streams, faststart files);
otherwise `UnsupportedMediaException`. After each `moof` the following `mdat` header is read so its end is known; samples are read by
skipping to their offsets, which must not go backwards (`isobmff.forward-order`).

## Sample entries

`SampleEntry` keeps the fixed fields that matter (dimensions, channels, bits, sample rate, QuickTime sound version 0–2) and every box after them in
`Extensions` (QuickTime `wave` children are surfaced too). `Configuration` is the payload of the codec's configuration box, tagged with the box
type: `avcC`, `hvcC`, `vvcC`, `av1C`, `vpcC`, `esds` (also for `mp4v`), `dOps`, `dfLa`, `dac3`, `dec3`, `damr`, `alac`; for text and metadata entries it
is the bytes after the eight common entry bytes. Protected entries (`encv`, `enca`, …) report the clear format from `sinf/frma` as `OriginalFormat`
and set `IsProtected`.

Codec ids: `avc1/avc3 → h264`, `hvc1/hev1/dvh1/dvhe → hevc`, `vvc1/vvi1 → vvc`, `av01 → av1`, `vp08/vp09 → vp8/vp9`, `jpeg/mjpa/mjpb → mjpeg`,
`mp4a` and `mp4v` by the `esds` object type (`0x40/0x66–0x68 → aac`, `0x69/0x6B → mp3`, `0x20 → mpeg4` (MPEG-4 part 2), `0x60–0x65 → mpeg2video`,
`0x6A → mpeg1video`, `0xA5/0xA6 → ac3/eac3`, `0xDD → vorbis`), `.mp3` and `ms\0U → mp3`, `Opus`, `fLaC`, `ac-3`, `ec-3`, `samr`, `sawb`, `alac`, the
QuickTime PCM codes → `pcm`, `tx3g`, `wvtt`, `stpp → ttml`. Anything else maps to `Unknown` and keeps the code in `MediaTrack.CodecTag`.

Language: the media header's 15-bit packed ISO-639-2/T code, or, for values below 0x400, the QuickTime Macintosh language code mapped to
ISO-639-2/T (0 = `eng`).

AMR-WB (`sawb`) carries the same `damr` setup box as AMR-NB (3GPP TS 26.244). Files whose `sawb` entry has no such box (observed in files written by
ffmpeg, which MP4Box calls invalid) get an empty configuration.

Disposition: `Default` is set on the first enabled track of each kind, within its alternate group when it has one; `Disabled` marks tracks whose
`tkhd` enabled flag is clear.

PCM audio (`sowt`, `twos`, `in24`, `fl32`, `raw `, ...) is stored one sample frame per table sample. The demuxer joins frames that follow each other in the file
into packets of up to 1024 frames (the packet size ffprobe uses), so a minute of 48 kHz audio is 2,813 packets rather than 2.9 million. The first packet
after a seek starts at the sample the seek landed on. `MediaTrack.PacketCount` is unknown (-1) for these tracks. Other codecs are never joined: QuickTime
tracks that store a framed codec as single bytes (observed with MP3 in `multitrack.mov`, 17.6 million one-byte samples) are reported per table sample,
because finding the frames is codec work.

A track may have several sample descriptions; `IsoBmffDemuxer.SampleDescriptionIndex` is the 1-based index for the packet last returned.

## Differences from `ffprobe -show_packets` (7.0.2 and 8.0.1), each checked against the files

The comparison tool applies ffprobe's edit-list handling before comparing (it subtracts the first non-empty edit's start from every timestamp and
adds leading empty edits) and lists the following as explained, never as agreement:

| Difference | Who follows the spec |
|---|---|
| Last packet's duration: ffprobe replaces it (parser frame length for MP3/AC-3/AAC, or the edit's end); the table says otherwise | The table value is what the file stores; OneAudio and MP4Box's sample table agree with us. |
| Negative composition offsets (`ctts` v1, `trun` v1): ffprobe moves DTS earlier by the largest `dts - pts` for tables in `moov` and moves PTS later for fragments, so DTS never exceeds PTS | We keep `Pts = Dts + offset` as §8.6.1.3 defines it. |
| First fragment's durations that come from the `tfhd` default: ffprobe prints `N/A` | The box states them; we use them (§8.8.7). |
| First audio sample of a fragmented file whose run states 3528 for a 1024-sample frame (files written by one muxer): ffprobe shows `N/A` or the frame length | The run states it. |
| Edit lists with several segments (or one that cuts the media short): ffprobe applies them to its sample index, dropping the samples outside the segments and repeating those several segments use (a 106-segment video track lists 1,164 packets for 1,100 samples) | Edit lists are reported in `TrackEditInfo`, not applied (see Time). Every packet ffprobe lists is one of ours with the same size and position. |
| Fragment samples whose stated duration is 0 (`frag_every_frame` writes it for the last sample it knows of): ffprobe reports the distance to the next sample | The box states 0; so do we. |
| Video packets whose duration ffprobe takes from the video parser's frame timing (every duration a fixed multiple of the table's, e.g. 250 against 125 in one H.264 file) | The table states the delta. |
| Data tracks (RTP hint, MPEG-4 systems): ffprobe marks every packet a key frame | We report the track's sync sample table. |
| A file with samples in the movie box and in fragments, plus an edit list: ffprobe places the movie box's samples on the edited timeline in its own way | Timestamps are the media timeline of the tables (no edit applied). |
| A sample that straddles the first edit's start or lies outside every edit segment: ffprobe flags it discardable and moves its timestamps | We flag only samples that end before the first edit's start. |
| Packets whose timeline ffprobe built from guessed durations (`frag_every_frame` files): repeated decode times and a discard flag on the repeats | Times come from `tfdt` and the runs, exact. |
| A file cut off inside the media data: ffprobe still lists the sample that straddles the cut | We stop before it and record `Truncated` with the offset. |
| A decoding-time-to-sample delta far above the track's usual one (a gap in the timeline; 35 of 15,294 video samples in one Blender movie, with deltas of 50 to 600 ticks against the usual 25): ffprobe reports the usual duration for the packet while its DTS still advances by the delta | The table states the delta; `Duration` is the delta (§8.6.1.2). |
| `Discard` on a fragment sample before the edit start: ffprobe flags it only for samples from `moov`'s tables | The edit list applies to fragments too (§8.6.6). |
| H.264/HEVC keyframes: ffprobe also marks IDR pictures that the sync sample table omits | We use container information only (decode-free); MP4Box's report agrees with the table. |
| One-frame-per-sample PCM (`sowt`, `twos`): ffprobe regroups samples into ~1000-byte packets | We return the sample table's samples; bytes covered and offsets are identical. |
| Edit lists that play media twice (a 106-segment video track in one file, an MP3 track in another): ffprobe lists the repeated samples again; it also splits PCM packets at edit boundaries | Ours lists each stored sample once; `packetset` confirms every position and size of ours is among ffprobe's. |
| A fragmented file whose edit is `[0+0]` (segment_duration 0): ffprobe drops the first moov-table samples | §8.6.6 reads a zero duration as the whole track in a fragmented file; we keep every sample. |
| QuickTime MP3 stored as one-byte table samples (`multitrack.mov`, 17.6 million of them): ffprobe reframes by MP3 frame headers | Ours reports table samples (finding frames is codec work); bytes covered are the same. |
| A video track whose sample entry and `tkhd` say 240x180 while the H.263 picture header says 352x288 (`0625rvt6-42.mov`) | Ours is the container's value; the picture header is payload, read only by tools that parse the stream. |
| Duration of fragmented files: ffprobe scans the fragments for the real length; `mvhd` of a fragmented file often states a shorter one or none | Ours stays structural (no scan on open). `MediaFile.ContainerDuration` is the box value, `TracksDuration` the longest media header duration; fragment totals are not summed. |
| Duration of files whose `mvhd` is shorter than a track (or whose `mvhd` timescale is wrong): ffprobe reports the longest stream | `MediaFile.Duration` follows ffprobe (longest track), `ContainerDuration` keeps the box value. |

Everything else (size, byte offset, key flag, durations, timestamps) matches on every packet of every compared file.

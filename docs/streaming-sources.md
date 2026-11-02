# HLS and DASH presentations and growing files as sources

Two ways to read media that is not a finished file on disk:

- **A presentation**: an HLS playlist (RFC 8216) or a DASH manifest (MPD) whose segments are read as one media file
  (`PresentationSource.Open`, `PresentationSource.OpenAsync`, `Media.OpenPresentationAsync`; `Media.Open` on a local playlist or manifest does the same, content-sniffed).
- **A growing file**: a file that something is still writing (`GrowingFileSource`, `Media.OpenGrowing`). The reader waits at the end of the data for more.

Both deliver the same demuxer shape as every other format: tracks, then packets with exact `MediaTime`.

## Presentations

### Reading

- The playlist or manifest is parsed by the manifest model in `RecursiveAI.OneMedia.Streaming` (strict reader for DASH, lenient reader for HLS: what the standard forbids but servers write is a defect in the open options' defect log, with a `hls.*` code, and reading goes on).
- Each segment is fetched through a caller-supplied `IPresentationResolver` as one ranged read (a segment with a byte range is one range of its resource), with its initialization segment in front for fragmented MP4. The container reader that recognises the segment demultiplexes it. The two most recent resources stay open; everything else is released.
- A multivariant playlist or an MPD offers variants; `PresentationOptions.VariantIndex` picks one (default the first). A separate HLS audio group, and each DASH adaptation set, become further tracks; their packets are interleaved by decode time.
- Local files go through `FilePresentationResolver`, which refuses any location outside the playlist's directory. HTTP, blob storage and the browser's `Blob` are the caller's resolver (`DelegatePresentationResolver`); the library has no network code.

### Time

- Packet times are those the segments state (an MPEG-TS PTS, a fragment's `baseMediaDecodeTime`), in the track's own timescale. Nothing is re-timed.
- HLS `EXT-X-DISCONTINUITY`: the times of the segments after the break are shifted so they continue where the previous ones ended, and the first packet of each track after the break carries `PacketAttributes.Discontinuity`.
- DASH: `Period@start` less `presentationTimeOffset` is added to the times a segment states (zero for a single period starting at 0). The adapter takes the offset from the template, list or base once per representation; the segment's own `Time` is not used to place packets, so the offset is applied once.
- Seeking lands at the start of the segment containing the target; the first packet can be earlier than the target by up to one segment.

### Protected content

`EXT-X-KEY` with a method other than `NONE`, or `ContentProtection`, makes `IsProtected` true. No packets are delivered; the tracks that can be read without decryption are listed (init segments are read).

### Memory and I/O

- **A segment is read whole** into a pooled buffer and released when the next one starts, bounded by `MediaLimits.MaxSegmentBytes` (default 256 MiB; a larger one raises `MediaLimitExceededException`). Memory therefore depends on the largest segment, not on the presentation's length. Measured on one 94 MB MPEG-TS segment pair (240 s of 1280x720 at 6 Mbit/s, 183 MB of payload, 16,337 packets): peak working set 136 MB. A resolver that prefetches (for HTTP, the first 256 KiB of each resource, which also yields its length) cuts requests; the reader asks for each segment once.
- `MediaLimits.MaxSegments` (default one million) bounds the segment list across renditions.
- Requests for a 100-segment presentation (6,808 packets, 1.8 MB of media), through the local range server, our reader with an HTTP resolver that fetches the first 256 KiB of a resource on first touch, next to ffprobe 8.1.3 on the same URL:

| Presentation | Tool | Open: bytes / requests | Read everything: bytes / requests |
|---|---|---|---|
| HLS MPEG-TS, 100 segments | ours | 29,045 / 2 | 1,840,144 / 99 |
| | ffprobe | 48,973 / 3 | 1,869,189 / 101 |
| HLS fMP4, 100 segments | ours | 26,856 / 3 | 1,452,900 / 99 |
| | ffprobe | 42,908 / 4 | 1,479,756 / 102 |
| DASH, 51 + 51 segments | ours | 26,289 / 5 | 1,458,304 / 198 |
| | ffprobe | 26,289 / 5 | 1,484,593 / 203 |

Opening reads the playlist and the first segment only, never the whole list of segments.

### Defects and limits

Playlist deviations become `MediaDefect`s with code `hls.missing-header`, `hls.segment-without-duration`, `hls.uri-without-stream-inf`, `hls.malformed-tag` (the line number is in the message and the text offset is the defect's offset). Segment-level defects from the container readers are copied in with the segment number in the message. An I-frames-only playlist is read as an ordinary one with an `hls.iframes-only` warning.

### Not covered

Live DASH is not reloaded (a dynamic MPD is read as it stands); HLS subtitle renditions and packed-audio ID3 timestamps are not handled; encrypted segments are not decrypted.

## Growing files

`GrowingFileSource` is a forward-only source that waits at the end for more data under a `GrowthPolicy`: poll interval (default 100 ms), idle timeout (default 10 s; `Timeout.Infinite` never gives up), an `IsComplete` callback checked at each empty poll (then one last look), and the caller's `CancellationToken`. A reader that meets the end ends cleanly; it never fails because the file was short.

Formats read as they grow: fragmented MP4 (a fragment is delivered once its box is complete), Matroska with unknown-size Segment and Clusters (live), MPEG-TS and FLV. A progressive MP4 whose `mdat` comes first has no `moov` until the writer finishes: it reports the defect `isobmff.moov-not-written` and has no tracks.

## Method

`OneMedia.Oracles presdiff OURS[=THEIRS[+THEIRS2]]` compares our packets (track, pts, dts, duration, size, key flag, payload SHA-256) with `ffprobe -show_packets -show_data_hash`. Because ffprobe lists every rendition in an order of its own, each of our tracks is paired with the next unused ffprobe stream of the same kind. For a multivariant playlist ffprobe is pointed at the first variant. Accepted differences are rows in `tools/OneMedia.Oracles/exceptions.tsv`.

`tools/gen-streaming-presentations.sh` writes the matrix (31 presentations: HLS MPEG-TS and fMP4 with audio and video, as one file of byte ranges, audio-only, video-only, HEVC, 3-second segments, a multivariant playlist and an audio group; MPEG-TS also with AC-3, MPEG-2 video and programme date-times; DASH with number, timeline, list, single-file byte-range, two video representations, HEVC, WebM and streaming-profile templates) and, with `--long`, three 100-segment ones. `testdata/streaming` holds eight of them at 2 s each.

## Oracle results

ffprobe 8.1.3 (`n8.1.3-32-g760d10603e-20261102`) is the oracle for all 31 presentations and the three 100-segment ones. The pinned ffprobe 7.0.2 segfaults on every HLS MPEG-TS presentation (all eleven `hls-ts-*`: exit 139, no output) and so cannot be the reference there; both versions hang on a `SegmentList` of byte ranges into one file per track (`dash-av-single`, `dash-av-single-list`: killed after 180 s), so ours is compared with ffprobe on the two media files themselves.

Recorded ffprobe behaviours (each is an exception row; ours follows the segments, which agree packet for packet with ffprobe on the individual segments):

| Behaviour | Files | Evidence |
|---|---|---|
| ffprobe re-times packed audio in its HLS demuxer by 1 or 2 ticks (90 kHz) | `hls-ts-*` audio | ffprobe on the segment alone gives our times |
| For an audio-only HLS MPEG-TS playlist, ffprobe repeats the first packet's time for the second and shifts every later one a frame | `hls-ts-a` | sizes and hashes equal; segment alone gives ours; the total is 260 packets both ways |
| ffprobe drops one audio packet of a DASH presentation with a `SegmentTimeline` or number template | `dash-av-timeline`, `dash-hevc`, `dash-seg3`, `dash-streaming`, `dash-2reps`, `dash-100` | the six segments read one by one with ffprobe total 260 audio packets (it lists 259 from the MPD) |
| ffprobe cannot read a `SegmentList` of URLs and starts at the second segment of each track (125 of 150 video, 215 of 260 audio) | `dash-av-list` | each init + segment read alone with ffprobe agrees with ours |
| WebM segments in DASH: no DTS for VP9 and no duration for Opus blocks | `dash-webm` | as for Matroska files |
| ffprobe reports a position inside the segment | all | we have no single source range |

## Live reading

`OneMedia.Oracles growing FORMAT SECONDS` has ffmpeg write a recording at real-time rate while we read the file as it grows, then compares with ffprobe on the finished file. 60 s recordings, 4,085 packets each:

| Format | Packets ours / ffprobe | Differences |
|---|---|---|
| fragmented MP4 | 4,085 / 4,085 | 0 |
| Matroska (finished) | 4,085 / 4,085 | 0 (accepted Matroska rows only) |
| Matroska live (unknown sizes) | 4,085 / 4,085 | 0 (same) |
| MPEG-TS | 4,085 / 4,085 | 0 (accepted TS rows only) |
| FLV | 4,085 / 4,085 | 0 (duration, position rows only) |

Constant memory: 900 s of 1280x720 at 6 Mbit/s written without throttling (61,261 packets, 683 MB each), working set sampled over the run: fragmented MP4 46 to 50 MiB, Matroska live 47 to 51 MiB, MPEG-TS 47 to 51 MiB, FLV 46 to 49 MiB.

## Fuzzing

The `presentation` target feeds a playlist or manifest and up to 63 further resources (served in order of first request) through `PresentationSource`, reading up to 5,000 packets and seeking. Seeds are built from `testdata/streaming` by `tools/make-presentation-fuzz-seeds.py`.

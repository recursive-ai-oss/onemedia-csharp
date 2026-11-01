# Trimming, splitting and joining

`Media.Trim`, `Media.Split`, `Media.SplitBySize` and `Media.Concat` (and `onemedia trim|split|concat`) cut and join ISO base media files (MP4, MOV, 3GP, M4A, fragmented MP4 as input) without decoding or re-encoding. The layer underneath is `RecursiveAI.OneMedia.Editing`: `IsoCutter` (trim, split) and `IsoJoiner` (concat), over `IsoBmffDemuxer` and `IsoMuxer`. Packet payloads are copied unchanged. Matroska/WebM output is not part of this page yet (see the end).

## Trim

`Trim(source, output, from, to, mode)`: the range is on the source's presentation timeline (its edit list applied, so a file whose first frame shows at 0 is cut in the times a player shows). `to` is exclusive.

### Frame-accurate (default)

1. The first shown frame is the first video frame whose presentation time is at or after `from`. When `from` falls between two frames the next frame starts the clip and the report says by how much it is late.
2. Decoding starts at the last keyframe whose presentation time is not after that frame. The packets from that keyframe to the first shown frame are written and hidden by the video track's edit list (ISO/IEC 14496-12 §8.6.6: one segment, `media_time` = the first shown frame's media time, `segment_duration` = the clip length). `ClipReport.HiddenVideoPackets` counts them.
3. The last shown frame is the last frame whose presentation time is before `to`. Frames coded after it that are shown later (B-frames of the next group) are not written; frames shown later but coded before a kept frame are kept (they may be references) and hidden by the edit list's duration.
4. The clip length is the end of the last shown frame minus the start of the first, in video timescale ticks (exact). The movie timescale is the video track's.
5. Audio and other tracks follow the video: the clip starts at the first shown frame's time.
   - Audio edit lists start at the exact sample (`media_time` in audio ticks; rounded to the nearest sample when the video time is not a whole number of samples, and the report says so). Packets before the start are kept as pre-roll for codecs that need it (AAC and Vorbis and AC-3: one packet; MP3: two; Opus: 80 ms, RFC 7845 §4.2; PCM and lossless: none) and hidden by the edit list.
   - The edit list's duration is the clip length. Audio packets that start up to one video frame past the end are written (the end of the last frame is only known once seen) and hidden.
   - A track that starts after the clip does gets a leading empty edit, so it stays in step.
   - Timed text and data tracks (`tx3g`, `mett`, …) carry their own timing: a cue that straddles the start is shortened to begin at the clip's start, one that straddles the end is cut there, and times are rebased by the clip start. No edit list begins inside a sample: readers that round such an edit to the sample's start shift every cue (observed with ffmpeg).
6. A second video track is dropped (a note says so): a cut follows the first video track.

### Keyframe mode

`TrimMode.Keyframe` starts at the keyframe at or before `from`; nothing is hidden (`HiddenVideoPackets` is 0) and the report's `SourceStart` is that keyframe's time. Audio is aligned to it the same way as above, so audio and video stay in step. The end is the same as in frame-accurate mode.

### Audio-only media

No video frame decides the start. Frame-accurate trims start at exactly `from` (to the sample); keyframe mode starts at the packet that holds `from`. The clip length is `to - from`, shortened to the media when it ends sooner.

### What players do

The exact cut is stated by the edit list. A player that follows edit lists shows the clip from the requested frame; one that does not shows the hidden lead-in frames first (at most one group of pictures) and, for audio, the pre-roll and the packet's tail. Players check (`tools/trim/playback-trim.py`, a clip cut from frame 53 to frame 143 of a 25 fps source, frame numbers burned into the picture):

PLAYER_TABLE

ffmpeg-based tools honour the start of the audio edit to the sample but decode the last audio packet whole: the decoded audio is up to one codec frame longer than the clip (1,024 samples for AAC, 960 for Opus). The edit list's duration is exact.

### Refusals

`EditRefusedException` with an `EditRefusalReason`: `EmptyRange` (end not after start, start past the last frame, no frame in range), `NoKeyframe`, `ComplexEditList` (a track whose edit list has several media segments or a gap after media; join first, cut second is not possible: such a file is usually the output of `Concat`), `UnsupportedSource` (not an ISO file, not seekable, no track the muxer can carry; a video track the muxer refuses is `UnsupportedSource` with the muxer's reason). A cut that would need a re-encode does not arise: any start is expressible with an edit list.

### Limits

- Open GOPs: the keyframe flag is the container's (`stss`). A frame shown before the keyframe it follows in decode order (a leading picture) cannot be decoded from it; the cut never starts at one, because the first shown frame is chosen at or after the keyframe's own time.
- The start is found by reading the video track from the keyframe before `from` to the first shown frame (one group of pictures at most) and the clip by one pass from there; no other part of the file is read. The output is progressive, movie box first. Memory does not depend on the file size (the output's sample tables and a spool for the media data, as in `docs/muxing.md`).

## Split

`Split(source, openPart, times)` cuts at the keyframes at or before each time; `SplitBySize(source, openPart, maxPartBytes)` cuts at the first keyframe after the part reached the byte budget (a first pass over the file measures it).

Parts partition the packets: every video packet is in exactly one part (decode order between two keyframes), and an audio or data packet is in the part where it starts. Nothing is duplicated, so `Concat` of the parts returns the source's packets (size, flags, payload and times; `tools/trim/packet-compare.py`). Parts have no pre-roll: a decoder starts each part with a cold codec state, which can colour the first audio frame. Each part's video starts at its keyframe with nothing hidden. A part's audio starts at its first packet that starts at or after the keyframe's time, up to one packet late; a leading empty edit keeps it in step, so a few milliseconds of audio at each cut are not played (the last packet of the previous part is cut by its edit list at the same time).

## Concat

`Concat(sources, output)` joins files end to end. The first file decides the tracks carried and the brand. Tracks are matched by index and must agree in codec and sample entry tag, timescale, configuration bytes (parameter sets, setup bytes), audio format and frame size/rotation/aspect. Packets are copied; their times are shifted so each input follows the one before on the track's media timeline; each input's edit list segments are kept, shifted, in the output's. Neighbouring segments that continue each other in media time (also across a gap that exactly equals the media skipped) are merged, so joining the parts of a split gives back the original's edit list, and ffmpeg (which repeats samples at boundaries between segments of a list) lists the same packets as for the original.

A difference the output cannot carry is refused: `EditRefusedException` with `EditRefusalReason.NeedsReencode`, `TrackIndex` naming the track and the message saying what differs. Carrying differing configurations needs a second sample description on one track (`stsd` with two entries and `stsc` indexes), which the ISO muxer does not write today. [PM item in the unit report.]

Tracks whose inputs have no edit lists at all produce none; when any input has one, the others get a full-length segment, so presentation times stay as each file had them. Unrelated clips whose audio and video are not equally long accumulate the difference per join, as the sources' own edit lists say.

## Verification

`tools/trim/trim-matrix.py` (run by `tools/trim/run-trim-matrix.sh`): generates H.264, HEVC, AV1 and VP9 sources with AAC or Opus at 23.976, 25, 29.97 and 60 fps with three GOP shapes (closed GOPs of 12, 48 and 120 frames; B-frames 0, 2 and 3 where the encoder has them), cuts random ranges (on a frame, between frames, keyframe mode) and compares, through ffmpeg: the MD5 of every decoded frame, the first frame's time (0), the frame spacing, the length of the decoded audio and its alignment against the source's PCM at the expected sample. `tools/trim/packet-compare.py` compares the packets of two files through ffprobe (size, key flag, payload SHA-256, times up to one offset). `tools/trim/run-trim-large.sh` measures peak memory on a 4.5 GB file.

## Matroska and WebM

Matroska has no edit list. RFC 9559 §11.2 gives the mechanism: Block timestamps are signed offsets from the Cluster timestamp, and a frame whose timestamp is negative "MUST be decoded by the decoder but not played". A frame-accurate cut therefore shifts all times so the first shown frame is at zero, which puts the frames from the keyframe to it before zero; audio pre-roll is the same, and `CodecDelay`/`DiscardPadding` state codec delay and end padding. A cut at a keyframe needs nothing special. The Matroska muxer has to write a first Cluster with timestamp 0 and negative block offsets for this (the cluster timestamp is unsigned and is chosen from the earliest block today), so a Matroska output is not part of this change: the operations take ISO files in and write ISO files, and `Media.Trim` on a Matroska source is refused with `UnsupportedSource`. No player behaviour for Matroska output has been measured.

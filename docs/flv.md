# FLV (Adobe Flash Video File Format Specification 10.1)

`RecursiveAI.OneMedia.Flv`: `FlvDemuxer`, `FlvFormat` (the asynchronous open runs the reader on a pool thread over a ranged source), `FlvScriptTags`, and the AMF
reader (`Amf.cs`: AMF0, and AMF3 for the `AVMPLUS` marker).

## Structure

- Header `FLV`, version, flags (audio 4, video 1), DataOffset, then `PreviousTagSize0` and tags: 11-byte header (type 8 audio, 9 video, 18 script data; 24-bit size;
  24+8-bit timestamp in ms; stream id) and body, each followed by a 32-bit previous-tag size. A wrong previous-tag size is an informational defect;
  a tag cut off by the end of the file ends reading with a defect. Tags with the filter bit (encrypted) are skipped.
- Tracks come from the first tags: at most 256 tags or 8 MiB are read until every stream the header announces has its configuration. A seekable source
  then goes back to the first tag; a forward-only source delivers the tags already read first.
- Script data: AMF0 `onMetaData` (and any other leading script tag) is read losslessly as tags of system `flv`. Nested objects and ECMA arrays are flattened
  as `parent/child`; arrays are listed up to 64 elements plus a container tag. Strings are text, numbers are real or integer numbers, booleans are numbers, dates are text.

## Codecs and packets

- Video codec ids 2 (`flv1`), 3 (`flashsv`), 4 (`vp6f`), 5 (`vp6a`), 6 (`flashsv2`), 7 (H.264, `avcC`), 12 (HEVC, `hvcC`); the Enhanced RTMP form (bit 7 of the
  first byte) with FourCCs `avc1`, `hvc1`, `av01` (`av1C`), `vp09` (`vpcC`), `vp08`, `vvc1` (`vvcC`). A sequence-start packet becomes the track's configuration
  and is not a packet. A changed configuration later is a defect and the track keeps the first one.
- Audio: PCM (0, 3), ADPCM (1), MP3 (2, 14), Nellymoser (4, 5, 6), G.711 (7, 8), AAC (10; the AudioSpecificConfig becomes the configuration), Speex (11),
  and the Enhanced FourCCs `Opus`, `fLaC`, `ac-3`, `ec-3`, `.mp3`, `mp4a`. The sample rate and channels come from the tag flags, the AudioSpecificConfig or the first MP3 frame header.
- A packet is the tag body after its own headers: for H.264/HEVC the length-prefixed NAL units as in an MP4 sample, for AAC the raw frame. The source
  range is that payload.
- Timestamps are milliseconds. dts = the tag timestamp; pts = dts + the composition time offset (24-bit signed) of AVC/HEVC/VVC packets. Keyframe = frame
  type 1; frame type 3 (disposable) sets `Disposable`; frame type 5 (command) is not a packet. A tag has no duration: durations are unknown.
- `Duration`: the `duration` property of `onMetaData` when positive; otherwise, for a seekable source, the timestamp of the last tag, found from the last four
  bytes (the previous-tag size) with two small reads.
- Seek: to the last keyframe (video) or tag (audio only) at or before the target. The table is built once by walking the tag headers of the whole file
  (one 11-byte read per tag), because the `keyframes` index of `onMetaData` is optional and unreliable; this is the one scan, and only on the first seek.

## Differences from `ffprobe -show_packets` (7.0.2)

- **`pos`:** ffprobe prints the tag's offset; ours is the payload range.
- **`duration`:** a tag has no duration; ffprobe fills one in from the frame rate or the audio frame length, in ms; ours is unknown.

## Agreement with other readers

Checked on 30 files (ffmpeg-written, with H.264/HEVC/AV1/VP9/FLV1 video and AAC/MP3/ADPCM/Nellymoser/PCM/Speex audio; one of 3.6 GB): ffprobe 7.0.2
lists 217,868 packets and so do we, with no difference after the `pos` and `duration` rules above; `infocheck` has no difference. ExifTool 13.60 and
MediaInfo 26.10 agree on every field except: the `stereo` flag of `onMetaData` (we list the stored value 0; ExifTool leaves a false flag out), and the
duration of a file whose `onMetaData` has none (3.967 s from the last tag; MediaInfo adds the last frame's length: 4.084 s).

# Native FLAC (RFC 9639)

`RecursiveAI.OneMedia.Flac`: `FlacDemuxer` (seekable and forward-only sources), `FlacFormat` (registry; the asynchronous open runs the reader on a pool
thread over a ranged source, reading in 64 KiB windows), `FlacMetadataBlock`, `FlacSeekPoint`, `FlacBlockTags`.

## Structure

- Optional ID3v2 tag, the marker `fLaC`, metadata blocks (4-byte header: last flag, 7-bit type, 24-bit length), then frames (RFC 9639 §8, §9).
  An ID3v2 tag in front is skipped and exposed as the raw binary tag `id3:ID3v2`, and its frames are read by `Tags.Id3` as `id3v2` native tags beside it; sniffing looks through it. The Vorbis comment block comes first in the common view, so the ID3 tag only fills fields it leaves empty.
- Every block is listed with its range in `MetadataBlocks`; `PaddingBlocks` lists PADDING so that in-place edits can use it. STREAMINFO must come first and
  be 34 bytes (`MediaFormatException` otherwise). A second VORBIS_COMMENT block is ignored with a defect. Block type 127 is reported. SEEKTABLE
  points are read (placeholders skipped; non-ascending points reported). PICTURE blocks are tags (type, MIME, description, size; the image is read on
  access). APPLICATION, CUESHEET and unknown block types are binary tags with their ranges. VORBIS_COMMENT, PICTURE and SEEKTABLE bodies above
  `MaxTagBytes` are not read (`flac.block-too-large`).
- With a forward-only or ranged source the metadata area is kept in memory (up to `MaxAllocationBytes`) so tag values can be read later.

## Frames and time

- A frame starts at a sync code (`0xFFF8`/`0xFFF9`) whose header parses and passes its CRC-8 and whose channel count and sample rate agree with STREAMINFO
  (RFC 9639 §9.1). It ends at the next such header that either follows a matching CRC-16 (§9.3) or carries the sample number that is expected; a frame
  whose CRC-16 does not match is delivered with `PacketAttributes.Corrupt` and a defect (`flac.frame-crc`). Bytes between frames and at the end are `flac.junk`/`flac.trailing`.
- Timescale is the sample rate; pts = first sample of the frame (frame number × block size for fixed block-size streams, the coded sample number for variable
  ones), duration = block size. Every frame is a keyframe. A jump in the sample numbers is an informational defect (`flac.sample-gap`).
- The track's configuration is `fLaC` + the STREAMINFO block with the last flag set (the Matroska CodecPrivate form).
- Duration is STREAMINFO's total samples. When that is 0 (a file written to a pipe) and the source is seekable, the last frame in the last 64 KiB gives it
  (a documented tail read).
- Seek uses the SEEKTABLE where there is one and otherwise bisects on frame headers (4 KiB probes). `NextKeyframe` lands on the frame after the target.

## Differences from `ffprobe -show_packets` (7.0.2)

- **`unknown-length.flac`:** ffprobe has no duration; ours is read from the last frame.
- **Cover art:** ffprobe lists an attached picture as a second (video) stream with one packet; the picture is a tag here and not a track.

## Duration of a stream without a length

STREAMINFO may hold 0 as the number of samples; the length is then read from the last frame found in the final 64 KiB (ours 4.000 s for the fixture,
ffprobe prints none). A PICTURE block is a tag with its byte range; ffprobe makes it a second (video) stream with one packet, which is why `count` is excepted for those fixtures.

## Gap: decoding through OneAudio's file opener

`AudioDecoder.OpenFile` picks a reader from the first 512 bytes. A FLAC file that starts with an ID3v2 block longer than that (the `all-blocks` fixture has 600 bytes) shows no `fLaC` in that window, so the opener tries another reader and fails. (OneMedia's own `Id3AudioFormat` looks behind the tag itself and hands such a FLAC or Ogg file to its reader on a seekable source.) `Media.Open(...)` followed by `Decode()` does not have the problem: the file's own demuxer has already skipped the ID3 block, and the decoder reads its packets. The behaviour is covered by a test (`FlacBehindALargeId3BlockDecodesThroughMediaFile…`).

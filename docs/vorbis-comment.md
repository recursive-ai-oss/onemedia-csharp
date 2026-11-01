# Vorbis comments, FLAC PICTURE blocks and `METADATA_BLOCK_PICTURE`

`RecursiveAI.OneMedia.Tags.VorbisComment`: `VorbisCommentReader`, `VorbisCommentBlock` (an `ITagContainer`, system `vorbis`), `PictureBlockHeader`.

- **Layout** (Vorbis comment specification §Structure): vendor string (32-bit little-endian length, UTF-8), a 32-bit count, then that many
  `length + "NAME=value"` strings. Ogg Vorbis and the Theora, Speex comment headers end with a framing bit; Opus and FLAC do not. Names are 0x20–0x7D without `=`
  and case-insensitive; values are UTF-8 (invalid sequences are replaced and reported). The vendor string is `NativeTag` `vendor` and `VorbisCommentBlock.Vendor`.
  A length past the end of the block, a count past the available bytes and a name without `=` are typed defects (`vorbis.truncated`, `vorbis.count`, `vorbis.no-equals`);
  the comments read so far are kept.
- **Ranges.** Each tag has the range of its `length + string` item and of its value. In an Ogg stream the offsets are relative to the start of the comment packet,
  because a packet can span pages; the container's `Range` is the physical page span. In native FLAC the offsets are file offsets.
- **Pictures.** `METADATA_BLOCK_PICTURE` holds a base64 FLAC PICTURE block (RFC 9639 §8.8): picture type (32 bits), MIME, description, width, height, depth, colours,
  data. Each becomes an image tag (type, MIME, description) whose value is decoded when read. A FLAC `PICTURE` block gives the same tag directly.
  Cover pictures reach `CommonTags.Pictures` with their `PictureType`.
- **Common view.** `TagMappingTable` rows for the Vorbis names (`TITLE`, `ARTIST`/`PERFORMER`, `ALBUM`, `ALBUMARTIST`, `COMPOSER`, `GENRE`, `DATE`, `TRACKNUMBER`,
  `TRACKTOTAL`/`TOTALTRACKS`, `DISCNUMBER`, `COMMENT`, `DESCRIPTION`, `COPYRIGHT`, `LYRICS`, `ENCODER`, …), matched case-insensitively. `TRACKNUMBER=3/12` gives track 3
  and total 12. `REPLAYGAIN_*` and `R128_*` stay native. Facts: `DATE` is a capture-time candidate only as a day tag (a year alone has no day) and `ENCODER` a software candidate.
- Sources: Xiph Vorbis comment specification, RFC 9639, the Picard tag map (CC0) as a table of names.

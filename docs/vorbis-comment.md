# Vorbis comments, FLAC PICTURE blocks and `METADATA_BLOCK_PICTURE`

`RecursiveAI.OneMedia.Tags.VorbisComment`: `VorbisCommentReader`, `VorbisCommentBlock` (an `ITagContainer`, system `vorbis`), `PictureBlockHeader`.

- **Layout** (Vorbis comment specification §Structure): vendor string (32-bit little-endian length, UTF-8), a 32-bit count, then that many
  `length + "NAME=value"` strings. Ogg Vorbis and the Theora, Speex comment headers end with a framing bit; Opus and FLAC do not. Names are 0x20–0x7D without `=`
  and case-insensitive; values are UTF-8 (invalid sequences are replaced and reported). The vendor string is `NativeTag` `vendor` and `VorbisCommentBlock.Vendor`.
  A length past the end of the block, a count past the available bytes and a name without `=` are typed defects (`vorbis.truncated`, `vorbis.vendor-size`, `vorbis.field-size`, `vorbis.count`, `vorbis.no-equals`, `vorbis.name`, `vorbis.trailing`; pictures: `vorbis.picture`, `vorbis.picture-base64`, `vorbis.picture-too-large`);
  the comments read so far are kept.
- **Ranges.** Each tag has the range of its `length + string` item and of its value. In an Ogg stream the offsets are relative to the start of the comment packet,
  because a packet can span pages; the container's `Range` is the physical page span. In native FLAC the offsets are file offsets.
- **Pictures.** `METADATA_BLOCK_PICTURE` holds a base64 FLAC PICTURE block (RFC 9639 §8.8): picture type (32 bits), MIME, description, width, height, depth, colours,
  data. Each becomes an image tag (type, MIME, description) whose value is decoded when read. A FLAC `PICTURE` block gives the same tag directly.
  Cover pictures reach `CommonTags.Pictures` with their `PictureType`.
- **Common view.** `TagMappingTable` rows for the Vorbis names (`TITLE`, `ARTIST`/`PERFORMER`, `ALBUM`, `ALBUMARTIST`, `COMPOSER`, `GENRE`, `DATE`, `TRACKNUMBER`,
  `TRACKTOTAL`/`TOTALTRACKS`, `DISCNUMBER`, `COMMENT`, `DESCRIPTION`, `COPYRIGHT`, `LYRICS`, `ENCODER`, …), matched case-insensitively. `TRACKNUMBER=3/12` gives track 3
  and total 12. `REPLAYGAIN_*` and `R128_*` stay native. Facts: `DATE` is a capture-time candidate (a year or an ISO 8601 date, at the rank of a day-level tag) and `ENCODER` a software candidate.
- Sources: Xiph Vorbis comment specification, RFC 9639, the Picard tag map (CC0) as a table of names.

## Agreement with other readers

Checked on 72 Ogg/Opus/FLAC/Ogg Theora files (including the tag variants written by ffmpeg): common view 141 fields equal and
3 partly equal against ExifTool 13.60 and MediaInfo 26.10, native tags 161 equal. The differences, all explained:

- **Duration.** ExifTool estimates a Vorbis duration from the nominal bitrate (0.8–6.9 s for 4–596 s files) and gives none for Opus; MediaInfo counts an
  Opus stream from the granule, so it includes the pre-skip (4.007 s against our 4.000 s, which follows RFC 7845 §5.1).
- **Comments of a Theora stream.** `theora-*.ogv` carry a comment header on the video stream; neither tool reports it, we do (`vorbis:encoder`).
- **ATL.NET 7.19** (`atl` command): same on 121 fields. ATL.NET fills a missing title with the file name (the "ours-empty" lines of the first run are those titles); it does not read `PERFORMER`
  as the artist (we do, as the Vorbis comment spec names it the performing artist); it trims values and ours keep the stored text (compared trimmed).

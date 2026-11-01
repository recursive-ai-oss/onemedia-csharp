# Tag mapping

How the common tag view picks values from the native tag systems. The table is the one the code reads (`TagMappingTable.Rows`); this page is
generated from it, and a test fails when they differ.

- Rows of one field are tried in table order, and within a row the keys in the order listed. The first value is the field's value;
  the others stay reachable through `CommonTags.Values`.
- Native keys are spelled as the system spells them: four-character atom codes for `ilst` and `udta` (`©nam`), reverse-DNS names for
  key-table metadata (`com.apple.quicktime.title`), `namespace#property` for XMP, upper-case names for Matroska (nested SimpleTags as
  `PARENT/CHILD`). Matroska names are matched case-insensitively.
- Matroska: only tags that target the file (no track, edition, chapter or attachment UID) feed the view, plus the tags of the only
  track of a single-track file (except `ENCODER`, which names the stream's encoder). Within a key the lowest target level of 30 or above
  comes first, then levels below 30; tags with no language or `und` come before language-specific ones; tags of the file itself
  come before tags of the only track.
- 3GPP boxes and QuickTime user-data atoms share four-character codes with different layouts; the reader tells them apart as described in `docs/tags.md`.
- Sources: the MusicBrainz Picard tag-mapping table (CC0), the ExifTool QuickTime tag documentation (read as tables of facts), the Matroska
  tag-name list, Apple's QuickTime File Format specification and 3GPP TS 26.244 §8.

| Field | System | Keys (in precedence order) | Document | Note |
|---|---|---|---|---|
| Title | ilst | `©nam` | Picard tag map, iTunes MP4 |  |
| Title | mdta | `com.apple.quicktime.title`, `com.apple.quicktime.displayname` | ExifTool QuickTime Keys tags |  |
| Title | udta | `©nam` | ExifTool QuickTime UserData tags |  |
| Title | 3gpp | `titl` | 3GPP TS 26.244 §8 |  |
| Artist | ilst | `©ART` | Picard tag map, iTunes MP4 |  |
| Artist | mdta | `com.apple.quicktime.artist` | ExifTool QuickTime Keys tags |  |
| Artist | udta | `©ART`, `©prf` | ExifTool QuickTime UserData tags | ©prf is QuickTime's performer |
| Artist | 3gpp | `perf` | 3GPP TS 26.244 §8 | performer |
| AlbumArtist | ilst | `aART` | Picard tag map, iTunes MP4 |  |
| Album | ilst | `©alb` | Picard tag map, iTunes MP4 |  |
| Album | mdta | `com.apple.quicktime.album` | ExifTool QuickTime Keys tags |  |
| Album | udta | `©alb` | ExifTool QuickTime UserData tags |  |
| Album | 3gpp | `albm` | 3GPP TS 26.244 §8 | album title; the optional track number feeds TrackNumber |
| Composer | ilst | `©wrt` | Picard tag map, iTunes MP4 |  |
| Composer | udta | `©wrt`, `©com` | ExifTool QuickTime UserData tags | ©com is QuickTime's composer |
| Author | mdta | `com.apple.quicktime.author` | ExifTool QuickTime Keys tags |  |
| Author | udta | `©aut` | ExifTool QuickTime UserData tags |  |
| Author | 3gpp | `auth` | 3GPP TS 26.244 §8 | author |
| Director | ilst | `©dir` | Picard tag map, iTunes MP4 |  |
| Director | mdta | `com.apple.quicktime.director` | ExifTool QuickTime Keys tags |  |
| Director | udta | `©dir` | ExifTool QuickTime UserData tags |  |
| Genre | ilst | `©gen`, `gnre` | Picard tag map, iTunes MP4 | gnre holds the ID3v1 genre number plus one |
| Genre | mdta | `com.apple.quicktime.genre` | ExifTool QuickTime Keys tags |  |
| Genre | udta | `©gen` | ExifTool QuickTime UserData tags |  |
| Genre | 3gpp | `gnre` | 3GPP TS 26.244 §8 |  |
| Comment | ilst | `©cmt` | Picard tag map, iTunes MP4 |  |
| Comment | mdta | `com.apple.quicktime.comment` | ExifTool QuickTime Keys tags |  |
| Comment | udta | `©cmt` | ExifTool QuickTime UserData tags |  |
| Description | ilst | `desc`, `ldes` | ExifTool QuickTime UserData tags | desc is the short description, ldes the long one |
| Description | mdta | `com.apple.quicktime.description` | ExifTool QuickTime Keys tags |  |
| Description | udta | `©inf` | ExifTool QuickTime UserData tags | ©inf is QuickTime's information text |
| Description | 3gpp | `dscp` | 3GPP TS 26.244 §8 |  |
| Date | ilst | `©day` | Picard tag map, iTunes MP4 | stored as written, usually a year or an ISO 8601 date |
| Date | mdta | `com.apple.quicktime.year` | ExifTool QuickTime Keys tags |  |
| Date | udta | `©day` | ExifTool QuickTime UserData tags |  |
| Date | 3gpp | `yrrc` | 3GPP TS 26.244 §8 | recording year |
| Copyright | ilst | `cprt` | Picard tag map, iTunes MP4 |  |
| Copyright | mdta | `com.apple.quicktime.copyright` | ExifTool QuickTime Keys tags |  |
| Copyright | udta | `©cpy`, `cprt` | ExifTool QuickTime UserData tags |  |
| Copyright | 3gpp | `cprt` | 3GPP TS 26.244 §8 |  |
| Encoder | ilst | `©too` | ExifTool QuickTime UserData tags | Picard files ©too under Encoded By; the atom names the writing tool, so it is the encoder here |
| Encoder | mdta | `encoder`, `com.apple.quicktime.encoder` | ExifTool QuickTime Keys tags | ffmpeg writes the bare key encoder |
| Encoder | udta | `©too` | ExifTool QuickTime UserData tags |  |
| EncodedBy | udta | `©enc` | ExifTool QuickTime UserData tags |  |
| Grouping | ilst | `©grp` | Picard tag map, iTunes MP4 |  |
| Lyrics | ilst | `©lyr` | Picard tag map, iTunes MP4 |  |
| Rating | mdta | `com.apple.quicktime.rating.user` | ExifTool QuickTime Keys tags | user rating as a number |
| Rating | 3gpp | `rtng` | 3GPP TS 26.244 §8 | rating text |
| Keywords | mdta | `com.apple.quicktime.keywords` | ExifTool QuickTime Keys tags |  |
| Keywords | udta | `©key` | ExifTool QuickTime UserData tags |  |
| Keywords | 3gpp | `kywd` | 3GPP TS 26.244 §8 | one value per keyword |
| TrackNumber | ilst | `trkn` | Picard tag map, iTunes MP4 | trkn: 16-bit number at byte 2 |
| TrackNumber | 3gpp | `albm:track` | 3GPP TS 26.244 §8 | optional last byte of albm |
| TrackTotal | ilst | `trkn` | Picard tag map, iTunes MP4 | trkn: 16-bit total at byte 4 |
| DiscNumber | ilst | `disk` | Picard tag map, iTunes MP4 | disk: 16-bit number at byte 2 |
| DiscTotal | ilst | `disk` | Picard tag map, iTunes MP4 | disk: 16-bit total at byte 4 |
| Title | xmp | `http://purl.org/dc/elements/1.1/#title` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) | language alternative: the x-default item |
| Artist | xmp | `http://purl.org/dc/elements/1.1/#creator` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) | first creator |
| Description | xmp | `http://purl.org/dc/elements/1.1/#description` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) |  |
| Copyright | xmp | `http://purl.org/dc/elements/1.1/#rights` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) |  |
| Keywords | xmp | `http://purl.org/dc/elements/1.1/#subject` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) |  |
| Date | xmp | `http://ns.adobe.com/xap/1.0/#CreateDate` | XMP specification part 1 and 2 (Dublin Core and XMP basic properties) |  |
| Title | mkv | `TITLE` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | lowest level of 30 or above first (track before album) |
| Title | mkv-info | `Title` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | Segment Info Title, after the tags |
| Artist | mkv | `ARTIST`, `LEAD_PERFORMER` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| AlbumArtist | mkv | `ALBUM_ARTIST` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | not in the tag list; ffmpeg and others write it |
| Album | mkv | `ALBUM` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | not in the tag list (an album title is TITLE at level 50); ffmpeg and others write ALBUM |
| Composer | mkv | `COMPOSER` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Author | mkv | `WRITTEN_BY` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Director | mkv | `DIRECTOR` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Genre | mkv | `GENRE` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Comment | mkv | `COMMENT` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Description | mkv | `DESCRIPTION`, `SUMMARY`, `SYNOPSIS` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Date | mkv | `DATE_RELEASED`, `DATE_RECORDED`, `DATE` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | DATE is written by ffmpeg and others |
| Copyright | mkv | `COPYRIGHT` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Encoder | mkv | `ENCODER` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | Info WritingApp last |
| Encoder | mkv-info | `WritingApp` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| EncodedBy | mkv | `ENCODED_BY` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Lyrics | mkv | `LYRICS` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Rating | mkv | `RATING` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| Keywords | mkv | `KEYWORDS` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 |  |
| TrackNumber | mkv | `PART_NUMBER`, `TRACK` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | part number at the lowest level of 30 or above; ffmpeg writes TRACK, optionally as number/total |
| DiscNumber | mkv | `DISC` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | ffmpeg writes DISC, optionally as number/total |
| DiscTotal | mkv | `DISC` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | the part after the slash of DISC |
| TrackTotal | mkv | `TOTAL_PARTS`, `TRACK` | Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8 | the part after the slash of TRACK when TOTAL_PARTS is absent |
| Title | riff-info | `INAM` | ExifTool RIFF Info tags |  |
| Artist | riff-info | `IART` | ExifTool RIFF Info tags |  |
| Album | riff-info | `IPRD` | ExifTool RIFF Info tags | IPRD is the product, which music files use for the album |
| Composer | riff-info | `IMUS` | ExifTool RIFF Info tags | IMUS is Music By |
| Author | riff-info | `IWRI` | ExifTool RIFF Info tags | IWRI is Written By |
| Genre | riff-info | `IGNR`, `GENR` | ExifTool RIFF Info tags |  |
| Comment | riff-info | `ICMT`, `CMNT`, `COMM` | ExifTool RIFF Info tags |  |
| Description | riff-info | `ISBJ` | ExifTool RIFF Info tags | ISBJ is the subject |
| Description | bext | `Description` | EBU Tech 3285 (BWF) §2 | the free description of the recording |
| Date | riff-info | `ICRD` | ExifTool RIFF Info tags | as written, usually a year or an ISO 8601 date |
| Date | bext | `OriginationDate` | EBU Tech 3285 (BWF) §2 | the origination date; the time of day is the OriginationTime tag |
| Copyright | riff-info | `ICOP` | ExifTool RIFF Info tags |  |
| Encoder | riff-info | `ISFT` | ExifTool RIFF Info tags | the software that wrote the file |
| EncodedBy | riff-info | `ITCH`, `IENC`, `CODE` | ExifTool RIFF Info tags | ITCH is the technician |
| Keywords | riff-info | `IKEY` | ExifTool RIFF Info tags |  |
| TrackNumber | riff-info | `ITRK`, `IPRT` | ExifTool RIFF Info tags | ITRK, optionally as number/total |
| TrackTotal | riff-info | `ITRK` | ExifTool RIFF Info tags | the part after the slash of ITRK |
| Title | aiff | `NAME` | ExifTool AIFF tags |  |
| Artist | aiff | `AUTH` | ExifTool AIFF tags | AUTH names the author; players show it as the artist |
| Author | aiff | `AUTH` | ExifTool AIFF tags |  |
| Copyright | aiff | `(c) ` | ExifTool AIFF tags |  |
| Comment | aiff | `ANNO`, `COMT` | ExifTool AIFF tags | ANNO, then the text of the first COMT comment |
| Title | caf-info | `title` | Apple Core Audio Format Specification, Information chunk |  |
| Artist | caf-info | `artist` | Apple Core Audio Format Specification, Information chunk |  |
| Album | caf-info | `album` | Apple Core Audio Format Specification, Information chunk |  |
| Composer | caf-info | `composer` | Apple Core Audio Format Specification, Information chunk |  |
| Genre | caf-info | `genre` | Apple Core Audio Format Specification, Information chunk |  |
| Comment | caf-info | `comments` | Apple Core Audio Format Specification, Information chunk |  |
| Date | caf-info | `recorded date`, `year` | Apple Core Audio Format Specification, Information chunk | the recording date, else the year |
| Copyright | caf-info | `copyright` | Apple Core Audio Format Specification, Information chunk |  |
| Encoder | caf-info | `encoding application` | Apple Core Audio Format Specification, Information chunk |  |
| TrackNumber | caf-info | `track number` | Apple Core Audio Format Specification, Information chunk | the track number, optionally as number/total |
| TrackTotal | caf-info | `track number` | Apple Core Audio Format Specification, Information chunk | the part after the slash of the track number |

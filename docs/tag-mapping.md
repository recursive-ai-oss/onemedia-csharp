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
- Vorbis comments (Ogg Vorbis, Opus, FLAC, Theora, Speex) use upper-case field names matched case-insensitively; `TRACKNUMBER` and `DISCNUMBER` may read
  `number/total` (see `docs/vorbis-comment.md`). FLV script properties (`onMetaData`) are matched case-insensitively by name.
- Sources: the MusicBrainz Picard tag-mapping table (CC0), the ExifTool QuickTime and FLV tag documentation (read as tables of facts), the Matroska
  tag-name list, the Vorbis comment specification, Apple's QuickTime File Format specification and 3GPP TS 26.244 §8.

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
| Title | asf | `Title` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | Content Description title |
| Artist | asf | `Author`, `WM/Author` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | Content Description author; WM/Author is the same attribute in the extended description |
| AlbumArtist | asf | `WM/AlbumArtist` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Album | asf | `WM/AlbumTitle` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Composer | asf | `WM/Composer` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Director | asf | `WM/Director` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Genre | asf | `WM/Genre` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Description | asf | `Description` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | Content Description description; ExifTool and MediaInfo also call it Description |
| Date | asf | `WM/Year` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | WM/Year holds a year or a date as the writer typed it |
| Copyright | asf | `Copyright` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | Content Description copyright |
| Encoder | asf | `WM/ToolName`, `WM/EncodingSettings` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| EncodedBy | asf | `WM/EncodedBy` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Grouping | asf | `WM/ContentGroupDescription` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Lyrics | asf | `WM/Lyrics` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media |  |
| Rating | asf | `WM/SharedUserRating`, `Rating` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | WM/SharedUserRating is a number from 0 to 99; Rating is the Content Description text |
| TrackNumber | asf | `WM/TrackNumber` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | WM/TrackNumber, optionally written as number/total |
| TrackTotal | asf | `WM/TrackNumber` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | the part after the slash of WM/TrackNumber |
| DiscNumber | asf | `WM/PartOfSet` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | WM/PartOfSet, optionally written as number/total |
| DiscTotal | asf | `WM/PartOfSet` | ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media | the part after the slash of WM/PartOfSet |
| Title | mpegts | `event_name`, `service_name` | ETSI EN 300 468 §6.2.33 (service descriptor) and §6.2.37 (short event descriptor) | the present event's name first, then the service name |
| Description | mpegts | `event_text` | ETSI EN 300 468 §6.2.33 (service descriptor) and §6.2.37 (short event descriptor) | the present event's short text |
| Date | mdpm | `DateTimeOriginal` | ExifTool H264 tags (MDPM) | the camera clock in AVCHD; local time of unknown zone |
| Title | id3v2 | `TIT2`, `TT2` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Artist | id3v2 | `TPE1`, `TP1` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| AlbumArtist | id3v2 | `TPE2`, `TP2` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Album | id3v2 | `TALB`, `TAL` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Composer | id3v2 | `TCOM`, `TCM` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Genre | id3v2 | `TCON`, `TCO` | Picard tag map, ID3v2.3/2.4 column (CC0) | content type: (17) references, numbers and RX/CR are resolved to names (ID3v2.3 §4.2.1) |
| Comment | id3v2 | `COMM`, `COM` | Picard tag map, ID3v2.3/2.4 column (CC0) | the comment with an empty description; other descriptions stay on the native list |
| Date | id3v2 | `TDRC`, `TYER`, `TYE` | Picard tag map, ID3v2.3/2.4 column (CC0) | 2.4 recording time; 2.3 and 2.2 hold only the year here (the day and month are in TDAT) |
| Copyright | id3v2 | `TCOP`, `TCR` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Encoder | id3v2 | `TSSE`, `TSS` | ExifTool ID3 tags | TSSE is the software and settings used for encoding |
| EncodedBy | id3v2 | `TENC`, `TEN` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Grouping | id3v2 | `GRP1`, `GP1`, `TIT1`, `TT1` | Picard tag map, ID3v2.3/2.4 column (CC0) | iTunes writes GRP1; TIT1 is the content group description |
| Lyrics | id3v2 | `USLT`, `ULT` | Picard tag map, ID3v2.3/2.4 column (CC0) | unsynchronised lyrics |
| TrackNumber | id3v2 | `TRCK`, `TRK` | Picard tag map, ID3v2.3/2.4 column (CC0) | the part before the slash of TRCK |
| TrackTotal | id3v2 | `TRCK`, `TRK` | Picard tag map, ID3v2.3/2.4 column (CC0) | the part after the slash of TRCK |
| DiscNumber | id3v2 | `TPOS`, `TPA` | Picard tag map, ID3v2.3/2.4 column (CC0) | the part before the slash of TPOS |
| DiscTotal | id3v2 | `TPOS`, `TPA` | Picard tag map, ID3v2.3/2.4 column (CC0) | the part after the slash of TPOS |
| ReplayGainTrackGain | id3v2 | `TXXX:REPLAYGAIN_TRACK_GAIN`, `TXX:REPLAYGAIN_TRACK_GAIN` | Picard tag map, ID3v2.3/2.4 column (CC0) | user text frames; the description is compared without regard to case |
| ReplayGainTrackPeak | id3v2 | `TXXX:REPLAYGAIN_TRACK_PEAK`, `TXX:REPLAYGAIN_TRACK_PEAK` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| ReplayGainAlbumGain | id3v2 | `TXXX:REPLAYGAIN_ALBUM_GAIN`, `TXX:REPLAYGAIN_ALBUM_GAIN` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| ReplayGainAlbumPeak | id3v2 | `TXXX:REPLAYGAIN_ALBUM_PEAK`, `TXX:REPLAYGAIN_ALBUM_PEAK` | Picard tag map, ID3v2.3/2.4 column (CC0) |  |
| Title | ape | `Title` | Picard tag map, APEv2 column (CC0) |  |
| Artist | ape | `Artist` | Picard tag map, APEv2 column (CC0) |  |
| AlbumArtist | ape | `Album Artist`, `AlbumArtist` | Picard tag map, APEv2 column (CC0) |  |
| Album | ape | `Album` | Picard tag map, APEv2 column (CC0) |  |
| Composer | ape | `Composer` | Picard tag map, APEv2 column (CC0) |  |
| Genre | ape | `Genre` | Picard tag map, APEv2 column (CC0) |  |
| Comment | ape | `Comment` | Picard tag map, APEv2 column (CC0) |  |
| Date | ape | `Year`, `Date` | Picard tag map, APEv2 column (CC0) |  |
| Copyright | ape | `Copyright` | Picard tag map, APEv2 column (CC0) |  |
| Grouping | ape | `Grouping` | Picard tag map, APEv2 column (CC0) |  |
| Lyrics | ape | `Lyrics`, `Unsynced Lyrics` | Picard tag map, APEv2 column (CC0) |  |
| EncodedBy | ape | `EncodedBy` | Picard tag map, APEv2 column (CC0) |  |
| TrackNumber | ape | `Track` | Picard tag map, APEv2 column (CC0) | the part before the slash |
| TrackTotal | ape | `Track` | Picard tag map, APEv2 column (CC0) | the part after the slash |
| DiscNumber | ape | `Disc` | Picard tag map, APEv2 column (CC0) | the part before the slash |
| DiscTotal | ape | `Disc` | Picard tag map, APEv2 column (CC0) | the part after the slash |
| ReplayGainTrackGain | ape | `REPLAYGAIN_TRACK_GAIN` | Picard tag map, APEv2 column (CC0) |  |
| ReplayGainTrackPeak | ape | `REPLAYGAIN_TRACK_PEAK` | Picard tag map, APEv2 column (CC0) |  |
| ReplayGainAlbumGain | ape | `REPLAYGAIN_ALBUM_GAIN` | Picard tag map, APEv2 column (CC0) |  |
| ReplayGainAlbumPeak | ape | `REPLAYGAIN_ALBUM_PEAK` | Picard tag map, APEv2 column (CC0) |  |
| Title | id3v1 | `title` | ID3v1 and v1.1 (id3.org) |  |
| Artist | id3v1 | `artist` | ID3v1 and v1.1 (id3.org) |  |
| Album | id3v1 | `album` | ID3v1 and v1.1 (id3.org) |  |
| Comment | id3v1 | `comment` | ID3v1 and v1.1 (id3.org) |  |
| Date | id3v1 | `year` | ID3v1 and v1.1 (id3.org) | the year |
| Genre | id3v1 | `genre` | ID3v1 and v1.1 (id3.org) | the genre number, resolved through the ID3v1 list with the Winamp extensions |
| TrackNumber | id3v1 | `track` | ID3v1 and v1.1 (id3.org) | ID3v1.1 only |
| Title | lyrics3 | `ETT` | Lyrics3 v2.00 (id3.org) | extended title |
| Artist | lyrics3 | `EAR` | Lyrics3 v2.00 (id3.org) | extended artist |
| Album | lyrics3 | `EAL` | Lyrics3 v2.00 (id3.org) | extended album |
| Lyrics | lyrics3 | `LYR`, `LYRICS` | Lyrics3 v2.00 (id3.org) | version 2.00 field LYR; the whole block in version 1 |
| Title | vorbis | `TITLE` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Artist | vorbis | `ARTIST`, `PERFORMER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | PERFORMER is the spec's name for the performing artist |
| AlbumArtist | vorbis | `ALBUMARTIST`, `ALBUM ARTIST` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Album | vorbis | `ALBUM` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Composer | vorbis | `COMPOSER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Author | vorbis | `LYRICIST` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | lyricist |
| Director | vorbis | `DIRECTOR` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Genre | vorbis | `GENRE` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Comment | vorbis | `COMMENT` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Description | vorbis | `DESCRIPTION` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | the spec's short description of the contents |
| Date | vorbis | `DATE` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | a year or an ISO 8601 date, as written |
| Copyright | vorbis | `COPYRIGHT` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Encoder | vorbis | `ENCODER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| EncodedBy | vorbis | `ENCODEDBY`, `ENCODED-BY` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Grouping | vorbis | `GROUPING` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Lyrics | vorbis | `LYRICS`, `UNSYNCEDLYRICS` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| Rating | vorbis | `RATING` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments |  |
| TrackNumber | vorbis | `TRACKNUMBER`, `TRACK` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | the spec's TRACKNUMBER may read number/total |
| TrackTotal | vorbis | `TRACKTOTAL`, `TOTALTRACKS`, `TRACKNUMBER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | TRACKTOTAL, TOTALTRACKS, or the part after the slash of TRACKNUMBER |
| DiscNumber | vorbis | `DISCNUMBER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | may read number/total |
| DiscTotal | vorbis | `DISCTOTAL`, `TOTALDISCS`, `DISCNUMBER` | Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments | DISCTOTAL, TOTALDISCS, or the part after the slash of DISCNUMBER |
| Title | flv | `title` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Artist | flv | `artist` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Author | flv | `author` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Album | flv | `album` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Genre | flv | `genre` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Comment | flv | `comment` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Description | flv | `description` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Copyright | flv | `copyright` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E |  |
| Date | flv | `creationdate`, `date` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E | as written by the muxer |
| Encoder | flv | `encoder`, `metadatacreator` | ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E | the muxer's name and version |

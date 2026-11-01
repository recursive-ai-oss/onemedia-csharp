# Tag writing

Normative description of how OneMedia changes tags. Code: `RecursiveAI.OneMedia.Tags` (`TagEdits`, `TagWriter`, `TagWriteOptions`, `TagWriteReport`), staged edits on `CommonTags`,
`MediaFile.Save`/`SaveAs`/`SaveInPlace`, and `onemedia tags`. Byte-level behaviour (plans, padding, atomic save) is in `writer.md`.

## Model

- An edit is expressed on the common view (`TagEdits.Set/Remove/SetPicture/RemovePictures`) or on one native key (`SetNative/RemoveNative`). `TagWriter.Plan` reads the file's
  own tag layers and returns an `EditPlan` plus a `TagWriteReport`. Untouched frames, items, blocks and chunks are copied byte for byte; media payload is never read into memory.
- Edits go to **every system the file has**; `TagWriteOptions.Systems` restricts them to named systems and creates those that are absent. A file with no tag system gets its format's
  main one (iTunes item list, Matroska `Tags`, ID3v2, Vorbis comment, `LIST INFO`, AIFF text chunks).
- Happy path: `media.Tags.Title = "x"; media.Save();` (atomic replace), `SaveAs(path)`, `SaveInPlace()`. Setters stage edits in `Tags.Pending`; reads of a staged field return the staged
  value. The open file is stale after a save.
- `onemedia tags <file> [--set field=value] [--remove field] [--cover image] [--system name] [--out file | --replace | --in-place]`; without an output option it prints the plan.

## Write-back report

`TagWriteReport.Systems` has one entry per system written: fields written, fields removed, fields **not representable** (with the reason), conversions applied (cut to 30 bytes,
date reduced to a year, roles not stored, several values joined), and whether the system was created. `Notes` carries file-wide remarks (an ID3v2.2 tag upgraded, a second ID3v2 tag edited too).

## Key written per field

Generated from the mapping table (`tag-mapping.md`): the first key of the field's row is the one written; removal deletes every key of the row except the field's pair partner's.

| Field | ilst | mdta | udta | mkv | id3v2 | ape | vorbis | riff-info | aiff |
|---|---|---|---|---|---|---|---|---|---|
| Title | `©nam` | `com.apple.quicktime.title` | `©nam` | `TITLE` | `TIT2` | `Title` | `TITLE` | `INAM` | `NAME` |
| Artist | `©ART` | `com.apple.quicktime.artist` | `©ART` | `ARTIST` | `TPE1` | `Artist` | `ARTIST` | `IART` | `AUTH` |
| AlbumArtist | `aART` | — | — | `ALBUM_ARTIST` | `TPE2` | `Album Artist` | `ALBUMARTIST` | — | — |
| Album | `©alb` | `com.apple.quicktime.album` | `©alb` | `ALBUM` | `TALB` | `Album` | `ALBUM` | `IPRD` | — |
| Composer | `©wrt` | — | `©wrt` | `COMPOSER` | `TCOM` | `Composer` | `COMPOSER` | `IMUS` | — |
| Author | — | `com.apple.quicktime.author` | `©aut` | `WRITTEN_BY` | — | — | `LYRICIST` | `IWRI` | `AUTH` |
| Director | `©dir` | `com.apple.quicktime.director` | `©dir` | `DIRECTOR` | — | — | `DIRECTOR` | — | — |
| Genre | `©gen` | `com.apple.quicktime.genre` | `©gen` | `GENRE` | `TCON` | `Genre` | `GENRE` | `IGNR` | — |
| Comment | `©cmt` | `com.apple.quicktime.comment` | `©cmt` | `COMMENT` | `COMM` | `Comment` | `COMMENT` | `ICMT` | `ANNO` |
| Description | `desc` | `com.apple.quicktime.description` | `©inf` | `DESCRIPTION` | — | — | `DESCRIPTION` | `ISBJ` | — |
| Date | `©day` | `com.apple.quicktime.year` | `©day` | `DATE_RELEASED` | `TDRC` | `Year` | `DATE` | `ICRD` | — |
| Copyright | `cprt` | `com.apple.quicktime.copyright` | `©cpy` | `COPYRIGHT` | `TCOP` | `Copyright` | `COPYRIGHT` | `ICOP` | `(c) ` |
| Encoder | `©too` | `encoder` | `©too` | `ENCODER` | `TSSE` | — | `ENCODER` | `ISFT` | — |
| EncodedBy | — | — | `©enc` | `ENCODED_BY` | `TENC` | `EncodedBy` | `ENCODEDBY` | `ITCH` | — |
| Grouping | `©grp` | — | — | — | `GRP1` | `Grouping` | `GROUPING` | — | — |
| Lyrics | `©lyr` | — | — | `LYRICS` | `USLT` | `Lyrics` | `LYRICS` | — | — |
| Rating | — | `com.apple.quicktime.rating.user` | — | `RATING` | — | — | `RATING` | — | — |
| Keywords | — | `com.apple.quicktime.keywords` | `©key` | `KEYWORDS` | — | — | — | `IKEY` | — |
| TrackNumber | `trkn` | — | — | `PART_NUMBER` | `TRCK` | `Track` | `TRACKNUMBER` | `ITRK` | — |
| TrackTotal | `trkn` | — | — | `TOTAL_PARTS` | `TRCK` | `Track` | `TRACKTOTAL` | `ITRK` | — |
| DiscNumber | `disk` | — | — | `DISC` | `TPOS` | `Disc` | `DISCNUMBER` | — | — |
| DiscTotal | `disk` | — | — | `DISC` | `TPOS` | `Disc` | `DISCTOTAL` | — | — |
| ReplayGainTrackGain | `----:com.apple.iTunes:replaygain_track_gain` | — | — | `REPLAYGAIN_TRACK_GAIN` | `TXXX:REPLAYGAIN_TRACK_GAIN` | `REPLAYGAIN_TRACK_GAIN` | `REPLAYGAIN_TRACK_GAIN` | — | — |
| ReplayGainTrackPeak | `----:com.apple.iTunes:replaygain_track_peak` | — | — | `REPLAYGAIN_TRACK_PEAK` | `TXXX:REPLAYGAIN_TRACK_PEAK` | `REPLAYGAIN_TRACK_PEAK` | `REPLAYGAIN_TRACK_PEAK` | — | — |
| ReplayGainAlbumGain | `----:com.apple.iTunes:replaygain_album_gain` | — | — | `REPLAYGAIN_ALBUM_GAIN` | `TXXX:REPLAYGAIN_ALBUM_GAIN` | `REPLAYGAIN_ALBUM_GAIN` | `REPLAYGAIN_ALBUM_GAIN` | — | — |
| ReplayGainAlbumPeak | `----:com.apple.iTunes:replaygain_album_peak` | — | — | `REPLAYGAIN_ALBUM_PEAK` | `TXXX:REPLAYGAIN_ALBUM_PEAK` | `REPLAYGAIN_ALBUM_PEAK` | `REPLAYGAIN_ALBUM_PEAK` | — | — |

`—` means the system has no field for it: the report lists it as not representable and nothing is written there.

## Per system

- **MP4/MOV/3GP.** `ilst` items are rebuilt from the untouched items' bytes plus the edited ones (`trkn`/`disk` as binary pairs, several values as several `data` boxes, `covr` with a `data`
  box per picture and the image type 13/14/27/12, freeform items as `----:mean:name`). Setting Genre also removes `gnre`. `mdta` key-table metadata edits existing keys and appends new keys to
  `keys`. QuickTime `©` atoms are edited when present. Pictures live only in `ilst`: a file with only `udta` or `mdta` tags gets an item list holding the pictures. Growth is taken from a
  following `free` box; otherwise `stco`/`co64` are fixed by the box editor.
- **Matroska/WebM.** SimpleTags of file-level Tags (Targets naming no UID); the one the common view reads (levels 30 and up first) is replaced, keeping its language, default flag and nested
  tags. `Title` also goes to Segment Info. Pictures are `AttachedFile`s named `cover.<ext>` (other roles `<role>.<ext>`). New `Tags`/`Attachments` elements go in front of the first Cluster,
  beside the last element before it, so readers find them without scanning.
- **MP3.** ID3v2.3/2.4 (the existing version is kept; a new tag is 2.4, or 2.3 with `Id3v2Version`), ID3v1, APEv2 (same header and footer choice as the file). The ID3v2 tag keeps its size when
  the result fits (padding absorbs the change), else it is rewritten with `PaddingBytes`. Text is Latin-1 when it fits, else UTF-8 (2.4) or UTF-16 (2.3). A 2.2 tag is upgraded and the frames
  with no counterpart are dropped and named in the report. Consecutive ID3v2 tags each get the edits. Tag-wide unsynchronisation is refused.
- **FLAC.** Vorbis comment and PICTURE blocks are rewritten, padding blocks merged into one at the end; metadata that keeps its size keeps the layout (in-place capable).
- **Ogg.** Vorbis, Opus and FLAC-in-Ogg: the comment packet is rewritten, the header packets re-laid out in new pages (CRC recomputed). If the page count changes, later pages of that stream are
  renumbered and re-checksummed while copying, a page at a time. Pictures are `METADATA_BLOCK_PICTURE` entries. Speex, Theora and multiplexed header pages are refused.
- **WAV/RF64.** `LIST INFO` (rebuilt from the existing sub-chunks), `bext` fields (Description, OriginationDate and native `Originator`, `OriginatorReference`, `OriginationTime`, same size so in place),
  `id3 ` chunk. **AIFF.** `NAME`, `AUTH`, `(c) `, `ANNO`, `ID3 ` chunk. Pictures create an ID3 chunk. AVI and Wave64 are not edit targets.

## Not done

ASF descriptors, XMP packets in `uuid`/`meta`, iXML, Speex/Theora comments, ID3 tag-wide unsynchronisation, ID3v2 at the end of a file, QuickTime `mdta` key creation when the file has no
`keys` box, picture dimensions in FLAC PICTURE blocks (written as 0).

## Checks

`tools/run-tagwrite-checks.sh` and `tools/tagwrite-verify.py`: per file the fixed edit set (title, artist, comment, cover art, one removal) is applied, then ffprobe packet data hashes (cover
streams left out), ExifTool and MediaInfo field lists, and ffprobe/ffmpeg/MP4Box/mkvalidator error counts are compared before and after. Known explained differences: ffprobe ends the last MP3
packet at trailing APE/Lyrics3 tags it does not know; ExifTool and MediaInfo derive duration and bit rate from the file size; ExifTool names ID3 groups by version.

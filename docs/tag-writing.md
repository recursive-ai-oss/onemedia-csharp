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

- **ASF/WMA/WMV.** The Content Description (title, author, copyright, description, rating) and the Extended Content Description (`WM/*` attributes; unicode text, `WM/SharedUserRating` as a
  DWORD, `WM/Picture` for pictures) are rebuilt; every other attribute is carried as its bytes. All other header objects are copied; only the Header Object's size and child count, the File
  Properties' file size (when it equalled the old size) and the Header Extension's size change. Data packets and index objects refer to packets, not to file offsets, so nothing after the header
  needs fixing. A Padding object in the Header Extension absorbs growth and shrinkage, which makes the edit in-place capable; without one, growth adds a padding object of `PaddingBytes`.
  A picture over 65,535 bytes is not written (the Extended Content Description's value size) and a note says so; attributes in Metadata and Metadata Library objects (per-stream attributes,
  large pictures) are read but not edited. A text over 65,535 bytes, or a file with content encryption objects, is refused with `UnsupportedMediaException`. The layouts are those of `asf.md`
  (observed files and ExifTool's tag documentation); the format's own specification is not used. ASF has no comment field: `Comment` is not representable, `Description` is.
- **XMP.** A packet in a `uuid` box (BE7ACFCB-97A9-42E8-9C71-999491E3AFAC, beside `moov` or in it) is edited in place of its box; one is created at the end of the file only when XMP is named in
  `TagWriteOptions.Systems`. Title, Artist (first creator), Description, Copyright, Keywords (`dc:`) and Date (`xmp:CreateDate`) are written as elements of the first `rdf:Description`; the
  same property in attribute form is removed; every other property, namespace and attribute stays. Title, Description and Copyright become one `x-default` language alternative (other languages
  of that property are dropped and named). The packet keeps its length when the result fits (the `xpacket` whitespace padding absorbs it), else it grows by 2 KiB of padding. A packet that is
  not well-formed XML is refused. `XMP_` atoms in `udta` are not edited.
- **iXML.** The `iXML` chunk of a WAVE file is edited by native key only (the iXML specification has no common-view field): `SetNative("ixml", "PROJECT", ...)`, key = element path below the
  root, for example `BEXT/BWF_ORIGINATOR`. Other elements are kept; a document that gets shorter is padded with spaces to its old length so the chunk keeps its size; the document is written
  as UTF-8 (a different declared encoding is converted and noted). A chunk is created when iXML is named in `Systems`.

## Chunks and boxes added after an unpadded chunk

A chunk of odd length at the end of a RIFF/IFF file may lack its pad byte. The first chunk added after it carries one zero pad byte so the new chunk starts on an even offset; chunks added
after that start on even offsets already (found by the check on `wav-alaw.wav` and `Fnonull.aif`, where each added chunk used to add its own pad byte and shifted the next one). The box,
EBML and chunk editors refuse (`ArgumentException`) an addition inside an element that another edit replaces or removes, instead of losing it.

## Choices worth knowing

- **AIFF text chunks are UTF-8.** `NAME`, `AUTH`, `(c) ` and `ANNO` are written as UTF-8 and read back as UTF-8 by OneMedia; the AIFF specification leaves the encoding open, and a reader that
  assumes Latin-1 (ATL.NET does) shows non-ASCII text differently. ASCII text is the same in both.
- **WebM gets no cover art.** The WebM profile has no Attachments element, so a picture edit on a `webm` file is reported as not written.
- **Padding that hides something is not reused.** A `Void`/`free`/`JUNK` element whose stated size was cut to fit the file (damaged), or that a Matroska SeekHead entry points into, is not
  taken as padding: a reader may reach an element inside it.
- **Damaged files are refused, not guessed at.** A Matroska file for which the reader reports errors (other than a wrong CRC-32 or a Segment cut off by the end of a truncated file), a SeekHead
  with cut-back entries, or an element of invalid unknown size is refused with `UnsupportedMediaException`: the reader found its way by searching, and moving bytes could change what it finds.
  In MP3 files an ID3v2 tag that overlaps the ID3v1 tag is refused, an APEv2 footer whose items do not parse is left untouched, and a file that merely ends in `TAG` is not taken for MP3
  unless the reader agrees it is MPEG audio. `TagWriteOptions.Systems` is honoured for Matroska: naming other systems leaves its Tags and attachments alone.
- **Matroska Tags after the Clusters.** When a file has file-level Tags after the Clusters and no SeekHead entry for them, the edit goes into those Tags (the bytes are right: `mkvextract`
  shows the new values) but the reader, which does not scan for them, does not see them. Files written with an index of `Tags` are unaffected.
- **ASF `ID3` attribute.** The embedded ID3 tag in an ASF attribute is kept as it is and noted in the report; removing a field that the ID3 tag also holds does not remove it there.

## Not done

Speex/Theora comments, ID3 tag-wide unsynchronisation, ID3v2 at the end of a file, an ID3v2 tag in front of a FLAC stream (copied as it is; the report notes it, and readers that prefer it, such as ATL.NET, keep seeing the old values), QuickTime `mdta` key creation when the file has no `keys` box, picture dimensions in FLAC PICTURE blocks
(written as 0), XMP in `XMP_` atoms and in files other than ISO BMFF, ASF Metadata/Metadata Library attributes, 3GPP text boxes.

## Checks

`tools/run-tagwrite-checks.sh` and `tools/tagwrite-verify.py`: per file the fixed edit set (title, artist, comment, cover art, one removal) is applied, then ffprobe packet data hashes (cover
streams left out), ExifTool and MediaInfo field lists, and ffprobe/ffmpeg/MP4Box/mkvalidator error counts are compared before and after. Known explained differences: ffprobe ends the last MP3
packet at trailing APE/Lyrics3 tags it does not know; ExifTool and MediaInfo derive duration and bit rate from the file size; ExifTool names ID3 groups by version.

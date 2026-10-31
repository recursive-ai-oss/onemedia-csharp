# Tags, common view and basic facts

How `RecursiveAI.OneMedia.Tags` and `RecursiveAI.OneMedia.Metadata` read the tags of ISO base media files (MP4, MOV, 3GP) and Matroska/WebM, and what
the common view and the basic facts are built from. Writing tags is a later feature; the models are shaped so a writer can round-trip them: every
item keeps its source range, and values are fetched from the source by range, never copied into the tree.

## Layers

| Layer | Types | Notes |
|---|---|---|
| Native tags | `NativeTag`, `ITagContainer`, `TagSystems` | One `NativeTag` per item: system, key, track, kind (text, number, real, binary, image), data type, locale or language, byte range of the item and of its value, decoded text or number. Values up to 64 KiB are decoded when read; longer text and all binary values and pictures are read on access (`Text`, `ReadValue()`). |
| ISO systems | `ItunesItemList` (`ilst`), `QuickTimeKeys` (`mdta` key table with `ilst`), `QuickTimeUserData` (`udta` atoms, incl. `©`-atoms with language), `ThreeGppAssets` (`titl`, `auth`, `perf`, `gnre`, `dscp`, `cprt`, `yrrc`, `loci`, `albm`, `kywd`, `rtng`, …), `XmpPacket` (`uuid` XMP box and `XMP_` atom) | Plain and `meta`-wrapped. XMP is exposed as the raw packet range plus the flat property list. |
| Matroska systems | `MatroskaInfoTags` (Segment Info: `Title`, `MuxingApp`, `WritingApp`, `DateUTC`), `MatroskaTagSet` (`Tags`/`Tag`/`Targets`/`SimpleTag`), `MatroskaAttachments` (`AttachedFile`, cover art as image items) | Targets keep `TargetTypeValue`, `TargetType` and the track UIDs (mapped to track numbers). Nested SimpleTags are keyed `PARENT/CHILD`. |
| Common view | `CommonTags`, `CommonField`, `TagPicture` | A view over the native tags through `TagMappingTable` ([tag-mapping.md](tag-mapping.md)). Nothing is stored. |
| Facts | `MediaFacts`, `Fact<T>`, `FactCandidate<T>`, `MediaDateTime`, `GeoLocation` | Duration, capture time, location, make, model, software, orientation, frame size. Each is an ordered candidate list; the first candidate is the value. |
| Entry points | `IMetadataProvider.ReadMetadata()` on `IsoBmffDemuxer`, `IsoBmffAsyncDemuxer` and `MatroskaDemuxer`; `MatroskaAsyncDemuxer.ReadMetadataAsync()` | → `MediaMetadata` (`NativeTags`, `Tags`, `Facts`, `Chapters`, `Containers`, `XmpPackets`, `Defects`, indexer `[system, key]`, `Find`). |

## Reading rules

- **I/O.** Only structure is read: the movie box (already loaded by the opener), the top-level box headers up to 64 boxes (stopping at the first
  `moof`), and for Matroska the Segment's leading elements up to the first Cluster plus the SeekHead entries that point at `Tags` and
  `Attachments`. Tags after the Clusters cost a header read each, never a scan. Cover art is read when asked for. Over a ranged source
  `MatroskaAsyncDemuxer.ReadMetadataAsync` fetches the located elements whole (each up to `MaxAttachmentBytes`) so the result stays valid after the
  source closes; an element over the limit is reported (`tags.limit`) and skipped.
- **QuickTime `udta` versus 3GPP boxes.** The same four-character code can have a QuickTime layout (a list of `u16 size, u16 language, text` records)
  or a 3GPP layout (full box: version/flags, packed language, zero-terminated UTF-8 or UTF-16). A box is read as 3GPP when its first four payload
  bytes are zero and the language word has its top bit clear; otherwise as QuickTime.
- **`ilst` data types** (ISO/IEC 14496-12 and the QuickTime spec): 1 UTF-8, 2 UTF-16BE, 4 and 5 text variants, 13/14/27 JPEG/PNG/BMP pictures, 21–24 and
  65–78 integers and reals. `trkn` and `disk` are `00 00, number, total, 00 00` pairs. `gnre` holds the ID3v1 genre number plus one.
  An unknown code is kept as binary with an `Info` defect.
- **Key-table metadata (`mdta`).** `keys` lists reverse-DNS names; each `ilst` item's four-character type is a 1-based index into it.
- **Matroska common view.** Only tags that target the file (no track, edition, chapter or attachment UID) feed it, and for a file with exactly one track
  also that track's tags, except `ENCODER` (which names the stream's encoder). Within a key: tags of the file before tags of the only track; the
  lowest `TargetTypeValue` of 30 or above first, then values below 30; tags without a language or with `und` before language-specific ones; then
  file order. Everything else stays on the native list.
- **Dates.** `MediaDateTime.TryParse` accepts ISO 8601 (with `Z`, `±hh:mm`, `±hhmm` or no zone) and date-only text; a bare year is not an instant. The
  zone certainty is `Utc`, `Offset` (with minutes), `Local` or `Unknown`.
- **Locations.** ISO 6709 in the `±DD.DDDD±DDD.DDDD[±AAA]/`, `±DDMM.MMM…` and `±DDMMSS.SSS…` forms (with an optional `CRS…` suffix), 3GPP `loci`
  (16.16 fixed point; an all-zero box means unknown and is ignored), and XMP `exif:GPSLatitude`/`GPSLongitude`.
- **Chapters.** Nero `chpl` lists (version 1: 4 reserved bytes, a one-byte count, then per chapter a 64-bit start in 100 ns units and a length-prefixed
  UTF-8 title) give `MediaMetadata.Chapters`; checked against ffprobe and ExifTool on ffmpeg-written files. Matroska `Chapters` and QuickTime text chapter
  tracks are not read yet.

## Fact precedence

A candidate list is ordered by rank, lower first, then file order. The rank is fixed and documented here; a replaceable resolver is a later feature.

| Fact | Rank | Source |
|---|---|---|
| Capture time | 10 | `com.apple.quicktime.creationdate` (carries an offset) |
| | 20 | XMP `exif:DateTimeOriginal`, `xmp:CreateDate`, `photoshop:DateCreated` with a zone |
| | 25 | `mdta` `creation_time` |
| | 30 | `©day` (`udta`, `ilst`) |
| | 40 | the same XMP properties without a zone |
| | 45 | Matroska `DATE_RECORDED` tag |
| | 50 | Matroska Info `DateUTC` (when the file was muxed) |
| | 60 | `mvhd` creation time, as UTC |
| Location | 10 / 20 / 30 / 40 | `mdta` ISO 6709 / `©xyz` / 3GPP `loci` / XMP GPS |
| Make, model, software | 10 (12 for `com.android.*`) / 20 / 30 | `mdta` / `udta` `©mak`, `©mod`, `©swr` / XMP; Matroska `WritingApp` 50, `MuxingApp` 51 |
| Orientation | – | `tkhd` matrix of the first video track → 0/90/180/270 plus mirror |
| Frame size | – | first video sample entry (ISO) or `TrackEntry.Video` (Matroska): coded and display size |
| Duration | – | movie header or Segment Info |

## Defects

All carry the byte offset of the item. Reading continues; nothing here throws on malformed tag data (typed exceptions come only from the structure
layers or from `ReadValue()` over a limit).

| Code | Kind | Meaning |
|---|---|---|
| `tags.invalid-utf8`, `tags.invalid-utf16` | `InvalidValue`, `InvalidSize` | Text is not valid; bad sequences are replaced with U+FFFD, an odd trailing byte is dropped. |
| `tags.value-too-large`, `tags.limit`, `tags.xmp-limit`, `tags.mkv-large`, `tags.mkv-depth` | `LimitReached` | A value or element is over `MaxTagBytes`/`MaxAttachmentBytes`, a packet has too many properties, SimpleTags nest too deeply; the part is skipped. |
| `tags.short-read` | `Truncated` | The source ended inside a value. |
| `tags.item-no-data`, `tags.data-short`, `tags.freeform-size`, `tags.int-length`, `tags.type-set`, `tags.data-type` | various | `ilst` item without a `data` box, a `data` box shorter than its type and locale, an unusable `mean`/`name` size, an integer with an impossible length (not reported for `trkn`/`disk` pairs), an unknown type set or code. |
| `tags.keys-short`, `tags.keys-count`, `tags.keys-size`, `tags.keys-index` | `Truncated`, `InvalidSize`, `InvalidValue` | Key table shorter than its count, a key whose size does not fit, an item pointing past the table. |
| `tags.udta-raw-text`, `tags.udta-text-size` | `Inconsistent`, `SizeExceedsParent` | A `udta` atom without a text-record header (read as plain text); a record longer than its atom. |
| `tags.3gpp-short`, `tags.3gpp-unterminated`, `tags.3gpp-location` | `Truncated`, `InvalidValue` | A 3GPP box shorter than its fields, a string without a terminator, a `loci` position out of range. |
| `tags.xmp-malformed`, `tags.xmp-gps` | `InvalidValue` | The XMP packet is not well-formed XML (DTDs are prohibited, so entity bombs are refused), or the GPS pair is not a position. |
| `tags.datetime`, `tags.iso6709` | `InvalidValue` | A date or position text that does not parse; the fact is not produced. |
| `tags.chpl-short` | `Truncated` | A chapter list shorter than its header or count. |
| `tags.mkv-header`, `tags.mkv-no-name`, `tags.mkv-attachment`, `tags.mkv-date`, `tags.mkv-target-level` | various | A tag-bearing element without a readable header, a SimpleTag without a name, an attachment without data, an invalid `DateUTC`, a `TargetTypeValue` outside 10–70. |

Limits used (`MediaLimits`): `MaxTagBytes` (text and binary values, `udta` atoms, key tables, XMP packets, chapter lists), `MaxAttachmentBytes`
(pictures and, over a ranged source, the fetched Matroska elements), `MaxAllocationBytes`, `MaxElementCount` and `MaxStructureDepth` through the
structure layers.

## How the reads were compared

`tools/OneMedia.Oracles meta` (run with `tools/run-metadata-compare.sh`) reads each file with our reader and with ExifTool 13.60
(`-j -G1 -a -api largefilesupport=1`, once plain and once with `-n`) and MediaInfo 26.10 (`--Output=JSON`), and writes one row per field to
`metadata-compare.tsv`: `native` rows (each text tag's value among the oracle's values of the matching group), `common` rows (the common field against
the oracle's field of the same meaning; MediaInfo's General section only) and `fact` rows. Dates are compared as instants, positions as numbers
(the oracles print degrees-minutes-seconds, decimals or hemisphere letters). Each row records what each oracle did: agree, disagree or does not report.
`tools/summarize-metadata-compare.sh` prints the counts. The corpus is 47 fixtures written by ffmpeg 7.0.2, MP4Box 26.07, mkvmerge 103.0 and ExifTool
(`tools/gen-metadata-fixtures.sh`; a subset is in `testdata/metadata`) plus public sample files (Blender movies, the Matroska test files, vendor
samples) fetched by the corpus scripts.

### Differences from ExifTool and MediaInfo, and why

| Difference | Explanation |
|---|---|
| ExifTool shows one value per tag name and group | It keeps the last of several Matroska tags with the same name (a `TITLE` at two levels) and files every track's tags under one `Track` group, so the second track's `ENCODER`/`DURATION` tags do not appear. We keep all of them on the native list. |
| Which of several values is "the" value | For a name repeated at several levels ExifTool takes the last in file order, MediaInfo joins them with ` / `, we take the lowest level of 30 or above first. Both values are reachable (`CommonTags.Values`). |
| MediaInfo's `Title` for Matroska with attachments | It lists the attachment file names. Attachments are not titles in the common view. |
| Track-targeted Matroska tags | ExifTool and MediaInfo show them as file tags. We keep them native-only unless the file has exactly one track. |
| `Encoder` of a remuxed Matroska file | mkvmerge keeps the source's file-level `ENCODER` tag next to its own `WritingApp`; the tag wins over the Segment Info application, MediaInfo reports the application. ExifTool lists both. |
| QuickTime `©aut` | MediaInfo and ExifTool call it Performer or Author; the common view has an `Author` field and `Artist` takes `©ART`/`©prf`. |
| QuickTime `©swr` | MediaInfo's "Writing application" shows it; we expose it as the Software fact, not as the Encoder field. |
| Duration | MediaInfo derives it from the streams where ExifTool and we use the movie header; where a track is longer than the header they differ. |
| `mvhd` before 1970 | A writer that stored Unix-epoch seconds in the 1904-epoch field gives 1942 for a 2008 recording. We report the literal value (MediaInfo does too); ExifTool shows 2008. |
| 3GPP `loci` | Coordinates are 16.16 fixed point; the quantised value (51.500687 for 51.5007) is what the file holds. |
| `trkn`/MP4Box `©trk` | MP4Box adds its track number as a text item `©trk` beside the numeric `trkn`; the numeric pair is the track number. |

### Where it stands

On 116 files (63 MP4/MOV/3GP/M4A, 53 Matroska/WebM/MKA) against ExifTool 13.60 and MediaInfo 26.10: 1,409 compared fields; every one agrees with at least one
oracle or is in the table above, except one malformed fixture on purpose (`north_of_here` as a position, reported as `tags.iso6709`). Rows where an oracle
reported the field and agreed, by kind: native tags ExifTool 625/646 (the 21 others are the collapse rows above and the Matroska track-tag cases), MediaInfo
496/498 (it prints degrees-minutes-seconds positions as if they were decimal degrees); common view ExifTool 227/229 and MediaInfo 224/232; facts ExifTool
450/453 and MediaInfo 262/268 (the rest are the duration and positions above).

# Privacy strip

`PrivacyStrip` (and `Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`) writes a copy of a media file without location and without
device identifiers. This page states exactly what is looked for, what is done to it, and what is not covered, so that a result can be checked.

## Contract

- **Default output is a new file.** `PrivacyStrip.SaveAs(in, out, options)` leaves the input alone; `Save` replaces the file atomically (temporary file,
  then rename); `SaveInPlace` overwrites only the changed ranges and works only when nothing moves (ISO base media: always).
- **Media payload is never touched,** with one exception, stated here so it can be checked: in an MPEG transport stream the AVCHD camera data (MDPM)
  sits inside the H.264 SEI messages of the video, so stripping its position entries changes bytes of the video packets (see MDPM below). Everywhere
  else audio and video samples are copied by range and the per-track payload hash of the output equals the input's. (Telemetry and text tracks are
  data/subtitle tracks, and their samples change by design.)
- **ISO base media (MP4, MOV, 3GP, M4A, fragmented MP4): nothing moves.** Every change keeps its length: a field is overwritten with zeros, a whole atom
  or item becomes a `free` box of its own size. No chunk offset, `moof` offset or `sidx` entry changes. `EditPlan.IsSameLayout` is true.
- **Make and model stay.** Camera make/model/software, firmware version, and orientation are not touched.
- **The report names what it removed and what it left.** `StripReport.Entries` lists each field or telemetry stream that was neutralised: category, system, name,
  track, the byte range in the original (for telemetry that occurs in many samples: first to last occurrence), the number of occurrences and bytes, and the
  action. `StripEntry.CountText` says what the count counts (`1 occurrence`, `3 occurrences in 40 samples`; `SamplesExamined` is the number of samples of the
  track that were looked at, so a count of 1 in 40 samples does not mean 39 were missed). `NotRemoved` lists everything the file holds that the strip found and
  left, **named from what is in this file**: a field with a rule that could not be edited, and also every box, chunk, element, object, opaque value, picture
  with metadata, data track and trailer the strip has no rule for (see "What the report names" below). `IsComplete` is true when it is empty. The CLI exits
  3 when it is not. The areas no run looks at are the fixed `NotExamined` list (three items, below), not per-file findings. How far this has been
  checked: the generated fixtures in the tests and the device-corpus sweep (36 recordings that ExifTool shows with a position or serial number, plus every
  AVI, and 687 further corpus files; [device-corpus.md](device-corpus.md)) found no position or serial left in the output that the report did not name.
  Layouts outside those files can still hold one the rules do not know; the report then names the box, chunk or track, not the field.
- **Categories.** `StripCategories.Location` and `StripCategories.DeviceIds` (flags); the default is both. CLI: `--location`, `--device-ids`.

## Actions

| Action | Meaning |
|---|---|
| `ConvertedToPadding` | the atom/item is now a `free` box of the same size, payload zeroed |
| `Zeroed` | the value bytes were overwritten with zeros; headers and lengths stay |
| `Overwritten` | the bytes were overwritten with a neutral value that is not zero and the field's key was changed so readers skip it: 0xFF in MDPM entries (zeros could join into a start code), spaces in text, and in DJI protobuf samples zeros plus the key changed to field number 15 |
| `Rewritten` | an XMP packet was re-serialised without the properties and padded with spaces to its old length |
| `Removed` | a tag writer removed the key (non-ISO formats; the tag area is rewritten) |

## What is stripped, by system

### ISO base media

| Where | Location | Device IDs |
|---|---|---|
| `udta` `©xyz` | whole atom | |
| `udta` `loci` (3GPP TS 26.244 §8.3) and the same in an iTunes item list | whole atom | |
| `mdta` key table items whose key name contains `location`, `iso6709`, `gps`, `latitude`, `longitude`, `coordinates`, `geotag` | whole item | |
| `mdta`/`ilst` items whose key name contains `serial`, `uniqueid`, `content.identifier`, `owner`, `deviceid`, `imei`, `udid`, `cameraid` | | whole item |
| `udta` GoPro atoms `LENS`, `CAME`, `MUID`, `GUMI`, `BCID` | | whole atom |
| `udta` `GPMF` device-level KLV: `CASN`, `MUID`, `LINF`, `CINF` | | value zeroed, key renamed `EMPT` |
| `udta` `QVMI`, `MVTG`, `RMKN`, `SIGM`, `Exif`: embedded Exif (found with `ExifBlock`) | GPS directory emptied (count, entries, link and every GPS value zero) | `BodySerialNumber`, `LensSerialNumber`, `CameraOwnerName`, `ImageUniqueID`, `CameraSerialNumber`, `MakerNote`: value zero, tag id changed to 0xFFFF |
| XMP (`uuid` BE7ACFCB…, `XMP_`): any property or attribute whose name contains `gps`, `latitude`, `longitude`, `location`, `geo`, `coordinates`, or is the word `lat`, `lon`, `lng`; `photoshop:City/State/Country`; `Iptc4xmpCore:Location/CountryCode` | removed | `serial`, `uniqueid`, `owner`, `deviceid`, `imei`, `udid`, `exif:ImageUniqueID` removed |
| `gpmd` track (GoPro GPMF) | `GPS5`, `GPS9`, `GPSU`, `GPSF`, `GPSP`, `GPSA`, `GPRI`, `GLPI`, `LNED`: value zeroed, key renamed `EMPT` | `CASN`, `MUID`, `LINF`, `CINF` likewise |
| `camm` track | packet types 4 (local position), 5 (position), 6 (GPS): payload zeroed, type set to 0xFFFF | |
| `mebx` track | atoms whose key (from the sample entry's key table) contains the location words above, including nested detected-face groups: value zeroed, local id set to 0 | atoms whose key names a serial/id |
| `djmd` track (DJI protobuf, protocols in the table of `DjmdDecoder`) | the position message (latitude, longitude, units), `abs_alt`, `rel_alt`: value overwritten with zeros (a varint as `80 … 00`, the same length), key changed to field 15 of the same wire type | the serial number (path 1-1-5): zeros, key changed to field 15, in every sample, whatever the protocol. **Unknown protocols** (a sample that names a protocol outside the table, or none and no earlier sample of the track named one; the DJI Osmo Pocket 3, `PP-101`, is one) are stripped conservatively: besides the serial, every field outside the device description (top-level field 1), the stream description (2) and the frame counter and camera settings (field 3, children 1 and 2) is overwritten the same way, as is every 64-bit double anywhere (how every known layout stores a coordinate), and bytes at the top level that do not parse as fields are zeroed. The report carries a note with the number of such samples, not a `NotRemoved` entry. The protocol is table-known for: `wm265e`, `pm320`, `wm260`, `wm261`, `Mavic4`, `Air3`, `Air3s`, `Lito_X1`, `Mini4_Pro`, `wa345e`, `AVATA2`, `NEO2`, `dji_neo`, `ac203`, `ac204`, `ac206`, `oq101` (a sample without a protocol name takes the name an earlier sample of the track gave). The model string stays |
| subtitle and text tracks in ISO files (`tx3g`, `text`, `wvtt`, `stpp`, DJI per-frame text) | text rules below: overwritten with spaces | `serial`-style keys |
| GoPro settings block (start of `mdat` before the first sample, and `fdsc` samples) | the position string by value | lens serial (48 bytes), camera-serial hash (16), camera serial (15) after the `FIRM` string; and every value read from a removed field, by value. Occurrences in `fdsc` samples are counted in the `gopro settings` entries, whose range starts in the `mdat` gap; the `fdsc` track has no entry of its own (checked on ten GoPro sample files: the serial numbers ExifTool reads from the `fdsc` track are gone after strip) |

"By value": after the labelled fields are read, their values (serials ≥ 6 bytes, the media id, the position text, facts-layer serial and location readings)
are searched for in every data track that no decoder handles and in the unreferenced settings block. Each occurrence is zeroed and reported
(`System` = the track's sample entry or `mdat-gap`).

### Text rules (ISO text tracks; scan only elsewhere)

Applied to the bytes of each subtitle/text sample, in order, then overlapping matches merged and blanked with spaces (the sample keeps its length):

1. `key: value` or `key = value`, the key a whole word, case-insensitive: `latitude`, `longitude`, `lat`, `lon`, `lng`, `altitude`, `alt`, `abs_alt`,
   `rel_alt`, `gps_alt` with a numeric value (key through value); `latitude` and `longitude` may also be followed by spaces and the number alone
   (`latitude 47.62`); `serial_number`, `serialnumber`, `serial number`, `serial`, `sn` with a word of at least four characters (device IDs).
2. `GPS(…)` or `HOME(…)` (spaces allowed before the bracket) whose contents are only digits, `. , - + ; space m M`.
3. ISO 6709 strings, `±DD[.d]±DDD[.d][±alt]/` (also the minute and second forms with 4/6 and 5/7 digits), the closing `/` required.
4. Two decimals with at least three fraction digits, the first within ±90 and the second within ±180, separated by up to six of: space, comma,
   semicolon, tab, line break, a degree sign, and a lone hemisphere letter `N S E W` (`47.62° N, 122.34° W`).
   A number that is the tail of a longer number or word, or followed by a second point (`1.2.345`), does not count.
5. Degrees, minutes and optional seconds with marks (`47°37'14.8"N`, `47° 37' 14.8" N`; UTF-8 degree sign, ASCII or prime marks), with a hemisphere
   letter when one follows.

WebVTT cues in ISO files (`wvtt`) are read past their box headers (`vttc`, `payl`, ...), which would otherwise glue to the first word of a cue. Text
that starts with a UTF-16 byte order mark (3GPP timed text may be UTF-16) is read as UTF-16 and blanked with UTF-16 spaces.

Ordinary subtitle text (times, `1.5`, version numbers, the word "latitude" without a value) is left alone; the tests list the cases. A pair of
decimals that is not a position (for instance `12.345 6.789` in a caption) is blanked all the same: the rule prefers removing to missing. Standalone
subtitle files (`.srt` and others) are not opened by the strip.

In Matroska/WebM, ASF, AVI and the other non-ISO formats a subtitle block lies inside a structure whose checksum or size a same-length edit would have to
maintain, so a track whose text matches the rules is reported in `NotRemoved` (`text:subtitle text`, with the first block's range) and not edited.

### MPEG transport streams (AVCHD camera data)

AVCHD camcorders repeat their camera data in an H.264 SEI message (type 5, UUID `17ee8c60-f84d-11d9-8cd6-0800200c9a66`, marker `MDPM`) at the start of
each group of pictures. The strip reads the file once in 1 MiB blocks (constant memory; 188-, 192- and 204-byte packets are detected from five sync
bytes), follows each H.264 PID, looks in the first 8 KiB of every PES packet for SEI NAL units, removes emulation prevention bytes to find the entries
(`tag` plus four data bytes), and overwrites each position entry with 0xFF (tag byte included) in place in the transport packets. Position tags are
0xB0–0xC8 and 0xCA–0xCC (GPS version, latitude and longitude with references, altitude, time, status, mode, DOP, speed, track, map datum, date).

- **The video payload changes, by necessity.** Only the bytes of those entries differ (the tests and the verification count them); every other byte of the
  file, including the other bytes of the same NAL units and every TS header, is copied. Decoded pictures are identical: `ffmpeg -f framemd5` of the input and
  the output are equal (checked on real AVCHD streams).
- 0xFF, not zero: zeros next to zeros could form a start code or need an escape. Zero bytes that are followed by an emulation prevention byte (`00 00 03`)
  stay as they are so the escape remains valid; that leaves at most those zero bytes of an entry.
- **No MDPM serial number field is documented in the public tag descriptions**, so none is stripped; maker and model (tag 0xE0 and the Sony model entries) stay.
  A recorder that stores such data elsewhere is not covered.
- An SEI message that runs past the first 8 KiB of its PES packet is not searched: the report lists it in `NotRemoved`. MDPM in an MP4/MOV (some
  cameras write it there) is not looked for.
- Bytes dropped or inserted in a recording move the packets after them off the packet grid; the scan then looks for the next run of sync bytes one
  packet apart and goes on, so the packets after the damage are searched too.
- Only H.264 video PIDs are followed; MPEG-2 video and other PIDs are not inspected.

### AVI `strd` (Fujifilm)

A Fujifilm camera writes the text `AVIF`, four bytes and an Exif directory without a TIFF header into the `strd` chunk of its video stream (offsets count from the
directory, observed with ExifTool in a FinePix movie: IFD0 with make and model, an Exif IFD, and a Fujifilm maker note that holds `InternalSerialNumber`).
The strip parses that directory with the Exif reader and applies the Exif rules above: the GPS directory is emptied, the maker note and the serial and owner
entries are zeroed and their tag ids set to 0xFFFF. Make and model stay readable. The chunk keeps its length and no other byte moves, also when an INFO tag
removal rewrites the tag area in the same run (the change is applied on top of that plan). A `strd` chunk of any other layout, with bytes in it, is listed in
`NotRemoved` with its stream, size and offset.

### Other formats

Matroska, ID3, APE, Vorbis comments, RIFF INFO, ASF: every native tag whose key contains one of the words above (`location`, `gps`, `latitude`,
`longitude`, `serial`, `uniqueid`, `owner`, …; the whole tokens `lat`, `lon`, `lng`, `gnss`, `sn`; RIFF `IARL`; ID3 `TOWN`) is removed through the tag
writer for that format (`docs/tag-writing.md` describes what it rewrites). In Matroska the removal reaches every copy: tags aimed at a track, chapter,
edition or attachment, and SimpleTags nested under another. The report lists them as `Removed`. A Matroska Tags element that no SeekHead entry points to
(after the last Cluster) is invisible to the tag layer; the strip finds it by hopping over the top-level elements and lists it in `NotRemoved`. Where the tag writer refuses a file, the tags are listed in `NotRemoved`. Position or serial data inside
the streams of these formats is not inspected, except subtitle text (above); what else the file holds is named in `NotRemoved` ("What the report names").

**Layout of these edits (measured on the test files).** The Matroska tag writer turns a removed `SimpleTag` into a `Void` element of the same size and
fixes the checksum of its parent, so a Matroska strip keeps the layout (`IsSameLayout`; `SaveInPlace` works). The ID3v2 writer keeps the tag's size when
the remaining frames fit and zeroes the rest as padding, so an MP3 strip keeps the layout too; for that the strip passes no extra padding. Other writers
(Vorbis, RIFF, APE, ASF) rewrite their tag area and may move bytes: `SaveInPlace` then refuses with `PaddingRequired`, leaving the file untouched
(on the review's test files FLAC, Ogg Opus, WAV and MP3 fitted and matched `SaveAs` byte for byte; a chained Ogg refused). (Frames removed from the end of an
ID3 tag used to stay in the file as stale bytes inside the padding; the writer now notices and clears them.)

## Neutral values

Zero everywhere. A zero latitude/longitude/altitude is what a reader then shows for the stripped GPMF/CAMM samples where the stream is still read (CAMM
packets are renamed to an unassigned type, GPMF keys to `EMPT`, mebx atoms to local id 0, so ExifTool and OneMedia stop reporting the streams).

## What the report names

The strip reads the structure of each file once more, after the rules have run, and lists in `NotRemoved` what it has no rule for. Each finding says what
was present (name, size, offset, track) and that it was not inspected or edited. The table gives, per format, what is removed, what is named, and what is out
of scope. Two things are cheap and safe, so they are done rather than named: **padding** (`free`, `skip`, `wide`, `JUNK`, `PAD `, `FLLR`) that holds bytes is
zeroed and listed in `Entries` (padding has no meaning), and an **Exif block inside a vendor value** (a maker's record in a `udta` atom or `uuid` box, found by
its `Exif` marker) is scrubbed with the Exif rules; the report's notes then say the rest of that atom was not inspected. Padding that is all zero is not reported.

Pictures (cover art, thumbnails) are named only when they hold metadata: a JPEG with an Exif, XMP or Photoshop segment, a PNG with `eXIf` or XMP, or an image
format the strip cannot look into. Opaque values that name neither a position nor a device are listed as harmless and not reported: the FLAC seek table,
QuickTime movie-window and play-flag atoms (`WLOC`, `SelO`, `AllF`, `LOOP`, `ptv `), QuickTime Player's mute key, ASF leaky-bucket pairs, CD table-of-contents
values (`WM/MCDI`, ID3 `MCDI`), embedded fonts, firmware text, and all DV packs. These are judgements ([Agent default]) and are listed in `StripInventory`.

| Format | Removed (rules) | Named in `NotRemoved` when present | Out of scope by design |
|---|---|---|---|
| ISO base media: MP4, MOV, 3GP, M4A, fMP4 | Everything in "ISO base media" above; Exif in vendor atoms and `uuid` boxes; padding with bytes | Boxes outside the movie-file structure at top level, in `moov` and in `trak` (vendor boxes, compressed `cmov`, non-XMP `uuid` boxes by GUID); opaque `udta`/`mdta`/`ilst` values; pictures with metadata; data tracks of unknown layout (`mett`, `dbgi`, `rtmd`...); bytes after the last box | Free text; make, model, software, firmware; `mvhd`/`tkhd` dates; names in `keys`/`mebx`; sidecar files |
| Matroska, WebM | Tags by key (all copies); SimpleTags nested; subtitle text is only reported | A `Tags` element no SeekHead points to; Segment children RFC 9559 does not list; opaque or picture attachments with metadata; data tracks; bytes after the Segment | Free text in tags and chapters; `Title`, `MuxingApp`, `DateUTC`; `SegmentUID` |
| AVI (incl. OpenDML, DV-in-AVI) | RIFF INFO tags by key; Fujifilm `strd` (Exif directory); padding with bytes | `strd` of another layout with bytes; chunks outside `avih`/`strl`/`odml`/`INFO`/`movi`/`idx1` (for example `LIST ncdt`, `vedt`); bytes after the last RIFF form (an `AVIX` form is structure) | Free text; `IDIT` date; DV pack dates and camera settings (no position or serial pack exists) |
| WAV, BWF, RF64, W64 | INFO, `bext`, `iXML`, `cart` and ID3-chunk tags by key; padding with bytes | Chunks outside the defined set (vendor chunks, other `LIST` types, `uuid`); bytes after the last chunk | Free text; `bext` originator fields not matching a rule |
| AIFF, AIFC | `NAME`/`AUTH`/`(c) `/`ANNO`/`COMT` and ID3-chunk tags by key | `APPL` and other undefined chunks; trailers | Free text |
| CAF | `info` strings by key | `uuid` and other undefined chunks; trailers | Free text |
| ASF, WMV, WMA | Content description and extended content description by key | Header and Header Extension objects that are not in the reader's list (including content-encryption objects, which hold licence URLs); binary or picture attributes | Free text; DRM payload (the file reports `Protected`) |
| MP3, ID3, APE, Lyrics3 | ID3v2/v1 and APE items by key | Binary frames and items: `PRIV`, `GEOB`, `UFID`, chapter frames, unknown frames; pictures with metadata | Free text (`COMM`, `USLT`) |
| FLAC, Ogg (Vorbis comments) | Comments by key | `APPLICATION` blocks, `CUESHEET`, pictures with metadata | Free text; a later link of a chained Ogg |
| MPEG-TS, M2TS | AVCHD camera data (MDPM) position entries | MDPM SEI beyond the first 8 KiB of a packet; data and private streams (SCTE-35, KLV, teletext, timed metadata) | Descriptors and service names; no serial field of MDPM is documented |
| MPEG-PS, FLV, DV, elementary streams | Tags by key where the format has them | Data tracks; opaque values | Free text; DV pack dates |
| Standalone subtitle files, sidecars | Not opened | Not opened | The strip works on one media file |

An area the strip still cannot see is one it has no structure reader for (a chained container, bytes inside a codec bitstream, the inside of a data track
with a layout nobody documented); those are in the by-design column or named as a data track.

## Not covered (and what the report says)

Every `StripReport` carries `StripReport.NotExamined`, a fixed list of three areas no run looks at: free text in tags, comments and descriptions; vendor boxes and chunks nested below the levels listed in `NotRemoved`; trailers after the last structure, in formats whose structure is not walked (MP3, FLAC, Ogg, TS, PS, FLV, DV, elementary streams). `onemedia strip` prints it as a `not examined:` line. An empty `NotRemoved` says nothing about these. Everything else the file holds that the strip has no rule for is named per file in `NotRemoved` ("What the report names"). Since 1.0.2 the report names vendor atoms, boxes, chunks, objects and opaque values from what is in the file; before, only the fixed list above said they were not looked at, and a Fujifilm AVI kept its `strd` serial number without a word in the report.

Found by the review (`docs/strip-review.md`) and left on purpose or for lack of a layout. Since 1.0.2 a file that holds one of these is told so by name in `NotRemoved` unless an item says otherwise:

- Names the key rules do not know: a custom `mdta` key such as `com.example.position`, `unit_id` or `hardware.id`; ID3 `TXXX`/`GEOB`/`PRIV` frames
  with unlisted descriptions; RIFF INFO `ISRC`/`IENG`, BWF `bext` originator fields; Matroska track names and titles.
- Free text: a comment, title or description that contains a coordinate or serial (ISO `©cmt`/`©des`, XMP `dc:description`, Vorbis `COMMENT`, ID3 `COMM`,
  ID3v1 comment, Matroska `Title`).
- Positions written as decimal comma (`47,6205 -122,3493`), degrees with plain spaces (`N 47 37 14.8`), full-width digits, UTM/MGRS, plus codes, geo URIs
  and addresses in subtitle text.
- Placements the tag layer does not read: a `udta` outside `moov`/`trak`, `udta` or `meta` inside a `traf`, an `xml ` box in `meta`, upper-case
  `©XYZ`/`LOCI`, a GPS IFD reached from the Exif IFD instead of IFD0, an Exif block in an unlisted atom or a `uuid` box (Canon CMT), bytes appended after
  the last box, vendor atoms (`gps ` NMEA, Samsung trailers), unknown data tracks (Sony `rtmd`; listed in `NotRemoved` by track since 1.0.2, a note before).
- XMP packets that are not UTF-8: reported in `NotRemoved`.
- A chained Ogg file: only the first logical stream's comments are read and edited; a later link's comments stay.

- Text that matches none of the text rules (a place name in a subtitle, a position written in words, text in a script the rules do not know). Subtitle blocks in Matroska and the other non-ISO
  formats are only reported, not edited. Standalone subtitle files.
- DJI's `dbgi` debug track (undocumented layout): not inspected; the report lists it in `NotRemoved`. Fields of `djmd` messages the decoder does not map
  (GPS time, home point, quaternions): not touched when the protocol is in the table; for an unknown protocol they are removed along with everything else outside the groups named above. The Osmo Pocket 3 recording in the device corpus has no position in its `djmd` samples; its serial number is removed.
- AVI files: RIFF INFO tags and the Fujifilm `strd` Exif directory are edited (above). Other camera data in a stream's `strd` chunk, and the `LIST ncdt` of Nikon AVI files, stay and are named in `NotRemoved`. Up to 1.0.1 the Fujifilm `InternalSerialNumber` stayed and the report did not mention it.
- The names in a `keys` box and in `mebx` sample entries stay (a name is not a value); values are gone.
- Sidecar files next to the video (DJI `.SRT`, Sony clip XML, XMP): the strip works on one file.
- Position or serial data the strip does not know a name for. The rules are the tables in `StripRules`; unknown vendor atoms stay and are named in `NotRemoved`.
- Free-text fields (title, comment, description) are not scanned for coordinates.
- Thumbnails/cover art images with their own Exif are not opened; they are named in `NotRemoved` when they hold Exif, XMP or Photoshop segments.
- Filesystem timestamps and the file name.
- Container dates (`mvhd`/`tkhd` creation time) stay: dates are a separate unit.
- Matroska, ASF, RIFF, ID3: tag fields only; anything else present is named in `NotRemoved` (table above).

## Verification method

`tools/strip/verify-strip.py <original> <stripped>` reads the secrets from the original with ExifTool (`-n -ee -a -G3`): every sample coordinate, every
serial/unique-id/content-id/owner value; builds their forms (UTF-8 and UTF-16 text, 4/5/6-decimal text, ISO 6709, float64 LE/BE, int32 pairs at 1e7 and
1e6 scale, hex ids); searches the stripped file's bytes; lists the tags ExifTool still shows whose names say location/serial/id with a non-neutral value;
the lines MediaInfo shows; and compares per-stream SHA-256 of `ffprobe -show_data_hash` for audio and video. `tools/strip/run-corpus.sh` runs a folder.
`--ffmpeg` adds the `framemd5` comparison (for MDPM, where packet bytes differ by design); `tools/strip/make-mdpm-real.py` turns the date entries of a real camcorder recording into position-tagged entries so there is something to strip while the stream still decodes.
`tools/strip/make-samples.sh` writes small samples in several tag systems, with text tracks, with ffmpeg; `tools/gen-strip-fixtures.py` writes the committed synthetic fixture
(`testdata/strip/strip-mixed.mp4`), a sparse multi-GiB variant (`--big`) and the fuzz seeds.

Memory: planning reads the movie structure, the tag atoms and the telemetry samples (one packet at a time); media payload is copied by range. A 4.5 GiB
synthetic file stripped with 43 MB peak resident memory.

# Privacy strip

`PrivacyStrip` (and `Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`) writes a copy of a media file without location and without
device identifiers. This page states exactly what is looked for, what is done to it, and what is not covered, so that a result can be checked.

## Contract

- **Default output is a new file.** `PrivacyStrip.SaveAs(in, out, options)` leaves the input alone; `Save` replaces the file atomically (temporary file,
  then rename); `SaveInPlace` overwrites only the changed ranges and works only when nothing moves (ISO base media: always).
- **Media payload is never touched.** Audio and video samples are copied by range. The per-track payload hash of the output equals the input's.
- **ISO base media (MP4, MOV, 3GP, M4A, fragmented MP4): nothing moves.** Every change keeps its length: a field is overwritten with zeros, a whole atom
  or item becomes a `free` box of its own size. No chunk offset, `moof` offset or `sidx` entry changes. `EditPlan.IsSameLayout` is true.
- **Make and model stay.** Camera make/model/software, firmware version, and orientation are not touched.
- **The report is complete or says why not.** `StripReport.Entries` lists each field or telemetry stream that was neutralised: category, system, name,
  track, the byte range in the original (for telemetry that occurs in many samples: first to last occurrence), the number of occurrences and bytes, and the
  action. `NotRemoved` lists what was found and left, with the reason. `IsComplete` is true when it is empty. The CLI exits 3 when it is not.
- **Categories.** `StripCategories.Location` and `StripCategories.DeviceIds` (flags); the default is both. CLI: `--location`, `--device-ids`.

## Actions

| Action | Meaning |
|---|---|
| `ConvertedToPadding` | the atom/item is now a `free` box of the same size, payload zeroed |
| `Zeroed` | the value bytes were overwritten with zeros; headers and lengths stay |
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
| XMP (`uuid` BE7ACFCB…, `XMP_`): any property or attribute whose name contains `gps`, `latitude`, `longitude`, `location`, `geo`, `coordinates`; `photoshop:City/State/Country`; `Iptc4xmpCore:Location/CountryCode` | removed | `serial`, `uniqueid`, `owner`, `deviceid`, `imei`, `udid`, `exif:ImageUniqueID` removed |
| `gpmd` track (GoPro GPMF) | `GPS5`, `GPS9`, `GPSU`, `GPSF`, `GPSP`, `GPSA`, `GPRI`, `GLPI`, `LNED`: value zeroed, key renamed `EMPT` | `CASN`, `MUID`, `LINF`, `CINF` likewise |
| `camm` track | packet types 4 (local position), 5 (position), 6 (GPS): payload zeroed, type set to 0xFFFF | |
| `mebx` track | atoms whose key (from the sample entry's key table) contains the location words above, including nested detected-face groups: value zeroed, local id set to 0 | atoms whose key names a serial/id |
| GoPro settings block (start of `mdat` before the first sample, and `fdsc` samples) | the position string by value | lens serial (48 bytes), camera-serial hash (16), camera serial (15) after the `FIRM` string; and every value read from a removed field, by value |

"By value": after the labelled fields are read, their values (serials ≥ 6 bytes, the media id, the position text, facts-layer serial and location readings)
are searched for in every data track that no decoder handles and in the unreferenced settings block. Each occurrence is zeroed and reported
(`System` = the track's sample entry or `mdat-gap`).

### Other formats

Matroska, ID3, APE, Vorbis comments, RIFF INFO, ASF: every native tag whose key contains one of the words above (`location`, `gps`, `latitude`,
`longitude`, `serial`, `uniqueid`, `owner`, …; RIFF `IARL`) is removed through the tag writer for that format (`docs/tag-writing.md` describes what it
rewrites). The report lists them as `Removed`. Where the tag writer refuses a file, the tags are listed in `NotRemoved`. Position or serial data inside
the streams of these formats is not inspected.

## Neutral values

Zero everywhere. A zero latitude/longitude/altitude is what a reader then shows for the stripped GPMF/CAMM samples where the stream is still read (CAMM
packets are renamed to an unassigned type, GPMF keys to `EMPT`, mebx atoms to local id 0, so ExifTool and OneMedia stop reporting the streams).

## Not covered (and what the report says)

- Subtitle and text tracks (a recorder may write position text there): not inspected; a note says so.
- The names in a `keys` box and in `mebx` sample entries stay (a name is not a value); values are gone.
- DJI telemetry in subtitle tracks and sidecars, AVCHD MDPM GPS: decoders land in another unit; not stripped here.
- Position or serial data the strip does not know a name for. The rules are the tables in `StripRules`; unknown vendor atoms stay.
- Free-text fields (title, comment, description) are not scanned for coordinates.
- Thumbnails/cover art images with their own Exif are not opened.
- Filesystem timestamps and the file name.
- Container dates (`mvhd`/`tkhd` creation time) stay: dates are a separate unit.
- Matroska, ASF, RIFF, ID3: only tag fields (see above).

## Verification method

`tools/strip/verify-strip.py <original> <stripped>` reads the secrets from the original with ExifTool (`-n -ee -a -G3`): every sample coordinate, every
serial/unique-id/content-id/owner value; builds their forms (UTF-8 and UTF-16 text, 4/5/6-decimal text, ISO 6709, float64 LE/BE, int32 pairs at 1e7 and
1e6 scale, hex ids); searches the stripped file's bytes; lists the tags ExifTool still shows whose names say location/serial/id with a non-neutral value;
the lines MediaInfo shows; and compares per-stream SHA-256 of `ffprobe -show_data_hash` for audio and video. `tools/strip/run-corpus.sh` runs a folder.
`tools/strip/make-samples.sh` writes small samples in several tag systems with ffmpeg; `tools/gen-strip-fixtures.py` writes the committed synthetic fixture
(`testdata/strip/strip-mixed.mp4`), a sparse multi-GiB variant (`--big`) and the fuzz seeds.

Memory: planning reads the movie structure, the tag atoms and the telemetry samples (one packet at a time); media payload is copied by range. A 4.5 GiB
synthetic file stripped with 43 MB peak resident memory.

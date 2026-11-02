# Capture-date writes

`CaptureTimeWriter` (and `Media.SetCaptureTime`, and `onemedia tags <file> --capture-time <iso8601>`) sets the time a recording was made in every
date field the file carries, each in that field's own notation and zone semantics, so file managers, players and metadata tools show the
instant that was written. It is a tag write: media payload bytes are copied, never re-serialised (the per-track payload hash is unchanged),
and the result is a new file unless `Save` or `SaveInPlace` is used (the safe writer's rules, including `PaddingRequired`).

The time is a `DateTimeOffset`: the instant and the offset the recorder was in. The offset matters only for fields that have no zone of their
own; there the wall clock of that offset is written.

## The report

`CaptureTimeReport` lists

- **Written**: system, field, old text, new text and the field's zone semantics, in file order;
- **Left**: capture-date fields that were found and not changed, each with the reason (a year only, a release or tagging date, an unset header);
- **Disagreements**: dates the file still carries that differ from the one written. Telemetry GPS times are never rewritten and always show up
  here when they differ; so do fields the write does not set;
- **HeaderTimesLocal / HeaderTimesBasis**: whether the binary header times hold UTC (the default, as the specification says) or the local wall
  clock, and why (the specification, a matching device profile, or `CaptureTimeOptions.HeaderTimesAreLocal`);
- **Tags**: the tag-level report of the same write, and **Warnings**.

## Fields written, per format

| Format | Fields written | Zone semantics | Added when absent |
|---|---|---|---|
| MP4 / MOV / 3GP | `mvhd`, every `tkhd`, every `mdhd`: creation and modification time | UTC seconds since 1904 (ISO/IEC 14496-12 §8.2.2); the local wall clock when a device profile or the option says the device stores local time | no: the header boxes always exist; a header whose times are both 0 is left unset |
| | `udta` `©day`, `ilst` `©day` | UTC with a `Z` (Windows mis-reads an offset here, see below) | no |
| | `mdta` `com.apple.quicktime.creationdate` | ISO 8601 with the offset, `+0200` as Apple cameras write it | no |
| | `mdta` `creation_time`, `com.apple.quicktime.creation_time`, `date`, `com.apple.quicktime.date` | UTC with a `Z` / ISO 8601 with offset | no |
| | XMP `xmp:CreateDate`, `photoshop:DateCreated`, `exif:DateTimeOriginal` (when the packet has them) | ISO 8601 with the offset | no XMP is created |
| | Exif blocks in `QVMI`, `MVTG`, `RMKN`, `SIGM` user-data atoms: `DateTimeOriginal`, `DateTimeDigitized`, their `OffsetTime*`, and the sub-second tags zeroed | wall clock plus its offset tag (when the block has none there is no room for one: reported) | no |
| Matroska / WebM | Info `DateUTC`; tags `DATE_RECORDED`, `DATE_DIGITIZED` | `DateUTC` is UTC nanoseconds since 2001 (RFC 9559 §5.1.2.9); the tags are ISO 8601 with the offset | `DateUTC` is added to Info |
| WAV / BWF / RF64 | RIFF INFO `ICRD`; `bext` `OriginationDate` and `OriginationTime` | wall clock of the offset, no zone (EBU Tech 3285) | `ICRD` is added to a WAV that has no `ICRD`, `IDIT` or `bext` date |
| MP3 | ID3v2.4 `TDRC`; ID3v2.3 `TYER`, `TDAT` (DDMM) and `TIME` (HHMM); `TDOR` when present | wall clock of the offset, no zone | `TDRC` (or the v2.3 trio) is added |
| FLAC / Ogg Vorbis / Opus | Vorbis comment `DATE`; `CREATION_TIME` when present | `DATE`: wall clock of the offset, no zone; `CREATION_TIME`: UTC with a `Z` | `DATE` is added |
| ASF / WMV / WMA | File Properties creation date | UTC (FILETIME, 100 ns since 1601) | no: the object always exists |
| AVI | not written: AVI is not an edit target (its indexes hold absolute offsets) | | |
| MPEG-TS, PS, FLV, DV, CAF, AIFF | not written | | |

Fields that look like dates and are not the capture date are left and listed with a reason: `DATE_ENCODED`, `DATE_TAGGED`, `DATE_RELEASED`,
`DATE_PURCHASED` and a free `DATE` in Matroska, the ID3 `TDEN`, `TDTG`, `TDRL`, `TORY`, ID3v1 and `yrrc` and `year` (a year only), the Exif
`IFD0.DateTime` (the image's modification time), and ASF `WM/EncodingTime` and `WM/MediaOriginalBroadcastDateTime`.

The range is 1904 to 2262; a time outside it throws `ArgumentOutOfRangeException`. A version 0 header box holds 32-bit seconds (to 2040); a
later time leaves that box and says so.

### Choices made where the specifications are silent

- **Header times are UTC unless told otherwise.** ISO/IEC 14496-12 says UTC, and Windows and macOS both read them as UTC (below). Some cameras
  store local time; `DeviceProfile.HeaderTimeIsLocal` (data in `DeviceProfiles.cs`) and `CaptureTimeOptions.HeaderTimesAreLocal` select the local
  wall clock, and the report says which was used.
- **Fields that are absent are added only where the format has one canonical capture-date field and a writer that can add it** (Matroska
  `DateUTC`, WAV `ICRD`, MP3 `TDRC`, Vorbis `DATE`); not for containers whose header always has the times, and never XMP or Exif blocks.
  `CaptureTimeOptions.AddMissing = false` turns the additions off.
- **Zone-less fields get the wall clock of the offset given**, not UTC: a person looking at an ID3 or RIFF date expects the time on the clock
  where the recording was made. The resolver reads them as "zone unknown" and the report says so.
- **User-data `©day` is UTC with a `Z`**, because of the Windows behaviour below. The offset is kept in `mdta` `creationdate`.

## What display applications showed

Written time for the table: **2024-05-01T00:30:00+02:00**, which is **2024-04-30 22:30 UTC**, so the day differs between UTC and the offset's
own clock and the displayed date shows which clock an application reads. Windows 11 Enterprise evaluation 10.0.26300, Photos 24.24010.29003,
time zone Pacific (UTC-7 on that date). macOS 27.0.1 (26A434), time zone CST. "Before" files carried 2020-01-02 03:04:05 (UTC).

| Format | Windows Explorer (Shell property → column) | Windows Photos (Info pane) | macOS |
|---|---|---|---|
| MP4 / MOV with `mvhd` set | `System.Media.DateEncoded` = 2024-04-30 22:30 UTC → "Media created" 4/30/2024 15:30 (before: 1/1/2020 19:04 = 03:04 UTC shown in UTC-8) | April 30, 2024 15:30 (before: January 1, 2020 19:04) | AVFoundation `creationDate` 2024-04-30T22:30:00Z (before 2020-01-02T03:04:05Z); Spotlight `kMDItemContentCreationDate` shows the file-system time |
| MOV with a `©day` in `udta` | `System.Photo.DateTaken` = 22:30 UTC → "Date taken" 4/30/2024 15:30 | the `mvhd` date, as above | not read |
| MOV with `mdta` `creationdate` and a different `mvhd` | "Media created" = the `mvhd` date, the Apple `creationdate` is ignored | not tested separately | AVFoundation `creationDate` = the `mvhd` date (2022-01-01) |
| MP4 / MOV whose `mvhd` is 0 | no "Media created"; `©day` still gives "Date taken" | file-system creation date (the day the file was copied), not `©day` | AVFoundation reports a year-12173 date for a 0 `mvhd` in a MOV and none in an MP4 |
| FLAC | "Date released" shows the `DATE` text as written (2024-05-01T00:30:00) | not applicable | no date |
| Matroska, WebM, WAV, MP3, Ogg, ASF/WMV | no date property at all (before and after) | not applicable | AVFoundation cannot open Matroska or WMV; WAV, MP3, FLAC, Ogg give none |
| Apple Photos | pending: needs a GUI session; `osascript` against Photos launched the app and did not answer within 15 s (automation permission), so nothing was imported |
| Google Photos | pending: needs an account; none was created |

What this means for the fields written:

- **`mvhd` is what Windows (Explorer "Media created", Photos) and macOS (AVFoundation) show**, always read as UTC and displayed in the viewer's
  local zone. That is why the default writes UTC there. A camera that stores local time in `mvhd` shows a time shifted by its zone in those
  applications; the profile and the option exist to reproduce that, not to fix it.
- **The user-data `©day` is read by Windows as UTC and its offset is mis-applied.** Observed with the same instant written four ways:
  `2024-05-01T00:30:00+02:00` showed 00:32 UTC (the offset read as minutes), `+0200` showed 02:30 UTC (the offset added instead of subtracted),
  `2024-04-30T22:30:00Z` showed the right instant, a bare `2024-05-01T00:30:00` was taken as UTC, and a bare date showed nothing. The writer
  therefore puts UTC with a `Z` there. Photos shows the `mvhd` date and falls back to the file's own creation date, never `©day`.
- `mdta` `creationdate` (with its offset) is shown by none of Explorer, Photos or AVFoundation here; it is what Apple's own camera writes and what
  OneMedia's resolver ranks first, so it is kept consistent with the rest.

## Oracle agreement

After a write, ExifTool 13.60, MediaInfo 26.10 and ffprobe (7.0.2 on the worker) are read on the result, and every field of theirs that parses
as a date and time is compared with the instant written (to the second; UTC, the offset's wall clock, or the date alone are accepted for
fields that carry no zone). The check is `tools/run-datewrite-checks.sh` with `tools/datewrite-verify.py`. Differences that are not ours:

- ExifTool's `Composite:DateTimeOriginal` for ID3v2.3 files joins `TYER`, `TDAT` and `TIME` as year, then the four `TDAT` digits as they are
  (`2024:13:05` for 13 May); the raw ID3 fields it lists (`Year 2024`, `Date 1305`, `Time 1234`) are right (ID3v2.3 §4.2.1 says `DDMM`).
- MediaInfo labels zone-less fields (`Recorded_Date` of an MP3) as UTC while showing the wall clock written. These are counted as "label" in the
  summaries, not as differences.
- mkvmerge's `StatisticsWritingDateUTC` / `_STATISTICS_WRITING_DATE_UTC` tags (the time the file was written) and MediaInfo's
  `Encoded_Library_Date` (the build date of the muxer library) are not capture dates and are not touched.
- MediaInfo's ASF `Encoded_Date` lists the File Properties creation date together with `WM/EncodingTime`; the first is the one written.

## Checks and limits

- Reading back: the resolver resolves to the written value on every file the writer accepts, and every date candidate of a written file names
  the same instant (test `EveryDateCandidateOfTheResultNamesTheWrittenInstant`).
- Refusals are the tag writer's: damaged Matroska, tag-wide ID3 unsynchronisation, content-protected ASF, Ogg streams that are not Vorbis, Opus
  or FLAC, and AVI.
- The fuzz target `datewrite` (time and options in the first six bytes) ran 300 s clean after fixing three findings of its own: an ASF header
  object size near 2^63 wrapped a bounds check (twice, in the header and in the header extension), a RIFF last chunk without its pad byte left
  an odd padding remainder, and an ISO header box whose size field disagreed with its extent could not be replaced. Their repros are in
  `testdata/fuzz/datewrite`.
- Not covered: telemetry GPS times (never rewritten), `tmcd` timecode, SCTE-35 and the DJI/Sony sidecars, MXF, and Exif blocks outside the four
  user-data atoms (an Exif APP1 in a still image is not a media container here).

## Reproducing

```
tools/gen-datewrite-fixtures.sh [dir]                     small fixtures written by ffmpeg from generated input
tools/run-datewrite-checks.sh build
tools/run-datewrite-checks.sh run <iso|mkv|mp3|flac|ogg|wav|asf|fixtures> [corpus-dir]
tools/date-check/prepare.sh <DateWriteCheck-dir> <folder>  before and after files for the display applications
tools/date-check/run-on-vm.sh <folder> <results>           Windows: Shell properties and Photos screenshots (guest already up)
tools/date-check/run-on-mac.sh <folder> <results-file>     macOS: AVFoundation, Spotlight, Photos probe
```

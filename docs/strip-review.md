# Privacy-strip review

The project's own attempt (not an external audit) to break `PrivacyStrip` (`Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`) before it is advertised. The reviewer was a separate pass within the project and did not write
the strip. Every secret in the test files is invented (a position over open water, made-up serial numbers); nothing in the repo is real data.

**Verdict: the strip can be advertised with its limits stated, not as "removes all location and identifiers".** It does what `docs/strip.md` lists, and after this
review's fixes the report no longer claims removals that did not happen. Claim 1 holds only for the places and names the rules know; five kinds of leftover
(listed under Open findings) stay in files with nothing in the report. The first version of the strip failed claims 1 and 5 in five places (findings 1–5);
those are fixed.

## Method

- **Inputs we generate** (`tools/strip/review/gen-adversarial.py`, 65 files): the same invented position and serial hidden in 58 ISO placements and
  encodings (trak-level and doubled `udta`, plain QuickTime `meta` without version/flags, `meta` in `trak`, a second `meta`, upper-case keys, `----` freeform, XMP in
  `uuid`/`XMP_`/`xml ` and in aliased namespaces, entities, CDATA, UTF-16, several packets; Exif little- and big-endian with an inline serial, a thumbnail IFD and a
  GPS IFD reached from the Exif IFD; GPMF with deep nesting, GPS9, two tracks and two DEVCs; CAMM types 4/5/6 in two tracks; `mebx` key variants; tx3g/wvtt/stpp
  text in 27 spellings and in UTF-16; a fragmented file with `udta`/`meta` in a `traf`; trailers, NMEA atoms, unknown tracks) plus WAV (INFO, `bext`, ID3 chunk),
  MP3 (ID3v2, ID3v1), FLAC, Ogg Opus, chained Ogg, and Matroska (global, track-targeted and nested tags; a Tags element after the last Cluster without a SeekHead
  entry; track name, title, subtitle).
- **Separate search** (`search.py`, `review-check.py`; not the unit's `verify-strip.py`): each secret in about 65 byte forms (ASCII, lower case, UTF-16 LE/BE, hex, base64,
  text with 4–6 decimals, European comma, DMS and NMEA text, float32/64 both orders, int32 at 1e7 and 1e6 both orders, 16.16 fixed, TIFF rationals, UTM/MGRS/plus
  code, full-width digits), searched in input and output. On the GoPro corpus the secrets are taken from the original by ExifTool (`-ee -a -G3 -u -n`: 30 coordinates
  and 2–3 serial strings per file) and searched in the output's bytes; the output is read back with ExifTool 13.60 `-ee -a -G3 -u` and MediaInfo 26.10 `--Full`.
- Oracles, worker: ffprobe 7.0.2-static, MP4Box 26.07, mkvalidator 0.6.0, ExifTool 13.60, MediaInfoLib 26.10. Host: AMD Ryzen 9 7950X3D, 32 threads. Fuzz via `wq` heavy.

## Findings

| # | Severity | What | Status |
|---|---|---|---|
| 1 | High | Matroska: a tag aimed at a track (or chapter, edition, attachment) and a SimpleTag nested under another were listed as `Removed` but stayed; the tag writer edits file-level top-level SimpleTags only. The report was false and the position stayed (ExifTool shows it). | **Fixed** (`TagEdits.RemoveNativeEverywhere`, `RemoveNamed` in the Matroska writer) + `MatroskaTagsAimedAtATrackAndNestedSimpleTagsAreRemovedToo` |
| 2 | High | RIFF: `IARL` in a second `LIST INFO` chunk was listed as `Removed` but stayed (the writer edited the first INFO list; ffmpeg-written WAVs already carry one). | **Fixed** (`RemoveFromOtherInfoLists`) + `RiffInfoArchivalLocationIsReallyRemovedWhenTheReportSaysSo` |
| 3 | High | WebVTT (`wvtt`) cues: the four type bytes `payl` sit directly before the text, so a cue that starts with a number or `lat:`/`SN:` never matched the whole-word rules: a position or serial at the start of a cue stayed, nothing in the report. | **Fixed** (box headers masked in the scan copy) + `WebVttCuesAreScannedPastTheirBoxHeaders` |
| 4 | Medium | MPEG-TS: bytes inserted or dropped in a recording moved every later packet off the 188 grid; the scan skipped them silently (8 of 21 MDPM position entries stayed in a test stream with 100 junk bytes mid-file). | **Fixed** (resync on the next run of sync bytes) + `MdpmPositionsAfterDroppedOrInsertedBytesAreStillFound` (0, 37, 100 bytes) |
| 5 | Medium | UTF-16 timed text (BOM after the 2-byte length) was not scanned. | **Fixed** (read as UTF-16, blanked with UTF-16 spaces) + `Utf16TimedTextIsScannedAndBlankedAsUtf16` |
| 6 | Medium | Text rules missed degrees-minutes-seconds with marks, `47.62° N, 122.34° W`, numbers separated by a tab or line break, `latitude 47.62 longitude 122.34` without a colon. | **Fixed** + `TextRulesTakeDegreesMinutesSecondsLineBreaksAndSpacedKeywords`, `TextRulesStillLeaveOrdinaryTextAlone` |
| 7 | Medium | `docs/strip.md` said XMP properties containing `geo` are removed; the code had no such rule (`xmp:Geo` stayed). Also `lat`/`lon`/`lng` names. | **Fixed** (code now matches the document) + `XmpPropertiesNamedGeoAreRemovedAsTheDocumentationSays` |
| 8 | Medium | A UTF-16 XMP packet was neither edited nor reported. | **Fixed**: now in `NotRemoved` (not edited) + `XmpPacketsInUtf16AreReportedNotSilentlyKept` |
| 9 | Medium | A Matroska Tags element after the last Cluster with no SeekHead entry is invisible to the tag layer: nothing removed, nothing reported. | **Fixed**: found by hopping over the top-level elements, listed in `NotRemoved` (not edited) + `ATagsElementAfterTheLastClusterWithoutSeekHeadEntryIsReported` |
| 10 | Low | Unknown data tracks (Sony `rtmd`, vendor tracks) were left without a word. | **Fixed**: named in the report's notes + `UnknownDataTracksAreNamedInTheNotes` |
| 11 | Low | ID3 `TOWN` (file owner) and short key tokens (`com.example.lat`, `DEVICE_SN`) were not matched. | **Fixed** (whole tokens only: `translate` and `snow` stay) + `ShortNamesCountOnlyAsWholeTokens`, `Id3FileOwnerIsADeviceId` |
| 12 | Low | `onemedia strip a a --overwrite` aborted with an unhandled exception and a stack trace (exit 134). The file was untouched (the library refuses), only the CLI handling was bad. | **Fixed** (message and exit 1); no unit test (the tool has no test project); checked by hand |
| O1 | Medium | **Concatenated files:** a second ID3v2 tag in the middle or at the end of an MP3, and the later links of a chained Ogg, are not read; their tags stay (position and serial found by byte search), nothing reported. | **Open.** A fix needs a scan of the whole file for tag headers (the strip reads structure only). |
| O2 | Medium | **Placements the tag layer does not read:** `udta` outside `moov`/`trak`, `udta`/`meta` inside a `traf`, an `xml ` box in `meta`, upper-case `©XYZ`/`LOCI`, an Exif block in a `uuid` (Canon CMT) or an unlisted atom, bytes after the last box (Samsung-style trailers), NMEA `gps ` atoms, Sony `rtmd` and other unknown data tracks (position stays as binary; a note names the track). | **Open**, listed in `docs/strip.md`; each needs a layout from a real device. |
| O3 | Medium | **Names and free text:** keys the tables do not know (`com.example.position`, `unit_id`, ID3 `TXXX:DEVICE`, `GEOB`, `PRIV`, `TSRC`, RIFF `ISRC`/`IENG`, BWF originator, Matroska track name, XMP `aux:ImageNumber`) and coordinates or serials written inside titles, comments and descriptions (ISO `©cmt`, Vorbis `COMMENT`, ID3 `COMM` and ID3v1, XMP `dc:description`, Matroska `Title`). | **Open by design**; documented. |
| O4 | Low | Text spellings still missed: decimal comma, `N 47 37 14.8` with plain spaces, full-width digits, UTM/MGRS, plus codes, geo URIs, addresses. | Open, documented. |
| O5 | Low | Exif: a GPS IFD reached from the Exif IFD (not IFD0) stays; the IFD0 link and tag-id bytes that are renamed or zeroed lie outside the reported value ranges. | Open. |
| O6 | Low | Subtitle text in Matroska and other non-ISO files is only reported, never edited (documented). | Open by design. |
| O7 | Info | Two int32 little-endian hits of a GoPro latitude remain in two files (HERO6, HERO8), inside `gpmd` accelerometer data. Chance: ~50 MB × 60 four-byte patterns gives about one expected hit per file; ExifTool shows no position after the strip. Judged coincidence. | — |

## Numbers

**Adversarial set, 65 files, both categories.** Files with any secret form left, by byte search: **29 before the fixes, 26 after**. The 26 are: free text and unlisted names
(O3, O4: 11 files), unread placements (O1, O2, O5: 13 files), and two cases the report now names (Matroska trailing Tags, UTF-16 XMP; both in `NotRemoved`). Fixed here: `text-wvtt`, `text-tx3g-utf16` and `tele-mebx-variants` are clean now; `mkv-tags` and `wav-id3-info` lost the location that was reported but not removed. Read back with ExifTool and MediaInfo, the remaining
values are exactly the ones above.

**GoPro corpus (the 10 files in the corpus: HERO5 to HERO8, MAX, Karma), both categories:** per file 30 coordinates (1–3 in two files) and 2–3
serial strings taken from the original; **0 bytes left** in the output except the two coincidental hits (O7); ExifTool `-ee -a -G3 -u`: 0 position or serial tags with a
non-neutral value; output size equal to input on 10/10.

**Claim 2, payload:** 10/10 GoPro files, ffprobe 7.0.2 `-show_data_hash` over packet size, timestamps, flags and SHA-256 of the video stream and of the audio stream:
**20/20 identical** before and after. In the adversarial set the strip changes only reported ranges plus the key/id bytes next to them (below). MDPM in TS: the unit's earlier
`framemd5` equality on a real camcorder file was not re-run here (the real recording is no longer in the corpus); this review checked that only MDPM entry bytes
change (tests above, 21 entries → 0, all other bytes of the damaged streams equal).

**Claim 3, validators:** 65 adversarial outputs, ffprobe 7.0.2 `-v warning` lines and MP4Box 26.07 `-info` error lines: **0 files worse than their input**; GoPro 10/10
output errors 0 (input 0) in both tools; mkvalidator 0.6.0 on the two Matroska files: 0 errors and warnings in input and output alike. `SaveInPlace` (CLI `--in-place`) on the 65
files: **64 are byte-identical to the `SaveAs` result, 1 (chained Ogg) refused with `PaddingRequired` and left the file untouched** (the documented rule is stricter than
needed: FLAC, Opus, WAV, MP3 and Matroska fitted). `Save` shares `SafeWriter` with the writers, whose crash tests are in `docs/writer-review.md`; no new kill test was run
(`WriterReviewTests` takes more than 10 minutes and was not rerun; the strip changes nothing in `SafeWriter`).

**Claim 4, categories:** on the adversarial set, no device form disappeared in `--location` mode and no location form in `--device-ids` mode (65 files, 0 violations). On the GoPro
files `--location` leaves LensSerialNumber, CameraSerialNumber, SerialNumberHash (ExifTool still shows them) and removes every position; `--device-ids` leaves GPSLatitude/GPSLongitude and
the other GPS tags and removes the serials; make, model, firmware version and orientation are unchanged on the three files compared.

**Claim 5, report truth** (`check-report.py`: every changed byte in a reported range or ≤12 bytes before one; every reported range changed): before the fixes the checker found
two false reports (Matroska track-level `LOCATION`, WAV `IARL`; findings 1 and 2). After: **0 false reports**. Unreported changed bytes remain in 12 files: 8 Exif files (10 bytes each: the IFD0 GPS entry
and renamed tag ids, O5) and 4 tag-writer files (FLAC, Opus, MP3, WAV), where removing a frame shifts the rest of the tag area inside its padding; the removed item itself is reported correctly.

**Fuzz** (libFuzzer + SharpFuzz 2.3.0, one process each, worker, `wq` heavy): `strip-file` 600 s, **2,698,316 executions**, 69,203 coverage features, 6,900-entry corpus, **0 crashes**;
`strip-bytes` (every block scrubber, now including the UTF-16 and WebVTT text paths) 600 s, **5,584,440 executions**, 8,565 features, **0 crashes**. 26 new seeds in `testdata/fuzz/strip-file`, 3 in `strip-bytes`. The fuzz build predates the RIFF second-list fix (finding 2) and the CLI fix; so those two were not fuzzed.

**Tests:** `StripTests`, `StripTelemetryTextTests`, `StripReviewTests` (new: 27 tests including theory rows), `TagWriteTests`, `TagWriteFuzzTests`, `WriterFixTests`: all pass on the worker. The full suite exceeds 28 minutes on the worker and was killed at
the `wq` limit; it was not completed.

## Verdict per claim

| Claim | Verdict |
|---|---|
| 1. No location or device identifier remains in any tag system, Exif/XMP or telemetry track; what cannot be removed is in `NotRemoved` | **Holds with listed limits.** Holds for the places and names in `docs/strip.md` (GoPro 10/10, DJI/MDPM/Exif/XMP fixtures, 39 of 65 adversarial files fully clean). **Fails as a blanket statement:** O1–O5 leave data silently, because the strip cannot report what it does not read. Advertise "removes the documented fields", with the Not covered list. |
| 2. Payload bytes unchanged (MDPM-in-SEI excepted) | **Holds** (20/20 stream hashes; MDPM entries only, tests). |
| 3. Opens in ffprobe, MP4Box, mkvalidator with nothing worse; `Save` atomic; `SaveInPlace` only where documented | **Holds.** `SaveInPlace` works in more cases than documented; the documentation is conservative. `Save` atomicity rests on the writer review, not re-run. |
| 4. Category options do what they say | **Holds.** |
| 5. The report tells the truth | **Holds after the fixes** (two false reports found and fixed); the reported range of a renamed key or tag id lies just in front of the value range and the IFD0 link in Exif is not listed (O5). |

## Reproduce

`tools/strip/review/run-review.sh <work dir>` on the worker (builds the tool, generates the set, strips in all three modes, validators, ExifTool, MediaInfo, GoPro files, payload
hashes); `check-report.py <originals> <outputs>`; `gen-adversarial.py <dir>`; fuzz with `tools/fuzz.sh run strip-file 600` and `strip-bytes 600`.

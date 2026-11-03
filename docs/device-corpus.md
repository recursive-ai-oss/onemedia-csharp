# Device corpus

Real recordings from freely licensed sources, used to check demuxing, facts, telemetry, strip and repair against ffprobe, ExifTool and MediaInfo.
The files are never committed: `tools/corpus/devices-manifest.tsv` lists each one (URL, SHA-256, size, licence, credit, device as the file states it, all fetch-only) and
`tools/corpus/devices-fetch.sh` downloads them. `devices-discover*.py` and `devices-probe.py` found them (Internet Archive items and Zenodo records with a stated
CC0, public-domain-mark, CC BY or CC BY-SA licence; a file is kept when it names a camera or phone, or is a screen recording). This page names devices and formats only.

Oracles: ffprobe 7.0.2 (8.1.3 for transport streams, where 7.0.2 crashes), ExifTool 13.60, MediaInfo 26.10, `gpmfdemo` from GoPro's gpmf-parser 2.2.1 release.
Driver scripts: `tools/run-device-corpus.sh` (packets, info, facts), `tools/run-device-triage.sh` (cause per track), `tools/device-repair-check.py` (repair).

## What is in it (193 files, 6.1 GB)

| Class | Files | Devices |
|---|---|---|
| iPhone | 15 | 3GS, 5s, 6s Plus, 8, 11, 12 mini, 13 Pro Max (HEVC/H.264, Live Photo metadata tracks, face tracks) |
| Android | 10 | Pixel 8 / 8 Pro, Xiaomi, Motorola |
| Action cameras and drones | 7 | one HERO10 with GPMF, four older GoPro clips without telemetry, DJI Osmo Pocket 3 (djmd), one DJI FC6310R |
| Dashcams | 2 | re-encoded copies |
| AVCHD / M2TS camcorders | 63 | Sony (DSC, HDR), Panasonic, Canon; camera clock zones from -07:00 to +09:00 |
| Sony MP4 camcorders | 3 | |
| Compact cameras and DSLRs (MOV/AVI/MP4) | 37 | Nikon, Olympus, Canon, Panasonic GH6, Fujifilm |
| Mac screen recordings, other screen recorders, other | 8 + 35 + 13 | |

## Demux against ffprobe

Every track of every file, packet by packet (size, payload hash, position, DTS, PTS, duration, key flag). 317,942 packets in ffprobe's listings (ISO/QuickTime/AVI files 210,958
in 130 files; 106,984 in the 63 transport streams); ours 210,422 and 106,984. Payload bytes, positions and hashes agree for every packet both list. The difference is listing and
timing choices, sorted per track by `tools/run-device-triage.sh` (counts are tracks):

| Class | Files | Packets (ffprobe / ours) | Tracks same | Tracks with a difference, by cause |
|---|---|---|---|---|
| AVCHD / M2TS camcorders (TS, ffprobe 8.1.3) | 63 | 106,984 / 106,984 | 87 | 54 pos-key |
| Compact cameras and DSLRs | 37 | 45,168 / 45,034 | 63 | 6 last-duration, 4 edit-drop, 3 timing-other, 1 key-superset (PCM, fixed below), 1 regroup |
| Screen recorders (not Mac) | 35 | 61,335 / 61,335 | 43 | 24 last-duration, 1 timing-other |
| Mac screen recordings | 8 | 28,807 / 28,384 | 0 | 4 edit-repeat, 3 timing-other, 1 edit-drop, 1 last-duration |
| iPhone | 15 | 28,707 / 28,721 | 29 | 14 timing-other, 9 dts-shift, 7 edit-drop, 6 last-duration |
| Android | 10 | 17,453 / 17,456 | 11 | 16 last-duration, 3 edit-drop |
| Other | 13 | 16,710 / 16,710 | 18 | 4 last-duration, 1 timing-other |
| Dashcams | 2 | 5,314 / 5,314 | 2 | 3 last-duration, 1 timing-other |
| Sony MP4 camcorders | 3 | 3,886 / 3,891 | 3 | 3 edit-drop |
| Action cameras | 5 | 3,194 / 3,194 | 10 | 4 key-superset, 1 last-duration |
| DJI | 2 | 383 / 383 | 6 | 1 key-superset |

Causes (each is a documented choice in docs/isobmff.md and docs/mpeg.md, or recorded here):

- **last-duration** (61 tracks): the last sample's duration; ffprobe takes it from the edit or movie end, ours from the sample table.
- **pos-key** (54 tracks, TS): ours marks an intra picture that opens with a parameter set as a key frame; ffprobe 7.0.2 and 8.1.3 want a recovery point SEI (1,203 pictures in 45 camcorder files; where the SEI is present both agree).
- **edit-drop** (18): samples after the end of the last edit; ffprobe leaves them out, we list them.
- **dts-shift** (9): ffprobe moves DTS earlier for negative composition offsets.
- **edit-repeat** (4): an edit list that plays media twice.
- **timing-other** (23 tracks): all in the edit-start family: the first 3 or 4 packets differ in DTS or duration where ffprobe 7.0.2 applies the edit start to video DTS and to timed-metadata tracks in its own way; sample counts and bytes are equal. Not a bug found; no further cause isolated.
- **key-superset** (5 after the fix, 5 H.264 tracks): ffprobe marks more key frames than the sample table's sync list (6 vs 2, 21 vs 11, 25 vs 13, 27 vs 7, 28 vs 14). The extra ones are open-GOP I pictures that the `stss` box does not list; ffprobe marks them from the stream. Cause inferred from the counts; not checked picture by picture. We keep the container's statement.
- **regroup** (1, PCM): ours uses the sample table's chunk as the packet (22,050 bytes), ffprobe cuts 2,048-byte packets; bytes equal.
- **extra-ffprobe-track** (1): ffprobe lists one track more than ours in one file.

Fixed during this pass: an AVI whose index leaves the flag clear on every sound chunk (37 PCM packets): ffprobe marks all audio as key frames, ours took the index flag (marks 0). Audio chunks are key frames now (test `AudioChunksAreKeyFramesEvenWhenTheIndexClearsTheFlag`; the file re-checked, 1,147 packets equal).

## Stream facts (`tools/run-device-corpus.sh`, infocheck)

130 non-TS files: 9 differ from ffprobe in a track or container fact; 63 transport streams: 0 (1 excused, listed in `exceptions.tsv`). Remaining differences:

| Difference | Cause |
|---|---|
| duration within 0.05 s of ffprobe's (6 files) | last sample / edit rounding (the last-duration family) |
| duration 7.0 s vs 5.87 s (DJI FC6310R) | ours is the longest track, a 7-sample 1 Hz telemetry track; ffprobe shows the video. A choice, not a defect: backlog item to leave `Data` tracks out of `TracksDuration` when audio or video exist |
| size 913x511 vs 912x510 (1 file) | the sample entry and track header say 913x511 (MP4Box and MediaInfo agree); ffprobe shows the H.264 SPS crop. We report the container's declaration |
| audio channels 2 vs 1 (1 AAC file) | the sample entry says 2; the AudioSpecificConfig, ffprobe and MediaInfo say mono. **Unexplained which we should prefer**; not changed |

## Facts against ExifTool 13.60 and MediaInfo 26.10

`metadata-compare` over all 193 files (native keys, common fields, facts). Rows by field, ours against ExifTool and MediaInfo:

| Field | Match | Not matching |
|---|---|---|
| Make | 129 (+1 other candidate) | 1 unreported by us (Fujifilm AVI, no layout in the tag docs), 1 not reported by ExifTool |
| Model | 84 (+1 other candidate) | 1 unreported by us (the same Fujifilm AVI) |
| Software | 95 | 5 reported by us but not by ExifTool (GoPro), 1 unreported by us (Panasonic: "Ver.x" sits deep in an Exif block of the `PANA` atom, not read) |
| Capture time (with zone certainty) | 189 of 189 | none |
| Location presence | 18 of 18 files | none |
| Rotation | 128 of 128 (Composite Rotation, as MediaInfo) | none |
| Frame size (coded) | 193 of 193 | |
| Duration | 125 (+5 where ExifTool and MediaInfo agree with each other on stream length) | 63 (58 MTS, 4 M2TS, 1 AVI): the transport stream length is estimated differently (all within 0.63 s except one where ExifTool prints 0) |
| Title, Artist, Comment, Copyright, Cover | all that ExifTool reports | 4 tags we do not report (3 titles, 1 artist) |
| Encoder | 4 | 109 we do not report (ExifTool names a muxer/encoder string the common view does not carry) |
| Description | 2 partial | 14 unreported by ExifTool |

Native layer: 672 rows differ in form (ExifTool names and prints the same value differently); 418 match exactly, 201 match one of the two tools, 5 are not reported by the tools and 4 are not parsed by us.

Time zones: AVCHD clips carry the camera clock zone in the MDPM flags byte (63 files, read against ExifTool's printed zone); iPhone files carry `creationdate` with an offset.

## Telemetry

- GoPro HERO10 GPMF: every stream equals ExifTool and `gpmfdemo` (counts and values).
- DJI Osmo Pocket 3 `djmd`: 40 of 40 samples equal ExifTool's reading.
- iPhone `mebx`: key and sample counts equal ExifTool on every file.

## Strip

10 files carrying location or device ids, from several classes: 9 come out clean (ExifTool and MediaInfo show no location and no device id). The DJI Osmo Pocket 3 `djmd` samples still hold the camera serial number; the report says "not removed" (backlog: the djmd protocol table does not cover that field).

## Repair (cut at 60 %, `tools/device-repair-check.py`)

Twelve device models; "none" = no reference, "sibling" = another recording of the same model, "self" = the healthy file. Packets compared with the original's (bytes / key flags / times). All counts are samples of the recovered range.

| Model (class) | None | Sibling | Self |
|---|---|---|---|
| Nikon L340 (compact, H.264 + PCM) | refused (reference needed) | Medium; video 337/337 and PCM 564/564 equal | same |
| Nikon D5300 (compact) | refused | Medium; video 519/519, PCM 483/483 (times: the original's edit list hides leading pictures) | same |
| Olympus TG-4 (compact) | Low (video not framed) | Medium; video 253, PCM 386 equal (edit list as above) | same |
| Canon 550D (compact) | refused | Medium; video 636, PCM 630 equal | same |
| Olympus TG-830 (compact) | refused | Medium; video 180 and PCM 186 packets compared, one PCM packet differs in bytes (the cut) | same |
| Compact camera with companded PCM | refused | refused: companded PCM has no ISO sample entry | refused |
| iPhone (HEVC + AAC) | Low, AAC only (282 packets framed; 130 bytes equal) | High; video 288, AAC 280 equal | High |
| iPhone (H.264 + AAC) | Low, AAC only (95 equal) | High; AAC 589, video 354 equal | High |
| Sony camcorder (MP4) | refused | High; video 321, AAC 501 equal | High |
| Pixel (HEVC + AAC) | refused | Medium; video 224 equal, AAC 7 of 343 bytes equal | Medium; video 224, AAC 348 of 348 equal |
| Android phone (H.264, variable frame rate) | Low, AAC 425 equal | Medium; video 197 and AAC 425 equal; times 3/197 (VFR) | same |
| Xiaomi (HEVC + AAC, variable frame rate) | Low; 55 video samples | Medium; video 609 and AAC 991 bytes equal, video times 6/609 (VFR), AAC times 19/991 | same |

What the table says: bytes and key flags equal for every recovered sample but one cause below; the one failure with a sibling reference is **unexplained** (Pixel AAC with a sibling: 7 of 343). Without a reference only files with in-band parameter sets recover (phones, some at Low); compact cameras and Sony MP4 need a reference.
Time differences are limited to variable frame rate phones (the real durations are in the lost sample table) and to edit-list hiding of leading pictures.

Fixes made on these files (tests: `ReconstructionTests`, 30; `VendorCameraTests`, 9):

- Nikon L340 / D5300: the camera does not escape slice data (358 of 570 pictures hold `00 00 00` inside a NAL); the reference now switches the escape check off, and PCM runs end at a confirmed picture at any frame position. L340 self-reference was 1 of 570 frames; now 337 (video) and 564 PCM packets, all equal.
- PCM packets now equal the original's (2,048 bytes, short last packet of a chunk), compared byte for byte; only the final partial packet at the cut differs.
- Pixel AAC: a `mett` metadata track (206 to 232 byte samples, sometimes runs of equal sizes) sits between samples; gaps over 32 bytes broke the chain and false AAC starts inside it won. The sizes of the reference's unframed tracks now set the hop and the allowed gap. Self reference: AAC 348 of 348 equal (was 7). Xiaomi (runs of 452-byte samples): AAC 991 equal.
- Make, model and software from vendor atoms (Nikon `NCDT`/`NCTG` and `TAGS`, Olympus `TAGS`, Panasonic `PANA`, Canon `CNMN`, DJI `©mdl`, Nikon AVI): all Make and Model rows on the corpus match ExifTool except the two listed above.

## Planned for 1.0.x

Privacy first:

1. **Strip leaves the DJI Osmo Pocket 3 serial number** in its `djmd` telemetry samples: the layout of that protocol is not in the published tag tables. `StripDeviceIds` reports the field as not removed, so the leftover is visible in the report; until it is fixed, do not rely on strip to remove device identifiers from that camera's files.
2. `TracksDuration` takes telemetry tracks into account (DJI FC6310R, above).
3. AAC channel count: prefer the AudioSpecificConfig over the sample entry when they disagree.
4. Pixel AAC reconstructed with a sibling recording as reference (7 of 343 packets equal).
5. Panasonic "Ver.x" software, Fujifilm AVI make and model, Olympus MOV1/MOV2 layouts (no sample).
6. Leading pictures in the edit list of reconstructed compact-camera files.
7. The coverage gaps below: each is a device class to add and check when freely licensed files are found.

## Coverage gaps

No Samsung phone; no iPhone with Dolby Vision, spatial video or slow motion; no XAVC; no real dashcam originals (the two found are re-encoded); no OBS file with its own metadata; no Olympus MOV1/MOV2, no Leica. One HERO10 for GPMF, one DJI Osmo Pocket 3 for `djmd`, one iPhone family for `mebx`. Transport streams come from AVCHD camcorders only; M2TS has no oracle sweep beyond ffprobe 8.1.3. Real recordings cut by a power loss (as opposed to cut by our generator) are absent.

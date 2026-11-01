# ASF (WMV, WMA, ASF): reader notes

Normative notes for the `RecursiveAI.OneMedia.Asf` reader and `Tags.Asf`. Read and tag-read only; there is no ASF writer.

## Where the layouts come from

Microsoft's ASF specification was never downloaded, opened or accepted, and nothing derived from it was used. Every structure below
is confirmed by at least one of these documents or observations:

| Source | What it confirmed |
|---|---|
| Library of Congress format description fdd000067 (identification page only; links to Microsoft pages not followed) | the Header Object GUID and the file identification |
| ExifTool's ASF tag documentation, read as a document (its source never read) | stream-type GUIDs, field offsets inside File Properties, Stream Properties, audio and video format data, attribute names and value types, picture types |
| MusicBrainz Picard tag-mapping table (CC0) | Windows Media attribute names that carry common tags |
| Published sample files (samples.ffmpeg.org `asf-wmv`, `A-codecs`, `dvr_ms`, bug samples; media only) dumped object by object with `tools/asf/dump-objects.py` | object GUIDs and nesting, sizes and ordering; roles checked against ffprobe, MediaInfo and ExifTool output on the same file |
| ffprobe `-show_packets -show_data_hash` listings of those files, compared byte by byte with hexdumps | the data packet layout, payload headers, replicated data, compressed payloads, audio spread, Simple Index entries, Extended Stream Properties, attribute value types |
| Files written by ffmpeg (`tools/corpus/generate.sh`, 13 files incl. tagged, cover, small packet size, >2 GiB) | the same layouts as written by an independent encoder, plus tag round trips |

A layout that none of these confirmed is not implemented (see Gaps).

## Structure

- **Objects:** 16-byte GUID + 64-bit little-endian size that counts the 24-byte header. The Header Object adds a child count and two
  reserved bytes (30 bytes before the children), the Header Extension 46, the Data Object 50. The tree holds ranges; unknown GUIDs stay as
  `Unknown` nodes with their bytes in the file. Children whose sizes run past the parent are cut and flagged `asf.object-size`.
- **Header Object children used:** File Properties (file id, size, creation date, packet count, play duration, send duration, preroll,
  flags, min/max packet size, max bit rate; times in 100 ns, preroll in ms), Stream Properties (type GUID, type-specific data, flags
  with the stream number in the low 7 bits and an encrypted flag), Header Extension (Extended Stream Properties, Language List, Metadata,
  Metadata Library, Padding, and nested stream properties), Content Description (five length-prefixed UTF-16LE strings: title, author,
  copyright, description, rating; the Language List's lengths and the Content Description's lengths are byte counts), Extended Content
  Description (name, type, value; types: Unicode text, binary, bool, dword, qword, word, GUID), Codec List, Stream Bitrate Properties.
- **Data Object:** a file id, a packet count, then packets. A Data Object stating just its own 50 bytes is a placeholder (captures
  made while broadcast): packets run to the end of the file.
- **Data packet:** optional error-correction data (first byte bit 7 set), length-type flags, property flags, then fields whose widths
  the flags choose (packet length, sequence, padding length, 4-byte send time, 2-byte duration), then one payload or a payload count
  and per-payload headers (stream number with the key-frame bit, media object number, offset or presentation time, replicated data,
  payload length). Compressed payloads (replicated data length 1) hold whole small objects, each with a one-byte length, one time step
  apart. With one packet size (min = max) the next packet starts that far on regardless of what the packet states.
- **Objects** are reassembled from payloads in the order they complete; PTS is the object's presentation time.
- **Audio spread:** when an audio stream's error-correction type is the spread GUID (BFC3CD50-618F-11CF-8BB2-00AA00B4E220: span, virtual
  packet length, virtual chunk length), chunks are stored row by row and played column by column; the reader de-scrambles them. Confirmed
  byte for byte against ffprobe.
- **Simple Index:** after the data: file id, interval (100 ns), max packet count, entry count, then packet number (4) + count (2). It only
  narrows a seek; the exact keyframe is found by reading packet headers forward.

## Neutral model

- Track id = stream number; time scale 1000. PTS = DTS = presentation time in ms less the preroll (ffprobe prints PTS this way and
  nothing for WMV3 / MPEG-4 video, see exceptions). Audio objects are always keyframes; video uses the key-frame bit.
- Track order = order of Stream Properties objects in the header (streams that exist only in an Extended Stream Properties object follow).
- Duration = play duration less preroll (equals ffprobe's stream duration and MediaInfo's; ExifTool's Duration includes the preroll).
  Unknown for broadcast files whose play duration is 0.
- Codecs from the WAVEFORMATEX format tag or the video FourCC: WMA v1/v2/Pro/Lossless/Voice, MP3, MP2, PCM, AAC, WMV1/2/3, VC-1,
  MS-MPEG4 v1–3, MPEG-4 part 2, H.264, MJPEG; others stay as unknown codec with the raw tag.
- Source byte range of a packet = its payload bytes; none for an object assembled from several payloads. (ffprobe reports the start of the
  data packet; exception `pos`.)

## Protection

A Content Encryption or Extended Content Encryption object, or a stream flagged encrypted, marks the file `Protected` (`IsProtected`,
defect `asf.protected`). Tracks and tags are still listed; no packets are returned. Observed: `wma_with_metadata_library_object_tag.wma`
carries a real Content Encryption object. Media Foundation checks are pending.

## Damage and truncation

Typed defects (all with byte offsets): `asf.header-count`, `asf.object-size`, `asf.object-too-large`, `asf.truncated-object`,
`asf.file-properties`, `asf.duplicate-file-properties`, `asf.no-file-properties`, `asf.no-data-object`, `asf.stream-properties`,
`asf.stream-number`, `asf.duplicate-stream`, `asf.extended-stream-properties`, `asf.header-extension`, `asf.file-id`, `asf.packet-size`,
`asf.packet-*` (the packet parser's reason: padding, short, payload length), `asf.packet-count`, `asf.truncated-packet`,
`asf.short-data`, `asf.unknown-stream`, `asf.incomplete-object`, `asf.orphan-fragment`, `asf.compressed-payload`, `asf.protected`; tags:
`tags.asf-attributes`, `tags.asf-content-description`, `tags.asf-number`, `tags.asf-picture`, `tags.limit`, `tags.value-too-large`.
A damaged packet is skipped (the next starts one packet size on); a file that ends inside a payload delivers the arrived part of that one
object flagged corrupt (observed in ffprobe's output for files cut at round sizes).

## Seeking, streaming, I/O

- Seeking goes to a keyframe of the first video stream (first stream when none); times are the output times. Needs a seekable source.
- Forward-only and async ranged sources read the same engine; picture values are read from bytes kept while the header went by.
- Probe and tag reads touch the Header Object only (one or two reads); packets are read on demand. A Simple Index is looked for at the
  end of the Data Object when seeking. Files with no index scan packet headers once on first seek (documented: ASF has no other way).

## Tags

Content Description and the Extended Content Description / Metadata / Metadata Library attributes are read as `NativeTag`s of system
`asf` with exact ranges; WM/Picture is split into type, MIME type, description and image. Common view: `TagMappingTable` asf rows
(`docs/tag-mapping.md`). Content Description's description maps to **Description** (ExifTool and MediaInfo call it Description). ffmpeg writes
lowercase non-standard attributes (`title`, `author`, `date`) beside the standard ones: `date` is not mapped.

## Differential and documented deviations

`exceptions.tsv` scopes each class: `pos` (payload start vs packet start), `duration` (ffprobe derives packet durations from the bit rate;
the file states none), `pts` for WMV3 and MPEG-4 (ffprobe prints no PTS), `sample_g726` (damaged tail), `asf2divX_fault` (ffprobe stops at
the first damaged packet; we skip it and report defects), `new_fileformat` (declared packet size 1250, packets 2048 apart; ffprobe
resynchronises on packet headers, we do not), `fps_sample1` (DVR-MS elementary stream chunks). ffprobe's duration heuristics (estimate from size and bit rate, nothing for files shorter than the header states) are reported
as notes by `infocheck`, not differences.

## Tag agreement (55 files, ExifTool 13.60, MediaInfo 26.10, ATL.NET 7.19)

Common view: 95 match, 3 match-partial, 2 ours-missing, 1 unreported. Native attributes: 174 match, 90 match-ExifTool, 56 match-MediaInfo, 114 not listed by
either oracle. Explained classes:

- **Description:** ExifTool calls Content Description's description "Description", MediaInfo "Comment"; we follow ExifTool (the compare tool maps MediaInfo's name).
- **Duration (50 files):** ExifTool and ours differ because ExifTool's duration includes the preroll; MediaInfo and ffprobe agree with ours (play duration less preroll).
- **Not listed by either oracle (114):** `DeviceConformanceTemplate`, `WMFSDKVersion`, `WMFSDKNeeded`, `VBR Peak`, `Buffer Average`, `WM/WMCollectionID`, `WM/WMCollectionGroupID`,
  `WM/WMContentID`, `WM/MediaPrimaryClassID`, `DVR Index Granularity`: technical or library attributes the oracles do not print (ExifTool's `-a -G1` listing was checked on
  samples); we keep them as native tags.
- **match-partial (3):** MediaInfo names Author "Accompaniment" and AlbumArtist "Performer".
- **ours-missing (2):** `track0.wma` carries only the legacy zero-based `WM/Track` (0), which MediaInfo shows as position 1 and ExifTool as `Track`; not mapped. `wma_0x163.wma`:
  MediaInfo's Performer is the file's `WM/AlbumArtist`, we map that to AlbumArtist.
- **unreported (1):** `wma_with_metadata_library_object_tag.wma` has an Album in its Metadata Library that neither oracle prints.
- ATL.NET: 105 fields identical on 55 files; 5 fields (Content Description description) only we report, because ATL ignores it.
- `img_0001.asf` (damaged header GUID) is rejected by every tool.

## I/O (docs/io-asf.txt)

Probe plus metadata, 55 files incl. a generated 2.33 GB WMV (wmv2 + wmav2 at 40 Mbit/s repeated by stream copy). Ours reads a 36,864-byte head in 3 requests
over HTTP range for the 2.33 GB file and the same for every file whose header fits (up to ~110 KB / 16 reads when the header is large with pictures), against
ffprobe 5.1 MB, MediaInfo 3.2 MB and ExifTool 41 KB / 8 reads on that file. Probe never scans the data packets.

## Gaps

DVR-MS binary streams are listed as Binary tracks (no stream typing); no resync of mis-sized packets; the Index Object is not parsed
(Simple Index only); an `ID3` attribute stays a binary tag and its ID3v2 frames are also read as `id3v2` native tags (cover art included); DRM details (license URL, key id) not exposed;
no ASF writing or tag editing; WMA audio does not decode (OneAudio has no WMA decoder; MP3 in ASF decodes through `.Audio`).

## Windows evidence: Media Foundation and the Shell

Two runs on Windows 11 Enterprise evaluation (build 26300; `mfplat.dll` 10.0.26100.9278, `mf.dll` 10.0.26100.8521, `mfreadwrite.dll` 10.0.26100.9278,
`mfasfsrcsnk.dll` 10.0.26100.9278, `wmvcore.dll` 12.0.26100.9278) put 18 ASF/WMV/WMA files (8 written by ffmpeg, 10 published samples) and 8 MP4-family files (docs/isobmff.md) through Windows' own media stack
as a black box: the Media Foundation source resolver, presentation and stream descriptors and source reader (`tools/mf-check/MfProbe.cs`), the Shell property system, and the Shell thumbnail provider. Our side is
`onemedia info --json` and a packet dump of the same files (time converted to 100 ns, rounded to nearest). The first run's probe crashed on a COM declaration bug; the second ran clean (26 files, 1.7 min). Raw output is not committed: it carries the published samples' tags.

### Media Foundation: streams, duration, protection (18 files)

- **Streams:** 62 of 67 audio/video streams (all 26 files) agree on kind, codec tag (WAVE format tag or video FourCC), audio rate and channels, video size and frame rate. The five that differ: `fps_sample1.dvr-ms` video (MF names MPEG-2 video, ours has an empty codec name), two
  frame rates that are the same value to MF's 100 ns frame duration (`nintendothemesacappellax.wmv` 24000/1001, the 3GP 9.53 fps), and two MP4 frame rates that really differ (docs/isobmff.md). 17 of 18 ASF files have the same stream count (`fps_sample1.dvr-ms`: MF 3 streams, audio/other/video; ours 2 data tracks and a video). The three protected files show the same streams as ours. On `asf_file_with_multiple_video_and_audio_streams.asf` MF lists the same 26 streams (23 audio, 3 video) and only the two streams that carry data have samples, as in ours. MF's stream identifier is the ASF stream number, equal to our track id.
- **Duration** (`MF_PD_DURATION`): 17 of 17 files with a play duration equal ours to the 100 ns tick (`fps_sample1.dvr-ms` is one tick off, 39099999 against 39100000: MF truncates where we round the exact value); the broadcast file with play duration 0 is 0 in MF and unknown in ours.
- **Protection:** 3 of 3 protected files (`industrial.wmv`, `WMA_protected_napster.wma`, `wma_with_metadata_library_object_tag.wma`) have `MF_SD_PROTECTED` = 1 on every stream, and the 15 others have none, as `IsProtected` says. MF's presentation attributes name the scheme and carry the licence acquisition URL and key id (`DRM` header; not exposed by us, see Gaps). `MFCreateSourceReaderFromMediaSource` fails with `0xC00D3700` on the three, so no sample times exist for them in MF; the protected-content path of Media Foundation is a separate documented mechanism that this probe does not use. That is the expected answer for protected files, not a probe fault: the three files are the only ones where the reader failed, and they are exactly the ones flagged protected.

### Media Foundation: samples (15 readable files)

The probe read every sample to the end of each stream. Over the 26 streams that carry samples (13,655 MF samples, 13,779 ours), **20 streams equal ours in count and byte total**, and on those the first 20 samples equal ours
in size and SHA-256 (100%), and in time except `multivideo.asf`'s audio (below). The six streams that differ, each explained:

| File / stream | MF vs ours | Explanation |
|---|---|---|
| `multivideo.asf` video 2 and 3 (two WMV1 streams) | 317 vs 353 and 1136 vs 1225 samples | MF starts each video stream at its first key frame: dropping our leading non-key packets (36 and 89, 2,782 and 63,356 bytes) leaves exactly MF's counts, byte totals and first times (90.66 s, 59.96 s). Our reader returns every packet |
| `multivideo.asf` audio | counts and bytes equal; first 4 of 20 times differ | Our times are the object time less the preroll, and the first four packets come out negative (-15.57, -11.17, -7.89, -3.81 s); MF reports 0 for them (it clamps negative presentation times) and the rest are equal |
| `asf_file_with_multiple_video_and_audio_streams.asf` video | 521 vs 522 | MF omits the last object (184 bytes at 10,396.44 s; ffprobe 7.0.2 delivers it too). The file is a broadcast capture with play duration 0 and no end marker; MF seems to hold back the final object of an open-ended stream. Not settled |
| `fps_sample1.dvr-ms` (3 streams) | 53/54 audio, 119/118 data, 1540/1538 video samples | DVR-MS stores its streams as chunks that MF reassembles into frames; we list the chunks as data tracks (see Gaps). MF types them: audio `0x50` (MPEG-1 layer II) 48 kHz 2-channel, MPEG-2 video 720x480, and a third stream of a non-audio, non-video type. Byte totals are within 4,040 (audio) and 4 (video) |

The Shell and MF agree on the codec tags for every file; `System.Video.FourCC` and `System.Audio.Format` (previous run) are the same values as MF's subtypes.

### Multi-bitrate: `multivideo.asf` mutual exclusion

The file holds one audio stream and two WMV1 video streams of 4 kbit/s (stream 2) and 88 kbit/s (stream 3), 240x180. MF's stream descriptors select streams 1 and 3 by default and leave stream 2 deselected, i.e. it treats the video
streams as alternatives (the file's mutual-exclusion data), and offers stream 2 only when asked. We list all three as tracks with no relation between the two videos, so a remux that copies all tracks would write both. MF's own profile
reading (`MFCreateASFContentInfo`, `IMFASFProfile::GetMutualExclusion`) was not in the second run (the function is exported from `mf.dll`, not `mfplat.dll`; the probe is fixed) and a re-run of `-Only multivideo` would list the exclusion records. Parsing the exclusion
object is a follow-up (needs its layout from a non-Microsoft description or from a file; none is in Sources yet).

### Shell properties and thumbnails (run 1)

| Property | Result |
|---|---|
| Duration (`System.Media.Duration`) | 25 of 26 files equal to the millisecond (ASF 17 of 17, MP4 8 of 8); the broadcast file is 0 in the Shell and unknown in ours. Matches MediaInfo and ffprobe, not ExifTool (preroll) |
| Protected (`System.DRM.IsProtected`) | 18 of 18 ASF files agree |
| Title / Author | 8 of 9 tagged ASF files equal; the 9 untagged show nothing in both |
| Audio format tag, rate, channels; video FourCC, size | 41 of 41 checks agree (all 26 files) |
| Album, genre, track, composer, album artist, comment | equal on the files compared by eye (`wmav2-tagged.wma`: Tone / Sine / Tones / Sound / 2 / C / Various) |
| `System.Media.DateEncoded` | equals `WM/EncodingTime`, not File Properties' creation date, which we use for capture time (`WM/EncodingTime` stays a native tag). On `wma_with_metadata_library_object_tag.wma` the two are within a second and the Shell value equals ours |
| Thumbnail | for every file with a video stream except the DVR-MS file (64x64 to 512x288) and for the `WM/Picture` file (256x256); none for audio-only files without a picture, `fps_sample1.dvr-ms` and the protected files |

**The open Title on `wma_with_metadata_library_object_tag.wma`, explained.** Its Content Description object is empty (34 bytes: five zero lengths); `Title` (offset 6256) and `Author` (6450) are attributes of the
Metadata Library object. The Shell takes `System.Title` and `System.Author` from the Content Description only, so it shows nothing; we and ExifTool read the library attributes. The other two protected files carry their titles in the Content
Description and the Shell shows them. Our reading is the superset; no change.

Other explained differences: the Shell lists the first audio and first video stream only; `wmav2-cover.wma` is an MJPEG video stream (`MJPG`, 64x64) in MF, the Shell and ours.

### Explorer and Photos

The five screenshot files were opened (run 2, `-Screenshots`). Evidence is thin: the host console window stayed in front, hiding the Photos content area, and Explorer was captured in its list view, so tags and
dates are not visible. What the captures show: Explorer lists all 18 files with the right type names (`WMA File`, `WMV File`, `ASF File`, `Microsoft Recorded TV` for the DVR-MS file); Photos opened `wmv2-wmav2-tagged.wmv`,
`h264-aac.mp4`, `industrial.wmv` and `multivideo.asf` (window title = file name; for the protected file the edit button is greyed out); the launcher refused `wmav2-cover.wma` (audio, not a Photos type). Title and
artist display is covered by the Shell properties above, not by the screenshots. A re-capture with Explorer's Details pane open and the console minimised would be needed for date and title display evidence; not planned.

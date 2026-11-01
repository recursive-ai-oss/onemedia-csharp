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
resynchronises on packet headers, we do not), `fps_sample1` (DVR-MS elementary stream chunks), `wma_with_ID3_APIC` (cover art inside an ID3
attribute). ffprobe's duration heuristics (estimate from size and bit rate, nothing for files shorter than the header states) are reported
as notes by `infocheck`, not differences.

## Gaps

DVR-MS binary streams are listed as Binary tracks (no stream typing); no resync of mis-sized packets; the Index Object is not parsed
(Simple Index only); ID3 attribute handed out as binary until the ID3 layer reads it; DRM details (license URL, key id) not exposed;
no ASF writing or tag editing; WMA audio does not decode (OneAudio has no WMA decoder; MP3 in ASF decodes through `.Audio`).

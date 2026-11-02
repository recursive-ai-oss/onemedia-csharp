# Timed events: timecode, SCTE 35, timed ID3, `emsg`

Namespace `RecursiveAI.OneMedia.TimedEvents`; entry points `MediaFile.Events` and `MediaEvents.List(file, limits, includeMessages, ct)`;
CLI `onemedia events <file> [--json] [--limit N]`, and `onemedia info` prints one `events:` line per track-based channel.
Events are not cues: they have their own small model, timed in `MediaTime`, each carrying the byte range it was read from. Nothing is decoded;
payloads are parsed, never interpreted beyond their syntax (an encrypted SCTE 35 section keeps its bytes).

## Model

| Type | What it is |
|---|---|
| `EventChannel` | One stream of events: `Kind`, `Scheme` (URI, or a short name for track-based channels), `Value`, `Track` (null for `emsg`), `Timecode` (the sample entry, for timecode channels), lazy `Events`, `TryGetTimecodeAt(MediaTime, out Timecode)` |
| `TimecodeEvent` | `FrameNumber` (the sample's counter) and `Start` (the SMPTE timecode it means) |
| `Scte35Event` | a `Scte35Section` plus `IsSpliceTimeSpecified` and, from `emsg`, the carrying `EventMessageBox` |
| `TimedId3Event` | one `Id3Tag` (the existing ID3v2 reader's frames) |
| `MessageEvent` | an `emsg` whose scheme is not understood: scheme, value, id, data |
| `Scte35Section` | `splice_info_section`: command (`Scte35SpliceInsert`, `Scte35SpliceSchedule`, `Scte35TimeSignal`, `Scte35EmptyCommand` for null and bandwidth reservation, `Scte35PrivateCommand`), descriptors (avail, DTMF, time, segmentation; unknown ones as the base type), `pts_adjustment`, `SplicePts`, `AdjustedSplicePts` |
| `TimecodeSampleEntry` | the `tmcd` sample entry: flags (`TimecodeAttributes`), timescale, frame duration, frames per second, optional `name` atom; `FromFrameNumber` / `ToFrameNumber` |
| `EventMessageBox` | `emsg` version 0 and 1 |

Every channel is read lazily: `MediaFile.Open` touches no event bytes, and `Events` costs what its channels cost (below).

## Time

* **Timecode.** `TimecodeEvent.Time` is the sample's presentation time on its track (edit list applied). `TryGetTimecodeAt(t)` finds the sample
  covering `t` and adds the elapsed frames (exact rational arithmetic, no floating point). Drop-frame follows SMPTE 12M as publicly described: for
  30 and 60 frames per second, 2 or 4 frame numbers are dropped at the start of every minute except every tenth. Counting round-trips for
  every frame of two hours at 30 and 60 fps (a unit test). Other drop-frame rates are shown as non-dropping.
* **SCTE 35 in MPEG-TS.** Stream type 0x86 carries PSI-style sections (with a pointer field), not PES. Each complete section becomes one packet.
  `Time` is the splice time plus `pts_adjustment` modulo 2^33 (H.222.0 PTS arithmetic), unwrapped to sit near the stream clock, when the command
  states a time; for commands without one (immediate splices, null, schedule, bandwidth, private) it is the stream clock at the section's
  arrival, i.e. the latest PTS/DTS seen on any elementary stream, including streams switched off for reading.
* **Timed ID3 in MPEG-TS.** Stream type 0x15, PES with a PTS; `Time` is that PTS on the 90 kHz clock, raw (TS times are not zeroed).
* **`emsg`.** Version 1 states an absolute presentation time. Version 0 states a delta from the start of its segment: the base is the first
  `traf`'s `tfdt` of the next `moof` divided by that track's timescale, else the `sidx` earliest presentation time; if neither exists the event has
  only its delta and `MessageEvent.TimeIsAbsolute` is false. A duration of 0xFFFFFFFF is unknown.
* **SCTE 35 `utc_splice_time`** is stored raw: seconds since the GPS epoch with leap seconds counted, not Unix time.

## Where events are found, and what finding them costs

| Carriage | Found by | Cost |
|---|---|---|
| `tmcd` track in ISO | the track list (codec tag `tmcd`) | the track's packets (4 bytes each) |
| TS `scte-35`, `timed-id3` tracks | the PMT | a pass over the stream for that PID (sections are small; the demuxer already scans for packets) |
| `emsg` in ISO | a walk over **top-level box headers** to the end of the file | one header read per top-level box; media data is skipped, not read. `info` leaves it out on purpose so probing keeps its I/O; `Events` and `onemedia events` do the walk |
| Matroska | not an event source | see below |

## Matroska and other containers

Matroska has no timecode track. `ffmpeg -timecode` stores the value as a `TIMECODE` tag on the video track (observed in files written by ffmpeg 7.0.2
and listed by mkvinfo), so it is a native tag (`media.Native["mkv", "TIMECODE"]`), and `Events` is empty for such a file. A test covers it.

## Defects

All are typed `Defect`s with the byte offset of the structure, and reading continues where it safely can.

| Code | Meaning |
|---|---|
| `scte35.truncated`, `scte35.command-truncated`, `scte35.descriptor-loop`, `scte35.descriptor-length` | section, command or descriptor shorter than its length fields say; what was read is kept |
| `scte35.crc` | CRC_32 of the section does not match; the section is still returned (TSDuck rejects it) |
| `scte35.syntax-indicator`, `scte35.protocol-version`, `scte35.command-type` | values the syntax fixes or reserves (section_syntax_indicator 0, protocol_version 0, unknown command type) |
| `emsg.truncated`, `emsg.string`, `emsg.version`, `emsg.scte35` | box too short, unterminated scheme or value string, version above 1, a `urn:scte:scte35:2013:bin` message that does not parse |
| `tmcd.sample-size`, `tmcd.negative` | a sample shorter than 4 bytes; a negative frame counter where the entry does not allow it |
| `id3-event.not-a-tag` | bytes in a timed-ID3 payload that are not an ID3v2 tag (a payload may hold several tags back to back) |

## Limits

`MediaLimits` applies unchanged: `MaxElementCount` bounds the `emsg` box walk and the number of events in a channel, and `MaxTagBytes` the part of an
`emsg` payload that is read. A SCTE section carried in TS is bounded by the demuxer's packet limits. Parsers never allocate from a length field: they
take spans of bytes already read and check each length against what remains.

## Remux

The ISO muxer carries `tmcd` tracks MP4↔MOV unchanged (the sample entry bytes and the 4-byte samples), with no loss entry. `emsg` boxes are not
written by any muxer; `Remuxer.Inspect` reports a `Structure`/`NotWritten`/`emsg` loss per `emsg` channel. A transport stream with only event tracks
is refused with `MuxRefusedException` (neither track type has a target), not written empty.

## Oracle agreement (details in the unit report)

* Timecode: `ffprobe`'s `timecode` tag, 24 ffmpeg-written files (24, 23.976, 25, 29.97 and 59.94 drop-frame and non-drop, 30, 50, 60; MOV and MP4)
  and their remuxes: equal.
* SCTE 35: TSDuck 3.44-4676 `tstables --xml-output`, field by field on a generated stream (11 sections) and on a real capture (18 sections,
  10 distinct forms): equal. A 50-byte `splice_insert` rebuilt from TSDuck's published listing of the capture reproduces its CRC.
* **Deviation:** ffprobe 7.0.2 shows the 0x86 PID as an `unknown`/`CUEI` stream with no packets (and `ffmpeg -map` refuses it), so a packet-PTS
  comparison for SCTE 35 is impossible; TSDuck is the oracle for it.
* Timed ID3 in TS: `ffprobe -show_packets -show_data_hash` (PTS, size, hash) equal on 6/6; frames equal an independent decode.
* `emsg`: MP4Box `-diso` (scheme, value, id, data, time from `tfdt` plus delta) equal on 4/4.

## Sources

SCTE 35 itself was not obtained (the free download needs an account): the section layout was written from public descriptions and checked
against TSDuck output; the CRC check against a real capture's published listing. The tmcd layout comes from files written by ffmpeg and MP4Box's
dump of them; ID3-in-`emsg` uses the scheme string `https://aomedia.org/emsg/ID3`.

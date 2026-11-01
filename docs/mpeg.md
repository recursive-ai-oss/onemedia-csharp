# MPEG transport streams, program streams and raw video

What the readers in `RecursiveAI.OneMedia.MpegTs`, `.MpegPs`, `.ElementaryStreams` and `.Avchd` do, the defects they report, the choices
the specifications leave open, and every place they differ from ffprobe. Sources: ITU-T H.222.0 (= ISO/IEC 13818-1) for transport and
program streams, PES, PSI and the CRC; ISO/IEC 11172-1 as publicly described for MPEG-1 program streams; ISO/IEC 13818-2 sequence and
picture headers; ITU-T H.264/H.265/H.266 Annex B; the AV1 specification §5 for OBUs and the IVF header layout as published with the VP8/AV1
tools; ETSI EN 300 468 for service and event names; ExifTool's tag documentation for the AVCHD camera-data (MDPM) tag ids.

## Formats

| Format id | Recognised by | Tracks |
|---|---|---|
| `mpegts` | `0x47` at the packet spacing 188, 192 (a 4-byte `TP_extra_header` before each packet, as in M2TS/MTS) or 204 (16 bytes of Reed–Solomon parity after each packet); five repeats in the first 64 KiB | one per elementary stream of every program in the PAT |
| `mpegps` | pack start code `00 00 01 BA` (MPEG-2 `01` marker or MPEG-1 `0010` marker) | one per PES stream id (and per `private_stream_1` sub-stream id) |
| `h264`, `hevc`, `vvc` | Annex B start codes whose first NAL units are parameter sets or a slice | one video track |
| `mpegvideo` | sequence header `00 00 01 B3` with non-zero size | one video track |
| `obu` | temporal delimiter OBU, then a sequence header | one video track |
| `ivf` | `DKIF` | one video track (VP8, VP9 or AV1) |

Sniffers for the raw video kinds answer `Likely`, not `Certain`: an Annex B stream looks like any other start-code stream.

## Transport streams

- **Packet = access unit.** Video tracks deliver one access unit per packet (H.264/HEVC/VVC cut at the access unit delimiter or the first
  slice of a new picture; MPEG-1/2 at the picture start code with its sequence and GOP headers). Audio tracks deliver one codec frame per packet (ADTS/AC-3/E-AC-3
  frames, MPEG audio frames). PES payloads of other types (DTS, AAC-LATM, VC-1, PCM, subtitles, data) pass through, one PES payload per packet.
  A PES that holds several units, or a unit that spans PES packets, is re-cut; a PES with `PES_packet_length` 0 (video, §2.4.3.7) runs to the next payload unit start.
- **Time.** Raw 90 kHz values, unwrapped from 33 bits per PID (a value that falls more than half the range below the previous one adds 2^33).
  The first value is not rebased: `Track.Edit.StartTime` is the track's first presentation time and the container's `StartTime` is the earliest of them.
  Seek targets are in the same raw time; a target before the first keyframe lands on the first keyframe.
  A unit without its own stamp gets `dts = previous dts + duration`; presentation time is unknown for H.264/HEVC without a stamp (the reordering is not
  derived from picture order count). Frames after the first in a PES get `pts` from the frame length of the codec.
- **Duration** = latest `pts + duration` minus earliest `pts` over all tracks, read from the last 1 MiB when the source is seekable; forward-only sources report none.
- **Keyframes.** From `random_access_indicator` of the adaptation field where the stream sets it; otherwise from the bitstream (IDR pictures, intra random access points; MPEG-2 I pictures).
- **Probe.** Reads from the front until every PMT has been seen and every track is described, but at most 5,000,000 bytes (`MpegTsDemuxer.ProbeLimitBytes`), then the last 1 MiB for the
  duration. Service information (SDT, EIT) is read only from the probed front. A 2 GB transport stream therefore costs a fixed prefix and a fixed tail, not a scan.
- **Seeking** has no index to use: the file is bisected on the presentation time of the first video stream (to a 2 MiB window), then read forward to the last keyframe at or before the target.
- **Descriptors.** Language (`0x0A`), registration (`0x05`), AC-3 (`0x6A`), E-AC-3 (`0x7A`), DTS (`0x7B`), AAC (`0x7C`), Opus/extension (`0x7F`) and the ISO 639 code identify
  codecs and languages; stream types map to codec ids in `TsStreamTypes`. `mpeg1video`, `mp2`, `mp1` and `aac-latm` are codec ids first used here.
- **M2TS.** A 192-byte stream with an AAC or other private stream and no descriptor is identified by sniffing its first payload (`ProbingAssembler`).

### Tags and facts

- System `mpegts`: `program_number`, `service_name`, `service_provider` (SDT service descriptor), `event_name`, `event_text` (EIT short event descriptor), `transport_stream_id`.
  Common view: Title from the event name, then the service name; Description from the event text (rows in `docs/tag-mapping.md`). ATL.NET and TagLib# read none of these formats.
- System `mdpm` (AVCHD camera data in H.264 SEI user data, found in the first access units): `DateTimeOriginal`, `Make`, and every entry as `raw_XX` (tag id in hex, 4 data bytes in hex).
  The recording time is the camera's clock; the zone is `Unknown` because the flag byte of entry 0x18 is not decoded (see below). Capture-time candidate rank 35 (after a file-level vendor day tag, before XMP without a zone).
  GPS and exposure entries stay raw: no published sample carries them.
- System `avchd-sidecar`: files `NNNNN.CPI`/`.clpi` (same stem as the stream) from `CLIPINF` and every `.MPL`/`.mpls` from `PLAYLIST`, found beside a stream under a `STREAM` directory when the file is opened by path.
  The key is the file name, the text is the path and size. **The contents are not decoded**: no sample file, oracle or description shown to be independent of the Blu-ray Disc Association books was available,
  so the clip-information and playlist parsers are deferred.

### MDPM and the time zone

ExifTool 13.60 prints `DateTimeOriginal 2012:01:26 09:18:35+00:00` for the published sample (`00007.mts`, entries 0x18 = `00 20 12 01`, 0x19 = `26 09 18 35`); the first byte of 0x18 is `00`.
One sample cannot settle what the flag byte means, so the offset ExifTool prints is not reproduced and the zone is `Unknown`.

## Program streams

Pack headers (MPEG-2 with the system clock reference extension and program mux rate; MPEG-1), system header, program stream map (stream types for codec ids), PES with PTS/DTS.
`private_stream_1` sub-streams: AC-3 / E-AC-3 (`0x80`–`0x87`, framed), DTS (`0x88`–`0x8F`), LPCM (`0xA0`–`0xA7`) and DVD sub-pictures (`0x20`–`0x3F`) pass through one PES payload per packet.
`private_stream_2` (the VOB navigation packs, `00 00 01 BF`) becomes a data track with codec `dvd-nav`, one packet per pack, payload unchanged.
MPEG-2 pictures without a PES stamp get a presentation time from the temporal reference placed relative to the GOP's last stamped picture.
Probe: at most 5,000,000 bytes from the front (at least 256 KiB), tail 1 MiB for the duration, bisect seek as for transport streams.

## Elementary video streams

No timestamps exist, so: the track time scale is the numerator of the stream's frame rate (VUI timing, or the sequence header's rate code), 25 when it declares none; `dts = n × denominator`;
presentation time is unknown except for AV1, which does not reorder (`pts = dts`). IVF carries its own: the header's time base is the track time scale and each frame header's 64-bit stamp is the `pts`.
`SourceRange` is the exact access unit (the payload, for IVF). Seeking scans from the start.

## Defects

| Code | Kind | Meaning |
|---|---|---|
| `ts.sync-lost` | UnaccountedBytes | the `0x47` byte is missing at the packet spacing; the reader resynchronises on the next place where it repeats |
| `ts.leading-bytes` | UnaccountedBytes | bytes before the first transport packet |
| `ts.partial-packet` | Truncated | the stream ends inside a packet |
| `ts.continuity` | InvalidOrder | `continuity_counter` jumped (packets lost), or changed in a packet with no payload (H.222.0 §2.4.3.3) |
| `ts.duplicate-packet` | Duplicate | the same counter twice with identical content, which §2.4.3.3 allows once; dropped |
| `ts.transport-error` | ChecksumMismatch | `transport_error_indicator` set; the payload is dropped |
| `ts.section-crc` | ChecksumMismatch | a PSI/SI section fails its CRC-32 (Annex A) and is ignored |
| `ts.section-too-long` | SizeExceedsParent | a section longer than the 4,096 bytes allowed |
| `ts.adaptation-length` | SizeExceedsParent | the adaptation field runs past the packet |
| `ts.pes-start` | InvalidValue | a payload unit start that does not begin with `00 00 01`; the unit is skipped |
| `ts.pmt-descriptors` | SizeExceedsParent | a PMT stream entry's descriptor loop runs past the section |
| `ts.pid-conflict` | Duplicate | a PID announced both as an elementary stream and as program tables |
| `ps.skipped-bytes`, `ps.unexpected-start-code`, `ps.pes-header`, `ps.truncated-packet`, `ps.late-stream` | various | bytes skipped between packets; a start code that is not a pack/system/PES code; a PES header that does not fit; a packet cut by the end; a stream first seen after the probe |
| `mpeg.audio-sync` | InvalidValue | an audio frame header that fails its checks; the reader searches on |
| `obu.header` | InvalidValue | an OBU header with the forbidden bit set or a size past the data |
| `ivf.header-length`, `ivf.time-base`, `ivf.truncated-header`, `ivf.truncated-frame` | various | header length not 32, time base zero, file ends in a frame header or frame |

All keep reading where they safely can. The limits object bounds PES and unit sizes (`MaxPacketBytes`), buffer growth (`MaxAllocationBytes`) and track count.

## Asynchronous opening

`OpenAsync` for ranged and remote sources runs the synchronous reader on a pool thread over a blocking range adapter. It is correct and I/O-bounded the same way,
but it holds a pool thread while it waits and is **not suitable for single-threaded browser WebAssembly**; a true asynchronous reader needs an asynchronous `ByteInput` (planned with the WASM suite).

## Differences from ffprobe 7.0.2

Each is scoped in `tools/OneMedia.Oracles/exceptions.tsv`. Counts are in the sweep summary below.

| Class | Scope | What differs and why |
|---|---|---|
| `raw-stream-dts` | `h264`, `hevc`, `vvc`, `mpegvideo` | a raw stream has no timestamps; ffprobe prints N/A for dts, we count frames at the frame rate |
| `raw-mpeg2-pts` | `mpegvideo` | ffprobe derives pts from temporal references; ours is unknown in elementary streams |
| `ivf-pos` | `ivf` | ffprobe's `pos` is the frame header, ours the payload (the packet's source range) |
| `nav-packets` | `mpegps` data track `dvd_nav_packet` | ffprobe lists no packets for the navigation stream |
| `audio-pts-interpolation` | TS/PS audio | see below |

<!-- sweep results and the audio class are added after the final run -->

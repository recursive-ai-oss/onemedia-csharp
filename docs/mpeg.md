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
  A PES packet that carries only a PTS gives `dts = pts` (H.222.0 §2.4.3.7), also when the pictures are reordered; the decode times of such a stream are then not monotonic.
- **Field pictures.** An H.264 stream may code each frame as two field pictures, each its own access unit. The slice header says which (`field_pic_flag`, `bottom_field_flag`, §7.3.3;
  `frame_num` width and `frame_mbs_only_flag` come from the sequence parameter set). A field lasts half a frame (rounded down); the second field of a pair that has no stamp of its own
  is half a frame (rounded up) after the first, in both `pts` and `dts`. MPEG-2 field pictures are paired by `temporal_reference` and opposite parity and make one packet.
- **Durations.** A video packet lasts one frame at the rate the sequence header declares (rounded down to a whole 90 kHz tick); an MPEG-2 picture with `repeat_first_field` lasts
  1.5 frames (progressive sequences: 2 or 3 frames, §6.3.10). An H.264 picture whose picture timing SEI has `pic_struct` 5 or 6 lasts 1.5 frames, 7 (frame doubling) two
  and 8 (frame tripling) three (Annex D, Table D-1; found with `avchd_lite_14s_playback_speed_problems.MTS`, declared 59.94 frames per second, every picture
  doubled, so 3003 ticks apart). The declared rate stays in the track's frame rate; stamp spacing alone never overrides it (ffprobe 7.0.2 agrees on a stream
  with 3600-tick spacing and a 1800-tick declaration but no `pic_struct`). Streams without a declared rate take the distance to the next decode time.
  The container duration is the audio and video range: a subtitle or data stream that starts early does not move it (found with `dean00007.mts`, whose subtitle starts 66 ms early).
- **Duration** = latest `pts + duration` minus earliest `pts` over all tracks, read from the last 1 MiB when the source is seekable; forward-only sources report none.
- **Keyframes.** From the bitstream: H.264 IDR pictures and recovery point SEI messages (the SEI is unescaped before its payload sizes are read), HEVC IRAP pictures, MPEG-2 I pictures,
  MPEG-4 Part 2 intra VOPs; and an H.264 intra picture that opens with a sequence parameter set (cameras that write no recovery point SEI mark GOP starts this way: `avchd_lite…MTS`),
  unless it is the second field of a frame. The adaptation field's `random_access_indicator` is not used: cameras set it inconsistently.
- **Probe.** Reads from the front until every PMT has been seen and every track is described, but at most 5,000,000 bytes (`MpegTsDemuxer.ProbeLimitBytes`), then the last 1 MiB for the
  duration. Service information (SDT, EIT) is read only from the probed front. A 2 GB transport stream therefore costs a fixed prefix and a fixed tail, not a scan.
- **Seeking** has no index to use: the file is bisected on the presentation time of the first video stream (to a 2 MiB window), then read forward to the last keyframe at or before the target.
- **Descriptors.** Language (`0x0A`), registration (`0x05`), AC-3 (`0x6A`), E-AC-3 (`0x7A`), DTS (`0x7B`), AAC (`0x7C`), Opus/extension (`0x7F`) and the ISO 639 code identify
  codecs and languages; stream types map to codec ids in `TsStreamTypes`. `mpeg1video`, `mp2`, `mp1` and `aac-latm` are codec ids first used here.
- **M2TS.** A 192-byte stream with an AAC or other private stream and no descriptor is identified by sniffing its first payload (`ProbingAssembler`).
  The probe buffers up to 8 KiB of the stream before it decides, because a capture that starts mid-frame shows its first AC-3 sync word (`0x0B77`, checked against the next frame's) only later.
- **No tables.** A capture without PAT/PMT in its first packets (`foxksaz.ts`) is described by guessing from the PES stream ids; the search for tables runs for at most 24 MB. Tables that
  arrive later rewind the read and replace the guesses. ATSC stream type `0x80` is MPEG-2 video unless the packets are M2TS or the program carries an `HDMV` registration (then LPCM); `0x81` is AC-3.
- **Damaged streams.** A byte that is not `0x47` where a packet should start makes the reader search for the next place where packets line up; the tail scan for the duration does the same.
  A packet with `transport_error_indicator` set, or a repeated packet (same continuity counter and content), is dropped and reported; the unit that spans a loss is marked corrupt.

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

### Agreement with ExifTool 13.60 and MediaInfo 26.10

`OneMedia.Oracles meta` over the three AVCHD/TS samples and the generated transport streams (`out6/metadata-compare.tsv`, 28 rows): 9 match both tools, 10 match MediaInfo where ExifTool prints nothing, 8 differ, 1 is missing on our side.
Every row that is not a match:

- `Duration` (5 files): ours equals ffprobe's container duration (`4.021`, `14.016`, `12.064`); ExifTool prints the span from the first to the last stamp (`3.920`, `13.983`, `12.093`) and MediaInfo its own estimate (`4.118`, `4.024`, `4.028`, `13.987`, `12.116`). Each tool has its own end rule; none is wrong, they name different things.
- `mdpm:raw_18`, `raw_19`, `raw_E0` (`dean00007.mts`): entries we keep verbatim and neither tool prints (ExifTool decodes `0x18`/`0x19` into `DateTimeOriginal`, which matches ours at `2012-01-26 09:18:35`; `0xE0` is an unnamed entry).
- `Encoder` (`dean00007.mts`): MediaInfo prints `Panasonic` there (the MDPM make); ours is the `Make` fact, which ExifTool agrees with.
- `mpegts:program_number`, `mpegts:transport_stream_id`: ExifTool prints neither; MediaInfo agrees with ours.
- Capture time: ExifTool prints `+00:00` after the camera's clock time; the recording zone is not decoded here (`Unknown`), so ours has no offset. The wall-clock time is equal.

## Program streams

Pack headers (MPEG-2 with the system clock reference extension and program mux rate; MPEG-1), system header, program stream map (stream types for codec ids), PES with PTS/DTS.
`private_stream_1` sub-streams: AC-3 / E-AC-3 (`0x80`–`0x87`, framed), DTS (`0x88`–`0x8F`), LPCM (`0xA0`–`0xA7`) and DVD sub-pictures (`0x20`–`0x3F`) pass through one PES payload per packet.
A DVD sub-picture unit (its first two bytes give its size) is joined from as many PES packets as it needs. DTS and LPCM payloads pass through one PES per packet.
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
but it holds a pool thread while it waits. Single-threaded browser WebAssembly has no thread to wait on: there a source that answers at once (memory, a file in the virtual file system) works,
and a source that answers later is refused with a `NotSupportedException` that says so, instead of a hang. A true asynchronous reader needs an asynchronous `ByteInput`; see `docs/platforms.md`.

## Differences from ffprobe 7.0.2

The pinned ffprobe/ffmpeg 7.0.2 static build **crashes (segmentation fault)** on any transport stream that contains the SDT ffmpeg's own muxer writes. The packet
differential therefore runs on copies with that table removed (`tools/corpus/ts_tools.py stripsdt`, kept under `generated/mpeg/tsx/`); the SDT itself is checked by the unit tests and by
ExifTool and MediaInfo. Every other difference below is a scoped rule in `tools/OneMedia.Oracles/exceptions.tsv` (rule, follows, reason); a rule only excuses the difference it names.
Sweep (`tools/run-mpeg-sweep.sh`): 149 files (generated transport, program and elementary streams, and the published samples), 54,785 ffprobe packets and 53,814 of ours (the difference is the classes below); 0 unexplained.

| Class | Scope | What differs and why |
|---|---|---|
| `raw-stream-dts`, `raw-mpeg2-pts`, `raw-stream-duration-rounding` | `h264`, `hevc`, `vvc`, `mpegvideo` | a raw stream has no timestamps: ffprobe prints N/A (or derives pts from temporal references, or rounds the frame duration in its 1/1200000 base); ours counts frames at the stream's rate |
| `ivf-pos` | `ivf` | ffprobe's `pos` is the frame header, ours the payload (the packet's source range) |
| `nav-packets` | `mpegps` `dvd_nav_packet` | ffprobe lists no packets for the navigation stream |
| `audio-pts-interpolation` | TS/PS audio | frames after the first in a PES are placed by frame length; rounding differs by at most one tick (16 frames in `dvd-pcm.vob` and `last_samurai.ts`) |
| `unstamped-units` | TS/PS video | a picture inside a PES without a timestamp has none in ffprobe; ours continues decode time and, for MPEG-2, places the picture by temporal reference |
| `pts-only-with-reordering` | `Aladin.mpg` | PES packets with PTS only on a reordered stream: ours `dts = pts` per H.222.0, ffprobe derives a monotonic dts |
| `pts-from-neighbour` | TS subtitle/data | ffprobe gives a teletext PES without PTS the time of its neighbours; ours leaves it unknown |
| `leading-fragment`, `leading-fragment-stamp`, `pos-of-leading-fragment`, `leading-junk-in-first-frame` | TS/PS | a capture that starts mid-unit: ffprobe lists the tail as a packet (and gives it the first PES packet's stamp and position, or merges up to 564 junk bytes into the first audio frame); ours starts at the first unit that begins in the file and gives it the stamp (H.222.0 §2.4.3.7). The harness pairs packets after dropping the 1–3 surplus that pair best |
| `cut-tail` | files cut inside a frame | ffprobe lists the incomplete last audio frame; ours stops before it and reports `Truncated` |
| `late-stream-and-cut-tail` | `TITLE01-ANGLE1.VOB`, `dvd.mpeg` | a sub-picture stream first seen after the probed prefix (1.66 MB into 3.7 MB) is a defect (`ps.late-stream`), not a track |
| `dvd-lpcm-header`, `dvd-lpcm-duration` | `mpegps` LPCM | ffprobe keeps the 3-byte audio header in each packet and derives a duration from it; ours delivers the samples |
| `dts-core-frames` | `mpegps` DTS | ffprobe frames DTS; ours delivers each PES payload as one packet (no DTS framer in 0.2) |
| `opus-in-ts-control-headers` | `mpegts` Opus | ffprobe splits PES payloads by the Opus control headers; ours does not (not implemented in 0.2) |
| `mpeg4-visual-duration` | `mpegts` MPEG-4 Part 2 | no frame rate is read from its headers; ours has key frames, ffprobe also has durations |
| `declared-rate` | `avchd_lite…MTS` | the SPS declares 59.94 Hz, the camera stamped 29.97 Hz; ours states the declared rate |
| `measured-spacing` | `foxksaz.ts` | video without a sequence header at the start: ours takes durations from stamp spacing (1501/1502), ffprobe 1501 |
| `timestamp-wrap-base` | `wrap-33bit.ts` | the stream starts just below 2^33: ffprobe subtracts 2^33 (negative times), ours keeps the stamps and continues upward through the wrap |
| `positions-in-204-byte-packets` | `h264-aac-204.ts` | ffprobe's `pos` is 16 bytes short; ours is the offset of the transport packet |
| `duplicate-packet-payload`, `transport-error-payload` | damaged generated streams | ffprobe appends the payload of repeated and error-flagged packets; ours drops them (H.222.0 §2.4.3.3) and reports |
| `garbage-between-packets` | `block_green…ts` | ffprobe keeps the 132 stray bytes in the picture; ours skips them |
| `broken-timestamps` | `broken-ntsc.mpg`, `block_green…ts` | the stamps are irregular on purpose; the two read different ones (and the pinned ffprobe reports a six-hour duration for 13 s) |
| `ffprobe-open-fails` | `h264-aac-garbage.ts`, `res_change_ffmpeg_aspect.ts` | the pinned ffprobe reports no streams; ffprobe 8.0 lists them, with the packets equal to ours but for the garbage bytes and one 66-byte leading junk |
| `ffprobe-duration-glitch` | `nature_704…h264` | ffprobe prints a one-tick duration for most packets |
| `duration-tail-estimate` | container duration (`infocheck`) | ours is the last stamped PES packet plus one frame over the last 1 MiB; ffprobe parses the tail's packets. Within 40 ms on every file |
| `duration-from-bitrate` | MPEG-1/2 elementary streams | ffprobe estimates a duration from the bit rate; ours is unknown |
| `mpeg1-video-detected` | `mpeg1-mp2.ts` | PMT type `0x01` and a sequence header without extension: MPEG-1; ffprobe names all MPEG video `mpeg2video` |

Observations that settled choices: in `dean00007.mts` and `avchd_lite…MTS` the adaptation field's `random_access_indicator` is never set, so key frames come from the bitstream; the `avchd_lite` camera
marks its GOP starts with SPS+PPS and an I slice and no recovery point SEI, and ffprobe marks those pictures (and not the second field of an IDR frame that repeats the parameter sets, as in `HD-h264.ts`).

## Large file

`generated/mpeg/large/ts-2gb.ts` (2,371,987,540 bytes, H.264 + AC-3, the seed looped; SDT stripped for the pinned ffprobe). Probe and metadata read, ours next to the tools
(bytes / reads; local `strace` counts, HTTP range server for the second column):

| Tool | Local reads | Over HTTP range |
|---|---|---|
| ours | 1,576,976 B / 4 | 1,576,976 B / 5 requests |
| ffprobe 7.0.2 | 5,460,112 B / 170 | 5,591,184 B / 3 |
| MediaInfo 26.10 | 17,402,896 B / 266 | 17,629,008 B / 2 |
| ExifTool 13.60 | 509,424 B / 64 | — |

Full pull of the file: 87,738 packets, 2.31 GB, **0 bytes allocated per packet after warm-up**, peak working set 44 MB (the process, not the file), 0.9 s with the file in the page cache.

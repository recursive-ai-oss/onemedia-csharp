# Timed telemetry

Decoders for the timed sensor data that cameras and phones store in a data track of an ISO base media file: GoPro GPMF (`gpmd`), Google CAMM (`camm`) and Apple QuickTime timed metadata (`mebx`), DJI per-frame text and `djmd` protobuf, and AVCHD camera data (MDPM) in the SEI of a transport stream. Namespaces `RecursiveAI.OneMedia.Metadata.Telemetry` (reader, items, channels) with `.Gpmf`, `.Camm` and `.Mebx` for the format layers. Nothing is decoded from audio or video samples.

## API

- `media.Telemetry`: every telemetry track as typed `TelemetryChannel` lists (the same model the fact resolver reads). Eager: it reads the whole track, so use the pull reader for long files.
- `MediaFile.OpenTelemetry(track)` returns a `TelemetryTrackReader`; `TryRead(out TelemetryItem item)` is the pull path. It owns the file read position like the other readers, keeps one packet in memory, and allocates nothing per sample once its buffers have grown (test: decoders into a reused collector allocate 0 bytes over 200 repeats; pulling `hero5.mp4`, 22,861 samples, allocates 0 bytes after the first quarter of the file).
- `TelemetryItem` is a struct: `Kind`, channel index, exact `MediaTime Time` and `Duration`, up to 14 `double` components, the source `Range` of the sample's bytes, and for GPS the fix and GPS time. `RawData(item)` returns the sample's bytes for items the decoder keeps raw.
- `GpmfKlv`/`GpmfKlvReader` and `CammPacket`, `MebxKeyTable` expose the format layers.
- `TelemetryFacts.LocationCandidates` and `CaptureTimeCandidates` turn the first GPS fix and the first GPS time into fact candidates. The resolver ranks them.

## Timing

A packet (container sample) has an exact presentation time and duration. The `N` samples a payload holds are spread evenly: sample `i` starts at `pts + duration * i / N`, computed exactly (timescale = packet timescale x N). GPS5 per-sample GPS time is the payload's `GPSU` plus the same spread; GPS9 carries its own time (days since 2000-01-01 and seconds since midnight). CAMM and `mebx` samples are one record per packet.

## Item layout

| Kind | Components |
|---|---|
| Gps | 0 latitude, 1 longitude, 2 altitude (m), 3 2D speed (m/s), 4 3D speed (m/s), 5 DOP (NaN: none), 6 fix. GPS9 adds 7 days, 8 seconds; CAMM type 6 adds 7 horizontal accuracy, 8 vertical accuracy, 9 east, 10 north, 11 up velocity, 12 speed accuracy, 13 raw GPS-epoch seconds |
| Vector3 | x, y, z in stored order |
| Orientation | w, x, y, z; CAMM type 0 also keeps the raw angle-axis at 4..6. GPMF CORI/IORI are assumed w,x,y,z (unverified) |
| CameraSettings | component 0 is the value (GPMF SHUT seconds, ISOG/ISOE, WBAL kelvin; CAMM type 1 pixel exposure ns, with rolling-shutter skew ns in 1) |
| Scalar | values of one sample (mebx numbers, GPMF scalar streams) |

SCAL divisors are applied; units come from SIUN/UNIT. Keys with no typed mapping (and complex multi-structure keys such as HUES, FACE, SCEN, MFGI, VPTS) are emitted as raw items with their byte range and type, in their own Unknown channel. Device-level GPMF fields outside a stream (for example `MINF`) are emitted once per device as raw items.

## GPMF

KLV: 4-byte key, type, size, repeat (big-endian), data padded to 4 bytes; type 0 nests. `DEVC > DVID, DVNM, STRM`; in a stream SCAL, TYPE, SIUN and UNIT apply to the data keys after them, while STNM, TSMP, GPSU, GPSF, GPSP, ORIN and ORIO describe the stream wherever they stand. ORIN is the axis order as stored and ORIO the order to present; the decoder keeps stored order, and `GpmfAxes.TryRemap` converts (output axis i is the input component naming the same axis, negated when the letters differ in case). GoPro's demo remaps; ExifTool does not.

## CAMM

Packet: u16 reserved, u16 type, little-endian. Types 0 to 7 decode as in the Google specification; other types are raw. `time_gps_epoch` is "time since the GPS epoch" in the specification. We read that as the GPS time scale (1980-01-06, leap seconds not counted) and convert to UTC with a leap-second table; the raw seconds stay in component 13. ExifTool reads the number as Unix time. No real CAMM device file was available to say which writers follow the specification, so this reading is a default that may change.

## mebx

The sample entry's `keys` box lists local key ids with `keyd` (namespace and key) and `dtyp` (data type). A sample is concatenated atoms `[size][local key id][value]`. Ids 0 and 0xFFFFFFFF are placeholders and ignored. Well-known types 1, 2, 21-24, 28, 65-67, 70-72, 74-79 decode; nested atoms (type 28, e.g. detected-face groups) are walked up to depth 6. `com.apple.quicktime.location.ISO6709` becomes a Gps item. Detected faces are personal data: only synthetic samples are committed, and the nested layout is built from ExifTool's tag documentation and unverified on a device.

## DJI per-frame text (`dji-srt`)

DJI drones write one text block per frame (or per second) as a timed-text (`tx3g`/`text`) subtitle track or as a `.SRT` sidecar. `DjiSrt.Read(bytes or path)` decodes a sidecar; a subtitle track decodes through `media.Telemetry` / `OpenTelemetry` like any other (every `tx3g` or `text` track is a candidate and yields nothing unless its text has DJI keys). There is one parser (`DjiTextDecoder`); `Sidecars.ReadDjiSrt` uses it for the capture-time and location evidence.

Layouts read, from the published descriptions of DJI's output: bracketed `[key: value]` pairs (several pairs may share a bracket, spaces around the colon are free); `SrtCnt`/`FrameCnt` and `DiffTime`; the clock line `YYYY-MM-DD HH:MM:SS,mmm` (comma or dot); `GPS(lat,lon,alt)` with a unit letter after the altitude; `BAROMETER(v)` and `BAROMETER:vM`; the compact line `F/5.6, SS 400, ISO 100, EV 0, GPS (a, b, c)`; `F.PRY (pitch, roll, yaw)` and `G.PRY (...)`. `HOME` and distance fields are not decoded. Keys: `latitude`, `longitude` (also the misspelling `longtitude`), `rel_alt`, `abs_alt`, `iso`, `shutter`, `fnum`, `ev`, `ct`, `focal_len`, `gb_yaw`, `gb_pitch`, `gb_roll`; others (`color_md`, `tint`, `pp_*`, `dzoom_ratio`) are skipped.

Units: `fnum` and `focal_len` written as integers are scaled (f-number / 100, focal length / 10, as the older firmware writes them), written with a decimal point they are literal. A shutter of `1/N` is the exact fraction; `SS N` in the compact line is `1/N`.

| Channel | Kind | Components |
|---|---|---|
| `dji gps` | Gps | latitude, longitude, absolute altitude (m), NaN speeds and DOP, fix 0 |
| `dji altitude` | Scalar | relative altitude, absolute altitude |
| `dji camera` | CameraSettings | ISO, shutter (s), f-number, EV, colour temperature (K), focal length (mm) |
| `dji gimbal`, `dji aircraft` | Orientation | yaw, pitch, roll in degrees (`OrientationSample` Euler fields) |
| `dji barometer` | Scalar | value |
| `dji clock` | Scalar | year, month, day, hour, minute, second, millisecond, frame counter, DiffTime ms |

The clock line carries no time zone and is not GPS time: `GpsSample.GpsTime` stays empty, the clock is its own channel. Every item's range is the whole text sample (file offset for a track, the cue text inside the sidecar), so a later strip operation can blank the block.

Settled by observation, not by a real device file: no freely licensed DJI sample was found (see Verification), so the layouts follow the descriptions. The compact `GPS (a, b, c)` line of the Phantom 4 RTK description shows values that look like longitude first; the decoder reads latitude first as the description says, which is unverified.

## DJI `djmd` protobuf (`djmd`)

Newer DJI cameras write a `djmd` data track of protocol buffer messages (and a `dbgi` track of debug values, hundreds of megabytes per recording, which is not read). `ProtobufReader` walks any message without a schema (field number, wire type, value or payload). `DjmdDecoder` maps fields by protocol name (the first string of the message, for example `dvtm_wm265e.proto`) and path of field numbers, using the paths of the public tag documentation. Conventions were read back from ExifTool 13.60 on synthetic messages (`tools/gen-dji-fixtures.py`): coordinates are doubles (radians when the unit field of the GPS message is 0, degrees otherwise), altitudes are integers in millimetres (the relative one may be a float), attitude is a signed integer in tenths of a degree, ISO and colour temperature are integers. Protocol families mapped: Mavic 3 / Matrice 30 (`wm265e`, `wm260`, `wm261`, `pm320`), Air 3 / Mini 4 Pro / Lito (`Air3`, `Air3s`, `Lito_X1`, `Mini4_Pro`), Mavic 4, Matrice 4E (`wa345e`), Avata 2 / Neo (`AVATA2`, `NEO2`, `dji_neo`), Osmo Action 4 to 6 and Osmo 360 (`ac203`, `ac204`, `ac206`, `oq101`). Shutter, f-number, quaternions, accelerometer and GPS date-time are not mapped (ExifTool's formats for them were not settled by a readable synthetic value); they stay in the raw message.

Channels: `djmd gps`, `djmd altitude`, `djmd camera`, `djmd aircraft`, `djmd gimbal`, `djmd frame` (typed, as for the text), plus raw channels `djmd message` (every sample's whole message, so any unmapped field is reachable with `ProtobufReader` and its byte range), `serial number` and `model` (the strings, with the byte range of the text for a later strip operation). A protocol not in the table gives only the raw channels. Aircraft serial numbers are personal data: those items keep their ranges.

## AVCHD camera data (`mdpm`)

AVCHD camcorders repeat the camera data block (SEI user data unregistered, UUID `17ee8c60-f84d-11d9-8cd6-0800200c9a66`, marker `MDPM`) about once per group of pictures in the H.264 video of an M2TS/MTS stream. The `Mdpm` format reads those blocks from the video packets of a transport stream (`TelemetryTrackReader.FormatOf(file, track)`; it needs the file because only the container tells TS from other H.264). Reading the telemetry therefore reads every video packet of the file: the cameras put the block in the video, and nothing else locates it. A stream without GPS entries yields no channel.

Entries (tag byte and four data bytes; rationals are two big-endian 16-bit halves): `B1`/`B2`-`B4` latitude reference and degrees, minutes, seconds; `B5`/`B6`-`B8` longitude; `B9`/`BA` altitude reference and metres; `BB`-`BD` UTC time of day; `BE` status (`A` active); `BF` measure mode (`2`/`3`); `C0` DOP; `C1`/`C2` speed with unit (K, M, N); `CA`-`CC` date digits. Channel `mdpm gps`: latitude, longitude (signed by the references), altitude, speed (m/s), NaN, DOP, mode; fix 2D/3D only when the status is `A`; `GpsTime` only when the three date entries hold eight digits (`YYYYMMDD`). That date layout is not confirmed on a camera file (ExifTool joins the entries as text and loses the day for the layout we write), so a stream without it reports no GPS time. The item range is the video packet, because the SEI is spread over the transport packets; a later strip operation must rescan it.

## Device facts from telemetry

`TelemetryFacts.MakeCandidates` and `ModelCandidates` feed the default resolver behind the container's own candidates. GoPro: the camera model is the `MINF` text of the GPMF block in the `udta` atom (read at open without touching the track; matches ExifTool's `Model` on all six GoPro files that have one, and ExifTool reports none for the files without), else a `DVNM` device name that is not a settings block (`Camera`, `Global Settings`, `Highlights`, lens and calibration blocks are skipped); the make "GoPro" is inferred from the format. DJI: the `model` string of `djmd` metadata and the make "DJI" inferred from the format. Device names that need the track need `media.Telemetry` to have been read.

## Defects and limits

Hostile DJI text, protobuf and SEI input never throws and adds no defect codes (text keys that do not parse are skipped, a damaged protobuf message ends the walk). Defect codes: `gpmf.truncated` (nested data past its parent, clamped), `gpmf.depth`, `gpmf.type-size`, `camm.short`, `camm.trailing`, `mebx.atom-size`, `telemetry.items`. Limits: 65,536 items per packet, GPMF depth 6, mebx depth 6. Hostile input never throws from the decoders.

## Privacy

The privacy strip (`PrivacyStrip`, `Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`) removes position and device identifiers from telemetry tracks in ISO files: in `gpmd` (GoPro GPMF) the position keys (`GPS5`, `GPS9`, `GPSU`, `GPSF`, `GPSP`, `GPSA`, `GPRI`, `GLPI`, `LNED`) and device identifiers (`CASN`, `MUID`, `LINF`, `CINF`) have their values zeroed and their keys renamed `EMPT`; in `camm` the position packet types (4, 5, 6) are zeroed and retyped; in `mebx` atoms whose key names a location or serial are zeroed; in DJI `djmd` protobuf the position and altitude fields are overwritten. Sample lengths and chunk offsets do not change, and each stripped stream is listed in the report. Other data tracks are searched only for the values read from removed fields, and free text, unknown vendor atoms and trailers are not examined. See [strip.md](strip.md) for the full list and the limits. `onemedia tags` and `TagWriter` edit tag fields only. Every location and device item keeps its `Range`.

## Verification

Method: `tools/run-telemetry-compare.sh` (ExifTool 13.60 `-ee -n -b -j -G3 -a`, and GoPro's gpmf-parser v2.2.1 `gpmfdemo -a -f` run as a released binary).

| Check | Result |
|---|---|
| GoPro samples (Fusion, hero5, hero6, hero6+ble, hero6a, hero7, hero8, karma, max-360mode, max-heromode) vs ExifTool | ACCL, GYRO, MAGN, GRAV, CORI, IORI, SHUT, ISOG/ISOE, WBAL, WRGB, YAVG, UNIF, FCNM, GPS5: equal counts, max abs difference <= 1e-13 (float noise). GPS block times and GPSMeasureMode/GPSP sets equal |
| same vs `gpmfdemo` | every key equal in count and value (3 decimals, tolerance 5e-4), including Karma's 16 drone keys, after the ORIN to ORIO remap |
| CAMM fixture, types 0-7, vs ExifTool | equal; ExifTool's "Truncated camm record" is our `camm.short` |
| mebx fixture vs ExifTool | location, orientation, vitality score, still-image-time, detected face, unknown text key equal; ExifTool warns about placeholder ids, we ignore them |
| CAMM and mebx fixtures vs ffprobe data stream | packet count, pts, duration and size equal to the packets we decode from |

Explained differences: ExifTool prints GPS speed in km/h (ours m/s, ratio exactly 3.6); ExifTool's CAMM epoch (above); ExifTool does not remap axes.

Not compared value by value: HUES, FACE, SCEN (the demo lists complex multi-structure keys differently; HUES first rows match), MFGI and VPTS (no oracle prints them). A mapping of HUES/FACE onto ExifTool tags was tried and dropped because ExifTool's layout differs; they stay raw with ranges.

Fuzz: targets `telemetry-gpmf`, `telemetry-camm`, `telemetry-mebx`, `telemetry-file`.

### DJI and MDPM

Method: `tools/run-dji-mdpm-compare.sh` (synthetic files from `tools/gen-dji-fixtures.py` and `tools/gen-mdpm-gps-fixtures.py`, ExifTool 13.60 `-ee -n -j -G3 -a`, `tools/dji-mdpm-compare.py`).

| Check | Result |
|---|---|
| `djmd-real.mp4` (wm265e layout, 5 samples) vs ExifTool | latitude, longitude, absolute and relative altitude, ISO, drone and gimbal yaw/pitch/roll, frame number: equal counts, max difference 0 |
| `djmd-osmo.mp4` (ac203 layout) vs ExifTool | latitude, longitude, GPS altitude, ISO, colour temperature, frame number: equal, max difference 7e-15 |
| `mdpm-gps.mts`, `mdpm-south-west.mts` (3 blocks) vs ExifTool | latitude, longitude (signed by the references), altitude, DOP, speed: equal, max difference 4e-14 |
| published AVCHD samples `dean00007.mts`, `avchd_lite_14s_playback_speed_problems.MTS` | no GPS in either; ExifTool and OneMedia both report none |
| Pull path | decoders into a reused collector allocate 0 bytes (test); 300,000 text samples (1,200,000 items, 80 MB file): 0 bytes after the first quarter, 41 MB resident |

One real `djmd` recording is in the device corpus: a DJI Osmo Pocket 3, whose 40 samples equal ExifTool's reading ([device-corpus.md](device-corpus.md)). Its protocol (`PP-101`) is not in the layout table, so its position fields are not mapped and strip leaves those samples unchanged ([strip.md](strip.md)). No freely licensed DJI aircraft video or sidecar was found, so the aircraft layouts and the text formats still rest on published format descriptions and ExifTool's reading of synthetic messages.

Fuzz: targets `telemetry-dji-text`, `telemetry-djmd`, `telemetry-mdpm`.

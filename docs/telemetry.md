# Timed telemetry

Decoders for the timed sensor data that cameras and phones store in a data track of an ISO base media file: GoPro GPMF (`gpmd`), Google CAMM (`camm`) and Apple QuickTime timed metadata (`mebx`). Namespaces `RecursiveAI.OneMedia.Metadata.Telemetry` (reader, items, channels) with `.Gpmf`, `.Camm` and `.Mebx` for the format layers. Nothing is decoded from audio or video samples.

## API

- `media.Telemetry`: every telemetry track as typed `TelemetryChannel` lists (u41's model). Eager: it reads the whole track, so use the pull reader for long files.
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

Packet: u16 reserved, u16 type, little-endian. Types 0 to 7 decode as in the Google specification; other types are raw. `time_gps_epoch` is "time since the GPS epoch" in the specification. We read that as the GPS time scale (1980-01-06, leap seconds not counted) and convert to UTC with a leap-second table; the raw seconds stay in component 13. ExifTool reads the number as Unix time. No real CAMM device file was available to say which writers follow the specification [Agent default].

## mebx

The sample entry's `keys` box lists local key ids with `keyd` (namespace and key) and `dtyp` (data type). A sample is concatenated atoms `[size][local key id][value]`. Ids 0 and 0xFFFFFFFF are placeholders and ignored. Well-known types 1, 2, 21-24, 28, 65-67, 70-72, 74-79 decode; nested atoms (type 28, e.g. detected-face groups) are walked up to depth 6. `com.apple.quicktime.location.ISO6709` becomes a Gps item. Detected faces are personal data: only synthetic samples are committed, and the nested layout is built from ExifTool's tag documentation and unverified on a device.

## Defects and limits

Defect codes: `gpmf.truncated` (nested data past its parent, clamped), `gpmf.depth`, `gpmf.type-size`, `camm.short`, `camm.trailing`, `mebx.atom-size`, `telemetry.items`. Limits: 65,536 items per packet, GPMF depth 6, mebx depth 6. Hostile input never throws from the decoders.

## Privacy

Every location and device item keeps its `Range`, so a strip operation can find and overwrite the bytes.

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

# Device corpus

Real recordings from freely licensed sources, used to check demuxing, facts, telemetry, strip and repair against ffprobe, ExifTool and MediaInfo.
The files are never committed: `tools/corpus/devices-manifest.tsv` lists each one (URL, SHA-256, size, licence, credit, device as the file states it, all fetch-only) and
`tools/corpus/devices-fetch.sh` downloads them. `devices-discover*.py` and `devices-probe.py` found them (Internet Archive items and Zenodo records with a stated
CC0, public-domain-mark, CC BY or CC BY-SA licence; a file is kept when it names a camera or phone, or is a screen recording).

## What is in it (193 files, 6.1 GB)

| Class | Files | Devices |
|---|---|---|
| iPhone | 15 | 3GS, 5s, 6s Plus, 8, 11, 12 mini, 13 Pro Max (HEVC/H.264, Live Photo metadata tracks, face tracks) |
| Android | 10 | Pixel 8 / 8 Pro, Xiaomi, Motorola |
| Action cameras and drones | 7 | one HERO10 with GPMF, four older GoPro clips without telemetry, DJI Osmo Pocket 3 (djmd), one DJI FC6310R |
| Dashcams | 2 | |
| AVCHD / M2TS camcorders | 63 | Sony (DSC, HDR), Panasonic, Canon; time zones from -07:00 to +09:00 |
| Sony MP4 camcorders | 3 | |
| Compact cameras and DSLRs (MOV/AVI/MP4) | 37 | Nikon, Olympus, Canon, Panasonic GH6, Fujifilm |
| Mac screen recordings, other screen recorders | 8 + 35 | |

Gaps: no Samsung phone, no iPhone with Dolby Vision, spatial video or slow motion, no XAVC, no real dashcam originals (the dashcam items found are re-encoded), no
OBS file with its own metadata. DJI and iPhone telemetry rest on one or two files.

## Demux against ffprobe (7.0.2; 8.1.3 for transport streams)

`tools/run-device-corpus.sh` and `tools/run-device-triage.sh` sort every track's differences into causes. Of 193 files, bytes, positions and hashes agree for every packet except where listed:
Packet bytes were wrong in one case, now fixed (below). All other differences are timing or listing choices that docs/isobmff.md and docs/mpeg.md already state:
`last-duration`, `dts-shift` (ffprobe moves DTS earlier for negative composition offsets), `edit-start`, `edit-drop` (samples after the last edit), `edit-repeat`
(an edit list that plays media twice), and in the 63 transport streams `pos-key-documented`: ours marks an intra picture that opens with a parameter set as a key
frame, ffprobe 7.0.2 and 8.1.3 want a recovery point SEI (1,203 pictures in 45 camcorder files; with a recovery point SEI both agree).

## Fixed

- PCM in QuickTime files (`sowt` and friends) whose sample table states a size of 1 and counts frames: packets were one quarter or half of the real size. The frame size now comes from the sample description.
- PCM packets no longer cross a chunk boundary (ffprobe ends a packet at every chunk).
- MDPM: the flags byte of the recording time names the camera zone (west bit, daylight-saving bit, half-hour count). Read from 63 files against ExifTool's printed zone. Capture time is now an offset-bearing candidate.
- MDPM: Sony model name from entries 0xE4 to 0xE6.
- `manu` / `modl` user-data atoms give Make and Model (Canon PowerShot MP4).

## Not fixed (see the handoff for status)

- Make, model and software are not read from Nikon `NCDT`, Olympus `TAGS`, Panasonic `PANA`, Canon `CNMN`, and Nikon AVI chunks (37 compact-camera files).
- Reconstruction of cut files: compact-camera files with PCM audio recover little video (one of 570 frames in one Nikon file); PCM packet sizes differ from the original; Pixel AAC tracks match for the first 7 to 222 packets only.
- Strip leaves the DJI Osmo Pocket 3 serial number in its `djmd` samples and says so (protocol not in the table).

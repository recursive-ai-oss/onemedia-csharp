# DV (raw `.dv`, DV in AVI)

How `RecursiveAI.OneMedia.Dv`, `Codecs.Dv` and the AVI reader handle DV. Documents: IEC 61834-2 and SMPTE 314M (DIF structure, packs) as
publicly described, Microsoft's note on DV data in AVI files (type 1 and type 2), and ExifTool's DV tag documentation for the names of the
pack fields. The field positions below were checked against files written by independent tools (stated where used).

## Frame layout

A DV frame is a set of DIF sequences of 150 blocks of 80 bytes (12,000 bytes). Each block starts with a 3-byte identifier (section type in
the top three bits, then sequence and channel numbers, then the block number).

| Stream | DIF sequences per frame | Frame size |
|---|---|---|
| 525/60, 25 Mbit/s (DV, DVCAM, DVCPRO 25) | 10 | 120,000 |
| 625/50, 25 Mbit/s | 12 | 144,000 |
| 525/60 or 625/50, 50 Mbit/s (DVCPRO50) | 20 or 24 | 240,000 or 288,000 |
| 1080i/50 or 720p, 100 Mbit/s (DVCPRO HD) | 40 or 48 as stated by the stream | per the DSF bit and the class |

A sequence is a header block, two subcode blocks, three VAUX blocks, then nine groups of one audio block and 15 video blocks. The stream
parameters come from the first sequence:

- **System** (525 or 625 lines): the DSF bit, bit 7 of byte 3 of the header block.
- **Class**: the low five bits of the video source pack's last data byte (`0x60`): 0 is 25 Mbit/s, 4 is 50 Mbit/s, anything else the
  four-channel (100 Mbit/s) systems. Observed: DVCPRO HD 1080i/50 is `0x14`; 1080i/60 and 720p are not verified.
- **Picture**: 720×480 (525) or 720×576 (625); DVCPRO50 the same; DVCPRO HD 1080i/50 is stored as 1440×1080. Frame rate 25, or
  30000/1001. Display aspect (4:3 or 16:9) from the video source control pack (`0x61`); pixel aspect follows from it.
- **Audio parameters** from the audio source pack (`0x50`): sampling rate (bits 5–3 of the last data byte: 48, 44.1, 32 kHz) and
  quantisation (bits 2–0: 16-bit linear, 12-bit non-linear), channel count from the class and mode.
- **Recording date and time** from the VAUX packs `0x62`/`0x63` (BCD; the camera's local clock). Zone unknown. Dates before 1995 and unset
  clocks (all `0xFF`) are not recording times. The time code (`0x13` in the subcode blocks) is kept as a native tag.

Everything above is read from the first frame; the other frames are only framed.

## Raw DV

`.dv` and `.dif` streams are a sequence of whole frames: the format sniffer accepts a header block followed by the subcode and VAUX blocks
in their places (certain) or a header block alone (possible). The track is one `dvvideo` video track with the frame rate, size and aspect
above; each packet is one frame (`size` = frame size), always a key frame, `pts` = `dts` = frame number at the frame rate. A file may start
at any frame boundary but must start with a frame: bytes before the first header block are not skipped (a file that does not begin at a frame
is not recognised). A partial last frame is delivered with the `Corrupt` flag and a `dv.truncated-frame` defect; a file shorter than one
sequence raises `dv.truncated-first-frame`.

## DV in AVI

- **Type 1**: one stream of type `iavs` whose chunks (`00dc`) are whole DV frames, picture and sound together. It is exposed as one
  `dvvideo` video track (size, rate and aspect from the first frame) with the stream's `dvinfo` (`DVINFO`) as configuration. The DV packs of
  the first frame are native tags (system `dv`).
- **Type 2**: an ordinary `vids` stream with fourcc `dvsd`/`dvhd`/`dv25`/… holding DV frames without audio, and the sound in a separate PCM
  `auds` stream. Both are ordinary tracks.

## Audio in DV

The sound inside a DV frame is stored in the audio blocks shuffled across the frame's DIF sequences and, at 12 bits, non-linear. Returning it
as PCM or as an audio packet needs a de-shuffling step that goes beyond framing and header parsing. The reader reports the audio parameters
as native tags and does not expose an audio track for type 1 AVI and raw DV. ffprobe lists the synthesised audio streams, so each file with
embedded audio shows one deviation class in the differential, `dv-embedded-audio` ([avi.md](avi.md)). A de-shuffler is a follow-up.

## Defects

| Code | Kind | Meaning |
|---|---|---|
| `dv.truncated-first-frame` | `Truncated` (Error) | The file ends inside the first DIF sequence; no track. |
| `dv.truncated-frame` | `Truncated` | The last frame is cut; delivered with `Corrupt`. |

## Deviations from ffprobe 7.0.2

- `dv-embedded-audio`: ffprobe adds one `pcm_s16le` stream, with one packet per frame, for each audio channel pair it finds in the frame, so it
  lists 1 + N streams and N + 1 packets per frame where we list 1 and 1 (N is 1 or 2 on the corpus: ffprobe shows 2 or 3 streams). The
  harness accepts a stream-count or packet-count difference only when every extra stream is `pcm_s16le` and the first track is `dvvideo`; all
  other fields of the video track still have to match. De-shuffling the sound is not implemented and is a documented gap.
- `truncated-last-frame`: ffprobe lists an empty packet, we return the bytes present with `Corrupt`.
- `dv-frame-start`: a raw file that does not start at a frame boundary is opened by ffprobe (it searches for the next header); sniffing here
  looks at the head of the file only, so the file is not recognised.

Everything else (frame sizes, positions, payload hashes, times, stream parameters) agrees on the corpus.

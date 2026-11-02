# MPEG transport stream muxer

`MpegTsMuxer` (`RecursiveAI.OneMedia.MpegTs`) writes one program as an MPEG transport stream (ITU-T H.222.0 / ISO/IEC 13818-1) from packets
in decode order. It implements `IMuxer` (`docs/muxing.md`), writes strictly forward (the output may be a pipe) and holds a few packets of
buffers whatever the length of the stream. `Remuxer`/`RemuxSession` drive it for `.ts`, `.m2ts` and `.mts` outputs (`RemuxFormat.MpegTs`,
`RemuxFormat.M2ts`, `onemedia remux --format ts|m2ts`).

## What is written

| Part | Rule |
|---|---|
| Packets | 188 bytes (`Ts188`), or 192 bytes (`M2ts192`: a 4-byte `TP_extra_header` with a 30-bit arrival time stamp in front of each packet, the clock of the stream at 27 MHz) |
| PAT, PMT | one program (`ProgramNumber` 1, `TransportStreamId` 1, PMT on PID 0x1000, first elementary PID 0x100, later tracks follow in order); CRC-32/MPEG-2; first in the stream and in every segment, repeated every `PsiIntervalMilliseconds` (100, 10 to 500) of stream time |
| PES | one PES packet per input packet. Video: `PES_packet_length` 0, PTS **and** DTS always (also when equal: a reader that finds only a PTS may assume reordering and guess). Audio and others: exact length, PTS (+ DTS when different). `data_alignment_indicator` set |
| PCR | in the adaptation field of the first transport packet of each PES packet of the PCR track (first video track, else first track). When two packets are more than `PcrIntervalMilliseconds` (80, 10 to 100) apart, PCR-only packets fill the gap; PAT and PMT repeat there too, so a gap of seconds still has tables about every 160 ms |
| Continuity | per PID, 4 bits, not advanced by adaptation-field-only packets (§2.4.3.3); carried across segments |
| Random access | `random_access_indicator` on the first packet of a keyframe PES |
| Stuffing | adaptation field stuffing, never `0xFF` padding inside a PES |

## Time

- All times are 90 kHz, rounded to nearest (half away from zero) from the track's time base; the report notes that rounding happened.
- The first decode time of the stream is moved to `InitialDelay` (1.4 s) so that the first PTS is never negative, and the PCR runs `PcrLead` (0.5 s) ahead of the decode time of the packet it
  travels with. Times are 33 bits and wrap (§2.4.3.7); only differences carry meaning. A decode time that moves backwards on one track is refused (`NonMonotonicDts`); a packet without any time
  continues the previous one's decode time plus its duration (noted in the report); as the first packet it is refused (`MissingTimestamp`).
- A video packet with no DTS gets its PTS as DTS, which is right only without reordering (noted).
- **Remux** moves an edit list's start and AAC priming into the times (`PresentationTime`), so a stream that started at zero after an edit starts at the same presentation instant here; edit lists
  with several edits, rotation and dispositions have no TS form and are reported as not carried over (`NotRepresentable`).

## Payloads

Media bytes are copied; framing is added or converted through the codec layer, every addition a whole NAL unit or header:

| Input | Written |
|---|---|
| H.264, HEVC, VVC in `avcC`/`hvcC`/`vvcC` samples | Annex B: 4-byte start codes in place of the length prefixes; an access unit delimiter in front (`InsertAccessUnitDelimiters`, default on); the record's parameter sets in front of a keyframe that has none (`InsertParameterSets`, default on) |
| H.264/HEVC Annex B | copied; the same delimiter and parameter-set rule (parameter sets learnt from the first access unit that carries them) |
| Matroska `V_MS/VFW/FOURCC` H.264/HEVC | the record or Annex B parameter sets after the 40-byte bitmap header, as above |
| AAC, raw access units | ADTS header from the AudioSpecificConfig (`esds` unwrapped), ADTS frames are copied |
| MPEG-1/2 video | copied (stream type 0x01/0x02) |
| MP1/2/3 | copied, stream type 0x03, or 0x04 under 32 kHz; pieces under 32 bytes (QuickTime stores MP3 in chunks as small as one byte) are gathered into one PES of up to 2048 bytes stamped with the first piece's time (noted) |
| AC-3, E-AC-3 | PES stream_id 0xBD, stream type 0x06 with the AC-3 (0x6A) or enhanced AC-3 (0x7A) descriptor |
| DVB subtitles, teletext | PES payloads as given, stream type 0x06, subtitling (0x59) or teletext (0x56) descriptor with the default page ids (noted: the reader does not hand over the real ones) |
| Timed ID3 | PES with stream_id 0xFC, stream type 0x15, registration `ID3 ` and a metadata_descriptor (0x26), as HLS expects |
| SCTE-35 | sections on their own PID (stream type 0x86), no PES |
| Captions inside video (SEI, MPEG-2 user data) | travel in the video payload unchanged |

Everything else is refused with `UnsupportedCodec`: Opus, Vorbis, FLAC, VP8/VP9/AV1, PCM, ALAC, AMR, WMA and the like. Opus in TS would need a control header the reader side does not parse either; it is
left out until both sides do it. AAC with `channelConfiguration` 0 (the layout is in a program config element inside the AudioSpecificConfig) is refused with `InvalidConfiguration`: ADTS could only
describe it by inserting that element into the first access unit, which this muxer does not do. A track the input has and the muxer refuses is listed in the remux report as not written.

## Segments

`StartSegment(Stream next)` switches the output between two packets: bytes buffered for the old segment are flushed to it (not closed), PAT and PMT go first into the new one, counters continue, and the next
PCR rides on the first video PES. `CanStartSegmentAt(packet)` is true for a keyframe of the sync track (first video, else first track). `SegmentIndex` and `SegmentBytes` count what has been written.

## Deviations seen in the differential (`tools/tsmux/ts_diff.py`)

The differential remuxes each input, reads input and output with ffprobe and compares per stream: count, PTS, DTS (after one constant shift), keyframe flag and payload (after the reverse repackaging:
no delimiters or parameter sets the muxer added, no ADTS header). Documented differences:

- ffprobe drops a DTS equal to the PTS when the SPS announces reordering (`has_b_frames` 1) and guesses it from the previous packet; the script reads video timestamps from the PES headers instead.
- Audio that ffprobe reads from an ISO file with negative composition offsets or fragments is shifted by a constant against our reader (`docs/isobmff.md`); our reader on both sides is equal.
- ffprobe interpolates the time of audio frames that share a PES packet, so frames read back from a TS differ by one tick from the PES-level time.
- Transport stream inputs with damage (duplicate packets, error flags, garbage, truncation, continuity gaps) read differently in ffprobe and in this library's reader; `tools/OneMedia.TsMuxCheck` (our reader on both sides) is equal.
- A truncated last sample of the input (flagged corrupt by ffprobe, with a NAL length past its end) is not written.
- Two samples at one decode time in the input (`frag_every_frame`) are written with equal PES times; ffmpeg reports each pair as "non monotonically increasing dts" on both.
- ffmpeg's `-f null` reports an error for streams whose SPS timing makes the frame rate look halved; the same happens on a raw H.264 extraction of the input.

## Memory and I/O

Constant: PES assembly buffer of the largest packet, one output buffer of 256 transport packets, the pending-audio gather buffer (2 KiB). The reads are the readers' (`docs/remux.md`); the writes are sequential.

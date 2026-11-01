# Remux

Normative description of how OneMedia copies the tracks of one container into another (`RecursiveAI.OneMedia.Remuxing`, `Media.Remux`, `onemedia remux`) and
how it moves the movie box of an MP4 to the front (`Remuxer.Faststart`, `Media.Faststart`). Muxer rules: `docs/muxing.md`. Editors that change a file in place: `docs/writer.md`.

## What a remux does

A remux reads packets from any reader and gives them to a muxer. Compressed payload bytes are **copied**. Three things change, and only these:

1. **Framing** the target container does not store the way the source did (below).
2. **Codec configuration records** are built the way the target stores them: `avcC`/`hvcC` from the parameter sets in the stream, `esds` from an AudioSpecificConfig,
   `dOps` from `OpusHead`, `dfLa` from FLAC STREAMINFO. The muxer does these mappings (`CodecConfigMapper`, `docs/muxing.md` sample-entry table); the remux layer
   only supplies a configuration the source did not store.
3. **Times**: rescaled exactly by the muxer, with the first decode time of a track moved to zero and the difference put in the edit list; Ogg-style times that
   already have the priming taken off are moved forward by the priming (below).

Everything the output does not hold is listed in `RemuxReport.Losses`.

### API

| Call | What it does |
|---|---|
| `Media.Remux(in, out)` / `Media.Remux(in, out, format)` | the one-line forms; format from the output's extension |
| `Remuxer.Remux(MediaFile, Stream, RemuxFormat, RemuxOptions?, ct)` | an open file into a stream; `RemuxAsync` reads from either kind of source |
| `Remuxer.Remux(path, path, …)` | files; the output is created new (`Overwrite` to replace), and deleted when the remux fails or is cancelled |
| `RemuxSession` | what the above drive: give it input tracks and a destination, feed it packets (filtered or retimed by the caller), call `Finish`. Trim, split and concatenation use this |
| `Remuxer.Faststart(source, stream)` / `Media.Faststart(in, out)` | the movie box to the front, without a remux |

`RemuxOptions`: `Faststart` (default true), `Select` and `Edits` (drop a track; change language, name, disposition, display rotation), `AllowDrop` (default true),
`Limits`, `ConfigurationProbeBytes`, `CreationTime`, `Overwrite`, `Observer` (every packet as written).

### Targets

`RemuxFormat`: `Mp4`, `M4a`, `M4v`, `Mov`, `ThreeGp`, `ThreeG2`, `FragmentedMp4`, `Cmaf` (one track), `Matroska`, `WebM`. The ISO family is written by `IsoMuxer`, Matroska and WebM by `MatroskaMuxer`
(`docs/muxing-matroska.md`; WebM takes VP8, VP9, AV1, Vorbis and Opus only). `Faststart` has no meaning for Matroska, whose index is written by the muxer.

What a Matroska or WebM output does not carry is listed: display rotation, AAC priming and edit lists (`Timing`), ISO sample entry boxes for colour, field order, clean aperture and HDR metadata (`CodecField`, `NotRepresentable`), and for WebM video the `Colour` element and `SegmentUID`
(the validator the muxer is checked with rejects them there). `Changes` notes that BlockAdditions, interlacing and stereo mode are not written.

## Choosing and dropping tracks

Every input track is wanted unless `Select` returns false or its `TrackEdit.Drop` is set (reported as `Deselected`). Tracks that are not wanted are switched off in the
demuxer, so their payloads are not read. A wanted track the target cannot hold is refused by the muxer when it is declared; with `AllowDrop` (default) the track is left out
and reported with the muxer's reason mapped to a `LossReason`:

| `LossReason` | When |
|---|---|
| `UnsupportedCodec` | no sample entry for the codec in this target (MPEG-2 video, MPEG-4 part 2, Motion-JPEG, VC-1/WMV, Vorbis, WMA in MP4) |
| `MissingConfiguration` | setup bytes needed and neither the input nor the stream's first packets provide them (Annex B video without parameter sets; VVC and AV1 elementary streams) |
| `InvalidConfiguration` | setup bytes present but unparsable, or laid out for another container |
| `Protected` | the track is encrypted |
| `TargetLimit` | the target holds fewer tracks (CMAF holds one) |
| `NotRepresentable` | any other refusal of the track |

With `AllowDrop` false the first such track throws the muxer's `MuxRefusedException` and nothing useful is written. If every track is left out, `Finish` throws `MuxRefusedException`
(`UnsupportedCodec`) naming each track.

## Repackaging

| Source form | Target form | Rule |
|---|---|---|
| Annex B H.264/HEVC (TS, M2TS, PS, elementary stream, AVI without a configuration record) | length-prefixed NAL units, 4-byte lengths, `avcC`/`hvcC` | The record is built from the SPS/PPS (H.264) or VPS/SPS/PPS and the prefix SEI before the first slice (HEVC) found in the first packets (`AvcDecoderConfigurationRecord.TryCreateFromAnnexB`, `HevcDecoderConfigurationRecord.TryCreateFromAnnexB`). Parameter sets **equal to a set in the record** are left out of the samples; a changed set is kept in the sample and the entry stays `avc1`/`hvc1`. A packet that would be empty without its parameter sets keeps them. Every other NAL unit (AUD, SEI, slices) is copied byte for byte. |
| ADTS (TS, elementary AAC) | raw access unit, `esds` | The header and CRC are removed (`AdtsRepackager.TryGetRawData`); the AudioSpecificConfig is the source's, or derived from the first header when the source has none |
| length-prefixed with `avcC`/`hvcC` (MP4, MOV, Matroska, FLV) | the same | no change |
| MPEG audio, AC-3/E-AC-3, AMR, FLAC, Opus, PCM, VP8/VP9/AV1 with a record | the same | no change; the muxer derives `esds`/`dac3`/`dec3`/`vpcC` from the first frame where the source stores none |

Packets are held back only until every track that needs a configuration derived from the stream has it, up to `ConfigurationProbeBytes` (32 MiB); then they are replayed in order.
A track still without one is left out as `MissingConfiguration`. Memory after that does not depend on the length of the input.

### Priming and times

- An edit list, start offset or priming the reader reports goes to the muxer in `MediaTrack.Edit`; the ISO muxer copies the edit list or builds one from priming and start time (`docs/muxing.md`).
- Ogg Opus packet times already have the pre-skip taken off (first packet at minus the pre-skip, `TrackEditInfo.PacketTimesIncludePriming`). The remux moves every time of that track forward by
  the priming, so the first packet is at zero, clears the flag and keeps the priming in the edit information. The report says so in `Changes`.
- Transport and program streams start at their first time stamp; the muxer moves the first decode time to zero and writes the difference as an initial empty edit, which keeps presentation time.
- **Decode times from presentation order.** Matroska stores one time per block, the presentation time, so its video packets arrive without a decode time. The session gives the k-th packet in decode order the k-th smallest presentation time, moved back by one constant lag so that
  no packet is due before it can be decoded. Both are judged over a window of 24 packets (or 32 MiB), which is held back; the lag is fixed from the first window. Video without reordering gets decode time equal to presentation time. `Changes` says so once per track.
- **Opus in laced Matroska blocks.** A laced block carries one time for all its frames, and without a `DefaultDuration` no frame has a duration. The Opus TOC byte (RFC 6716 §3.1) gives each packet's length, so a frame that would start before the previous one ended is placed where it ended. Other lacing is left as the reader delivers it.
- **PCM layout.** Integer or float, width and byte order are not in the neutral track; the WAV, AIFF and CAF readers and FLV's codec name state them and the remux hands them to the ISO muxer (`fpcm` for floats, `ipcm` for integers; QuickTime codes in MOV).
- **ALAC cookies.** CAF and QuickTime store the magic cookie as atoms (`frma`, `alac`, terminator) and a bare 24-byte configuration also occurs; the ISO `alac` box holds the version/flags word and the 24 bytes, so the cookie is unwrapped to that.
- **Streams the input did not declare.** A packet whose track number is past the reader's track list (a logical stream that begins after the headers, in Ogg) is skipped and reported once as a `Track` loss with reason `NotInspected`.
- A raw elementary stream has no time stamps; the reader numbers frames at the declared or default rate and the output keeps that numbering.

## What is reported

`RemuxReport`: the muxer's `MuxReport` (bytes, packets and payload bytes per output track, its notes), one `RemuxTrackReport` per input track (output index or -1, packets, repackaging), `Losses`, `Changes` and the
input's defect count.

| `LossKind` | Reported when |
|---|---|
| `Track` | a track was left out (see the reasons above) |
| `Tag` | the input has native tags (one entry per tag system, with the count and the first keys). **No muxer writes tags yet**, so every tag system is `NotWritten`; if the tags cannot be read, one `NotInspected` entry says so |
| `Picture` | embedded pictures (cover art), `NotWritten` |
| `Chapter` | chapters, `NotWritten` |
| `CodecField` | sample entry boxes of an ISO source that the output does not write (anything other than `colr clap fiel gama btrt chnl srat mdcv clli`, which are copied, and the configuration/`pasp` boxes the muxer regenerates), and sample descriptions after the first |
| `Disposition` | MP4/QuickTime keep only `Default` and `Disabled`; other disposition flags (forced, commentary, hearing impaired …) are listed |
| `Structure`, `Timing`, `Attachment` | reserved for the Matroska target (attachments, edit lists the target cannot hold) |

`Changes` lists what the remux did to carried data: configuration derived from the stream, framing changed, times moved. `Mux.Notes` lists what the muxer rounded or filled in.

Unknown top-level and movie-level boxes of an ISO source (`udta` payloads outside the tag readers, vendor `uuid`, `meta` with other handlers) are not carried by a remux; use `Faststart`, which copies them, when only the layout should change.

## Faststart

`Remuxer.Faststart` is an **edit**, not a remux. A progressive MP4/MOV/3GP whose `moov` follows `mdat` is rewritten as `ftyp`, `moov`, then every other box in its original order. All media data
and all other boxes are copied by range, so the output is exactly the input plus one moved box. Only the chunk offset tables change: ISO/IEC 14496-12 §8.7.5 stores absolute file offsets in `stco`/`co64`, and every
entry that pointed past the new position of the movie box and before the old one moves forward by the movie box's size. A table whose offsets no longer fit 32 bits is promoted to `co64`, which grows the
movie box, which moves the offsets again; the rewrite repeats until the size is stable (rounds are bounded by the number of tables).

- The movie box is held in memory (bounded by `MediaLimits.MaxAllocationBytes`); media data is streamed through a 1 MiB buffer. Peak memory does not depend on the file size.
- A file whose `moov` is already before `mdat` is copied unchanged (`Relocated` false). Fragmented files (`moof`, `mvex`) are refused: there is no single movie box.
- Not adjusted (as in the editors): `iloc` item locations of HEIF-style files and vendor boxes that hold absolute offsets.
- `Remuxer.Remux(…, Faststart = true)` (the default) gets the same layout by writing `moov` first through the muxer's spool; that path rebuilds the sample tables (see `docs/muxing.md`). Use `Faststart` when the file should
  stay exactly as it is.

## Differential method

`tools/OneMedia.RemuxCheck remux <in> <out>` remuxes a file with `Remuxer` and writes `<out>.map` (output ← input track, repackaging) and `<out>.pkts` (every packet as handed to the muxer, with its SHA-256). It also checks that
re-framed video keeps its NAL units: the NAL units of the input (parameter sets excluded) hash to the same value as those parsed back out of the length-prefixed samples written (`nals=same`).
`tools/remux-compare.py` runs `ffprobe -show_packets -show_data_hash` on both files and compares per stream, in order: pts, dts, duration, size, flags and data hash. It is `tools/mux-compare.py` (classes in `docs/muxing.md`) with these
differences: a track marked `annexb-to-length-prefixed` or `adts-to-raw` is compared by size and hash with what was written (`.pkts`), because the bytes differ by design, and the class `untimed-input` covers raw elementary streams, which
have no time stamps for ffprobe to print. `tools/run-remux-diff.sh` adds the structure check (MP4Box `-info`, mkvalidator for Matroska) and `ffmpeg -f null` on input and output and counts messages that appear only for the output.
`tools/run-mux-diff.sh`-style summaries are written to `summary.tsv`.

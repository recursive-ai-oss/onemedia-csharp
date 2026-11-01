# Muxing

How packets become a file. The neutral contract is `RecursiveAI.OneMedia.IMuxer` (`src/OneMedia/Muxing.cs`); the ISO base media muxer is `RecursiveAI.OneMedia.IsoBmff.IsoMuxer` (`src/OneMedia/IsoBmff/Mux/`). This page is normative for the contract and for the ISO muxer; other muxers follow the contract and add their own page section.

## The contract

1. `AddTrack(MediaTrack, options?)` declares a track from the reader's `MediaTrack` (codec id, `CodecConfiguration`, time base, edit/priming info, language, disposition). It returns the output track index. All tracks are added before the first packet.
2. `WritePacket(PacketInfo, ReadOnlySpan<byte>)` takes packets in decode order, all tracks interleaved in the order the caller wants them in the file. The payload is copied unchanged to the output. Nothing is repackaged by the muxer: Annex B → `avcC` and ADTS → raw are the codec layer's repackagers, called before the muxer.
3. `Finish()` finalises the file and returns a `MuxReport` (bytes written, packets and payload bytes per track, notes about anything the muxer rounded, guessed or synthesised). `Dispose` without `Finish` leaves the output incomplete.
4. Every call that can touch I/O takes a `CancellationToken`.

Refusals are `MuxRefusedException` with a `MuxRefusalReason`, the output track index and the codec: `UnsupportedCodec`, `MissingConfiguration`, `InvalidConfiguration`, `NonMonotonicDts`, `MissingTimestamp`, `InvalidTrack`, `InvalidState`, `LimitExceeded`, `OutputNotSuitable`. A refused `AddTrack` or `WritePacket` leaves the muxer usable. Each reason has a test (`IsoMuxerTests`).

Time is `MediaTime`. A packet's time base may differ from the track's; the muxer rescales exactly with round-to-nearest and reports a note when it rounded.

## ISO base media muxer

### Flavours and brands

| `IsoFileKind` | major brand | notes |
|---|---|---|
| `Mp4` | `isom` (minor 512) | compatible `isom iso2`, plus `avc1`/`mp41` where they apply |
| `M4a`, `M4v` | `M4A `, `M4V ` | as MP4 |
| `Mov` | `qt  ` (minor 0) | QuickTime handler names and sound descriptions, QuickTime PCM entries |
| `ThreeGp`, `ThreeG2` | `3gp4` (`3gp5` with H.264), `3g2a` | |
| fragmented | flavour brand + `iso5`/`iso6`; CMAF adds `cmfc` | CMAF: major `iso6`, one track per file |

`CompatibleBrands` and `MinorVersion` override the defaults.

### Layouts

| `IsoLayout` | file order | payload path | memory |
|---|---|---|---|
| `MoovFirst` (default; "faststart") | `ftyp moov mdat` | payload goes to a spool (16 MiB in memory, then a temporary file; `SpoolFactory` replaces the file) and is copied after the `moov` is known | sample tables + spool budget |
| `MoovLast` | `ftyp mdat moov` | straight to the output. On a seekable output a 16-byte `mdat` placeholder is patched at the end (`free` + `mdat`, so 64-bit sizes need no shift); on a non-seekable output the payload is spooled as for `MoovFirst`, and a spool that cannot be made is `OutputNotSuitable` | sample tables |
| `Fragmented` | `ftyp moov(mvex) [moof mdat]… [sidx] [mfra]` | one fragment buffered at a time | one fragment (cut at the reference track's keyframe once `FragmentDuration`, default 2 s, has passed, or at `MaxFragmentBytes`, default 64 MiB) |

A `sidx` (`WriteSegmentIndex`) needs the fragments' sizes before it can be written in front of them, so fragments go through the spool in that mode. `mfra` (`WriteRandomAccess`) is written at the end.

### What is stored per packet (constant working set)

Sample tables are paged lists, so memory grows with the packet count and never with the payload:

| table | bytes |
|---|---|
| sample size (`stsz`) | 4 per packet |
| key frame (`stss`) | 4 per keyframe |
| duration run (`stts`) | 8 per run of equal durations |
| composition run (`ctts`) | 8 per run of equal offsets |
| chunk (`stco`/`co64`, `stsc`) | 8 per chunk, +12 per change of samples per chunk |

A 4.38 GB, 64,576-packet file is copy-muxed moov-first with 64 MB peak RSS (below). `CompactSampleSizes` writes `stz2` when every size fits 4, 8 or 16 bits. `stco` becomes `co64` per track when the last chunk offset passes 4 GiB; the movie box is rebuilt once its own size is known, since the offsets in front of the media data depend on it.

Chunks are runs of adjacent packets of one track. A source that interleaves finely produces many small chunks (8 bytes each); optional chunk buffering is a follow-up.

### Time, edits and priming

- ISO/IEC 14496-12 §8.6.1.3: the first decode time of a track is zero. A track whose first DTS is not zero is shifted to zero and the difference becomes a leading empty edit (`media_time` −1) in the `elst`, so presentation time is preserved.
- The `ctts` is version 1 (signed) when any composition offset is negative.
- Durations are the gaps between decode times; the last packet's duration is the declared one (else the previous gap).
- The edit list is copied from `MediaTrack.Edit.Edits` when the reader gave one. Otherwise it is built from `Priming` (a `media_time` after the priming frames), `EndPadding` (shorter segment) and the shift above. The movie timescale is the first edit's timescale, else the first track that needs a synthesised edit's timescale, else 1000 (`MovieTimescale` overrides).
- Creation and modification times are zero unless `CreationTime` is set, so output is a pure function of the input (tested: `OutputIsDeterministic`).
- A packet flagged `Disposable` makes the track write `sdtp` (`is_depended_on` = 2); fragments set the same bits in the sample flags.
- Opus gets a `roll` sample group (`sgpd`/`sbgp`, −80 ms of roll) and the `iso2` brand.

### Sample entries

Entries come from `CodecConfiguration`; a configuration in a different layout than the target's is refused as `InvalidConfiguration` naming the expected box.

| codec | entry | configuration |
|---|---|---|
| H.264 | `avc1` (`avc3` etc. as the source said) | `avcC` |
| HEVC | `hvc1` (`hev1` as the source said) | `hvcC` |
| VVC | `vvc1` (`vvi1`) | `vvcC` |
| AV1 | `av01` | `av1C` |
| VP9 | `vp09` | `vpcC` (built from Matroska CodecPrivate when needed) |
| VP8 | `vp08` (not QuickTime) | none |
| AAC | `mp4a` | `esds` (from the AudioSpecificConfig) |
| MP3 | `mp4a` | `esds` with the MPEG-1/2 audio object type, derived from the first frame header |
| Opus | `Opus` | `dOps` (from `OpusHead`) |
| FLAC | `fLaC` | `dfLa` |
| AC-3 / E-AC-3 | `ac-3` / `ec-3` | `dac3` / `dec3` (derived from the first frame when absent) |
| AMR-NB / WB | `samr` / `sawb` | `damr` (default all modes, one frame per sample) |
| ALAC | `alac` | the magic cookie |
| PCM | `ipcm`/`fpcm` + `pcmC` (ISO); `sowt`, `twos`, `raw `, `in24`, `in32`, `fl32`, `fl64` (QuickTime) | the PCM format comes from `IsoTrackOptions.Pcm`, else the track's codec tag and bits |
| subtitle and data tracks (`tx3g`, `mp4s`…), and codecs the layers do not know that came from an ISO file | the source's own entry code | the source's own entry content, passed through |
| anything else, protected tracks, companded or unsigned PCM in ISO | refused | `UnsupportedCodec` |

`IsoTrackOptions` adds a track id, a handler type, extra sample-entry boxes (`colr`, `pasp`, `clap`…) and track-level boxes after `mdia` (`tref`, `udta`), written as given. One sample description per track.

Opus-in-ISOBMFF and FLAC-in-ISOBMFF come from Xiph's published encapsulation documents (see the sources list).

### Fragmented files and CMAF

`moov` carries `mvex`/`trex`; each fragment is `moof(mfhd, traf(tfhd, tfdt v1, trun))` + `mdat`, with `default-base-is-moof` and data offsets from the `moof`. A constant sample duration goes in `tfhd`; `trun` has v1 when a composition offset is negative. Cmaf = one track, brands `iso6` + `cmfc`, fragments start at a keyframe (or any sample for audio). Per-fragment bookkeeping (`sidx` references, `mfra` entries) exists only when those boxes are asked for; a plain fragmented file keeps nothing per fragment. Incremental output: a fragment is on the destination as soon as the next fragment starts, so live output to a non-seekable destination holds one fragment.

### Refusals

Non-monotonic DTS (`NonMonotonicDts`), a packet without a time (`MissingTimestamp`), unmappable codecs (`UnsupportedCodec`), missing or broken configuration, a track after the first packet or a packet after `Finish` (`InvalidState`), exceeded `MediaLimits` (`LimitExceeded`), a `sidx` with more than 65,535 references, CMAF with more than one track, and a non-seekable destination for a layout that needs to go back (`OutputNotSuitable`).

### Not stored

Edit lists beyond what the reader reports, sparse `tmcd` and hint tracks (carry them through `TrackBoxes`), `sgpd`/`sbgp` other than Opus roll, `subs`, `saiz`/`saio`, any encryption box. Annex B → `avcC` repackaging belongs to remux, not to the muxer.

## Differential method

`tools/OneMedia.MuxCheck copy` demuxes a file with our reader and muxes it (kind from the source's major brand), writing `<out>.map` (output track ← input track) and `<out>.pkts` (the reader's view of every packet, with SHA-256). `tools/mux-compare.py` runs `ffprobe -show_packets -show_data_hash` on input and output and compares per stream, in order: pts, dts, duration, size, flags and the data hash. `tools/run-mux-diff.sh` adds MP4Box `-info` and `ffmpeg -v error -f null -` on both files and counts messages that appear only for the output. `tools/run-mux-sweep.sh` runs the layouts: moov-first, moov-last, moov-last on a non-seekable output, fragmented, fragmented CMAF with `sidx` and `mfra`.

A difference is explained only when it falls in one of these classes (counted separately in the summary):

| class | meaning |
|---|---|
| `shift` | every pts/dts of a stream moved by one constant: ffprobe's handling of negative composition offsets in progressive files |
| `dur-na` | ffprobe prints N/A for the input's duration (a fragment default) |
| `dur-reader` | the output's duration is the one the reader delivered |
| `dur-gap` | the output's duration is the distance to the next decode time |
| `key-reader` | the key flag is the one the reader delivered |
| `disc-frag` | ffprobe applies an edit list's discard to the tables of a movie box and not to fragment samples; the two files differ when one is fragmented and the other not |
| `count-reader`, `in-reader` | ffprobe's reading of the input differs from the container reader's (fragment timelines with repeated `tfdt`, one-byte QuickTime MP3 samples, MP3 samples ffprobe splits in progressive files only), and ffprobe's reading of the *output* equals the reader's view in count, sizes, hashes, key flags and shifted times |

### Deviations seen in the corpus sweep

- `in-reader` also covers: fragmented sources whose `tfdt` repeats (ffprobe collapses the timeline; the reader follows `tfdt`), QuickTime files with one-byte MP3 samples, and MP3 samples that ffprobe splits in progressive files only.
- A segment index (`WriteSegmentIndex`) makes ffprobe 7.0.2 shift the presentation times of the first fragment by an amount of its own when composition offsets are negative. The same bytes with the `sidx` renamed to `free` read back with one constant shift. Decode times are unaffected.
- A source that holds two samples at one decode time (`generated/fmp4/every-frame.mp4`) gives a progressive output with zero durations in `stts`; MP4Box refuses to open that file ("not enough bytes in box stts"; with the zero deltas patched it opens). The muxer notes it in the report and copies the source's timeline.
- MP4Box `-info` prints "Error fetching sample: Bad Parameter" for fragmented MP3 tracks, ours and ffmpeg's alike.
- ffprobe prints N/A as the duration of fragmented samples in places where the input, a progressive file, had one.
- ffmpeg's null muxer repeats "non monotonically increasing dts" for outputs of sources that already trigger it, with other numbers.

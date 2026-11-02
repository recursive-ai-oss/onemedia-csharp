# Index repair

`Media.Repair(input, output[, reference])` (`RecursiveAI.OneMedia.Repair.Repairer`, CLI `onemedia repair <in> <out>`) rebuilds the indexes, counts and
durations of a damaged file into a **new file** and returns a `RepairReport`. The input is never modified. Media payload bytes are copied unchanged.
Truncated-file reconstruction (a lost `moov`) is a separate path; index repair reports it as `Unrepairable` with the reason.

## Contract

- The output must not exist (`IOException`). On `Unrepairable` or an exception no output file is left behind.
- A healthy input is reported as `NothingToRepair` and the output is a **byte-identical copy** (the copy is written; callers that want to skip it
  check the status and delete it).
- `RepairReport`: `Status` (`NothingToRepair`, `Repaired`, `PartiallyRepaired`, `Unrepairable`), `Items` (every `Rebuilt`/`Fixed`/`Dropped`/`Kept`/`Guessed`
  structure with the input byte range and a confidence), `Tracks` (packets kept and dropped, confidence, input ranges used), `Confidence` (the lowest of
  the rebuilt structures), `Warnings` (what the repair could not do and what would be needed), `DroppedBytes`.
- Confidence: `High` = derived from the file's own data with every cross-check agreeing; `Medium` = derived with a check that could not be made (key
  frames judged from picture headers, an interleaving inferred from time order); `Low` = guessed; `None` = nothing recovered.
- Healthy means: nothing was dropped or fixed and every index the file carries points at what it names. A file that lacks an optional index is not
  healthy for the formats where the index is what repair rebuilds (Matroska Cues, AVI `idx1`, fragmented MP4 `mfra`).
- Repairing a repaired file gives `NothingToRepair` (the tests check this for every format).

## Matroska / WebM (RFC 9559, RFC 8794)

One scan reads element headers and the first bytes of every block. Rebuilt: SeekHead (always, as the layout moves), Info `Duration` (the last block's end,
using `BlockDuration` or the track's `DefaultDuration`; a stated value within 100 ms plus two default durations of the computed one is kept), Cues (one cue
per video keyframe of the first video track; for audio-only files one per Cluster per audio track; `CueRelativePosition`, written only when the EBML header declares DocTypeVersion 4 or higher, points at the block or block
group), Segment size, and the size of every Cluster (8-byte size field; unknown-size Clusters from a live recording become known-size).
A final Cluster cut in the middle of a block loses that block (reported as `Dropped` with its byte range); garbage between Clusters is skipped by scanning for
the next Cluster header whose size fits; a truncated element other than a Cluster ends the scan; a missing or cut `Tracks` is `Unrepairable`.
A CRC-32 child of a Cluster whose content changed is left out; a Cluster `Position` that no longer matches is patched in place. Chained Segments are not
followed. Limit: zero fill that completes the declared size of a cut block cannot be told from data.
The report lists tracks in ascending track-number order and counts frames, not blocks: a laced block adds as many packets as it holds frames, as a demuxer (ffprobe) lists them.

## AVI / OpenDML (Microsoft AVI RIFF reference, OpenDML 1.02)

The chunks of every `movi` list are walked by their own headers; the RIFF and LIST sizes are not trusted. A cut final chunk, bytes that are not a chunk, and
the inserted data of a shifted grid are dropped (the next chunk is found by a chain of two plausible chunk headers, byte by byte). Rebuilt: RIFF/LIST sizes,
`avih.dwTotalFrames`, `strh.dwLength` (chunks, or bytes over `dwSampleSize` for PCM-style audio), `dmlh`, the `idx1` (offsets from the `movi` tag, key flag from
the picture header for MPEG-4 part 2, H.264 and MPEG-1/2, every chunk of other codecs), and for OpenDML files the standard indexes (`ix##` at the end of each
`movi`, at most 8192 entries and 2 GiB of span each) with the super index (`indx`) in each stream list. An OpenDML file is one that has an `indx`, an `odml` list
or an `AVIX` RIFF. Old index chunks are never reused.

## ISO base media (ISO/IEC 14496-12)

*Progressive* files are copied byte for byte and patched in place: `stco`/`co64` entries (same count and width), `mdhd` (sum of `stts`), `tkhd` (edit list sum or the
scaled media duration; a stated value within one tick is kept), `mvhd` (the longest track), and the size of a truncated `mdat`. Bytes after the last valid box are dropped.
Chunk offsets are decided in order: the stated ones if they point into the media data without overlap (and, for video whose samples can be recognised, each
chunk starts like one: a plausible length before a NAL unit for `avc1`/`hvc1`/`vvc1`/Dolby Vision entries, a `00 00 01` start code for `mp4v`/`mp2v`); all moved by one
constant; chunks laid out one after another in decode-time order (the layout is tried from the start of the media data and flush with its end, because some
writers put a few bytes of their own first); and, for files with recognisable video, a walk through the media data in which the next chunk is the video chunk that starts
like one, or else the next chunk of the other tracks in decode-time order (`Medium`). A file with several tracks whose video cannot be recognised (VP9, AV1, MJPEG, ...) and
whose offsets are lost is not guessed: `Unrepairable`.

A file whose media data is cut off with the movie box intact (faststart recordings): the sample tables (`stts`, `ctts`, `stsz`, `stsc`, `stco`/`co64`, `stss`, `stps`,
`sdtp`) are cut back to the samples that lie wholly inside the file, the edit list, `mdhd`, `tkhd` and `mvhd` durations follow, and the new movie box is padded with a `free`
box to its old size so no chunk offset moves. A sample that is only partly there is dropped (reported with its byte range). A track left without samples is removed. Tables
not listed (`sbgp`, `sgpd`, `subs`, `saiz`, ...) are copied as they were. If the shorter box would leave a gap of 1 to 7 bytes, nothing is cut and the status is
`PartiallyRepaired`. Files without a usable movie box belong to reconstruction.
*Fragmented* files get a new `mfra` (§8.8.9-8.8.11: one `tfra` per track, one entry per fragment) and `mehd` is corrected; a single `sidx` directly before the first fragment
of a one-track file is rebuilt (§8.16.3). A fragment whose `moof` or `mdat` is cut is dropped. Other `sidx` layouts are kept and reported.

## MPEG-TS (ITU-T H.222.0)

Packets of 188, 192 (M2TS) or 204 bytes are found by repeated sync bytes (a file cut at an arbitrary byte is recognised by that too). Per PID, a PES packet is kept when
its packets arrived in continuity order, none has the transport error flag, and its payload reaches the length its header states; otherwise all its packets are dropped,
as are the packets before a PID's first PES start, one-off duplicate packets, bytes where the grid was lost (and the packet before the loss) and a partial packet at the end
(the PES it belonged to is dropped too). Section PIDs are never dropped. The 4-bit continuity counter of the kept packets is renumbered so the output has no gaps; nothing else
in a packet is touched. PCR jumps without the discontinuity flag are reported, not changed. Limits: a PES packet of unbounded length (video) cut exactly at a packet boundary
cannot be recognised; memory is one bit per packet of the file.

## Not done (needs a reference or another tool)

- ISO files without `moov`, or with `moov` cut: reconstruction from the media data.
- Removing samples that lie beyond the end from the tables of a progressive file cut inside `mdat`.
- ASF, FLV and Ogg: an ASF repair would need the header object's file/packet counts and the simple index rebuilt from the data packets; FLV `onMetaData` duration and
  keyframe index from the tags; Ogg last-page granule positions and the final `EOS` flag. None is implemented.

## Checks

`tools/repair/damage.py <fmt> <healthy> <dir>` writes the damaged variants (Matroska 17, AVI 16, MP4 11 + 2, fragmented MP4 12 + 2, TS 12); `tools/run-repair-case.sh` repairs each and compares
the packets (`RepairCheck diff`: equal prefix per track; `RepairCheck subseq` for TS: every kept packet is one of the original's, in order), counts `ffprobe` error lines and probes
seeks at three points. `tools/run-repair-checks.sh` runs the whole matrix on the worker against pinned oracles and the corpus.

Known limits of the checks: in two TS cases cut at 90 % of the file the repaired output keeps one frame that was cut (the limit above: a PES packet of unbounded length cut off at a
packet boundary cannot be recognised). M2TS (192-byte packets) was not swept against an oracle, because ffmpeg 7.0.2 crashes on the sample files. The damaged files were made from healthy generated files, published samples and corpus files, not from recordings of devices.

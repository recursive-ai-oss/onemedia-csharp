# Structure writers and the safe writer

Normative description of how OneMedia edits files without disturbing what it was not asked to change. Code: `RecursiveAI.OneMedia` (`EditPlan`, `SafeWriter`, `WriteReport`),
`.IsoBmff` (`BoxBuilder`, `BoxEditor`), `.Matroska` (`EbmlWriter`, `EbmlEditor`), `.Riff` (`ChunkBuilder`, `ChunkEditor`).

## Model

- An edit is **planned**, then **applied**. An editor takes the lazy tree of a file plus the edits asked for (`Replace`, `Remove`, `AppendChild`) and builds an
  `EditPlan`: an ordered list of output pieces. A piece is either a **copy** of a source byte range (media payload, untouched elements and headers), literal
  **data** (new or edited elements) or a **generated** stream (chunk offset tables, `Cues`, which are written while streaming). Planning reads structure only;
  applying streams the pieces to a destination with a bounded buffer, so memory does not depend on the file size.
- An unedited file produces a plan that is one copy of the whole source: it comes back byte for byte. Default editing never re-emits a header it did not need to change.
  `RewriteHeaders = true` re-emits every header from its parsed values (a check that the writer reads what it writes); on a well-formed file the result is identical too.
  A header whose stated size was cut to fit the file (see Damaged files) is copied, not re-emitted.
- `plan.Changes` lists each replaced, inserted or removed range (`WriteChange`: description, source range, output range). Size changes forced on parents, and every
  offset fix-up, appear there as well. `WriteReport` adds bytes copied and written, padding used and available, and whether the layout was preserved.

## Fitting into padding

Padding is a `free`/`skip` box, a Matroska `Void` element or a RIFF `JUNK`/`PAD ` chunk (Wave64 `JUNK` GUID). An edit **fits** when the edited element
- has the same size as before, or
- is smaller and the freed space becomes padding (or is exactly zero; a gap smaller than the smallest padding header does not fit), or
- is larger and the growth is absorbed by the padding element that directly follows it (padding after the edited element, which shrinks or vanishes).

`AppendChild` takes the last unconsumed padding sibling of the parent when the new element fits it, otherwise the element is added at the end of the parent.

A plan whose every edit fits is a **same-layout** plan (`IsSameLayout`): nothing after it moves, no offset changes, no parent size changes. Only same-layout plans can be
applied in place. Otherwise the later bytes move and the plan is **grown**: ancestors get their sizes recomputed (keeping the width of the size field when the new size
still fits it) and the fix-ups below are made.

## Fix-ups when bytes move

The plan is a fixpoint: plan, derive the shift map (which source ranges moved by how much) from the plan's changes, plan again with the fix-ups the shift needs, until the
signature of the plan stops changing (at most 9 rounds; a plan that does not settle raises `UnsupportedMediaException`).

ISO BMFF (ISO/IEC 14496-12):
- `stco`/`co64` chunk offsets (streamed, never held in memory). A table is promoted `stco` → `co64` as soon as one offset would not fit 32 bits and stays `co64` in later
  rounds. The promotion grows `moov`, which moves `mdat` again; the fixpoint covers it.
- `tfhd` base_data_offset, `sidx` first_offset and `tfra` moof_offset follow the movement of what they point at. A `tfhd` with base 0 and absolute `trun` data offsets (seen
  in files from some cameras) moves by the shift of its first sample.
- Not adjusted: `iloc` (HEIF/AVIF), vendor boxes holding absolute file offsets, and `sidx` references after an edit between the indexed fragments. Those need a writer that knows them.

Matroska (RFC 9559, RFC 8794):
- `SeekHead` entries, `Cues` (cluster positions, `CueCodecState`, `CueRef*`) and the `Position` of clusters (relative to the Segment data start) are rewritten for moved targets.
  `Cues` is regenerated when its contents change size.
- Master elements that carry a `CRC-32` have it recomputed after any change inside them (checksum over the content after the CRC element, little-endian).
- Size fields: a known size is recomputed and keeps its width when possible; an unknown size stays unknown. Appending after an unknown-sized last element is legal.

RIFF family (Microsoft RIFF, EBU Tech 3285/3306, Apple AIFF, Sony Wave64):
- The outer size is recomputed (Wave64 sizes include the header; chunks are padded to 8 bytes there, to 2 elsewhere). The original pad bytes are copied; a missing final pad byte is
  kept missing unless something is appended after it.
- **RF64/BW64** keeps `0xFFFFFFFF` in the 32-bit size fields and `ds64.riffSize` is patched. A plain `WAVE` file whose edited size reaches 4 GiB is **promoted to RF64**: `RIFF`
  becomes `RF64`, a `ds64` chunk is written (absorbing a leading `JUNK` of at least 36 bytes, otherwise inserted), `data` and sample counts are taken from the file. Changing
  the contents of an RF64 `data` chunk is refused.
- AVI is not an edit target: its indexes hold absolute offsets.

## Damaged files

The reader is tolerant, so editing is best effort on damaged input, with these refusals (`UnsupportedMediaException`, offset set, nothing written):
- an append after a last node whose size runs to the end of the file because it states none (an unbounded size, or a size cut off by the end of the file): new content
  would be swallowed by that node. EBML is exempt because unknown sizes are legal there.
- a plan that does not reach a fixpoint, or an edit that needs more than a 32-bit size field can state (outside the promotion rules above).

A RIFF file whose outer size overshoots the end of the file (some writers state the file length instead of length − 8; RF64 `ds64` the same) is edited normally and the
real size is stated once content moves. An unedited or same-layout edit leaves the stated size alone.

## Safe writer

`SafeWriter` applies a plan:
- `Write(plan, source, stream)` streams to any `Stream`.
- `SaveAs(plan, source, destination)` writes a new file (refuses to overwrite unless asked), `fsync`s it and closes it.
- `Save(plan, path)` writes `.name.onemedia-XXXXXXXX.tmp` in the same directory, `fsync`s, and replaces the target with an atomic rename (`File.Move` with overwrite; the
  Unix permission bits of the original are kept). A failure or cancellation deletes the temp file; a killed process leaves the original untouched and at most one stray temp file
  that is never the target.
  - **Open handles and Windows.** The source handle `Save` itself uses is closed before the rename, because Windows cannot replace a file while any handle to it is open. `FileMediaSource` opens files with delete sharing, so a reader (`MediaFile`) opened on the same path does not by itself block other opens, but Windows can still refuse to rename over a file that another handle holds. In that case `Save` throws `FileInUseException` (an `IOException`): the original is complete and unchanged, and the temporary file is removed. Dispose the reader and save again, or use `SaveAs`. Linux and macOS replace the name atomically and the reader keeps reading the old file. No native calls are used to force a replacement, so the Windows outcome with an open reader is either a clean replacement or this exception, never a half state.
- `SaveInPlace(plan, path)` writes only the changed ranges of a same-layout plan, in place. A plan that is not same-layout throws `PaddingRequiredException` (bytes
  required, bytes available) before the first byte is written. It is **not atomic**: a crash in the middle leaves a mix of old and new bytes in the edited ranges. Use `Save` when that matters.
- Every entry point takes a `CancellationToken`.

## Method and results

`tools/OneMedia.WriterCheck` and `tools/run-writer-checks.sh` drive the checks on the corpus (worker). Per file:
- **roundtrip**: plain and `RewriteHeaders` plans written with `SaveAs`, bytes compared to the source.
- **edit**: a metadata edit per family (ISO `udta`/`ilst` replace, Matroska `Tags` replace, RIFF `LIST INFO`/`NAME` chunk replace). Passes when the per-track payload hash from our demuxer is equal,
  `ffprobe -show_packets -show_data_hash` (size, pts, dts, duration, flags, hash) is equal, MP4Box `-dts` is equal (offsets normalised) with `-info` exit code not worse, and mkvalidator's exit code is not worse. Copied pieces
  are also re-read from the output and compared byte for byte.
- **inplace**: 2 KiB of padding is added into a new file, a metadata unit is written into it, a longer value that fits is applied in place (only the changed range may differ, payload hash unchanged), then an
  oversized value must raise `PaddingRequiredException` with the file untouched.
- **large**: `SaveAs` with an edit on files over 4 GB, peak resident set size from `/usr/bin/time -v`.
- **promote**: a moov-first MP4 just under 4 GiB is grown by 16 MiB (`stco` → `co64`), a plain WAVE of 4,290,000,000 data bytes is grown by 16 MiB (→ RF64).
- **kill**: `kill -9` of `Save` at random points between 5% and 100% of an uninterrupted save.

The counts of the last run and the oracle versions are in the changelog entry of this change and in the unit report; the figures are reproduced by `tools/run-writer-checks.sh`.

## Not done here

Common-view tag writing, per-system tag writers, muxing, remux and trim, ASF writing, an index-aware AVI writer, asynchronous or ranged output (the destination is a `Stream`).

# Structure writers and the safe writer

Normative description of how OneMedia edits files without disturbing what it was not asked to change. Code: `RecursiveAI.OneMedia.Writing` (`EditPlan`, `SafeWriter`, `WriteReport`),
`.IsoBmff` (`BoxBuilder`, `BoxEditor`), `.Matroska` (`EbmlWriter`, `EbmlEditor`), `.Riff` (`ChunkBuilder`, `ChunkEditor`).

## Model

- An edit is **planned**, then **applied**. An editor takes the lazy tree of a file plus the edits asked for (`Replace`, `Remove`, `AppendChild`) and builds an
  `EditPlan`: an ordered list of output pieces. A piece is either a **copy** of a source byte range (media payload, untouched elements and headers), literal
  **data** (new or edited elements) or a **generated** stream (chunk offset tables, `Cues`, runs of padding zeros, which are written while streaming). Planning reads structure only;
  applying streams the pieces to a destination with a bounded buffer, so memory does not depend on the file size.
- **Padding is never held.** A `free`/`skip` box, a Void element, a `JUNK` chunk, an ID3v2 or ASF padding area or a FLAC PADDING block is a small header piece followed by a generated run of
  zeros, so a tag with 200 MiB of padding plans and writes in a few buffers (an ID3v2 tag may carry 256 MiB). One limit: the ID3v2 tag inside a WAVE/AIFF `id3 ` chunk is still built as
  one chunk, so a chunk with a very large padding is held while it is planned.
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

**Padding on growth.** When an edit makes a tag region larger than the room it had, the file has to be rewritten, and the new region is followed by a modest padding
(`GrowthPadding` on the three editors, `TagWriteOptions.PaddingBytes` for tag writes, **1024 bytes by default**) so that a later `SaveInPlace` of a slightly larger value fits:
a `free` box in MP4 (after the grown box, in its parent), a Void element in Matroska, a `JUNK` chunk in WAVE/RF64/AIFF/Wave64, and a PADDING block in FLAC (as before), the ID3v2 tag and the ASF header, and
zero padding after the comment list of an Opus comment header (RFC 7845 §5.2). 1024 bytes holds a longer title or a few more fields and costs nothing next to media; one padding is left
per grown element, so an edit that grows three Matroska SimpleTags leaves three. It is off when `PaddingBytes` is 0 (`TagWriteOptions.Minimal`), it never applies to an edit that fits the
existing layout, and an unedited file (or an edit that changes nothing) is copied byte for byte. Ogg Vorbis and Ogg FLAC comment headers have no padding to leave (a Vorbis comment packet
ends in its framing bit; an Ogg FLAC PADDING packet would change the header packet count) and keep their old behaviour: a later edit fits in place only at the same page layout.

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
- `Save(plan, path)` writes `.name.onemedia-<host>-<pid>-<random>.tmp` in the same directory (`<host>` is four hex digits of a hash of the machine name, `<pid>` the process id in hex), `fsync`s, and replaces the target with an atomic rename (`File.Move` with overwrite; the
  Unix permission bits of the original are kept). A failure or cancellation deletes the temp file; a killed process leaves the original untouched and at most one stray temp file
  that is never the target. **Stale temp files.** Before it writes, `Save`/`SaveAs` removes temp files of the same target in that folder that match the name pattern exactly and either
  belong to a process of this machine that no longer exists, or were last written more than 24 hours ago (the age rule covers another machine's debris on a shared folder and a process
  id that cannot be checked, as in the browser). A temp file whose process is alive, one of this process, one of another target and any file that does not match the pattern are left alone.
  Nothing is removed when the folder cannot be listed or written; failures to clean up are ignored.
  - **Open handles and Windows.** The source handle `Save` itself uses is closed before the rename, because Windows cannot replace a file while any handle to it is open. `FileMediaSource` opens files with delete sharing, so a reader (`MediaFile`) opened on the same path does not by itself block other opens, but Windows can still refuse to rename over a file that another handle holds. In that case `Save` throws `FileInUseException` (an `IOException`): the original is complete and unchanged, and the temporary file is removed. `MediaFile.Save()` and `SaveInPlace()` close the file's own handle around the write and open the saved file afterwards (the object then reads the new file and its staged edits are cleared; on a failed save the original is reopened and the edits stay), so they work on Windows with the file open; a handle held by another process still gives this exception. For `SafeWriter` and `TagWriter` called directly, dispose your own reader first, or use `SaveAs`. Linux and macOS replace the name atomically and the reader keeps reading the old file. No native calls are used to force a replacement, so the Windows outcome with an open reader is either a clean replacement or this exception, never a half state.
  - **What the replacement is, on Linux and macOS.** The result is a new file: it has a new inode and modification time, the original's permission bits (`chmod`), and the owner and group of the
    user running the save. Extended attributes, ACLs, SELinux labels, hard links and the creation time of the original are **not** carried over (no native calls are made), and a read-only file in a
    writable folder is replaced like any other. A symbolic link is followed: the file it points to is replaced (a temporary file beside that file) and the link stays a link. Two saves of
    the same path at once each write their own temporary file and the later rename wins; there is no lock. The folder itself is not `fsync`ed after the rename, so after a power loss (not a
    killed process) the old name may still point at the old file. Only `kill -9` is tested (500 rounds, no corrupt target); power loss is not, so no all-old-or-all-new claim is made for it. A program that must know the rename is durable has to flush the folder itself. This cannot be done from the BCL: opening a directory for `FlushToDisk` is refused on Linux and macOS
    (`UnauthorizedAccessException`) and shipped packages make no native calls; the file's data is `fsync`ed before the rename.
  - **A plan is checked against the file by length only.** If another program changes the file after it was planned and leaves its length alone, the copied ranges come from the changed file.
- `SaveInPlace(plan, path)` writes only the changed ranges of a same-layout plan, in place. A plan that is not same-layout throws `PaddingRequiredException` (bytes
  required, bytes available) before the first byte is written; `Required` is the real shortfall (for a shrink that leaves a gap too small for a padding header, the missing part of the header: 5 for
  a 3-byte gap in MP4). **Only bytes that differ are written.** Each new piece is compared with what the file holds at that position, and runs of equal bytes (the zeros of a padding area, the
  unchanged part of a rewritten box) are skipped; differing runs closer than 32 bytes are written together. A 3-byte title edit in 200 MiB of ID3 padding writes the changed bytes of the tag (a few
  dozen), not the padding, and `WriteReport.BytesWritten` says what was actually written. It is still **not atomic**: a crash in the middle leaves a mix of old and new bytes in the edited ranges,
  but the window is the few ranges that changed, not the whole padding. Use `Save` when that matters.
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

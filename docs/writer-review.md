# Lossless-writer review

Review of the safe writer, the editors and tag writing, done by trying to break them. Scope: `SafeWriter` and the editors (`src/OneMedia/Writing/`, `docs/writer.md`), tag writing in every system
(`src/OneMedia/Tags/Writing/`, `docs/tag-writing.md`), `MediaFile.Save`/`SaveAs`/`SaveInPlace` and the `onemedia tags` command. Muxers, remux and trim are out, except where they share the writer.

## Verdict

@@VERDICT@@

## Findings

Severity: **data loss** (a user's data or file layout is lost), **corrupt output** (the written file is wrong or unreadable), **wrong report** (the report says something that is not so), **cosmetic**.
"Fixed" findings have a regression test in `tests/OneMedia.Tests/Writing/WriterReviewTests.cs` (and, for fuzz repros, a file under `testdata/fuzz/`). Open items say why they stay open.

| # | Severity | Finding | State |
|---|---|---|---|
| 1 | data loss | `Save` on a path that is a symbolic link replaced **the link** with a regular file and left the file it pointed to unedited: the edit appeared to succeed, the link was gone, the real file unchanged. | Fixed: `Save` follows the link and replaces the target file; the link stays a link. Test `SaveThroughASymbolicLinkEditsTheTargetAndKeepsTheLink`. |
| 2 | corrupt output | RF64/BW64 files: the real RIFF size in `ds64` was **not updated** by any edit that changed the file's size (the editor treated the `0xFFFFFFFF` size field as an open-ended size and skipped the adjustment). A file that grew by 50 bytes kept a `riffSize` that was 50 short (0 in the two fixtures, which stated 0 to begin with). | Fixed in `ChunkEditor.Build`. Test `Rf64KeepsItsSizeFieldsAndPatchesDs64`. |
| 3 | corrupt output (refusal expected) | Matroska: a Segment (or other master) stating a size within a few bytes of the EBML maximum made the editor throw `ArgumentOutOfRangeException` when it re-emitted the header (found by the `writer-ebml` fuzz target, 68-byte repro). | Fixed: typed `UnsupportedMediaException` with the offset. Repro `testdata/fuzz/writer-ebml/u35-ebml-size-overflow.bin`, test `AnEbmlSegmentThatStatesAnAlmostMaximalSizeIsRefusedNotCrashed`. |
| 4 | cosmetic | A tag edit that asked for nothing (`TagEdits` empty) still rewrote ID3v2 tags: a v2.2 tag was upgraded to v2.4, an extended header dropped, a footer turned into padding. `MediaFile.Save()` with nothing staged did the same. | Fixed: an empty edit plans one copy of the file. Test `AnEmptyEditChangesNothing` (20 fixtures). |
| 5 | cosmetic | `onemedia tags f --set … --out f` (output = input) ended in an unhandled `ArgumentException` (exit 134, core dump). | Fixed: reported as an error, exit 1. |
| 6 | cosmetic | `SaveAs` without `overwrite` checked for an existing file and then replaced it with `File.Move(overwrite: true)`: a file created in between would have been overwritten. | Fixed: the move only overwrites when asked. Test `SaveAsWithoutOverwriteNeverReplacesAnExistingFile`. |
| 7 | open, by design (documented) | `Save` creates a new file: new inode, new modification time, the owner and group of the saving user; extended attributes, ACLs, SELinux labels and hard links of the original are not carried over. A read-only file in a writable folder is replaced. The folder is not `fsync`ed after the rename. | Documented in `docs/writer.md` (Safe writer). The BCL has no portable way to copy ownership or xattrs; calls into the OS are not allowed in the shipped packages. |
| 8 | open (low) | A plan is checked against the file by length only. If another program changes the file between planning and writing and leaves its length alone, offsets in the plan point into different structure. Needs a concurrent writer and an equal-length change. | Documented. A fix needs the plan to carry the modification time or a head/tail hash. |
| 9 | open (low) | `SaveInPlace` can tear on a kill or power loss for ranges that take more than one `write`. Documented since 0.3 (`not atomic`); measured below. | Documented; nothing to fix, `Save` is the atomic path. |
| 10 | cosmetic | A Matroska file with zero bytes after its Segment is refused with "damage in front of its first Cluster", a message that points at the wrong place. | Open (message only). |
| 11 | cosmetic | `PaddingRequiredException.Required` is 1 for a *shrinking* edit that leaves a gap smaller than the smallest padding header (a `free` box needs 8 bytes; a FLAC padding block 4). The edit can be written with `Save`. | Open (message only); stated in `docs/writer.md` ("a gap smaller than the smallest padding header does not fit"). |
| 12 | cosmetic | Applying the same tag edit a second time is not always a no-op: the first re-application may normalise the file (WAV `id3 ` chunk, QuickTime key table, AIFF); the third application is a fixed point on every fixture. | Documented in `docs/tag-writing.md`. |
| 13 | not a writer defect | The `tagwrite` fuzz target found an MP3 of one frame plus junk plus an APE tag that opened before an edit and not after (`No audio frame was found`). The frame is confirmed only by what follows it; the original opens through the accident of its tail layout (cut at the end of the junk it does not open). The writer left the audio bytes alone. | Repro `testdata/review/mp3-single-frame-before-ape.bin`, skipped test explains. Real files hold many frames. |

@@NUMBERS@@

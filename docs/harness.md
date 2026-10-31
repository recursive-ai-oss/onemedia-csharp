# Differential and I/O harness

How the readers are checked against reference tools, and how probe cost is measured. The code is the console app
`tools/OneMedia.Oracles` plus the scripts under `tools/oracles` and `tools/corpus`. None of it ships in a package.

## Layout

State lives under `$ONEMEDIA_HOME` (default `~/onemedia`), never in the repository:

| Path | Content |
|---|---|
| `oracles/bin` | pinned reference tools (ffprobe, ffmpeg, MP4Box, mkvinfo, mkvmerge, mkvextract, mkvpropedit, mkvalidator, mediainfo, exiftool) |
| `corpus/<set>/` | fetched sample media (`blender`, `matroska-test-files`, `xiph`, `samples`) |
| `corpus/generated/` | files written by the tools from synthetic input, with `MANIFEST.tsv` (hash, size, tool and version, command) |
| `results/<family>/` | one JSON result per file and `summary.txt` |

## Shared machines

Scripts that download or generate stop (or wait) while the file named by `ONEMEDIA_BENCH_LOCK` exists, so they never
overlap a timing run. `io.sh` runs the I/O measurement as light work: it pauses on that lock and resumes after it.

## Oracles

`tools/oracles/install-linux.sh` and `install-macos.sh` download release builds, check each SHA-256 against the pin in
the script, unpack user-locally and write small wrappers into `oracles/bin`. They are idempotent: a matching download is
reused, an unpacked tool whose pin matches is skipped. `RECORD=1` prints the hash of an entry that has no pin yet.
`build-mkvalidator.sh` builds mkvalidator from its published release tarball with CMake and a C compiler and installs
the executable. The tools are executed only; their files and sources are never read.

## Corpus

`tools/corpus/manifest.tsv` lists every fetched file: URL, archive kind, SHA-256 and size of the download and of the
file, licence or notice, and whether it may be committed (`committable`, small cuts with the notice) or is
`fetch-only`. `fetch.sh` downloads what is missing, verifies pins, and stops at once if a benchmark lock appears.
`generate.sh` writes the generated set; `generated-manifest.tsv` is a copy of its manifest from the last full run. Files that a pinned encoder cannot produce are reported as failed and skipped.
Not available in the pinned ffmpeg build: HE-AAC (no AAC encoder beyond the native LC one).

## Packet differential

`OneMedia.Oracles diff` runs `ffprobe -show_streams -of json` and `ffprobe -show_packets -show_data_hash SHA256 -of compact`
(compact lines stream in constant memory; the output carries the same fields as the JSON form) and our demuxer through
the format registry. Both sides are reduced to one record per packet:

- track (ffprobe stream index = our track index),
- `pts`, `dts`, `duration` as exact `MediaTime` in the stream's time base, unknown when ffprobe prints `N/A`,
- payload size, source position (`pos`), keyframe flag, SHA-256 of the payload.

Packets are compared per track in order, field by field; a count mismatch is its own difference. A difference is
excused only by a line of the committed exceptions file (`tools/OneMedia.Oracles/exceptions.tsv`: format, field, scope,
rule, which side follows the specification, reason). Reports separate real differences from excepted ones and keep
three examples per track and field.

Readers are plugged in with one line in `Readers.Create()`; structure providers with one line in
`StructureCompare.Providers`. With no reader registered every file reports `unsupported` and the ffprobe packet counts.

`--audio` adds the OneAudio comparisons: audio tracks of MP4/3GP against `Mp4PacketSource` (size, payload hash, decode
time and duration in the media timescale), and Matroska audio against `OggPacketSource` on the Ogg twin generated in
`generated/ogg` (payload size and bytes only; Ogg packets carry no container timestamps).

`structure` compares the box listing of `MP4Box -diso` (type and size, in file order) with the tree a registered
provider returns, and reads mkvinfo's element listing for Matroska.

`selftest` checks the harness itself: a fake format that replays ffprobe's packets must produce zero differences, and
one with planted mistakes (shifted times, short payloads, flipped flags, a missing packet) must produce exactly the
planted counts.

### Known ffprobe conventions

- Matroska `pos` is the start of the Block element, not of the frame payload.

## Sweeps

`tools/corpus/sweep.sh isobmff|matroska|all` builds the tool and runs `diff` in chunks of at most 30 minutes
(`--budget 25` stops starting files; exit 75 means files remain). `SWEEP_WRAP` gives a command prefix for each chunk, for
running through a job queue. Results are per file, so a rerun resumes.

## I/O measurement

`OneMedia.Oracles io` measures the cost of probing each file, in bytes read and request or call counts:

- **HTTP:** a local server (`RangeServer`, plain sockets, single `Range`, 16 KiB send buffer) counts requests, ranged
  requests and body bytes written. A client that closes early may have received up to about one socket buffer less than
  counted. ffprobe runs `-show_format -show_streams`; mediainfo gets the URL; our reader goes through
  `RangedMediaSource` and `ObservedMediaSource` over `HttpClient`. A tool without URL support is reported as failed.
- **Local files:** `strace -f -e trace=openat,close,read,pread64,readv,preadv,mmap` on the tool; bytes are the sum of
  returns of read-class calls on descriptors opened on the file, calls their count; mapped length is noted separately.
  Used for ffprobe, mediainfo and ExifTool, and for our reader through `ObservedMediaSource` over `FileMediaSource`.

Page cache does not affect these counts. Timing is not measured here.

### Baseline of the reference tools

`docs/io-oracle-baseline.tsv` holds the measured cost of ffprobe, MediaInfo and ExifTool on the whole corpus: bytes and
request or read-call counts per file. Our own reader's columns join it as the readers land (`io` already measures them
through the same sources). ffprobe and MediaInfo were run with the pinned builds listed in the installers.
ExifTool timed out (five minutes) on one Matroska audio file with nested tags and is marked `n/a`.

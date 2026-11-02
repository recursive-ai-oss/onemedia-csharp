# Speed rows

`bench/OneMedia.Bench` measures OneMedia against managed comparators and prints the ratio, allocations and memory for each
row. It is a console app, not packed, and the only project that references the comparator packages.

## What is measured

| Row | OneMedia | Comparator (the bar) | Reference tools (information) |
|---|---|---|---|
| `mp4-demux` | read every packet of every track | SharpMP4 | `ffmpeg -c copy -f null` |
| `mp4-audio` | read the audio track only, other tracks switched off | OneAudio `Mp4PacketSource` | – |
| `mp4-probe` | open, track list, duration | MetadataExtractor, TagLib#, ATL.NET (fastest of the three counts) | ffprobe, MediaInfo, ExifTool, `MP4Box -info` |
| `mkv-demux` | read every packet | the Matroska package (full deserialise, then walk the blocks) | `ffmpeg -c copy -f null`, `mkvextract tracks` (first track) |
| `mkv-probe` | open, track list, duration | the package's element reader: Info and Tracks read, clusters skipped | ffprobe, MediaInfo, ExifTool, `mkvmerge -J`, `mkvinfo` |
| `mp4-mux` | write the frames of an AAC track held in memory as an MP4 in memory (moov-first; moov-last beside it) | SharpMP4 | – |
| `mp4-remux` | read the AAC track of a file and write it as an MP4 in memory | SharpMP4 (its reader and builder) | `ffmpeg -c copy -map 0:a:0 -f mp4` |
| `mkv-mux` | read every packet and write a Matroska/WebM file to a counting sink | none (no managed Matroska writer) | `ffmpeg -c copy -f matroska`, `mkvmerge` |
| `tagw-id3`, `tagw-flac`, `tagw-ilst`, `tagw-riff`, `tagw-mkv` | change title, artist, album and comment of a tagged file held in memory; the edited file ends up in memory | TagLib#, ATL.NET (a library that cannot write the format shows `fail` or no row) | – |
| `remux-mem` | remux each file over 4 GiB into Matroska and MP4 on disk; peak resident memory is the figure | none | `ffmpeg -c copy -f matroska` |
| `playable` | `MakePlayable` for the generic MP4 profile on one H.264+AAC Matroska file | none (information) | `ffmpeg -c copy -f mp4` |

The Matroska package counts a laced block as one packet, where OneMedia delivers each frame of a lace; the table flags the
different counts. The comparator therefore does less work per file than OneMedia on laced audio, which favours the comparator.

Files per row are listed by `list`: a small generated file, open-movie files of different shapes (1080p, fragmented,
Matroska/WebM), the published Matroska test files, and the over-4 GiB files with the index at the end (MP4) and a plain
over-4 GiB Matroska. A file missing from the corpus is skipped and said so.

**Tag-write rows.** Each implementation starts from the same bytes in memory and ends with the edited file in memory: OneMedia plans the edit and writes a new copy (`SaveAs` without the disk); TagLib# and ATL.NET edit the memory stream they were opened on (`File.Create` with a stream abstraction; `new Track(stream, mimeType)` then `Save()`), so they copy the input into a fresh stream first, which is counted. The three do different amounts of work for padding: OneMedia keeps and rewrites padding as stored.

**The work is the same for everyone.** A packet read counts the packet, adds its size and XORs its first and last byte, so
every payload is delivered to the caller and not skipped. The counts are printed next to the times; a comparator whose
packet count differs from OneMedia's is flagged in the table (Matroska laced blocks, files the package cannot read). The probe
rows read what each library offers: tracks and duration, plus the title and first artist where the library has them;
OneMedia's native tags join its probe when its tag layer lands.

**Calls.** Each comparator is called as its published README shows, with a 64 KiB file buffer. Where the README does not cover a
row the obvious public call is used: SharpMP4 reads track by track with `ReadSample`; MetadataExtractor uses
`ImageMetadataReader.ReadMetadata(stream)` and reads every tag's description; TagLib# uses `File.Create(path)`; ATL.NET uses
`new Track(path)`. ATL.NET is audio-oriented and may refuse video files; a refusal is shown as `fail`.

## Method

- One fresh process per (row, implementation, file), so peak memory belongs to that work alone. The parent reads each file
  once before its rows so all implementations start with it in the page cache; the tables say so.
- In the process: a cold first run (JIT and type loading included), then a warm-up of at least 30 runs and 1.5 s for every
  implementation alike, so that the runtime has promoted the hot methods to optimised code before any sample (tier-0, then the
  instrumented stage, then optimised, each behind a 100 ms quiet timer). Then one sizing run and 5 samples, each the mean of enough
  back-to-back runs to take about 50 ms. The median is reported, and the range is kept in `results.tsv`. Probe rows (`*-probe`) are
  sampled this way on every file, since probe work does not grow with the file. Demux-style rows on files over 1 GiB get the cold run
  and then 3 samples of one run each. The cold first run is shown apart in the note column, for the comparators as well; it is a
  published, non-gating number (the first open in a process: what a one-off `info` call or a per-file open pays).
- Allocation is `GC.GetAllocatedBytesForCurrentThread` per run, and per packet. Peak memory is the process's peak working set;
  the working set before any work is printed beside it.
- Reference tools run as processes under `/usr/bin/time`, three times (once over 1 GiB): wall time measured by the parent, peak
  resident set from `time`. Their start-up cost is part of the figure.
- Ratio = OneMedia's median ÷ the fastest managed comparator's median for that file. The bar is a geometric mean ≤ 1.00× per
  row and no file above 1.25×. Rows with no managed comparator pass.
- Every run records CPU, logical cores, ISA flags, storage of the corpus, runtime, restored package versions, tool versions and the
  one-minute load average before, during (mean and max of 5 s samples) and after.

## Running it

```
dotnet run -c Release --project bench/OneMedia.Bench -- run --rows 0.1 --out results/x64 [--corpus DIR]
```

`--rows` takes a stage (`0.1`) or row ids. `--files a,b` keeps files whose path contains one of the strings, `--no-tools` leaves
out the reference tools, `--reps` and `--tool-reps` set the repetitions, `--budget MIN` stops starting new cells after that many
minutes (exit code 75) and `--resume` continues from `results.tsv`. `report` rebuilds `report.md`; `prepare` derives the
fragmented MP4 (done by `run`); `list` shows rows and files. The corpus is `$ONEMEDIA_HOME/corpus` (default `~/onemedia/corpus`).

Timings are only comparable between runs on a quiet machine. The published run is one command per machine under the
exclusive bench queue.

## JIT tiers: steady state and first open

Each cell runs in a fresh process. The runtime starts every method in unoptimised tier-0 code and promotes hot ones only after a
100 ms quiet period and a background compile, with a second instrumented stage under dynamic PGO. Measuring before that climb
finishes measures tier-0 code, which punishes libraries that do more of their own work per call. The samples are therefore taken
after a warm-up long enough for every implementation to have tiered up (see Method), and the library carries no tiering overrides.

The first call in a process is a separate matter: it is JIT dominated, it is what a one-off open pays, and it is reported as "cold first
run" for OneMedia and for every comparator. It is published but does not enter the ratio.

To see the unwarmed regime for a cell, run `OneMedia.Bench child ROW IMPL REL CORPUS REPS` under `DOTNET_TieredCompilation=0` (every
method fully optimised) or `DOTNET_TieredPGO=0`.

## Provenance of the comparator calls

Every comparator call was chosen from the package's published README, its package page and its public type and member
signatures. No comparator's source, decompiled code or unpacked package contents was read, and no reference tool's source was
read; the tools run as released binaries.

## Looking at a cell

- `OneMedia.Bench once ROW IMPL REL [CORPUS [TIMES]]` runs one cell, optionally in a loop, and prints the mean time of a run.
- `bench/iocount.sh DLL ROW IMPL REL` counts the read calls and bytes a cell makes on its input file (strace).
- `bench/profile.sh DLL ROW IMPL REL TIMES` samples a looped cell with `dotnet-trace` and prints self and inclusive time by method.
  It needs `dotnet-trace` in `~/onemedia/u5-bench/tools` (`dotnet tool install dotnet-trace --tool-path ...`).

## Subtitle rows

`OneMedia.Bench subsbench [-r ROWFILTER] [-n SAMPLES] [CUES...]` times the sidecar readers and writers next to SubtitlesParser (MIT)
and the Subtitle Edit core library (GPL; public API only, bench project only, never shipped). The bar is the fastest managed
comparator per row, which is the Subtitle Edit core library wherever it has the format.

**Input.** One synthetic SRT of N cues (seeded, 1–2 lines each, about one line in seven wrapped in `<i>`, accented letters and
commas), read once by OneMedia to write the WebVTT, ASS and TTML files of the same cues with OneMedia's writers. Every
implementation reads the same bytes, and each row prints the number of cues it parsed (or the byte length of its output) so that a
difference in work shows. Sizes are 20,000 and 100,000 cues (1.4 MB and 7.2 MB of SRT).

**Rows.** `parse srt|vtt|ass|ttml`; `convert srt->vtt|ass|ttml`. SubtitlesParser has no TTML parser and no writers.

**What the work is.**
- *Parse*: file bytes in, cues with times and text out. Both sides decode the bytes (OneMedia from bytes, the comparators through
  `Encoding.UTF8.GetString` and a split into lines) and read every cue.
- *Convert*: that parse, plus the target format written and encoded to file bytes (Subtitle Edit: `ToText` then
  `Encoding.UTF8.GetBytes`; OneMedia: `Write(...).Data`).
- OneMedia does more on the way than the comparators, which favours them: it detects the encoding (byte order mark, UTF-16 without
  one, UTF-8 validity), detects the line ending, parses inline markup into styled spans and keeps what the model has no field for as
  extras, logs defects with byte offsets, reports every rounding in a loss report (SRT milliseconds to ASS centiseconds is a
  reported approximation for most cues) and returns the written text next to its bytes. Subtitle Edit's SRT and ASS loaders keep
  the raw text with its tags and report nothing.

**Method.** One process; every implementation of a row is warmed with 30 runs, then sampled in turn (one run of each, round after
round) so that other processes' load lands on all of them alike; the median and minimum of `-n` samples (default 30) and the bytes
allocated per run by the calling thread are printed. The published figure is the median on an exclusive machine; runs on a busy
shared machine are indicative.

**Equivalence check.** `OneMedia.Bench subsdump [-m MUTATIONS] FILE...` reads the files, their CRLF, CR, BOM and UTF-16 variants and
seeded damaged variants (byte deletions, flips, inserted markup characters, duplicated lines, truncation) in all six sidecar
formats and prints one SHA-256 per input and format over everything the model holds (cues, spans, styles, regions, extras) and the
defects with offsets, plus what each writer produces from it and its loss report. Diffing the output of two builds proves a
performance change did not change behaviour: the tuning of the subtitle layer was held to 0 differences over 30,000 input/format
pairs (testdata sidecars, a rich generated document in five formats, 120 damaged variants of each).

`OneMedia.Bench subsloop WORK CUES SECONDS` repeats one OneMedia subtitle workload (`parse-srt`, `parse-vtt`, `parse-ass`, `convert-vtt`,
`convert-ass`) for a sampling profiler such as `dotnet-trace`. At 100,000 cues a run allocates 54–110 MB and keeps tens of MB alive until
it ends, so garbage-collector time is a large share of every row; medians on a shared machine swing more than the minimum.

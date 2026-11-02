# Platforms: the browser WebAssembly runtime

The whole library, the `.Audio` adapter included, runs on the browser-wasm runtime of .NET 10: single-threaded, interpreted, with an in-memory
virtual file system (Emscripten's MEMFS) in place of a disk. The unit-test suite is built for that runtime and run in headless Chromium, so this is
checked on every change rather than assumed.

## How the suite runs

- `tests/OneMedia.Tests.Browser` compiles the sources of `tests/OneMedia.Tests` unchanged into a browser-wasm app. The fixtures the tests read
  (`testdata/...`, `docs/tag-mapping.md`) are placed in the virtual file system at `/testdata` and `/docs`, where `AppContext.BaseDirectory`
  (`/`) points. The assembly has the same name as the test project, so the tests see the same internals.
- xUnit's own runner needs an app host and threads, which the browser runtime does not have. `Program.cs` in the host is a small reflection
  runner instead: it runs `[Fact]` and `[Theory]` methods (`[InlineData]`, `[MemberData]`, dynamic `Assert.Skip`) one after another, each on a new
  instance of its class, and prints one line per test, then `RESULT passed=… failed=… skipped=… excluded=…`.
- `tools/wasm-suite/run.sh` builds the host (needs the `wasm-tools` workload) and `tools/wasm-suite/run.py` serves the app bundle and drives
  headless Chromium through Playwright (Python package; `--channel chrome` uses an installed Chrome instead). Extra runner arguments:
  `--filter TEXT` (substring of `Namespace.Class.Method`), `--list`, `--trace`, `--quiet`, `--include-excluded` (runs the excluded tests anyway).
- The same bundle also starts under node: `node main.mjs [--filter TEXT]` in the bundle folder.

Last full run: Chromium 140.0.7339.16 through Playwright 1.55.0, x64 Linux, Release, about 9 minutes: **1,713 passed, 0 failed, 1 skipped, 13 excluded** test cases. The normal run of the same sources reports 1,729 cases and the same 1 skip; the 15 cases the browser run does not execute are the 13 excluded methods (two are theories with 3 and 2 rows).

## Excluded tests

A test that cannot run in a browser carries one trait, `[Trait("Browser", "<category>")]`. The normal test run ignores it; the browser runner counts
the test as excluded, per category, and prints the counts. A test is excluded only for one of these reasons, never because it fails on a bug.

| Category | Why a browser cannot run it | Tests |
|---|---|---|
| `concurrency` | The test needs another thread or a timer to act while the code under test blocks: a writer appending to a file that a growing-file reader waits on, a cancellation timer firing during a synchronous wait. There is one thread, and a timer cannot fire while it is busy. | 3 |
| `allocation` | Asserts zero allocated bytes per packet on the optimising JIT. The browser runtime interprets the code, and the interpreter allocates for its own frames (a constant 40 to 1,000 bytes per packet in the runs, not growing with our code). The zero-allocation gate is held by the normal suite and the NativeAOT build. | 5 |
| `timing` | Asserts a wall-clock bound on a seek (30 s allowed for work that takes milliseconds natively); the interpreter is 10 to 100 times slower. | 1 |
| `process` | Reads the id of the running process or starts one, or watches the directory: the browser has no processes and no `FileSystemWatcher`. | 2 |
| `filesystem` | Asserts Unix file permissions (read-only directory, mode bits kept by `Save`); the virtual file system has no permission model. | 1 |
| `md5` | Needs MD5 from `System.Security.Cryptography`, which the browser runtime does not provide. The FLAC packet encoder of OneAudio hashes with it for the STREAMINFO signature, so transcoding to FLAC fails in the browser (reported to OneAudio). | 1 |

Counts are test methods; a theory counts once however many rows it has. Nothing is excluded for needing an oracle, a large file or the network:
the suite has no such tests (oracle differentials, corpus runs and multi-gigabyte checks live in `tools/` and `bench/`, not in the suite).

## What changed in the library for the browser

- `FileMediaSource.ReadAsync` reads in place when `OperatingSystem.IsBrowser()`: asynchronous file reads need a pool thread to finish on, and
  a caller that blocked on the returned task (HLS and DASH playlists opened by path, `Media.Open` on a presentation) waited for ever. The task it returns is now complete.
- A reader that is written as synchronous code but opened over an asynchronous source waited on that source's tasks. Elsewhere it does so on a pool thread; in the
  browser nothing can finish the task while the thread waits, and the runtime's own error says only that monitors cannot be waited on. Those waits
  now go through one helper (`EngineHost.Wait`) that returns at once for a task that is already complete (every source over memory, a file in the virtual file system, or bytes the
  caller has already fetched) and otherwise throws `NotSupportedException` with a message that says what to do. `LateAnsweringSourceTests` pins both outcomes.

## Reading from a Blob or any source that answers later

| Reader | Source that answers later (a `Blob` slice, an HTTP range) | Source that answers at once (memory, file) |
|---|---|---|
| ISO BMFF / QuickTime, Matroska / WebM, ASF, Ogg | works: asynchronous engines, one awaited read per range | works |
| MPEG-TS / M2TS, MPEG-PS, elementary streams, AVI, DV, FLAC, FLV, WAV/BWF/RF64, AIFF, CAF, ADTS/AC-3/MP3/AMR | `NotSupportedException` in the browser (works elsewhere by waiting on a pool thread) | works |

The second row needs a true asynchronous engine per format (the readers parse with synchronous byte reads, and a read that has to wait cannot be
rolled back and retried without rewriting each reader's state handling). Until then, a browser page opening one of those formats from a `Blob` reads the bytes into memory first (`Media.Open(byte[])`), which is
fine for files that fit in memory.

## Known limits in the browser runtime

- **`MakePlayable` with a FLAC audio target throws in the browser.** OneAudio's FLAC encoder needs MD5, which the browser runtime does not provide. This holds until OneAudio ships a fix; other audio targets are not affected.
- **Most synchronous readers need a source that answers at once.** MPEG-TS and MPEG-PS, elementary streams, AVI, DV, FLAC, FLV, WAV/RF64/W64, AIFF and CAF work over memory and virtual-file-system sources. Over a source that answers later, such as a `Blob` slice, they refuse with `NotSupportedException`. ISO BMFF, Matroska, ASF and Ogg read such sources. Asynchronous engines for the other formats are planned, one format at a time, after 1.0.

## Not covered by the suite

- Allocation behaviour of the pull path in the browser runtime (excluded above; the NativeAOT smoke app covers the optimised build).
- Playback in the browser's media elements: that is `tools/playback`, a separate harness against real files.
- Speed in the browser: the suite runs the interpreter; no timings from it mean anything.

## Phones

The trimmed Release smoke apps for Android and iOS, how to run them and their last results are in [mobile.md](mobile.md).

# Packaging: CMAF and transport-stream segments, HLS and DASH

`Packager` (`RecursiveAI.OneMedia.Streaming`) cuts a file or a live stream into segments and writes HLS playlists (RFC 8216) and a DASH MPD
(ISO/IEC 23009-1, following the DASH-IF interoperability guidelines) beside them. `Media.PackageHls` and `Media.PackageDash` are one-line calls
over it; `onemedia package` is the command line. The manifests are also public models (`HlsPlaylist`, `DashMpd`) that read and write, so a
presentation can be parsed, edited and rewritten without the packager.

Nothing is decoded. Media bytes are the input's samples, repackaged where the target needs it (Annex B to length-prefixed, ADTS to raw), and
everything the segmenter writes goes through the same ISO muxer (`docs/muxing.md`) and transport-stream muxer (`docs/muxing-ts.md`) as remux.

## Layout

```
master.m3u8            HLS multivariant playlist (when HLS is asked for)
manifest.mpd           DASH MPD (when DASH is asked for)
video0/                one directory per track: video0, video1 (ladder renditions), audio0, audio1, text0
  init.mp4             initialization segment (CMAF)
  seg-00001.m4s ...    media segments, shared by HLS and DASH
  index.m3u8           media playlist
audio0/ ...
text0/ seg-00001.vtt   WebVTT segments for HLS; DASH points at subs.vtt
```

- **Several inputs make a bit-rate ladder**: the first input supplies every track, each further input its first video track as another rendition. Renditions switch
  cleanly only when their keyframes coincide; the report says when segment boundaries differ.
- **Single file** (`SingleFile`, `--single-file`): one `media.mp4` per track (init, a `sidx`, then the fragments). HLS playlists use byte ranges; DASH uses the
  on-demand profile (`SegmentBase` with the index range). Video on demand only. The segments are spooled in a temporary file until the track ends.
- **Transport-stream segments** (`SegmentFormat.MpegTs`, `--ts`): HLS only, one multiplexed rendition per input (`stream0`; a ladder gives each input its own audio),
  a `.ts` per segment or, with single file, one `media.ts` with byte ranges. Every segment starts at a keyframe with PAT and PMT and carries on the continuity counters.
  DASH is dropped with a note in the report. Opus, VP9 and AV1 have no transport-stream mapping the muxer writes and are refused.
- **Subtitles**: the input's text tracks (tx3g, WebVTT, SRT in Matroska, …) and `PackagingOptions.Subtitles` become HLS WebVTT renditions (each segment carries the cues that overlap it
  and an `X-TIMESTAMP-MAP`) and a DASH sidecar representation. Video on demand only.
- **Events** (`PackagingOptions.Events`, plus the input's own `emsg` events): DASH gets an `EventStream` in the period (timescale 1000, base64 `messageData`); HLS gets an `EXT-X-DATERANGE`
  (ID `<scheme>-<id>`, `X-VALUE`, hex `X-MESSAGE-DATA`, `SCTE35-CMD` for the SCTE 35 scheme) on the segment the event falls in, in every media playlist (RFC 8216 §4.4.5.1).

## Modes

| Mode | Behaviour |
|---|---|
| Video on demand (default) | Whole presentation written; playlists end with `EXT-X-ENDLIST`; the MPD is static. |
| Live (`LiveOptions`, `--live`, `--window N`) | One input, which may be forward-only (a pipe). Each segment and then the playlists are published as the segment completes (`SegmentWritten`, `ManifestWritten` callbacks). The playlist window holds `WindowSegments` (6); older segment files are deleted after `ExpiredSegmentsKept` (3) further segments; `EXT-X-PROGRAM-DATE-TIME` from `LiveOptions.StartTime`. When the input ends the playlists get `EXT-X-ENDLIST` and the MPD becomes static. Memory is one segment plus buffers whatever the length. |

Segment length is the target (`SegmentDuration`, default six seconds): a segment ends at the first keyframe at or after the target, so a long keyframe interval gives longer
segments and the playlists say so (`EXT-X-TARGETDURATION` is the rounded-up longest).

## Manifest reading

`HlsPlaylist.Parse` is strict about the tags it models and keeps the unknown ones in file order, so an unedited playlist rewrites to the text it was read from, apart from number
formatting. `HlsPlaylist.ParseLenient` accepts what real playlists get wrong and lists each deviation as a `ManifestDefect` with its line and offset: a missing `#EXTM3U`,
a segment URI with no `EXTINF` (kept, duration zero), a URI with no `EXT-X-STREAM-INF` (dropped), a malformed tag (ignored). Keys (`EXT-X-KEY`, with `KEYFORMAT` kept apart) are
modelled per segment as the keys in effect plus whether the tag appeared directly before the segment; an `EXT-X-KEY:METHOD=NONE` ends every key (RFC 8216 §4.3.2.4). When it rewrites, the
writer adds a `METHOD=NONE` before a segment whose keys no longer include a key format an earlier segment had, so reading the output gives the same keys in effect.
`DashMpd.Parse` matches elements by local name (another prefix or a newer namespace still reads), refuses DTDs, fetches nothing and keeps unknown elements. A segment template
carries its `presentationTimeOffset` (`DashSegmentRef.Time` is the media time `$Time$` stands for; `PresentationStart` is the position on the presentation timeline).

## Behaviour set by observation or choice

- **DASH profile**: `urn:mpeg:dash:profile:isoff-live:2011` for CMAF presentations, `isoff-on-demand` for single file, and `urn:mpeg:dash:profile:full:2011` when a sidecar subtitle
  representation is present (the live profile needs a segment template on every representation, which a sidecar `BaseURL` representation does not have).
- **`mimeType` and `startWithSAP`** are written on the adaptation set only. A conformance run reported them when they appeared on both levels.
- **TS segments**: audio that runs ahead of video in the input lands in the earlier segment (the segmenter cuts on video keyframes and keeps every packet in decode order).
- **ffprobe reading a DASH MPD** stops at the shortest stream's end, so its packet counts for the longer stream are lower; the differential classes that as explained. Re-framed sizes
  (Annex B to length-prefixed, ADTS to raw) are classed the same way, with equal payload after un-framing.

## Verification

Each number is from the checked-in scripts under `tools/package/` and the tests in `tests/OneMedia.Tests/Streaming`.

| Check | Result |
|---|---|
| Packet differential (ffprobe 7.0.2 static, ffprobe 8.1.3 for transport-stream sources): CMAF HLS and DASH, multi-file and single-file, 28 generated inputs; TS segments and TS single-file, 19 inputs | every file packet-equal to its input per stream; 0 unexplained; 8 of the TS inputs refused as designed (Opus, VP9, AV1) |
| TSDuck 3.44 `tsp -P continuity` on each concatenated TS segment set | 0 messages in 38 of 38 (dropping a segment on purpose reports "missing 13 packets") |
| Playback, hls.js 1.7.4 and dash.js 5.2.1 in a page of ours, play to the end at 2x plus a seek to the middle, 15 presentations (CMAF, single-file, TS, TS single-file, subtitles, ladder) | Chrome 155: hls.js 14 of 15 (the failure is `hevc-aac.mp4.ts`, HEVC in transport stream through hls.js; not investigated), dash.js 10 of 10. Firefox 141: 15 of 15 and 10 of 10. Playwright WebKit 26 (Linux): 15 of 15 and 10 of 10 |
| Native HLS (`<video src=...m3u8>`) | macOS WebKit: 7 of 7 (CMAF, CMAF single-file, TS, TS single-file, TS with subtitles, CMAF with subtitles, ladder). Playwright WebKit on Linux has no native HLS (media error 4) so 0 of 15 there is the build, not the output. macOS hls.js 7 of 7 and dash.js 4 of 4 in WebKit and Chrome 156 |
| Live, unpaced soak: 2 hours of 10 fps 320×180 audio and video piped as Matroska, 4 s segments, window 6 | 1794 segments, 9 files on disk throughout, resident memory 106 MiB plateau reached in 32 s and flat after (≈52 s wall: the input is not paced) |
| Live, real-time run (`live-paced.sh`): 600 s paced with `ffmpeg -re`, 4 s segments, window 6, `OneMedia.LiveCheck` reading every update as a player would | 150 video playlist versions, 150 audio, 150 MPD versions; each parsed with `HlsPlaylist`/`DashMpd`, rewritten to itself, window ≤ 6, media sequence never back, every listed segment and init segment on disk, no segment longer than the target duration; 0 failures; ended with `EXT-X-ENDLIST`; resident memory ≤ 73 MiB |
| Manifest parsers fuzzed (`hls-playlist`, `dash-mpd`, SharpFuzz and libFuzzer, 600 s each, on the final code) | 10,107,179 and 9,304,071 executions, 0 crashes on the merged tree (the first `hls-playlist` run found a sixth round-trip failure after 6.7 million executions, a quoted `FRAME-RATE` holding a comma written bare; fixed and run again). Earlier runs found five failures (four round-trip, one exception from the BCL XML reader), each now a test (`ManifestTests.FuzzFindingsRewriteToThemselves`, `AnEncodingSwitchTheTextReaderCannotMakeIsAMalformedDocument`) |
| Suite and platforms | 1726 tests (1 documented skip) pass on the 0.7.0-preview.1 tree; NativeAOT smoke 197 lines and browser-WebAssembly smoke 197 lines OK, with a packaging line (CMAF, HLS and DASH written and parsed, TS segments) in both |

### Validators

- **DASH-IF conformance software v3.1.0** (the command-line release, run as a black box): one run worked and reported two real findings, both fixed (`mimeType` and `startWithSAP` on both levels; the
  live profile claimed with a representation that has no segment template). Every later run, on the same and on new MPDs and on a public third-party MPD, failed inside the tool ("base
  URI must be absolute" while it fetched initialization segments), also after resetting its cache. The remaining modules (WAVE, CTA, low-latency) do not apply to plain DASH-IF content.
  DASH is therefore verified by the packet differential and by dash.js playback, not by the conformance tool. This is a known gap.
- **Apple `mediastreamvalidator`**: the HLS Tools download needs an Apple ID sign-in, so it has not been run. HLS is verified by the packet differential, hls.js and, in WebKit on macOS, native playback.
  Pending.

## Indicative timings (information only)

One 10-minute 640×360 H.264/AAC MP4 (37.8 MB), CMAF with 6 s segments, median of five runs, warm file cache, on a shared 32-core x64 Linux machine under a queue job; not a published row.
`tools/package/native-compare.sh` reproduces it.

| Tool | Output | Median wall time |
|---|---|---|
| `onemedia package` (HLS and DASH, includes .NET start-up) | 206 files, 37.3 MB | 0.19 s |
| Shaka Packager 3.9.3 (HLS and DASH) | 206 files, 37.4 MB | 0.42 s |
| MP4Box 26.07 `-dash` (DASH only) | 203 files, 37.3 MB | 0.39 s |

## Limits and known gaps

- Encryption is not written; a source playlist's `EXT-X-KEY` is read and reported, not decrypted.
- A ladder switches cleanly only on coinciding keyframes (reported, not forced).
- TS segments are HLS only and need a codec the transport-stream muxer carries.
- Live mode takes one input and holds the whole current segment in memory.
- DASH-IF conformance and `mediastreamvalidator` runs are missing (above).

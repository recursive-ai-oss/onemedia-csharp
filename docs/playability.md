# Browser playability

`MediaFile.CanPlay(profile)` and `Media.CanPlay(path, profile)` say whether a browser plays a file, and if not, why not and what would fix it. The
profiles are data (`BrowserProfiles`): static tables, nothing loaded at run time, no reflection. This page is the method, the evidence and the known
limits. Writing a playable file (`MakePlayable`) acts on the verdict later; the verdict is shaped for it.

## What a verdict holds

`PlaybackVerdict` has one `PlaybackModeVerdict` per way of handing media to a browser:

- **Progressive**: the file is a URL given to a `<video>` or `<audio>` element and the browser demuxes it.
- **MediaSource**: the file is appended to a Media Source Extensions `SourceBuffer`. Only fragmented MP4 and WebM can be, and the type string must pass `isTypeSupported`.

`CanPlay` is true when either mode plays. `Blockers` is the empty list then, otherwise the blockers of the mode with fewer of them. Each
`PlaybackBlocker` carries a stable `Code`, the `TrackIndex` it concerns (-1 for the container) and a `Remedy`:

| Remedy | Meaning | Who acts |
|---|---|---|
| `Remux` | the same samples in another container, or another sample-entry form, play | remux |
| `TranscodeAudio` | an audio track must be re-encoded | `.Audio` (OneAudio) |
| `VideoBlocker` | a video track would need re-encoding, which this library never does | reported only |
| `None` | encrypted samples, or no tracks | nothing |

`CanBeMadePlayable` is true when no blocker is a video blocker or unfixable.

| Code | Kind | Meaning | Remedy |
|---|---|---|---|
| `container.unsupported` | Container | the browser does not take the container as a downloaded file | Remux |
| `container.mediasource` | Container | the container does not go through MediaSource | Remux |
| `mediasource.not-segmented` | NotSegmented | not fragmented MP4 or WebM, so not appendable | Remux |
| `codec.container` | CodecInContainer | the browser plays the codec, not in this container (or not in this mode through this container) | Remux |
| `codec.unsupported` | Codec | the browser plays no such codec | audio: TranscodeAudio, video: VideoBlocker |
| `codec.mode` | Codec | the codec plays only one way (a URL, or MediaSource) | as above |
| `codec.profile` | CodecProfile | a profile the browser lacks (H.264 High 4:4:4, HE-AAC in a conservative profile) | as above |
| `codec.level` | CodecLevel | above the highest level that plays | as above |
| `codec.bit-depth` | BitDepth | more bits than play | as above |
| `codec.chroma` | ChromaFormat | a chroma format that does not play | as above |
| `codec.tag` | CodecTag | the sample entry form is wrong (`hev1` for Safari downloads) | Remux |
| `track.protected` | Protected | encrypted samples | None |
| `tracks.none` | NoPlayableTracks | no video or audio track | None |

Warnings never block: `progressive.movie-at-end` (the movie box follows the media data, so a download must reach the end before it can start; fix by
faststart remux), `hardware-dependent` (the codec profile plays only where the operating system or hardware decodes it: HEVC, 10-bit and 4:2:2/4:4:4
H.264 in Chrome and Firefox) and `mediasource.codec-string` (a track has no RFC 6381 string, so the SourceBuffer type is incomplete).

What a verdict cannot see: it reads the container header and codec configuration only. It does not scan samples, so a file whose blocks are
badly ordered (below) or whose frames use a feature the header does not announce can still fail. Hardware decoders, DRM, codec levels a browser
enforces from the actual frame rate, and autoplay policy are outside it.

## Profiles

| Id | Describes | Evidence |
|---|---|---|
| `chrome` | Chrome on Windows, macOS, Linux (proprietary codecs present) | verified: Chrome 156.0.8078.74, macOS arm64 |
| `chromium` | Chromium built without proprietary codecs (also most Linux distribution builds, embedded browsers) | verified: Chromium 140.0.7339.16, Linux x64 |
| `firefox` | Firefox where the OS or system libraries supply H.264, HEVC and AAC | verified: Firefox 141.0, Linux x64, system FFmpeg libraries |
| `safari` | the WebKit engine on macOS | verified: WebKit 26.0 (Playwright build), macOS arm64 |
| `webkit-linux` | WebKit (WPE) with GStreamer and its FFmpeg plug-ins | verified: WebKit 26.0 (Playwright WPE build), Linux x64 |
| `ios-safari` | Safari and every iOS browser | public documentation only |
| `android-chrome` | Chrome on Android | public documentation only |
| `generic-mp4` | what all of the above accept: H.264 Baseline/Main/High level <= 4.2, 4-2-0 8-bit `avc1`, AAC-LC, MP3 | derived: the intersection |

For the verified profiles every row a matrix file exercises was observed; rows no file exercises (for example Vorbis in MP4 for Chromium, HEVC in
MOV) are left out, which makes the verdict "blocked". Documented rows used for `ios-safari`, `android-chrome` and the hardware-dependence flags come from
MDN's *Web video codec guide* and *Web audio codec guide* and Chrome's and Apple's public media notes (facts only). They are not checked here.

The Playwright builds are not shipped browsers. Their differences are real and recorded in the profile names: Playwright's Chromium has no H.264 or
AAC (so `chromium` differs from `chrome`), Firefox's codecs come from the host (its H.264 and HEVC rows are therefore marked hardware-dependent),
WebKit on Linux plays whatever GStreamer decodes (so `webkit-linux` is not a model of Safari), and the macOS WebKit build shares Safari's media
stack but is not Safari: Safari itself cannot be driven headlessly here (safaridriver needs a one-time interactive authorisation).

## Method

`tools/playback/` holds everything; nothing in it is shipped.

1. `gen-matrix.sh` writes 75 small files (2 s, 320x240) with ffmpeg 7.0.2 (static build) from generated input: H.264 in every profile and level
   family, HEVC as `hvc1` and `hev1`, AV1, VP9 (profiles 0 and 2), VP8, MPEG-4 part 2, AAC, MP3, Opus, Vorbis, FLAC, AC-3, E-AC-3, ALAC, PCM and WMA, in
   MP4 (fast-start and movie-box-last), fragmented MP4, MOV, 3GP, WebM, Matroska, Ogg, TS, AVI, FLV, ASF, WAV, AIFF, CAF and raw streams.
2. `PlaybackCheck describe` opens each with OneMedia and writes the manifest: the content type and the `codecs` string our library derives, and the
   modes to try (`mediasource` only for fragmented MP4 and WebM whose string is complete).
3. `probe.py` serves the files over HTTP with Range support, loads each in a `<video>` or `<audio>` element (and, for MediaSource, appends the whole
   file to a `SourceBuffer`), and records `canPlayType`, `isTypeSupported`, errors, `currentTime`, decoded frames and the audio level. **Playing means:**
   no error, the clock passed 0.5 s, video frames were decoded, and for files with audio the generated 440 Hz tone is measured through the Web Audio
   API (peak > 0.01), because a browser may play a file's video and silently drop an audio track it cannot decode. `probe-loop.sh` restarts the run
   after a browser freeze (a watchdog records the freeze).
4. `PlaybackCheck compare` sets our verdict beside the observation and exits 1 on any disagreement. `run.sh` does 1 to 4 for the Playwright browsers;
   `install-playwright.sh` installs Playwright 1.55.0 (Python package) and its browsers user-locally, and the three distro libraries WebKit needs on a
   minimal Linux host.

## Result

5 profiles x 91 observations (75 files, 16 also through MediaSource) = 455: **ours equals observed in all 455.** In the table below `P`/`p` is
progressive playing/failing and `M`/`m` MediaSource playing/failing (a lone `P` means MediaSource was not tried); the verdict agrees with every cell. (WebKit rows: see the audio limit below.)

| file | chromium | firefox | webkit-linux | chrome | safari |
|---|---|---|---|---|---|
| 3gp-h264-baseline-aac.3gp | p | p | P | P | P |
| ac3-ac3.ac3 | p | p | P | p | P |
| adts-aac.aac | p | P | P | P | P |
| aiff-pcm.aiff | p | p | P | p | P |
| asf-wmv2-wma.wmv | p | p | P | p | p |
| avi-h264-high-mp3.avi | p | p | P | p | p |
| avi-mpeg4-mp3.avi | p | p | P | p | p |
| caf-pcm.caf | p | p | p | p | P |
| flac-flac.flac | P | P | P | P | P |
| flv-h264-high-aac.flv | p | p | P | p | p |
| fmp4-aac.m4a | p m | P M | P M | P M | P M |
| fmp4-av1-aac.mp4 | p m | P M | P M | P M | p m |
| fmp4-h264-high-aac.mp4 | p m | P M | P M | P M | P M |
| fmp4-h264-high-opus.mp4 | p m | P M | P M | P M | P m |
| fmp4-hevc-main-hev1-aac.mp4 | p m | P M | P M | P M | p M |
| fmp4-hevc-main-hvc1-aac.mp4 | p m | P M | P M | P M | P M |
| fmp4-opus.mp4 | P M | P M | P M | P M | P m |
| fmp4-vp9-opus.mp4 | P M | P M | P M | P M | P m |
| m4a-aac.m4a | p | P | P | P | P |
| m4a-alac.m4a | p | p | P | p | P |
| mka-opus.mka | P | p | P | P | p |
| mkv-aac.mka | p | p | P | P | p |
| mkv-av1-opus.mkv | P | p | P | P | p |
| mkv-h264-high-aac.mkv | p | p | P | P | p |
| mkv-h264-high-opus.mkv | p | p | P | P | p |
| mkv-hevc-main-aac.mkv | p | p | P | P | p |
| mkv-vp9-opus.mkv | P | p | P | P | p |
| mov-h264-high-aac.mov | p | P | P | P | P |
| mp3-mp3.mp3 | P | P | P | P | P |
| mp4-ac3.mp4 | p | p | P | p | P |
| mp4-av1-aac.mp4 | p | P | P | P | p |
| mp4-av1-opus.mp4 | P | P | P | P | p |
| mp4-eac3.mp4 | p | p | P | p | P |
| mp4-flac.mp4 | P | P | P | P | P |
| mp4-h264-baseline-aac.mp4 | p | P | P | P | P |
| mp4-h264-high-aac-moovlast.mp4 | p | P | P | P | P |
| mp4-h264-high-aac.mp4 | p | P | P | P | P |
| mp4-h264-high-ac3.mp4 | p | p | P | p | P |
| mp4-h264-high-alac.mp4 | p | p | P | p | P |
| mp4-h264-high-eac3.mp4 | p | p | P | p | P |
| mp4-h264-high-flac.mp4 | p | P | P | P | P |
| mp4-h264-high-mp3.mp4 | p | P | P | P | p |
| mp4-h264-high-opus.mp4 | p | P | P | P | P |
| mp4-h264-high-pcm.mov | p | p | P | P | P |
| mp4-h264-high-videoonly.mp4 | p | P | P | P | P |
| mp4-h264-high-vorbis.mp4 | p | p | P | P | p |
| mp4-h264-high10-aac.mp4 | p | P | P | P | P |
| mp4-h264-high422-aac.mp4 | p | P | P | P | P |
| mp4-h264-high444-aac.mp4 | p | P | P | P | P |
| mp4-h264-level52-aac.mp4 | p | P | P | P | P |
| mp4-h264-main-aac.mp4 | p | P | P | P | P |
| mp4-hevc-main-hev1-aac.mp4 | p | P | P | P | p |
| mp4-hevc-main-hvc1-aac.mp4 | p | P | P | P | P |
| mp4-hevc-main10-hvc1-aac.mp4 | p | P | P | P | P |
| mp4-mp3.mp4 | P | P | P | P | p |
| mp4-mpeg4-aac.mp4 | p | p | P | p | P |
| mp4-opus.mp4 | P | P | P | P | P |
| mp4-vp9-aac.mp4 | p | P | P | P | P |
| mp4-vp9-opus.mp4 | P | P | P | P | P |
| ogg-flac.ogg | P | P | P | P | p |
| ogg-opus.opus | P | P | P | P | P |
| ogg-vorbis.ogg | P | P | P | P | P |
| ts-aac.ts | p | p | P | p | P |
| ts-h264-high-aac.ts | p | p | P | p | P |
| wav-pcm16.wav | P | P | P | P | P |
| wav-pcm24.wav | P | P | P | P | P |
| webm-av1-opus.webm | P M | P M | P M | P M | p m |
| webm-opus.webm | P M | P M | P M | P M | P M |
| webm-vorbis.webm | P M | P M | P M | P M | P M |
| webm-vp8-opus.webm | P M | P M | P M | P M | P M |
| webm-vp8-vorbis.webm | P M | P M | P M | P M | P M |
| webm-vp9-opus.webm | P M | P M | P M | P M | P M |
| webm-vp9-vorbis.webm | P M | P M | P M | P M | P M |
| webm-vp9p2-opus.webm | P M | P M | P M | P M | P M |
| wma-wma.wma | p | p | P | p | p |

## Disagreements found and what they were

The matrix was run until nothing disagreed. Each disagreement was one of these, none left open:

- **Library bugs fixed**: `hev1` files were given an `hvc1.` string (now `hev1.`, and `avc3.` for H.264 with in-stream parameter sets); VP9 in WebM
  was given `vp09.00.00.08`, whose level 00 every browser rejects (now `vp9`, the short form WebM defines, when the level is unknown); MP3 and ALAC in
  MP4 had no string at all (now `mp4a.6B` and `alac`).
- **Test files ffmpeg wrote badly**: Opus plus video in WebM was rejected by Chromium's MediaSource ("a block with a timecode before the previous
  block": the Opus pre-skip puts the first audio block ahead of the first video block). Shifting the audio by the pre-skip in `gen-matrix.sh` fixes the
  file; a header-level check cannot see this class of fault. An AV1 fragmented MP4 written directly by ffmpeg has no sequence header in its `av1C`
  and Firefox refuses it; the matrix now fragments the progressive file instead, and the verdict reports `mediasource.codec-string` for such a file.
  A file remuxed by mkvmerge into WebM was tried and rejected by Chromium's MediaSource, so ffmpeg's output stays.
- **Measurement artefacts**: WebKit's Web Audio tap returns silence for MediaSource and WebM playback even while the audio plays, so for those
  rows only the clock and the video frames are checked (`audioNotChecked` in the results). WebKit on macOS freezes after a few dozen pages in one
  process: the probe restarts the browser every 12 pages and retries a stalled page once.

## Findings worth knowing

- WebKit on Linux (GStreamer) plays PCM from WAV, AIFF, CAF and QuickTime files but not from MP4; found by playing `MakePlayable` output (`make-playable.md`).
- Chromium without proprietary codecs plays *no* H.264, HEVC, AAC, AC-3 or ALAC; what Chrome adds is exactly those plus a wider container set (MOV, 3GP,
  ADTS; Matroska is accepted by both).
- Chrome on macOS plays H.264 High 10, 4:2:2 and 4:4:4 and HEVC (hev1 as well as hvc1) through the system decoders; they are warnings, not guarantees.
- Safari's engine takes `hvc1` but not `hev1` for downloads, takes `hev1` through MediaSource, takes Opus in MP4 only as a download, takes no
  Matroska, no AV1 (hardware only), no MP3 or Vorbis in MP4, and does take MPEG-TS, CAF, AIFF, AC-3, E-AC-3 and ALAC.
- Firefox takes no 3GP, Matroska, MPEG-TS, AC-3 or ALAC, but plays MOV; an `mpeg4` (part 2) video track plays only in Safari and WebKit-GStreamer.
- Every browser that decodes H.264 played the movie-box-last MP4 from a Range server; the warning is about plain downloads that cannot seek.

## Not covered yet

- Windows and Android browsers (Edge, Chrome on Windows and Android, Firefox on Windows/macOS, Safari on iOS) were not run; `ios-safari` and
  `android-chrome` rest on public documentation, and `chrome` was verified on macOS only. The Windows VM and device farms are the way to extend this.
- HLS and DASH playback (hls.js, dash.js, native HLS in WebKit) belongs to packaging.
- Headless Chromium running the WASM smoke app: the smoke app runs under node.

# Changelog

All notable changes are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- HLS and DASH presentations as sources (`PresentationSource`, `PresentationDemuxer`, `IPresentationResolver`, `Media.OpenPresentationAsync`; `Media.Open` also opens a local playlist or manifest): the segments of a chosen variant read as one media file with continuous times, discontinuity flags, several renditions interleaved, protected presentations reported, memory bounded by the largest segment (`MediaLimits.MaxSegmentBytes`, `MaxSegments`). Equal to ffprobe packet for packet on 34 generated presentations except for documented ffprobe behaviours (`docs/streaming-sources.md`).
- Growing files as sources (`GrowingFileSource`, `GrowthPolicy`, `Media.OpenGrowing`): fragmented MP4, live Matroska, MPEG-TS and FLV are read as they are written, in about 50 MiB over 683 MB.
- `onemedia` harness commands `presdiff`, `growing` and `presio`; fuzz target `presentation`.

## [0.6.0-preview.1] - 2026-11-02

Repair and reconstruction, MPEG-TS/M2TS and WAV/BWF/RF64 writing, a fix for QuickTime PCM byte order, and the subtitle speed rows. Everything in 0.4.0-preview.1 stays; subtitles, captions, capture facts, telemetry and timed events are still previews.

**Read this first.**
- **Repair is a preview.** It was checked on generated files, published samples and 10 GoPro clips, not on a corpus of device recordings (`docs/repair.md`, `docs/reconstruction.md`).
- **Reconstruction of truncated MP4/MOV/3GP recordings has limits:** PCM audio, and video without in-stream parameter sets, need a reference file from the same device; 5.1 AAC is dropped; without a reference the AAC sample rate is a guess and the track is reported at Low confidence; AAC through `Media.Repair` needs the audio package's `AacReconstructionProbe.Repair` (the `onemedia` tool does this for you).
- **Index repair limits:** a TS file cut at 90 % keeps one cut frame (seen in two sweep cases); M2TS has no oracle sweep because ffmpeg 7.0.2 crashes on those files; ASF, FLV and Ogg are not repaired.
- **TS/M2TS and WAV writing are in; each has limits** (`docs/muxing-ts.md`: Opus refused, AAC with a program config element refused, decode-order VFW Matroska written as PTS=DTS; `docs/muxing-wav.md`: one track, seekable output only). HLS/DASH packaging and HLS/DASH sources are planned and not in this release.

### Fixed

- Remuxing QuickTime PCM from MOV/MP4 read little-endian 24-bit, 32-bit and float entries (`enda`) as big-endian and wrote corrupt samples; they are now read correctly, and the QuickTime muxer refuses them where it has no little-endian entry. 8-bit signed `sowt`/`twos` entries were read as 16-bit and are now read as 8-bit. **This was a bug in earlier releases:** remux of little-endian 24/32-bit/float or 8-bit PCM from QuickTime wrote wrong samples in 0.3–0.4; re-remux affected files.
- Wording: the privacy-strip review in `README.md`, this changelog and `docs/strip-review.md` was called "independent". It is the project's own review, run by a separate pass within the project, not an external audit; the text now says so.
- A remux of a file with several text tracks that the target holds in another format (for example several SubRip tracks from Matroska into MP4 or MOV, or several `tx3g` tracks from MP4 into Matroska) kept only the last converted track and dropped the others ("has no ISO sample entry code") while the report's Changes said each had been converted. Every such track is now converted, and `RemuxReportHonestyTests` checks the report against the output tracks (count, format per track, lost tracks listed) on the multi-subtitle files and a set of text-track remuxes.
- `docs/telemetry.md` said the privacy strip was not in the release; it now describes what the strip does to GoPro, CAMM, `mebx` and DJI telemetry tracks and links `docs/strip.md`.
- WebVTT top-placed cues were written as `line:0,start`, which Chromium 140 ignores (the cue stayed at the bottom). `start` is the default line alignment, so the writer now writes `line:0`; checked in Chromium 140, Chrome 155, Firefox 141 and WebKit 26 (`tools/playback/vtt-placement.py`: cue at the top).
- `docs/date-writes.md` said a header whose times are both 0 is left unset. The behaviour is: `mvhd` is always written (it is the date Explorer, Photos and AVFoundation show), and only `tkhd`/`mdhd` headers of zeroes are left unset and listed. The page now says so; a test covers it.

### Added

- MPEG-TS and M2TS writing (`MpegTsMuxer`, `RemuxFormat.MpegTs`/`M2ts`, `onemedia remux --format ts|m2ts`, `docs/muxing-ts.md`): one program with PAT/PMT, PES with PTS and DTS, PCR and continuity counters, written strictly forward in constant memory; Annex B and ADTS repackaging of H.264, HEVC, VVC and AAC; captions, teletext, DVB subtitles and timed ID3/SCTE 35 are carried; segment switching for later packaging. Limits: Opus is refused; AAC whose layout is in a program config element is refused; a decode-order Matroska VFW H.264 track is written with PTS equal to DTS (see `docs/muxing-ts.md`).
- WAV, Broadcast Wave and RF64 writing (`WavMuxer`, `WavMuxOptions`, `BextChunk`, `CartChunk`, `docs/muxing-wav.md`): one audio track from any container that carries PCM, A-law or mu-law, as RIFF WAVE, with `bext`, `cart`, iXML, `LIST INFO` and other chunks, extensible format records for more than two channels or wider samples, and an automatic switch to RF64 past 4 GiB without moving a sample byte. `Remuxer.Remux` accepts `RemuxFormat.Wav` and `Rf64` (`onemedia remux in out.wav`, `--format wav|rf64`); chunks of a WAV or RF64 input travel unchanged. `WavMuxer.CreateFile` writes under a temporary name and renames when complete. Checked against ffmpeg 7.0.2 decoded samples and ffprobe packets on 113 generated files, against ExifTool and MediaInfo for the metadata chunks, and with 300 `kill -9` rounds (0 corrupt files).
- `StripReport.NotExamined`: the fixed list of areas no strip run looks at (free text, unknown vendor atoms and boxes, trailers), on every report; `onemedia strip` prints it as a `not examined:` line.
- Preview: repair (`Media.Repair`, `Repairer`, `RepairReport`, `onemedia repair <in> <out> [--reference <file>]`, `docs/reconstruction.md`). `onemedia repair` rebuilds the indexes of damaged MKV/WebM, AVI/OpenDML, MP4/MOV, fMP4 and MPEG-TS into a new file with a report; a progressive MP4 whose recording was cut off loses only the samples that are not there. The input is never modified and payload bytes are copied unchanged.
- Preview: reconstruction of truncated MP4/MOV/3GP recordings (`IsoReconstructor`, `ReconstructionReport`; also reached through `Media.Repair` when the movie box is missing or cut off; AAC tracks are recovered when the file is repaired through `AacReconstructionProbe.Repair` in `RecursiveAI.OneMedia.Audio` or through the CLI). The media data is walked and cut into samples by the codecs' own framing (H.264, HEVC and AV1 video; AAC through the audio package, AMR, MP3, AC-3 and PCM audio; GoPro metadata), and a new file is written with the playable prefix of the recording. The report gives each track's confidence and the input byte ranges its samples came from, and lists what was left out. Limits: PCM audio and video without in-stream parameter sets need a reference file from the same device (`--reference`); AAC with more than two channels (5.1) is dropped because the frame probe does not take it; without a reference the AAC sample rate is a guess (44.1 kHz first), so audio times may be off and the track is reported at Low confidence; the original's irregular frame durations cannot be told from the stream. Checked on ffmpeg-written files, GoPro samples and Blender movies against ffmpeg 7.0.2 and MP4Box 26.07 (method in `docs/reconstruction.md`).

### Changed

- Subtitle parse and convert speed is published against SubtitlesParser 1.5.1 and the Subtitle Edit core library 5.3.0: on 14 rows (20,000 and 100,000 cues) the geometric mean of our median over the fastest comparator is 0.47× on x64 and 0.52× on arm64, the worst row 1.21× on both ([docs/bench-results/0.5-x64.md](docs/bench-results/0.5-x64.md), [0.5-arm64.md](docs/bench-results/0.5-arm64.md)); SRT parsing and 100,000-cue ASS parsing are near Subtitle Edit's speed, not ahead of it.
- README, package pages and the tool and package descriptions say that repair, TS/M2TS writing and WAV writing are in this preview and that HLS/DASH packaging and sources are not.
- `docs/repair.md` lists the known limits of its checks.

## [0.4.0-preview.1] - 2026-11-02

The privacy strip and capture-date writes, subtitles and captions, timed events, AAC and FLAC targets for `MakePlayable`, and fixes. Capture facts and telemetry shipped as unadvertised previews in 0.3.0-preview.2 and are described here as previews.

**Read this first.**
- **Privacy strip** strips the location and device identifiers listed in `docs/strip.md` and reports what it found and could not remove. It does not search free text, unknown vendor atoms or trailers (`docs/strip-review.md`: on a 65-file adversarial set 39 files were fully clean and 26 still held a secret in a form it does not look for).
- **Facts, telemetry and date writes** were checked against ExifTool and MediaInfo on published samples and on files we generate, not on a corpus of device recordings. Windows Explorer and Photos and macOS AVFoundation display checks of written dates were run; the Apple Photos and Google Photos checks are pending.
- **Subtitles, captions, teletext, bitmap subtitles and timed events are previews.** They are not yet at the speed bar (subtitle parse and convert tuning is in progress), and their APIs may change. The CTA-608/708 and SCTE 35 standards were not available (they need an account), so those parts rest on the ATSC A/53 and A/72 documents, the US regulation 47 CFR 79.101 and black-box oracles (ccextractor, ffmpeg, TSDuck).

### Added

- Privacy strip (`PrivacyStrip`, `Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`, `docs/strip.md`): strips the location and device identifiers listed in `docs/strip.md` (tag systems, Exif, XMP, telemetry tracks and known text tracks) into a new file by default, without changing media payload bytes, and reports what it found and could not remove. It does not search free text, unknown vendor atoms or trailers; `docs/strip-review.md` records the project's own review (not an external audit) and the places it does not reach.
- Capture-date writes (`CaptureTimeWriter`, `CaptureTimeOptions`, `CaptureTimeReport`, `Media.SetCaptureTime`, `onemedia tags <file> --capture-time <iso8601>`, `docs/date-writes.md`): set the time a recording was made in every date field a file carries, each in its own notation and zone semantics (MP4/MOV/3GP header times, `©day`, `creationdate`, XMP and Exif dates; Matroska `DateUTC` and tags; RIFF INFO and BWF origination; ID3, Vorbis and ASF dates), with a report of what was written (old and new text), what was left and why, and which dates still disagree (telemetry GPS times are never rewritten). Payload bytes are unchanged. `TagEdits.SetNative` on `xmp` accepts `xmp:`, `photoshop:` and `exif:` properties and on `udta` a `©` atom.
- Preview: subtitle sidecars and the cue model (`SubtitleDocument`, `SubtitleCue`, `SubtitleSpan`, `Subtitles.Read`/`Write`/`Convert`, `SubtitleFormats`, `SubtitleAlignment`, `onemedia subs`, `docs/subtitles.md`): SRT, WebVTT, ASS/SSA, TTML/DFXP/IMSC1 text, SBV and LRC with styling spans, placement, word timing and exact `MediaTime`; content-based format detection, legacy text encodings, typed defects with byte offsets, same-format round trips that keep what the format can say, and a `ConversionReport` of what a conversion dropped or approximated (the fidelity table is in `docs/subtitles.md`). Compared with ffmpeg 7.0.2 for SRT, WebVTT, ASS and LRC; WebVTT loaded as `<track>` in Chromium, Firefox and WebKit.
- Preview: subtitle text tracks in containers (`TextTracks`, `docs/text-tracks.md`): tx3g, QuickTime text, `wvtt`, `stpp` (TTML/IMSC) and Matroska SRT, ASS and WebVTT tracks are read into cues and written back from cues; remux converts a text track only when the target cannot hold its format and lists the losses.
- Preview: embedded captions (`docs/captions.md`): CEA-608 and CEA-708 in H.264 and HEVC SEI, MPEG-2 user data (ATSC `GA94`, and the SCTE 20, DVD and display-order layouts read from sample bytes) and QuickTime `c608` tracks, decoded to cues with mode, row and column; Scenarist SCC read and written (drop-frame included); remux carries captions, and `CaptionSeiFeed` writes captions into H.264/HEVC SEI. Checked against ccextractor 0.96.6 and ffmpeg 7.0.2; the deviations are listed in `docs/captions.md`. CEA-708 window and pen semantics rest on oracle output alone.
- Preview: DVB teletext subtitles in MPEG-TS and program streams (`docs/teletext.md`), decoded to cues; 132 cues equal in text and start against ccextractor 0.96.5 on 51 tracks of real DVB captures. One G0 set only: no mosaics, DRCS or X/27 links.
- Preview: bitmap subtitles (`docs/bitmap-subtitles.md`): PGS (`.sup`, Matroska, M2TS), DVD sub-pictures (`.idx`/`.sub`, Matroska, VOB) and DVB subtitles decoded to RGBA image cues with position and timing, pixel-identical or within one level per channel of ffmpeg's rendering on crafted and published samples. No OCR, scaling or rendering.
- One subtitle list for everything a file carries: `MediaFile.Subtitles` now also lists teletext subtitle pages, bitmap tracks (PGS, VobSub, DVB subtitles) and the CEA-608 channels and CEA-708 services found in video, each `SubtitleTrack` with `Kind`, `Codec`, `Label`, language and cues; `MediaFile.ContainerSubtitles` lists the text and bitmap tracks without reading media data. Finding teletext pages and captions reads the video once on first access (`docs/subtitles.md`); opening a file does not. `Subtitles.Read` reads `.sup` and `.idx`/`.sub` (registered as read-only formats), and `onemedia subs info|extract` lists and extracts every entry of a media file.
- Timed events (`MediaFile.Events`, `MediaEvents.List`, namespace `RecursiveAI.OneMedia.TimedEvents`, `onemedia events`, `docs/events.md`): QuickTime/ISO `tmcd` timecode tracks with the start timecode and an exact presentation-time to SMPTE timecode mapping (drop-frame at 30 and 60 fps), SCTE 35 splice information in MPEG-TS (stream type 0x86) and in `emsg` (`splice_insert`, `time_signal`, `splice_schedule`, segmentation, avail, DTMF and time descriptors, CRC, `pts_adjustment`), and ID3 timed metadata in MPEG-TS (stream type 0x15) and in `emsg`, with other `emsg` messages kept as raw events. Events are read lazily; opening a file reads no more than before. Remux carries `tmcd` tracks MP4 to MOV and back and reports `emsg` boxes as not written. Matroska stores a timecode only as a `TIMECODE` tag, so it is a native tag there, not an event. Equal to ffprobe's `timecode` on 24 files, to TSDuck 3.44 on SCTE 35 (generated and a real capture), to ffprobe packets for timed ID3 in TS and to MP4Box for `emsg`.
- Make a file playable with AAC-LC and FLAC audio as well as Opus (`AudioTranscoder` now makes all three; `BrowserProfile.AudioTargets` says which a profile prefers, AAC first for `safari`, `ios-safari`, `webkit-linux` and `generic-mp4`): files whose audio only AAC could rescue, such as anything for the generic MP4 profile, are made instead of refused. AAC carries the encoder's 1,024-sample lead-in and its end padding in the MP4 edit list, so players that honour edit lists play exactly the source's length. `RecursiveAI.OneMedia.Audio` now uses OneAudio 1.7.0. See `docs/make-playable.md`.
- `onemedia tags <file> --check --absent <fields>` (library: `TagWriter.FindPresent(path, fields)`), described under Privacy.


### Changed

- `RecursiveAI.OneMedia.Audio` uses OneAudio 1.7.0.
- README support table updated for this release, with subtitle, telemetry, facts, strip, date-write and event rows and their gaps.

### Privacy

- The privacy strip (above) is the way to remove location and device identifiers across tag systems, Exif, XMP and telemetry tracks. Its limits are part of its contract: it does not search free text, unknown vendor atoms or trailers, and `StripReport.NotRemoved` lists what it found and left.
- `onemedia tags <file> --check` no longer says "clean" when it only looked at ID3v2 padding. It now says what it covers: "no leftovers in ID3v2 padding; fields still present in the tag are not checked". A value that an old version failed to remove is still an ordinary field of the tag, so `--check --absent comment,composer` exits 1 and names each listed field still present, with the tag system and key that holds it (never the value), in any tag system the file has. This is the follow-up promised in 0.3.0-preview.3 for files that 0.3.0-preview.1 left with a removed field in place.

### Fixed

- Remux with a replaced track: a one-track input (native FLAC, MP3, ADTS) kept writing its own packets next to the replacement's, because those demuxers cannot switch their track off; they are now dropped. Making a native FLAC file playable gave an unreadable MP4.
- ISO muxer: a track with a decoder lead-in (priming) that starts later than the lead-in is long now gets a gap of its start time followed by the media from the priming, so the lead-in is not presented (before, the media started at zero after a shorter gap, which plays the lead-in in decoders that do not drop it).
- `onemedia tags <file> --check`: the hint after a failed check prints the file's own path instead of `<file>`.
- On Windows, `MediaFile.Save()` (and `SaveInPlace()`) no longer fails with `FileInUseException` because of the file's own open handle: the file closes its handle around the write and opens the saved file afterwards, so `Tags`, `Tracks` and the rest read the new file and staged edits are cleared. Another process holding the file still gives `FileInUseException`; other platforms behave as before.
- Found by the 600-second fuzz run for this release: a VobSub `.idx` entry whose 16-digit hexadecimal `filepos` read as a negative number threw `ArgumentOutOfRangeException` from `VobSubReader.Read` instead of being reported as a missing sub-picture unit; a DJI `.srt` read as capture-time evidence (`Sidecars.ReadDjiSrt`) threw on a cue start too large for 64-bit ticks, on one that moved the clock outside the calendar, and on a clock text that is not a calendar date (29 February 2022); such readings are now skipped as not being evidence.
- Tag writing: an ASF header object or header-extension object with a size near 2^63 no longer wraps the bounds check; a RIFF last chunk that lacks its pad byte no longer leaves an odd padding remainder; ID3v2.3 date writes include the `TIME` frame; the ID3v2.3 recording date read from `TYER`, `TDAT` and `TIME` is no longer dropped when `TIME` is present.

## [0.3.0-preview.3] - 2026-11-02

Fixes for 0.3.0-preview.2, privacy first.

### Privacy

- Any save of an MP3 (or other ID3v2-led audio file) now zeroes leftovers in the ID3v2 padding: a frame or other non-zero bytes sitting after the live frames, whatever the edit. `Save`, `SaveAs` and `SaveInPlace` all do it, with edits for other fields and with no edits at all (before, a save with no edits copied the file unchanged, and an edit that left the live frames as they were kept the old padding). A plain save of a file with such leftovers, for example `onemedia tags file.mp3 --replace`, removes them.
- Zeroing leftover bytes in the ID3v2 padding is a safeguard: we know of no sequence of calls in 0.3.0-preview.1 or -preview.2 that leaves removed values in the padding (the case found there was removed trailing frames staying as live frames), and the check and the cleaning were tested on files crafted to have such leftovers.
- New check: `TagWriter.CheckResidue` and `onemedia tags <file> --check` report leftover removed or stale values in ID3v2 padding (non-zero bytes, a frame inside the padding) and a frame right after the tag where audio should start; they name where the bytes are and never print them. `--check` exits 1 when it finds leftovers in the padding and 0 when it finds none; it does not look at fields that are still in the tag (`--absent` came in 0.4.0-preview.1). Other tag systems write their padding as generated zeros on every edit; removing a value and saving was tested to leave no copy of it behind in MP3, AIFF, WAV, FLAC, Opus, Ogg, MP4 and Matroska files (APEv2, ASF and RIFF INFO leftovers were not separately tested). `--check` does not scan systems other than ID3v2.
- A file that 0.3.0-preview.1 left with a removed field still in place holds that field as an ordinary frame of the tag. No version can tell it from a tag you meant to keep, so a save does not remove it. Open the file with this version, remove those fields again and save (`onemedia tags file.mp3 --remove comment --remove composer --replace`), then run `onemedia tags file.mp3 --check`. (From 0.4.0-preview.1, add `--absent comment,composer` to make that check fail while a field is still in the tag.)

### Fixed

- A refused in-place edit says where the room is missing. `onemedia tags file.mp3 --set title=x --in-place` on an MP3 with an ID3v2 tag and an APEv2 tag at the end is refused because the edit goes to both tags and the APE tag has no padding to grow into; the message used to blame "padding" and quote the ID3v2 tag's free bytes ("needs 19 byte(s), the file has 1070 of padding"). It now reads "The edit does not fit in place: the APEv2 tag would grow by 19 byte(s) and has no padding to take it ...", `PaddingRequiredException.ShortfallIn` names the part, and `--system id3v2` (`TagWriteOptions.Systems`) writes the edit in place. The refusal itself is unchanged from 0.3.0-preview.2.
- `onemedia info` no longer offers the file's last-write time as a capture-time candidate unless asked, so its output repeats from run to run. Add `--file-times` for the old behaviour. The library's `MediaFile.UseFileSystemTime` (default true) controls the same candidate.
- The AVI codec-string example in the preview.2 entry below said `avc1.64001E` for a Sintel file. That string is right for the 480p trailer `sintel_trailer-480p.mp4` (High profile, level 3.0); the 1080p film `Sintel.2010.1080p.mkv` is High profile, level 4.0, `avc1.640028`, which is what ffprobe 7.0.2 reports for each of them.
- Public docs lost internal planning markers; `docs/telemetry.md` says that privacy strip is not in this release.

## [0.3.0-preview.2] - 2026-11-02

Fixes for 0.3.0-preview.1, plus early previews of capture facts and telemetry. The previews are checked against ExifTool on published sample files and on files we generate, not yet on a corpus of device recordings; their behaviour may change.

### Privacy

- In 0.3.0-preview.1, removing frames from the end of an ID3v2 tag in an MP3 in place did not remove them: the writer judged the edit unchanged and left the file as it was, so the removed frames stayed in the tag as valid frames, and readers such as ffprobe and ExifTool still showed the value. 0.3.0-preview.2 made an in-place removal of trailing frames take effect, but it did not clean files already affected: saving such a file again kept the frames, and only removing the field again cleaned it. See 0.3.0-preview.3 for the fix and for what to do with a file affected by either version. No other format was affected (checked on MP3, AIFF, WAV, FLAC, Opus, Ogg, MP4 and MKV).

### Added

- Preview: timed telemetry (`MediaFile.Telemetry`, a pull reader in `RecursiveAI.OneMedia.Metadata.Telemetry`, `docs/telemetry.md`): GoPro GPMF, Google CAMM, QuickTime timed metadata (`mebx`), DJI per-frame text and `djmd` tracks, and AVCHD MDPM GPS, decoded to typed per-sample channels (GPS, accelerometer, gyroscope, orientation, camera settings, raw items with byte ranges) without allocating per sample.
- Preview: capture facts as evidence (`IFactResolver`, `DefaultFactResolver`, `FactEvidence`, `docs/facts.md`): capture time, location and device resolved from every candidate with its source, raw value, time-zone certainty and byte range, in a documented precedence; a replaceable resolver; device profiles as data; an Exif reader for Exif blocks inside video; XMP, Sony clip XML and DJI SRT sidecars as evidence (`MediaFile.AddSidecar`, `AddSidecarsBesideFile`); telemetry GPS time and position as candidates; CLI `info` shows the candidates.

### Fixed

- AVI written by stream copy from an MP4 whose `avcC`/`hvcC`/`vvcC` record has an odd length (RIFF pads the `strf` chunk to an even size) is read again: the record ends at `biSize`, so the pad byte no longer defeats the parse. Such files get their codec string (for example `avc1.64001E`, High profile level 3.0, for the 480p trailer `sintel_trailer-480p.mp4`; the 1080p film `Sintel.2010.1080p.mkv` is High profile level 4.0, `avc1.640028`; not none) and remux to MP4 and Matroska with their video: an AVI whose record is present is carried as length-prefixed samples with that record instead of being searched for Annex B parameter sets.
- A remux, and `MakePlayable`, no longer report success when the picture was lost. `RemuxReport.LostMediaTracks` lists the wanted video tracks, and the audio tracks when none of them was written, that did not reach the output for any reason but the caller's own choice; `MakePlayable` returns `Failed` (no output file) with a `remux.dropped-track` blocker instead of `Made`, and `onemedia remux` prints a warning and exits with 3. Deselecting or dropping a track on request is still a choice, not a loss.
- MP4/MOV written from Matroska (or any input whose frames are reordered and that has no edit list of its own) kept an edit list shorter than the presentation by the reorder delay, so players dropped the last one or two frames (sintel 1,251 of 1,253 frames decoded, test5 1,103 of 1,104). The edit's `segment_duration` now runs to the end of the last frame shown (ISO/IEC 14496-12 section 8.6.6).
- Remux loss lines name the input track: the muxer's own message prefix ("Track 2:") counts the tracks it had accepted, not the input's numbering, so losses of input tracks 3 to 7 read "Track 2". They now read "Input track N:", as the change lines do.
- README and `docs/writer.md` no longer say `Save` leaves the whole old or new file after a power loss: only `kill -9` is tested (500 rounds, 0 corrupt targets), power loss is not, and the folder is not `fsync`ed (the BCL cannot), so the rename may not be durable.
- `docs/writer-review.md` gives one `SaveInPlace` tear figure everywhere: 3 of 300 with the two-pass writer, 7 of 300 for the original code; the earlier 1 of 300 came from a broken harness.
- `PaddingRequiredException.Required` for a refused MP3 `SaveInPlace`, an ID3 chunk in WAV/AIFF and an Opus comment header is the real shortfall: it no longer includes the 1,024 bytes of optional growth padding (`ChunkEditor.Replace` has an overload that names the optional tail). MP4, Matroska, FLAC and RIFF chunks already reported the real figure.

## [0.3.0-preview.1] - 2026-11-02

Writing, remux, editing and playability: the ISO and Matroska muxers, remux and faststart, trim/split/concat, tag editing in every system, `Save`/`SaveAs`/`SaveInPlace`, `CanPlay` and `MakePlayable`. `Save` replaces the file (new inode, the saver's owner and group; extended attributes, ACLs and hard links are not kept); `SaveInPlace` works within existing padding only and is not atomic. The ISO muxer and `CanPlay` shipped as previews in 0.2.0-preview.2 and are now covered by the writer and playback checks. Not in this release: MPEG-TS, WAV and ASF writing, subtitles, repair, packaging.

### Added
- Make a file playable (`PlayableMaker`, `Media.MakePlayable`, `AudioTranscoder` in `RecursiveAI.OneMedia.Audio`, CLI `onemedia playable`, `docs/make-playable.md`): moves the movie box first or remuxes into a container a browser profile plays, re-encodes audio to Opus when that is the only other blocker, and reports video blockers with their codes. `RemuxOptions.Replacements` and `IPacketFeed` let a remux merge packets from outside the input.
- Browser playability (`RecursiveAI.OneMedia.Playback`, `docs/playability.md`): `MediaFile.CanPlay(profile)` and `Media.CanPlay(path, profile)` return a verdict per mode (a plain URL, MediaSource) with typed blockers (container, codec, profile, level, bit depth, chroma, `hvc1`/`hev1` form, segmentation, encryption), each marked as fixable by a remux, an audio transcode, or a video blocker; plus warnings for hardware-dependent codecs, a movie box at the end and incomplete codec strings. Built-in profiles as static data: `Chrome`, `Chromium`, `Firefox`, `Safari`, `WebKitLinux`, `IosSafari`, `AndroidChrome`, `GenericMp4`; the first five were checked by playing a 75-file matrix in the real browsers (`tools/playback/`).
- Remux and faststart (`RecursiveAI.OneMedia.Remuxing`, `docs/remux.md`): `Media.Remux(in, out)` and `Remuxer.Remux` copy the tracks of any readable file into MP4, M4A, MOV, 3GP, fragmented MP4, CMAF, Matroska or WebM without touching the compressed data, with Annex B to length-prefixed re-framing (`avcC`/`hvcC` built from the stream), ADTS to raw AAC, track selection, language/name/disposition/rotation edits, and a `RemuxReport` that lists every track, tag system, picture, chapter and sample entry box that did not carry over, with a typed reason. `RemuxSession` is the packet-level layer for callers that filter or retime. `Remuxer.Faststart` and `Media.Faststart` move the movie box of a progressive MP4/MOV/3GP in front of the media data, copying every other byte unchanged. CLI: `onemedia remux`. Path remuxes also carry the input's tags and pictures through the common view.

- Tag writing (`TagEdits`, `TagWriter`, `TagWriteReport`, `docs/tag-writing.md`): common-view and native edits for MP4/MOV/3GP, Matroska/WebM, MP3 (ID3v2/ID3v1/APEv2), FLAC, Ogg Vorbis/Opus/FLAC, WAV/RF64 and AIFF, ASF/WMA/WMV (Content Description, Extended Content Description), XMP packets in `uuid` boxes and iXML elements by path, with a write-back report; setters on `CommonTags`, `MediaFile.Save/SaveAs/SaveInPlace`, `onemedia tags`; `EbmlEditor.ReplaceWithRun`; `Id3Genres.Number`; ReplayGain mapping rows for Vorbis, iTunes and Matroska.
- Trim, split and concat of MP4/MOV/3GP and Matroska/WebM without re-encoding (`RecursiveAI.OneMedia.Editing`, `docs/trim.md`): `Media.Trim` is frame-accurate by default (decoding starts at the keyframe before the start and an edit list hides the frames before it; audio is cut to the sample with pre-roll) or keyframe-only (`TrimMode.Keyframe`); `Media.Split`/`SplitBySize` cut on keyframes into parts that hold every packet once; `Media.Concat` joins files whose tracks match and refuses the rest with `EditRefusalReason.NeedsReencode` naming the track. Streams in constant memory. CLI: `onemedia trim`, `split`, `concat`. In Matroska the lead-in frames get negative timestamps (RFC 9559 §11.2); Chrome and Chromium honour that, WebKit and Firefox show the lead-in, and a frame-accurate start between keyframes is refused for H.264, HEVC and other parser-timed video (`EditRefusalReason.ExactStartNotSupported`) because ffmpeg-based players cannot play it.
- Preview: Matroska and WebM writing (`MatroskaMuxer`, `MatroskaMuxOptions`, `docs/muxing-matroska.md`): `IMuxer` over seekable output (SeekHead, Info with Duration, Tracks, a Void reserve for tags, Clusters cut on key frames, Cues, optional CRC-32) and a live mode for forward-only output (unknown-size Segment and Clusters). Codec setup bytes are repackaged from ISO sample entries (`avcC`/`hvcC` as stored, ASC from `esds`, `OpusHead` from `dOps`, FLAC and VP9 records), a track read from Matroska keeps its CodecID and CodecPrivate, WebM refuses codecs it does not allow. `TrackEditInfo.PacketTimesIncludePriming` is now also set for Matroska tracks with a `CodecDelay`, whose packet times already have it taken off.

### Fixed

- RIFF/RF64: the reader reports (as a warning, `riff.pad-outside-parent`) a nested chunk whose pad byte lies after its parent's stated end, and an untouched RF64 file with a nested all-ones chunk size that runs past the end of the file keeps that size when headers are regenerated. Both came from 600-second `writer-riff` fuzz runs.

- Tag and structure edits hold padding as generated zeros, so an edit runs in constant memory however large the padding is (an ID3v2 tag with 200 MiB of padding no longer allocates 400 MB), and `SaveInPlace` writes only the bytes that differ from what the file holds (`WriteReport.BytesWritten` is what was written), which also narrows the window a crash can tear.
- A tag edit that makes a region grow leaves padding behind it (`TagWriteOptions.PaddingBytes`, 1024 by default; `TagWriteOptions.Minimal` for none; `GrowthPadding` on `BoxEditor`, `EbmlEditor` and `ChunkEditor`): a `free` box in MP4, a Void element in Matroska, a `JUNK` chunk in WAVE/AIFF, zero padding in an Opus comment header. A later in-place edit of a somewhat longer value now fits. Unedited files and edits that fit are unchanged.
- A common `Date` write lists the other date tags the file keeps in `TagWriteReport.Warnings`, and `onemedia tags` prints them.
- `Save` and `SaveAs` name their temporary files `.name.onemedia-<host>-<pid>-<random>.tmp` and remove the debris of a killed save of the same target. `PaddingRequiredException.Required` is the real shortfall for a shrink that leaves a gap too small for a padding header, and a Matroska file with bytes after its Segment is refused with a message that says so.
- H.264, HEVC and VVC in AVI written from an MP4 by stream copy (`avc1`, `avcC` after the bitmap header) get their codec description and RFC 6381 string (`Rfc6381Codec()`, `DescribeCodec()`) from that record, the same string as the source MP4 gives; the in-stream parameter sets are read only when there is no record.
- Frame rates taken from H.264, HEVC and AV1 parameter sets (`CodecParameters.FrameRateNumerator`/`FrameRateDenominator`, so MP4 `avcC` tracks and AVI/TS fallbacks too) are in lowest terms: 10/1, not 20/2.

## [0.2.0-preview.2] - 2026-11-01

Fixes for AAC decoding from MPEG-TS/PS, MPEG-TS service names and several smaller reading issues. Two new layers ship as **previews**, not yet covered by the project's writer and playback gates or advertised: the ISO base media muxer and browser playability (`CanPlay`). Their APIs may change before 0.3.

### Added

- Preview: the neutral mux contract (`IMuxer`, `MuxReport`, `MuxRefusedException` with `MuxRefusalReason`) and the ISO base media muxer `IsoMuxer` (`RecursiveAI.OneMedia.IsoBmff`): MP4, M4A/M4V, MOV, 3GP/3G2 and fragmented MP4/CMAF; moov-first (default), moov-last and fragmented layouts with optional `sidx` and `mfra`; `co64` past 4 GiB with constant memory (a 4.38 GB file in 64 MB); edit lists for priming, padding and start offsets; sample entries from `CodecConfiguration`; typed refusals; zero allocations per packet in steady state. See `docs/muxing.md`.
- Preview: browser playability (`RecursiveAI.OneMedia.Playback`, `docs/playability.md`): `MediaFile.CanPlay(profile)` and `Media.CanPlay(path, profile)` return a verdict per mode (a plain URL, MediaSource) with typed blockers (container, codec, profile, level, bit depth, chroma, `hvc1`/`hev1` form, segmentation, encryption), each marked as fixable by a remux, an audio transcode, or a video blocker; plus warnings for hardware-dependent codecs, a movie box at the end and incomplete codec strings. Built-in profiles as static data: `Chrome`, `Chromium`, `Firefox`, `Safari`, `WebKitLinux`, `IosSafari`, `AndroidChrome`, `GenericMp4`; the first five were checked by playing a 75-file matrix in the real browsers (`tools/playback/`).
- `IsoBmffDemuxer.MediaDataPrecedesMovie` and `IsoBmffAsyncDemuxer.FileType`/`MediaDataPrecedesMovie`.


### Changed

- `MediaTrack.Rfc6381Codec()` names the sample entry for `hev1` (HEVC) and `avc3` (H.264) tracks, returns `vp9` for a VP9 track whose level is unknown (browsers reject `vp09.PP.00.DD`), and `vp9` for one without a configuration.

### Fixed

- `Decode()` of AAC in MPEG-TS and program streams no longer throws "An AAC access unit has fewer channels than the stream configuration": the adapter handed OneAudio the ADTS frames as stored, header included, where the decoder takes raw data blocks (as it already did for raw ADTS files).
- MPEG-TS service names and providers (`TransportStreamProgram.ServiceName`/`ServiceProvider`, `mpegts:service_name`/`service_provider`) appear for streams whose SDT comes before the PAT, as ffmpeg writes them: a service the SDT described earlier is named when its program is found.
- `MediaFile.DescribeDecode()` returns the same report as `Decode(out report)` (it asks the decoder), so an HE-AAC hint such as the doubled container rate of `test5.mkv`'s commentary track reads 22,050 Hz in both. `docs/audio-adapter.md` and the XML docs no longer list unrestricted parametric stereo as refused: OneAudio decodes it in the baseline profile.
- `MpegTsDemuxer.BitRate` right after open is the stream length over the duration read from the tail instead of the rate of the first few packets (72,192 where ffprobe says 738,167); it becomes the program clock figure once the whole stream has been read, and is 0 when neither is known.
- H.264 and HEVC in MPEG-TS and AVI get an RFC 6381 codec string (`MediaTrack.Rfc6381Codec()`) from the SPS in the stream, as the same video in MP4 or FLV does; the new `MediaTrack.StreamParameters` holds what the reader found. AVI reads at most 128 KiB of each of the first few video chunks to find it.
- FLV frame rates are given in lowest terms (15/1, not 30/2).
- An MP3's `TXXX:description` frame is the common view's Description (`docs/tag-mapping.md` regenerated).
- `docs/tags.md` lists the capture-time ranks of every format that yields candidates; `docs/pcm-files.md` and a test source no longer contain NUL bytes.

## [0.2.0-preview.1] - 2026-11-01

Every other reader: ASF/WMV/WMA, MPEG-TS/M2TS, PS/VOB, elementary video and audio streams, MP3 with ID3 and APE, Ogg, FLAC, FLV, AVI/OpenDML, DV, WAV/BWF/RF64/W64, AIFF/AIFC and CAF, with their native tags in the common view. The format readers are read-only. Structure writers and the safe writer come as a first preview (below); tag editing, muxing and remux, subtitles, repair and packaging are planned and not in this release. `RecursiveAI.OneMedia.Audio` moves to OneAudio 1.6.1, which decodes HE-AAC at the full output rate.

Known gaps: DV audio inside DV frames is not de-shuffled, so DV type 1 and raw DV have no audio track; `Decode()` does not play PCM, ADPCM, WMA, AC-3/E-AC-3 or ALAC tracks (it names the codec in an `UnsupportedAudioFormatException`); AVCHD CPI/MPL sidecars are listed, not decoded.

### Added

- ASF/WMV/WMA reading (`RecursiveAI.OneMedia.Asf`, `docs/asf.md`): header objects as a range tree, packets in file order with exact presentation times, keyframe seeks, audio de-scrambling, forward-only and ranged sources, `Protected` for DRM files (`MediaFile.IsProtected`), and native `asf` tags (Content Description, extended attributes, Metadata and Metadata Library, WM/Picture) in the common view. Content Description's description is `Description`. Tags and packets agree with ffprobe, ExifTool, MediaInfo and ATL.NET on the corpus; WMA audio does not decode, MP3 in ASF does through `.Audio`.
- New codec ids for ASF: `Mp2`, `WmaV1`, `WmaV2`, `WmaPro`, `WmaLossless`, `WmaVoice`, `Mpeg4`, `Wmv1`, `Wmv2`, `Wmv3`, `Vc1`.
- MPEG transport streams (188, 192 M2TS/MTS and 204-byte packets, PSI, service names, AVCHD camera data), program streams and VOB (MPEG-1 and MPEG-2, DVD streams), and elementary video: Annex B H.264/HEVC/VVC, MPEG-1/2 video and AV1 OBU, plus IVF. Access-unit, audio-frame and PES packets with exact 90 kHz times, field-picture and repeated-field timing, key frames from the bitstream, damage handling with typed defects, and a fixed-size probe (1.58 MB read from a 2.37 GB transport stream, 0 bytes allocated per packet). Details and every difference from ffprobe 7.0.2 in `docs/mpeg.md`.
- Audio elementary streams: MPEG audio (MP1/MP2/MP3, MPEG-2.5; Xing, Info, LAME and VBRI tags, free-format frames, resynchronisation with typed defects), ADTS AAC, AC-3/E-AC-3 and AMR/AMR-WB files, with `OpenAsync` over ranged sources and the MP3 encoder delay and padding as priming and end trim. `media.Decode()` plays them through OneAudio with ffmpeg's length.
- `Tags.Id3` (ID3v1/1.1, ID3v2.2–2.4 with unsynchronisation, extended header, footer, compressed frames; public `Id3Tag.Read` for other containers), `Tags.Ape` (APEv1/v2) and Lyrics3 as native tags with common-view rows, including ReplayGain and cover art.
- ID3v2 tags embedded in other containers are read as `id3v2` native tags: an ASF `ID3` attribute (the attribute stays as a binary tag) and `ID32` boxes in an MP4 `meta` box.
- Ogg (Vorbis, Opus, FLAC, Theora, Speex; all logical streams, chained files, forward-only and ranged sources), native FLAC, and FLV (`RecursiveAI.OneMedia.Ogg`, `.Flac`, `.Flv`, `Tags.VorbisComment`; `docs/ogg.md`, `docs/flac.md`, `docs/flv.md`, `docs/vorbis-comment.md`): packets, seeks, Vorbis comments and FLAC blocks as native tags in the common view, `onMetaData` as `flv` tags.
- WAVE (WAV, BWF, RF64, Wave64), AIFF/AIFC and CAF reading (`RecursiveAI.OneMedia.Riff`, `.Aiff`, `.Caf`, `Tags.Riff`; `docs/pcm-files.md`): PCM and compressed-in-WAVE packetization, `LIST INFO`, `bext`, `iXML`, `axml`, `cart`, AIFF text chunks and CAF `info` as native tags, cue points as chapters.
- ID3v2 tags in a WAVE or AIFF `id3 `/`ID3 ` chunk and in front of a FLAC or Ogg stream are read as `id3v2` native tags beside the raw block. In FLAC and Ogg the stream's Vorbis comments come before ID3 in the common view; in WAVE and AIFF ID3 comes before `LIST INFO`. A FLAC or Ogg file behind an ID3 tag longer than the sniffed head now opens as its own format on a seekable source.
- `TrackEditInfo.PacketTimesIncludePriming`: set for Ogg Opus, whose packet times already have the pre-skip taken off (RFC 7845 §4.4), so `MediaTrack.PresentationTime` subtracts it once.
- One codec id per codec across WAVE, AVI and ASF: ADPCM (Microsoft, IMA, G.726) now has the same neutral id in every container.
- AVI: `RecursiveAI.OneMedia.Avi` reads AVI and OpenDML (`AVIX`, super and standard indexes, `idx1`, index-less files by a bounded `movi` scan), DV in AVI (type 1 and type 2) and `LIST INFO`/`IDIT` tags; raw DV (`.dv`) through `RecursiveAI.OneMedia.Dv`; key frames from the index or the picture header; the reader registers with `AddOneMedia()` in the audio adapter. DV audio inside frames is not de-shuffled (no audio track for DV type 1 and raw DV). See `docs/avi.md` and `docs/dv.md`. AVI `LIST INFO` and `IDIT` are read by the same `Tags.Riff` reader as WAVE, and AVI shares the WAVE reader's chunk walker, so AVI defect codes are the shared `riff.*` codes (`riff.size-exceeds-file`, `riff.size-exceeds-parent`, `riff.unbounded-size`, `riff.trailing`).

- Structure writers and the safe writer, a preview (`docs/writer.md`); no tag-editing API sits on them yet: `EditPlan`, `SafeWriter` (`Write`, `SaveAs`, `Save` as temp + fsync + atomic rename, `SaveInPlace` within padding or `PaddingRequiredException`), `WriteReport`, and the editors `BoxEditor` (`BoxBuilder`), `EbmlEditor` (`EbmlWriter`) and `ChunkEditor` (`ChunkBuilder`) for ISO boxes, EBML and RIFF/RF64/Wave64/AIFF. Untouched files come back byte for byte; edits change only the edited ranges and keep media payload as copied ranges. Growing a header area moves later bytes and the editors follow: `stco` → `co64` promotion, `tfhd`/`sidx`/`tfra`, Matroska `SeekHead`/`Cues`/cluster positions and CRC-32, RIFF/RF64 sizes and plain WAVE → RF64 at 4 GiB. An outer RIFF size that overshoots the file is corrected once content moves; appends after a chunk or box with no stated end are refused.

- `MediaTrack.PresentationTime(...)`: a packet's time on the presentation timeline with the edit list, Matroska codec delay and start offset applied, in `MediaTime`.
- `MediaTrack.Rfc6381Codec()`: the track's RFC 6381 `codecs` string, now also `vp8` and the ISO timed-text codes `tx3g`, `wvtt` and `stpp`; codecs with no registered string return null.
- `Media.Open(Stream)` accepts a stream that cannot seek (new `MediaFile.Open(IForwardMediaSource)` and `MediaFormatRegistry.Open(IForwardMediaSource)`): formats readable front to back open and read once, a format that needs to seek throws `UnsupportedMediaException`, a second read throws `MediaReadStateException` (`CannotRewind`).
- `MediaReadStateReason.AsynchronousSource`; `AudioSeamLimitations.SbrPossiblyNotApplied`, `AudioDecodeReport.IsPossiblyCoreOnly` and `AudioDecodeReport.AfterReading(decoder)`.
- iTunes `©cpy` atoms in `ilst` feed `CommonTags.Copyright` (after `cprt`); `docs/tag-mapping.md` regenerated.
- `onemedia info`: track names (also in `--json`), every fact candidate with its source, reduced frame rates, clearer HE-AAC wording.

### Fixed

- `SafeWriter.Save` on Windows: the writer's own source handle is closed before the rename, and a target another handle keeps open raises `FileInUseException` with the original untouched instead of an unspecified `UnauthorizedAccessException`.

- Format sniffing: an Annex B head that ended right after a start code made the H.265 check read one byte past the end (found by fuzzing the registry).
- CAF with variable-size packets opened from a stream that cannot seek delivered no packets, because the packet table had gone by before the audio; the table is kept, and a file whose table is not before the audio is refused with `UnsupportedMediaException`.

- `onemedia info` printed Matroska frame rates unreduced (`200000000/8333333 fps`); they are now reduced or snapped to the nearest broadcast rate.
- Raw AV1 streams: an OBU whose size field was close to `int.MaxValue` overflowed the end-of-OBU test and threw `ArgumentOutOfRangeException`; it is now a malformed unit.


### Changed

- Reading tags allocates far less: an ID3 tag no longer clears a 64 KiB window for a few hundred bytes (an MP3 tag read went from about 150 KB to 20 KB allocated), the common tag view no longer allocates a list per mapping row, and an Ogg Vorbis file reads its setup header only when packet durations are asked for.
- `RecursiveAI.OneMedia.Audio` uses OneAudio 1.6.1, which decodes HE-AAC v1/v2 (SBR and baseline PS) at the full output rate. `Decode()` on HE-AAC no longer throws for SBR/PS signalling, and `MediaTrackPacketSource.SampleRate`/`Channels`, priming, `DiscardEndFrames`, lengths and seeking are counted at the output rate and channels. `Decode(out AudioDecodeReport)` reports the decoder's own rate and channels, with the file's declaration in `SignalledSampleRate`/`SignalledChannels` when they differ (a container hint of SBR that the stream does not bear out decodes at the core rate). OneAudio still refuses 64-96 kHz with bs_start_freq other than 9, dual-mono SBR, unrestricted PS and multichannel AAC with its typed unsupported error.
- `AudioSeamLimitations.SbrNotApplied` / `AudioDecodeReport.IsCoreOnly` now mean only SBR that first appears after OneAudio's first 256 access units and was skipped; `AudioDecodeReport.AfterReading` also refreshes rate and channels from the decoder. New `AudioDecodeReport.SbrActive` and `PsActive` report what OneAudio's AAC decoder runs.
- `onemedia info` no longer says "SBR not decoded" for HE-AAC tracks.
- `Packets()` and the `.Audio` decode calls on a file opened with `OpenAsync` throw `MediaReadStateException` (`AsynchronousSource`, still an `InvalidOperationException`) instead of a plain `InvalidOperationException`.

### Deprecated

- `AudioSeamLimitations.PsNotApplied`, `AudioSeamLimitations.SbrPossiblyNotApplied` and `AudioDecodeReport.IsPossiblyCoreOnly`: never set or true with OneAudio 1.6.1.

## [0.1.0-preview.3] - 2026-11-01

Fixes from a review of the second preview.

### Fixed

- **A second read of a `MediaFile` returned nothing without an error.** After `media.Packets(...)`, `Decode()` produced no audio; decoding one track and then another did the same, on MP4 and Matroska. Every read after the first now starts again from the beginning of the file (`Packets`, `PacketsAsync` and `Decode`, in any order). Where that is impossible, it throws instead of returning nothing: `MediaReadStateException` with `Reason` `CannotRewind` when the source cannot seek, and `Superseded` when a reader that is still alive is overtaken by a newer read of the same file. Seeking before the start of an MP4 now lands on the first sample.
- `Decode` throws `UnsupportedAudioFormatException`, as documented, for every AAC track whose configuration signals SBR or PS. OneAudio reported explicit signalling written one bit short (as MP4Box writes it) as an invalid configuration; the adapter now says what the track signals.
- The refusal message and the reports name the signalling correctly: explicit (audio object type 5/29 first) or backward-compatible (extension after the core configuration). Before, a backward-compatible track was described as explicit.
- `SbrImplied` said "SBR" for a plain AAC-LC track whose container declares twice its rate even when the stream has no SBR (a Matroska test file's commentary track). The container's rate is a hint, not evidence; it is now named and documented as one.

### Added

- `MediaReadStateException` and `MediaReadStateReason`.
- `CodecParameters.SbrSignalling` (`SbrSignalling`: `None`, `Explicit`, `BackwardCompatible`, `ContainerRate`), `AudioDecodeReport.Signalling` and `MediaTrackPacketSource.SbrSignalling`.
- `CodecParameters.ContainerDeclaresDoubleRate`, the new name of `SbrImplied`.


### Changed

- `CodecParameters.SbrImplied` is `[Obsolete]` and forwards to `ContainerDeclaresDoubleRate`. `onemedia info` prints `container declares twice the core rate` for it and `SBR, explicit signalling` or `SBR, backward-compatible signalling` for signalled tracks.
- `RecursiveAI.OneMedia.Audio` depends on `RecursiveAI.OneAudio` 1.5.0.
- Audio adapter: Matroska `DiscardPadding` on any block (not only the last) is passed to OneAudio as `PacketView.DiscardEndFrames`, for Opus, Vorbis, AAC, FLAC and AMR, converted at the rate OneAudio decodes (the core rate for HE-AAC). Vorbis end padding is now applied. `AudioSeamLimitations.MidStreamDiscardPadding` and `EndPaddingNotApplied` are marked `[Obsolete]` and never set; nothing is left unapplied except padding that asks to cut the start of a block (`LeadingDiscardPadding`).
- AMR samples that hold several frames (3GP with `damr` frames-per-sample above 1) decode through the adapter, since OneAudio 1.5.0 splits them by their table of contents.

## [0.1.0-preview.2] - 2026-11-01

Fixes from a review of the first preview. No new formats or features.


### Changed

- **Breaking (preview):** `Fact<T>.Value` no longer returns `default` for a fact the file does not state; it throws `InvalidOperationException`. Before, `media.Location.Value` read 0,0 (a real place) for a file with no location, and every other fact had the same trap. Check `HasValue`, or use the new `TryGetValue`. The type of `Value` is now `T` (it was `T?`), and `ToString()` returns the value as text or `none`.
- `onemedia info` prints `none` for a fact the file does not state (`location: none`), and `--json` writes `null` for it; before, the line or property was missing.
- `MediaTrackPacketSource` for an HE-AAC track reports the rate and channel count OneAudio actually decodes (the core coder's), not the output rate the file declares, so frame counts and priming are counted in the frames that are decoded.
- The README, `PACKAGE.md` and package descriptions say that subtitles are planned and not in this preview; the scope line is unchanged.
- `THIRD-PARTY-NOTICES.md` now says where the test media come from (all generated here from synthetic signals) and the README points to it by its real name.

### Added

- `Fact<T>.TryGetValue`.
- HE-AAC is reported instead of passing as plain AAC. `CodecParameters.SbrPresent`, `PsPresent` and `SbrImplied`, and `MediaTrack.DescribeCodec()`, give the signalled output rate (`SampleRate`), the core rate (`CoreSampleRate`) and whether SBR or parametric stereo is signalled, explicitly or by the backward-compatible extension. `SbrImplied` marks a plain AAC-LC configuration in a container that declares twice its rate (SBR signalled only inside the stream). `onemedia info` shows it on the track line.
- `RecursiveAI.OneMedia.Audio`: `AudioDecodeReport`, `MediaFile.DescribeDecode()` and `MediaFile.Decode(out AudioDecodeReport, ...)` say which rate and channel count the PCM really has, which rate and channels the file signals, and `AudioSeamLimitations.SbrNotApplied` / `PsNotApplied`. OneAudio 1.4.0 has no SBR or PS decoder: a track whose configuration signals SBR or PS is refused with a typed OneAudio exception, and one that signals it only in the stream decodes as the core coder (for example 22.05 kHz where the file says 44.1 kHz). `docs/audio-adapter.md` describes both.
- `MediaAudioExtensions.SbrDataIgnored(decoder)` surfaces OneAudio's `AacDecoder.SbrDataIgnored`: SBR data found inside a plain-LC stream, known only after reading.
- Test files for HE-AAC signalling in `testdata/heaac`, made by `tools/gen-heaac-testdata.sh`.

### Fixed

- An AAC AudioSpecificConfig with explicit SBR signalling that ends one bit before the end of its GASpecificConfig (as MP4Box writes it) is read instead of rejected.

## [0.1.0-preview.1] - 2026-11-01

First preview: reading MP4/MOV/3GP (including fragmented MP4) and Matroska/WebM, with native tags, a common tag view and basic capture facts. No writing or tag editing yet.

### Added

- `MediaFile.Tags`, `CaptureTime`, `Location`, `Device`, `Rotation`, `Native[...]` and `ReadMetadataAsync`, and tags and facts in `onemedia info` (text and `--json`).
- `MediaFile.ContainerDuration`, `TracksDuration` and `ExactDuration`; `Duration` is the container's figure unless a track without an edit list is longer.
- Matroska: zlib-compressed frames are inflated on read; `OutputSamplingFrequency` is the audio rate; `V_MS/VFW/FOURCC` tracks are named by their FourCC.
- ISO: QuickTime packetised audio (IMA 4:1 and similar) is joined per chunk; the QuickTime sound layout is read from the data; AMR is mono at its codec rate; QuickTime `text` entries are timed text; `tapt`, `gmhd`, `hinf` and `hnti` are containers; a video size of 0 is taken from the sequence parameter set.

- Repository skeleton: core package `RecursiveAI.OneMedia`, audio adapter `RecursiveAI.OneMedia.Audio`, the `onemedia` tool, tests, NativeAOT and browser-WebAssembly smoke apps.
- Byte sources: memory, file, seekable stream, caller-supplied ranged reads and forward-only streams, plus an observer hook for counting reads.
- `MediaTime` (exact rational time with explicit rounding), `MediaLimits`, typed defects and exceptions.
- Content-based format sniffing and an explicit format registry; sniffers for ISO base media files, EBML (Matroska/WebM) and the other container and stream signatures.
- The track and packet model, demuxer interfaces with an allocation-free pull path, and the structure-node contract.
- `Media.Open` / `MediaFile` happy path, the `onemedia info` command, and the audio adapter: tracks as OneAudio packet sources, OneAudio container registrations for MP4 and Matroska, and `Decode()`.

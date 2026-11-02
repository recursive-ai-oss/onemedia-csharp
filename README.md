# OneMedia

**Containers, metadata, subtitles; no decoding.** A managed .NET library for media containers, with no dependency beyond the .NET base class library. This preview reads and writes containers, edits metadata, and has first previews of subtitles and captions, capture facts, telemetry, timed events and a privacy strip. OneMedia is unaffiliated with other products or companies named OneMedia.

The goal is the depth of libavformat, GPAC, mkvtoolnix, MediaInfo and ExifTool in one library you can call from C#: demux and mux, lossless tag editing, evidence-backed capture time and place, subtitles and captions, repair, and HLS/DASH packaging. It never decodes or encodes an audio or video sample; audio decoding is handed to [OneAudio](https://github.com/recursive-ai-oss/oneaudio-csharp) through the `RecursiveAI.OneMedia.Audio` adapter.

## Status

**Preview, version `0.4.0-preview.1`: reading every container below (MP4/MOV/3GP, Matroska/WebM, MPEG-TS/PS and elementary streams, ASF, AVI, DV, Ogg, FLAC, FLV, MP3/ADTS/AC-3/AMR, WAV/AIFF/CAF), writing MP4/MOV/3GP/fragmented MP4/CMAF and Matroska/WebM, remux, faststart, trim, split and concat without re-encoding, tag editing, browser playability (`CanPlay`, `MakePlayable`, now with AAC and FLAC audio targets), a privacy strip for location and device identifiers, and capture-date writes.** Also in this release, as **previews** (below): capture facts as evidence, telemetry, timed events, and subtitles and captions (sidecars, container text tracks, CEA-608/708, SCC, teletext, bitmap subtitles). Repair, MPEG-TS and WAV writing and HLS/DASH packaging are planned and not in this release. The tables below list what works and how it was checked; gaps are listed, not hidden.

```csharp
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Audio;   // package RecursiveAI.OneMedia.Audio

using var media = Media.Open("clip.mov");
Console.WriteLine($"{media.Duration} {media.Video?.Codec} {media.Audio?.Codec}");
Console.WriteLine($"{media.Tags.Title} {media.CaptureTime}");     // a fact prints its best value, or "none"
if (media.Location.TryGetValue(out var place))                      // absent facts have no value: no 0,0 stand-in
    Console.WriteLine($"{place.Latitude}, {place.Longitude}");
foreach (var candidate in media.CaptureTime.Candidates) Console.WriteLine(candidate);

using var pcm = media.Decode();   // the audio track, decoded by OneAudio
```

`onemedia info <file>` (or `--json`) prints tracks, codecs (RFC 6381 strings), tags and facts with their candidate sources.

### Support (0.4)

| Format | Read | Write | Tag edit | Packets vs ffprobe 7.0.2 | Probe + metadata I/O (local reads), ours vs ffprobe / MediaInfo / ExifTool | Documented deviations |
|---|---|---|---|---|---|---|
| MP4, MOV, 3GP/3G2, M4A/M4V, fragmented MP4 | ✓ demux, codec headers, native tags (`udta`, `ilst`, `mdta`, 3GPP, XMP, Nero chapters), common tags, basic facts | ✓ mux (moov-first/last, fragmented, CMAF), remux, trim/split/concat, faststart | ✓ common tags, `udta`, `ilst`, `mdta` keys, XMP `uuid` | 121 corpus files, 744,306 packets, 0 unexplained | 4.38 GB file with the index at the end: 165,681 B in 34 reads vs 1,741,689 B / 32 (ffprobe), 23,628,223 B / 138 (MediaInfo), 1,103,868 B / 139 (ExifTool) | [docs/isobmff.md](docs/isobmff.md): edit-list repeats and drops, last-packet durations, negative composition offsets, fragmented durations are structural (no fragment scan) |
| Matroska, WebM (MKV, MKA, MK3D) | ✓ demux (seekable and live/forward-only), codec headers, tags with targets, attachments, basic facts | ✓ mux (seekable and live), remux, trim/split/concat | ✓ tags with targets | 92 corpus files, 464,934 packets, 0 unexplained | 4.45 GB file: 69,806 B in 20 reads vs 4,877,532 B / 16 (ffprobe), 72,168,087 B / 493 (MediaInfo), 79,600 B / 11 (ExifTool) | [docs/matroska-differential.md](docs/matroska-differential.md): attachments are not streams, RealAudio is not de-interleaved, RealVideo timestamps as stored |
| MPEG-TS, M2TS/MTS (AVCHD), PS/VOB, elementary video (Annex B, MPEG-1/2, AV1 OBU), IVF | ✓ demux (seekable and forward-only), PSI/SDT/EIT names, AVCHD MDPM, codec headers | — | — | 149 corpus files, 54,785 packets, 0 unexplained (SDT-stripped copies for the pinned ffprobe) | 2.37 GB transport stream: 1,576,976 B in 4 reads vs 5,460,112 B / 170 (ffprobe), 17,402,896 B / 266 (MediaInfo), 509,424 B / 64 (ExifTool) | [docs/mpeg.md](docs/mpeg.md): leading/trailing fragments, damaged-packet policy, DTS/Opus-in-TS not framed, AVCHD CPI/MPL sidecars listed not decoded |
| ASF, WMV, WMA | ✓ demux (seekable, forward-only), native `asf` tags in the common view, cover art, DRM files reported `Protected` (metadata only) | — | ✓ Content Description, Extended Content Description (Metadata/Library objects not edited) | 55 corpus files, 82,454 packets, 0 unexplained (50 equal; 5 DRM files not demuxed, as designed) | 2.33 GB WMV: 36,950 B in 3 reads vs 5,079,040 B / 155 (ffprobe), 3,204,929 B / 51 (MediaInfo), 41,324 B / 8 (ExifTool). Media Foundation and Shell check on Windows 11 (18 ASF files): streams 62/67 agree (all 26 files, MP4 included), durations 24/25 exact, protection 18/18, samples 20/26 ASF streams equal with each difference explained ([docs/asf.md](docs/asf.md)) | [docs/asf.md](docs/asf.md): written clean-room from public descriptions and observed files; WMA audio does not decode |
| MP3 (ID3v1/v2, APE, Lyrics3), ADTS AAC, AC-3/E-AC-3, AMR/AMR-WB files | ✓ demux, Xing/Info/LAME tags, priming and padding, native tags in the common view | — | ✓ ID3v2, ID3v1, APEv2 | 134 corpus files, 10,544,449 packets, 0 unexplained (2 free-format MP3 that ffprobe cannot open) | 2.2 GB MP3: 151,783 B in 12 reads vs 163,968 B / 5 (ffprobe), 147,616 B / 4 (MediaInfo), 96,159 B / 16 (ExifTool) | [docs/audio-es.md](docs/audio-es.md): duration of streams without a frame count is an estimate; AC-3/E-AC-3 do not decode through `.Audio` |
| Ogg (Vorbis, Opus, FLAC, Theora, Speex), native FLAC | ✓ demux of all logical streams, chained files, Vorbis comments and FLAC blocks as tags, pictures | — | ✓ Vorbis comments, FLAC blocks (picture dimensions written as 0) | 72 corpus files, 246,857 packets, 0 unexplained | 2.2 GB FLAC: 69,666 B in 3 reads vs 229,376 B / 7 (ffprobe), 65,696 B / 2 (MediaInfo), 38,783 B / 8 (ExifTool) | [docs/ogg.md](docs/ogg.md), [docs/flac.md](docs/flac.md), [docs/vorbis-comment.md](docs/vorbis-comment.md) |
| FLV | ✓ demux (Enhanced FLV codecs), `onMetaData` tags | — | — | 30 corpus files, 217,868 packets, 0 unexplained | 3.63 GB file: 69,632 B in 2 reads vs 2,228,087 B / 47 (ffprobe), 5,830,707 B / 115 (MediaInfo), 30,448 B / 5 (ExifTool) | [docs/flv.md](docs/flv.md) |
| AVI (OpenDML), DV | ✓ demux of AVI, OpenDML, DV in AVI and raw DV; `LIST INFO` and `IDIT` tags | — | — (`LIST INFO` editing is not implemented) | AVI 70 files, 164,648 packets, 0 unexplained; DV 14 files, 5,073 packets (ffprobe lists the audio inside DV frames as extra streams) | 4.65 GB OpenDML file: 4,821 B in 40 reads vs 1,308,840 B / 29 (ffprobe), 11,416,591 B / 176 (MediaInfo), 71,408 B / 11 (ExifTool) | [docs/avi.md](docs/avi.md), [docs/dv.md](docs/dv.md): DV audio is not de-shuffled, so DV type 1 and raw DV have no audio track |
| WAV/BWF/RF64/W64, AIFF/AIFC, CAF | ✓ demux (PCM and compressed), `LIST INFO`, `bext`, iXML, ID3 chunks, cue points | — | ✓ WAV/RF64 `LIST INFO`, `bext`, iXML, ID3 chunk; AIFF; not W64, CAF | 225 corpus files, 2,204,392 packets, 0 unexplained (222 equal; 3 NIST SPHERE files named `.wav` are not RIFF and not read) | 4.4 GB RF64 file: 4,216 B in 9 reads vs 884,816 B / 27 (ffprobe), 1,048,576 B / 16 (MediaInfo), 22,256 B / 5 (ExifTool) | [docs/pcm-files.md](docs/pcm-files.md): one AIFF-C layout (`digit0v0.aiff`) is misread as empty; `.Audio` does not decode PCM or ADPCM |

Bytes and request counts for every corpus file of every family, over HTTP range requests and local reads, are in [docs/io-results.txt](docs/io-results.txt) (method in [docs/harness.md](docs/harness.md)). Audio tracks of MP4/3GP equal OneAudio's own MP4 reader packet for packet (250,469 packets over 64 files). Tags and facts agree with ExifTool 13.60 and MediaInfo 26.10 on 116 files, with every difference explained in [docs/tags.md](docs/tags.md); the field mapping is [docs/tag-mapping.md](docs/tag-mapping.md). The audio adapter is described in [docs/audio-adapter.md](docs/audio-adapter.md), the comparison method in [docs/harness.md](docs/harness.md), and the benchmarks in [docs/bench.md](docs/bench.md).

### Writing, remux and editing (0.3)

Every operation copies compressed data unchanged and streams in constant memory; each returns a report of what it dropped, changed or refused. Method and results are in the linked pages.

| Operation | What it does | How it was checked |
|---|---|---|
| Mux (`IsoMuxer`, `MatroskaMuxer`; [docs/muxing.md](docs/muxing.md), [docs/muxing-matroska.md](docs/muxing-matroska.md)) | MP4/M4A/MOV/3GP/fragmented MP4/CMAF (moov-first, moov-last, fragmented, `sidx`, `mfra`, `co64`); Matroska and WebM (Cues, SeekHead, optional CRC-32, live mode). Typed refusals for codecs a target cannot hold | Output packets read back through ffprobe 7.0.2 (size, hash, times, flags), MP4Box `-info`, mkvalidator 0.6.0; a 4.38 GB MP4 written in 64 MB |
| Remux (`Media.Remux`; [docs/remux.md](docs/remux.md)) | Any readable file into MP4, M4A, MOV, 3GP, fMP4, CMAF, Matroska or WebM; Annex B to `avcC`/`hvcC`, ADTS to raw AAC; track selection and language/disposition/rotation edits; carries tags and pictures; a `RemuxReport` lists everything that did not carry over | MP4: 698 files, 962,615 packets, 0 differences (284 files a target cannot hold are refused with a reason); Matroska: 647 files, 1,301,013 packets, 419 differences all in one AVCHD file whose declared field rate disagrees with its picture spacing; WebM: 47 files, 0 differences |
| Faststart (`Media.Faststart`) | Moves the movie box in front of the media data of a progressive MP4/MOV/3GP, every other byte unchanged | moov-first output, packets equal to the input's, plays in Chromium |
| Trim, split, concat (`Media.Trim`, `Split`, `Concat`; [docs/trim.md](docs/trim.md)) | Frame-accurate (edit list hides lead-in frames) or keyframe-only; split on keyframes; join of matching tracks, refused with `NeedsReencode` otherwise | ISO: 288 of 288 trims equal in decoded frame MD5, first frame at 0 and audio to the sample (+15 on other layouts); Matroska: 172 trims equal, 116 frame-accurate H.264/HEVC cuts refused by design (ffmpeg-based players cannot play the lead-in); 5.06 GB trim at 50.6 MB peak (ISO) and 78.5 MB (Matroska) |
| `CanPlay(profile)` ([docs/playability.md](docs/playability.md)) | Verdicts per mode with typed blockers for Chrome, Chromium, Firefox, Safari, WebKit on Linux, iOS Safari, Android Chrome and generic MP4, as static data | 5 profiles x 91 observations (75 files, 16 also through MediaSource) played in Chromium 140, Firefox 141 and WebKit 26: ours equals observed in all 455. Safari, iOS and Android rows rest on published codec guides, not on those devices |
| `MakePlayable` ([docs/make-playable.md](docs/make-playable.md)) | Faststart or remux into a container the profile plays; audio re-encoded (through `.Audio` and OneAudio 1.7.0) to Opus, AAC-LC or FLAC, whichever the profile prefers, when that is the only other blocker; video blockers reported with codes | Matrix 75 files and a 48-file corpus sample in three browsers: every written output plays except one WebKit input that plays no better un-copied; untouched streams 91, 0 unexplained; a 1.13 GB MKV to WebM and Opus at 92 MB peak. With AAC and FLAC targets the generic MP4, Safari and iOS Safari profiles make files they used to refuse: for the generic MP4 profile the matrix goes from 11 made and 55 refused to 30 made and 36 refused, all 39 outputs play in Firefox 141, and 14 of 14 generated audio files are made and play; the Safari and iOS Safari profiles get AAC where they got Opus, played in WebKit 26 ([docs/make-playable.md](docs/make-playable.md)). Real Safari and Chrome on devices were not run; channel counts above two are not covered. WMA, AC-3, E-AC-3 and ALAC have no decoder and are refused |
| Tag editing (`CommonTags` setters, `TagEdits`, `onemedia tags`; [docs/tag-writing.md](docs/tag-writing.md)) | Common-view and native edits in the systems in the table, with a write-back report | 445 untouched round trips byte-identical; payload hash unchanged across sweeps of 8 families; reads back in ExifTool and ffprobe. Review and its findings: [docs/writer-review.md](docs/writer-review.md) |

### Metadata beyond tags (0.4)

| Feature | What it does | How it was checked, and what was not |
|---|---|---|
| Capture facts as evidence (`media.CaptureTime`, `Location`, `Device`; `IFactResolver`; [docs/facts.md](docs/facts.md)) | Every candidate keeps its source, raw value, time-zone certainty and byte range; a documented precedence picks the best; the resolver is replaceable; device profiles are data; embedded Exif; XMP, Sony clip XML and DJI SRT sidecars count as evidence | **Checked against ExifTool 13.60 and MediaInfo 26.10 on published samples and on files we generate, not on a corpus of device recordings.** Device-profile rows for phones and cameras rest on ExifTool's tag documentation and the few vendor samples available; treat the zone rules for devices not listed there as unverified |
| Telemetry (`MediaFile.Telemetry`; GoPro GPMF, Google CAMM, QuickTime `mebx`, DJI text and `djmd`, AVCHD MDPM GPS; [docs/telemetry.md](docs/telemetry.md)) | Typed per-sample channels (GPS, accelerometer, gyroscope, orientation, camera settings, raw items with byte ranges), pulled without allocating per sample | 10 published GoPro clips (HERO5 to HERO8, Fusion, MAX, Karma) against ExifTool (equal counts, values within 1e-13) and against GoPro's own `gpmfdemo` release binary (every key equal after the axis remap); CAMM and `mebx` fixtures against ExifTool and ffprobe. Not compared value by value: HUES, FACE, SCEN, MFGI, VPTS. CAMM `time_gps_epoch` is read as the GPS time scale, ExifTool reads Unix time, and no real CAMM device file was available to settle it. No real DJI telemetry file was available |
| Privacy strip (`Media.StripLocation`, `Media.StripDeviceIds`, `onemedia strip`; [docs/strip.md](docs/strip.md)) | **Strips the location and device identifiers listed in [docs/strip.md](docs/strip.md) and reports what it found and could not remove. It does not search free text, unknown vendor atoms or trailers.** New file by default; ISO files keep their layout; media payload bytes unchanged | Independent review ([docs/strip-review.md](docs/strip-review.md)): 10 GoPro files stripped to 0 position or serial bytes (two coincidental 4-byte hits in accelerometer data) and 20 of 20 audio/video payload hashes identical; on a 65-file adversarial set 39 files were fully clean and 26 still held a secret in a form the strip does not look for (free text, unlisted names, unread placements, two cases it now reports); 0 outputs worse than their input in ffprobe 7.0.2 and MP4Box 26.07. Do not rely on it as proof that a file is clean |
| Capture-date writes (`Media.SetCaptureTime`, `onemedia tags --capture-time`; [docs/date-writes.md](docs/date-writes.md)) | Sets the recording time in every date field a file carries, each in its own notation and zone, with a report of what was written, what was left and which dates still disagree | Read back in ExifTool 13.60, MediaInfo 26.10 and ffprobe 7.0.2. **Windows Explorer and Photos (Windows 11) and macOS AVFoundation were checked:** they show the `mvhd` date, not `©day` or Apple's `creationdate`. **Apple Photos and Google Photos display checks are pending** (they need a GUI session and an account) |
| Timed events (`MediaFile.Events`, `onemedia events`; [docs/events.md](docs/events.md)) | `tmcd` timecode with exact SMPTE mapping, SCTE 35 splice information in MPEG-TS and `emsg`, timed ID3 in MPEG-TS and `emsg` | **Preview.** `tmcd` equals ffprobe's `timecode` tag on 24 ffmpeg-written files and their remuxes; SCTE 35 equals TSDuck 3.44 on a generated stream (11 sections) and a real capture (18 sections); timed ID3 in TS equals ffprobe packets on 6 of 6; `emsg` equals MP4Box on 4 of 4. The SCTE 35 standard needs an account and was not available, so the section layout rests on public descriptions and TSDuck's output; ffprobe 7.0.2 shows the SCTE 35 stream with no packets, so it cannot be a packet oracle |

### Subtitles and captions (0.4, preview)

**Preview.** One cue model (`SubtitleDocument`, exact `MediaTime`, styling spans, placement, word timing, bitmap images) with conversions that return a loss report, `Subtitles.Read`/`Write`/`Convert`, `MediaFile.Subtitles` (one list across container tracks, teletext pages, CEA-608 channels and CEA-708 services), and `onemedia subs`. Reading the container tracks costs no media reads; finding teletext and captions reads the video once on first access ([docs/subtitles.md](docs/subtitles.md)). **Speed:** subtitle parse and convert at 20,000 and 100,000 cues, against SubtitlesParser and Subtitle Edit's library, come out at a geometric mean of 0.47× (x64) and 0.52× (arm64) of the fastest comparator, worst row 1.21×; SRT and large ASS parsing are close to Subtitle Edit's time, not ahead ([x64](docs/bench-results/0.5-x64.md), [arm64](docs/bench-results/0.5-arm64.md)). **The CTA-608/708 and SCTE 35 standards were not available** (they need an account): caption parts rest on the ATSC A/53 and A/72 documents, the US regulation 47 CFR 79.101 and black-box ccextractor and ffmpeg output, and the open points are listed in [docs/captions.md](docs/captions.md). The API may change before 1.0.

| Format | Read | Write | Checked against, and agreement | Documented deviations and gaps |
|---|---|---|---|---|
| SRT, WebVTT, ASS/SSA, LRC sidecars | ✓ | ✓ with a loss report ([fidelity table](docs/subtitles.md)) | ffmpeg 7.0.2: its output read by our reader and ours converted by it, compared as cues; WebVTT also loaded as `<track>` in Chromium 140, Firefox 141 and WebKit 26 and the cues read back | ASS/LRC are rounded to 10 ms; LRC has no end time, and enhanced word tags are word timing for us and text for ffmpeg; ffmpeg shows ruby inline |
| TTML/DFXP/IMSC1 text, SBV | ✓ | ✓ | no oracle reads them: round trips only | written as the IMSC1 text profile; no image profile |
| Container text tracks: tx3g, QuickTime text, `wvtt`, `stpp`, Matroska SRT/ASS/WebVTT | ✓ (without reading media data to list them) | ✓ in MP4/MOV and Matroska, with losses reported on remux | files written by ffmpeg 7.0.2, MP4Box 26.07 and mkvmerge 103.0 read back; WebKit 26 lists and loads `tx3g`, `wvtt` and Matroska SRT tracks, Chromium and Firefox expose no in-band text tracks | ISO/IEC 14496-30 is not available: the order of `vttc` children, `vsid`, `ctim` for split cues and sample-relative times in `stpp` are open ([docs/text-tracks.md](docs/text-tracks.md)); ffmpeg 7.0.2 cannot mux `wvtt` in MP4 |
| CEA-608/708 in H.264, HEVC and MPEG-2 video, `c608` tracks; SCC sidecar | ✓ | SCC ✓; captions written back into H.264/HEVC SEI on remux (`CaptionSeiFeed`); passed through on remux | ccextractor 0.96.6 release binary (banner 0.96.5) and ffmpeg 7.0.2, on six published PixelTools MPEG samples and generated files; the special-character table checked against both | oracles date an SCC pop-on caption at its line's timecode, we at the end-of-caption pair; roll-up windows are emitted per scroll by us and once full by ccextractor; ccextractor does not read HEVC SEI captions; **708 window and pen semantics rest on oracle output alone**; the SCTE 20 layout is read from sample bytes; the SCTE 20 and SCTE 128 documents were not used; TS caption muxing and HLS WebVTT segments are not in this release |
| DVB teletext subtitles (MPEG-TS, PS) | ✓ | — | 8 real DVB captures, 75 (file, PID, page) tracks: against ccextractor 0.96.5 on 51 tracks, 132 cues equal in text and start (ours 148, theirs 146), starts within 1 ms, ends within one frame; ffmpeg 8.0.1 on one Polish multiplex: 15 cues against our 23, 10 matched by text | ccextractor writes junk for mosaic cells and garbles Polish and Cyrillic pages; one G0 set only, no mosaics, DRCS or X/27 links; ffmpeg 7.0.2 crashes on teletext in TS, so ffmpeg 8.0.1 stood in; PS carriage exercised with generated streams only |
| Bitmap subtitles: PGS (`.sup`, Matroska, M2TS), VobSub/DVD (`.idx`/`.sub`, Matroska, VOB), DVB subtitles in TS | ✓ decoded to RGBA with position and timing | — | crafted streams re-wrapped by ffmpeg and mkvmerge: DVD pictures 3 of 3 pixel-identical, PGS and DVB 3 of 3 within 1 level per channel; published samples: PGS 1 of 1 and VobSub 3 of 3 pixel-identical, a 685-event idx/sub with all times, alpha and geometry equal | no CLUT in `SubTitleHD.ts`: we use the standard default CLUT and ffmpeg renders those pixels differently (not settled); DVB CLUT entry 0 is transparent for us and black for ffmpeg; PGS cropping is inferred; an idx without a palette line uses a grey ramp; no OCR |

**Timed events (preview).** See the metadata table above: `tmcd`, SCTE 35 and timed ID3 are read, and `tmcd` tracks are carried by remux between MP4 and MOV; `emsg` boxes are reported as not written.

**Saving.** `Save()` writes a temporary file and renames it over the original: 500 `kill -9` rounds left no half-written target. Power loss has not been tested. It makes a new file: new inode, the saver's owner and group, and the original's extended attributes, ACLs and hard links are not kept. `SaveAs` writes a new file. `SaveInPlace` works only inside existing padding (`free`, `skip`, `Void`, ID3 or FLAC padding) and otherwise refuses with `PaddingRequired` and the file untouched; **it is not atomic** (300 kill rounds: 3 torn with the current two-pass writer, 7 with the first version).

**Removed tag values and leftovers.** On 0.3.0-preview.1, removing the last frames of an ID3v2 tag in place removed nothing: the frames stayed in the file as valid frames that readers still showed. 0.3.0-preview.2 fixed the removal, but saving an already affected file again did not remove the frames. From 0.3.0-preview.3, any save of an MP3 (`Save`, `SaveAs`, `SaveInPlace`, with edits or none) also zeroes leftover bytes and frames in the ID3v2 padding, a safeguard: we know of no sequence of calls in preview.1 or preview.2 that leaves removed values there. `onemedia tags <file> --check` (`TagWriter.CheckResidue`) reports such leftovers without printing them; it does not look at fields still in the tag. A field that preview.1 failed to remove is still an ordinary frame of the tag, which no version can tell from one you want to keep: open the file with this version, remove those fields again, save, and run `onemedia tags <file> --check --absent comment,composer`, which exits 1 and names each listed field still present in any tag system.

**Known limits.** Writing MPEG-TS, WAV/BWF/RF64 and ASF is not implemented; the MP4 muxer writes one sample description per track, so joining files with different codec setups is refused; the Matroska muxer does not carry AAC priming, rotation, interlace or stereo mode; the Matroska trim drops tags, chapters and attachments; a common `Date` write keeps other date tags in the file and says so in `TagWriteReport.Warnings` (`Media.SetCaptureTime` writes every date field); editing ASF Metadata/Library objects, XMP outside ISO files, W64 and AVI is not implemented.

**Writer limits that were fixed in this release** ([docs/writer-review.md](docs/writer-review.md) section 6, [docs/writer.md](docs/writer.md)). Padding is generated as zero runs and an in-place save writes only the bytes that changed: a 3-byte ID3 title edit in a tag with 200 MiB of padding allocates 138 KB and writes 3 bytes (before: 419.5 MB and 209.7 MB), and `SaveAs` tag edits of files over 4 GB peak at 46 to 53 MiB. A growing edit leaves 1,024 bytes of padding so the next edit can be saved in place (`TagWriteOptions.Minimal` turns that off). Temporary files carry a recognisable name and stale ones are removed on the next save. `SaveInPlace` kill tests: 3 torn files in 300 rounds, which is why it stays documented as not atomic. What is tested for `Save` is `kill -9` (500 rounds, 0 corrupt targets); a power loss is not tested. The file is `fsync`ed before the rename, but the folder cannot be (the .NET base library cannot flush a directory), so after a power loss the rename may not be durable and the old name may still point at the old file.

**Speed** against the fastest managed library per task (ratio = OneMedia time ÷ theirs, steady state; lower is better). Full tables, allocations, memory, first-call times and the native tools' times are in [docs/bench-results](docs/bench-results); the method is in [docs/bench.md](docs/bench.md).

| Task | Compared with | x64 (Ryzen 9 7950X3D) geomean / worst file | arm64 (Apple M2) geomean / worst file |
|---|---|---|---|
| MP4 demux, every packet | SharpMP4 | 0.10× / 0.21× | 0.09× / 0.20× |
| MP4 audio track only | OneAudio `Mp4PacketSource` | 0.53× / 0.82× | 0.49× / 0.61× |
| MP4 probe (tracks, duration) | MetadataExtractor, TagLib#, ATL.NET | 0.10× / 0.41× | 0.12× / 0.27× |
| Matroska demux | Matroska (NEbml) | 0.26× / 0.31× | 0.25× / 0.41× |
| Matroska probe | NEbml element reader | 0.68× / 0.79× | 0.59× / 0.78× |
| MP3 tags (ID3v1/v2, APE) | TagLib#, ATL.NET | 0.50× / 0.63× | 0.63× / 1.04× |
| FLAC and Ogg tags | TagLib#, ATL.NET | 0.56× / 0.76× | 0.73× / 0.91× |
| ASF/WMA tags | TagLib#, ATL.NET | 0.60× / 0.75× | 0.74× / 0.93× |
| WAV/AIFF/CAF open and tags | TagLib#, ATL.NET | 0.50× / 0.70× | 0.59× / 0.88× |
| AVI probe | MetadataExtractor, TagLib# | 0.82× / 0.84× | 0.78× / 0.81× |
| MP4 mux (AAC track, in memory) | SharpMP4 | 0.15× / 0.33× | 0.09× / 0.18× |
| MP4 remux (AAC track) | SharpMP4 | 0.10× / 0.20× | 0.08× / 0.15× |
| ID3 tag write | TagLib#, ATL.NET | 0.44× / 0.48× | 0.49× / 0.53× |
| FLAC tag write | TagLib#, ATL.NET | 0.41× / 0.48× | 0.52× / 0.52× |
| MP4 `ilst` tag write | TagLib#, ATL.NET | 0.53× / 0.56× | 0.41× / 0.48× |
| WAV tag write | TagLib#, ATL.NET | 0.52× / 0.60× | 0.46× / 0.54× |
| Matroska tag write (one file; the comparators fail on the other) | TagLib# | 0.99× / 0.99× | 1.09× / 1.09× (within the 1.25× per-row limit; the tag-write geomean is under 1.0×) |

The probe and tag-read rows were measured again for 0.2 ([x64](docs/bench-results/0.2-x64.md), [arm64](docs/bench-results/0.2-arm64.md)); the demux rows are from the first preview's run; the mux, remux and tag-write rows are from the 0.3 run ([x64](docs/bench-results/0.3-x64.md), [arm64](docs/bench-results/0.3-arm64.md)). Matroska muxing, remux to Matroska and `MakePlayable` have no managed comparator; remuxing the files over 4 GiB peaks at 62 to 95 MiB (x64) and 71 to 96 MiB (arm64). ATL.NET cannot read AVI and TagLib# cannot read CAF, so those files have no ratio against them. Demux of the 0.2 containers has no managed comparator.

The first open in a process includes JIT time: about 17–23 ms for MP4 and 12–16 ms for Matroska, against 10–14 ms and about 3 ms for those libraries.

Every parser is fuzzed (70 targets, 600 seconds each for this release, no open crash); damaged input is reported as typed defects with byte offsets and never surfaces as an untyped exception. Reading pulls packets with zero allocations per packet and constant memory at any file size.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneMedia` | the library; depends on nothing but .NET |
| `RecursiveAI.OneMedia.Audio` | adapter that hands audio tracks to OneAudio; depends on the core package and `RecursiveAI.OneAudio` |
| `RecursiveAI.OneMedia.Tool` | the `onemedia` command-line tool (`dotnet tool install`) |

Target framework is `net10.0`. The library is NativeAOT, trimming and browser-WebAssembly clean.

## Building

```
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

To run part of the suite, use the runner's own filters, for example `dotnet test --project tests/OneMedia.Tests -c Release --filter-class '*XiphFlvTests'` (also `--filter-method`, `--filter-namespace` with a full name, `--filter-query`). The VSTest-style `--filter` is not understood by this runner: `dotnet test` then reports "Handshake failures" and exit code 5 with zero tests.

Build output goes to `artifacts/` in the repository root (ignored by git). To build somewhere else, set `ONEMEDIA_SCRATCH` to a directory; output then goes to `$ONEMEDIA_SCRATCH/artifacts`. Test files are written under the system temporary directory (`TMPDIR`).

`smoke/OneMedia.AotSmoke` is published with NativeAOT and fails on any trim or AOT warning; `smoke/OneMedia.WasmSmoke` runs the same checks under the browser-wasm runtime (`wasm-tools` workload; run the built `AppBundle/main.mjs` with node). `tools/` holds the differential and I/O harness and the corpus scripts, `fuzz/` the fuzz targets and `bench/` the benchmarks; none of them ship.

## Contributing notes

- Clean room: the code is written from published specifications and from observing the behaviour of other tools as black boxes. The source of other media libraries and tools is not consulted.
- Every public type and member has an XML doc comment; warnings are errors.
- `unsafe` code, `Unsafe.*`, `MemoryMarshal` and intrinsics are not used.

## Licence

`MIT OR Apache-2.0`, at your option. Copyright (c) Recursive AI, LLC. See `LICENSE-MIT` and `LICENSE-APACHE`. Test media provenance and third-party notices are in `THIRD-PARTY-NOTICES.md`.

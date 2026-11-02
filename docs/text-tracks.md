# Text tracks in MP4, QuickTime, 3GP, Matroska and WebM

Normative notes for how subtitle text tracks inside containers map to and from the cue model (`RecursiveAI.OneMedia.Captions`). Sidecar files are in `subtitles.md`.

## Formats

| Format | Where | Track identifies as | Read | Write | Remux target (when the output container does not hold it) |
|---|---|---|---|---|---|
| `tx3g` (3GPP TS 26.245, also `mov_text`) | MP4, MOV, 3GP | sample entry `tx3g` | yes | yes | Matroska: `S_TEXT/UTF8` |
| QuickTime `text` | MOV, MP4 | sample entry `text` | yes | no (kept as it is MOV↔MP4) | `tx3g` in ISO, `S_TEXT/UTF8` in Matroska |
| `wvtt` | MP4 | sample entry `wvtt` | yes | yes | Matroska: `S_TEXT/WEBVTT` |
| `stpp` (TTML, IMSC1 text) | MP4 | sample entry `stpp` | yes | yes | Matroska: `S_TEXT/WEBVTT` |
| `S_TEXT/UTF8` (`S_TEXT/ASCII`) | Matroska | CodecID | yes | yes | ISO: `tx3g` |
| `S_TEXT/ASS`, `S_TEXT/SSA` | Matroska | CodecID | yes | yes | ISO: `tx3g` |
| `S_TEXT/WEBVTT` | Matroska | CodecID | yes | yes | ISO: `wvtt` |
| `D_WEBVTT/*` | WebM (also Matroska) | CodecID | yes | yes | ISO: `wvtt`; WebM always gets `D_WEBVTT/SUBTITLES` |

`TextTracks.FormatOf(track)` names the format, `TextTrackDecoder` turns packets into a `SubtitleDocument`, `TextTrackEncoder` does the reverse and reports what the format could not carry. `MediaFile.ContainerSubtitles` lists the text and bitmap tracks with lazily read cues, without reading media data; `MediaFile.Subtitles` adds teletext pages and embedded captions (see `subtitles.md`, "One list"). Remux converts a text track only when the target container cannot hold the input format; everything else passes through byte for byte.

## Cue times

- A cue starts at the packet's presentation time (the track's edit-list start offset applied) and ends at the start plus the packet's duration. A packet with no duration gives a cue that ends where it starts.
- An empty sample (tx3g with zero-length text, a `vtte` box, a TTML document with no cues) is a gap and produces no cue.
- Writing uses a timescale of 1000 unless told otherwise, rounds times to it (to nearest) and says so in the encoding's notes. The first sample is an empty one when the first cue starts after zero, and empty samples fill the gaps, as the common muxers do.
- Cues that overlap in time: Matroska, `wvtt` and `stpp` hold them (the timeline is cut at every cue start and end, and each piece lists the cues showing; WebVTT pieces that continue a cue carry its original start in `ctim`). `tx3g` shows one block of text per sample, so overlapping cues are stacked in start order into one sample per stretch and a loss named `overlap` is reported.

## tx3g and QuickTime text

- The sample entry carries the default style (font, size, colour, face), the default box, the justification and the font table. The whole entry is kept in the document as an extra, so tx3g → tx3g keeps it.
- Sample modifier boxes map: `styl` (bold, italic, underline, colour, size, font) to spans; `hlit`/`hclr` to a background span; `krok` to word-timed spans; `href` to a link span; `tbox` to placement; `dlay` to a scroll delay; `blnk` to blink; `twrp` to wrap. Character offsets count code points in UTF-8 text and UTF-16 units in UTF-16 text.
- Style attributes are mapped only where they differ from the entry's default style. The default placement (justification 1, vertical −1) is no placement.
- Line breaks (CR, CRLF, NEL, LS, PS) read as line feed.

## S_TEXT/UTF8

The block is the cue text, line breaks written as CR LF (what ffmpeg and mkvmerge write), read as line feed. SubRip tags (`<b>`, `<i>`, `<u>`, `<font>`) go through the SRT reader and writer of the sidecar layer.

## S_TEXT/ASS

CodecPrivate holds the script header (`[Script Info]`, `[V4+ Styles]`, and the `[Events]` Format line). Each block is `ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text`; start and end come from the block. The text keeps its commas. Reading builds one script from the header and the events and uses the ASS reader of the sidecar layer; writing uses its writer and cuts the Dialogue lines into blocks (ReadOrder is the cue's index).

## WebVTT in Matroska and WebM

- `S_TEXT/WEBVTT`: CodecPrivate is the header text (signature, STYLE and REGION blocks). The block is the cue payload only; settings, identifier and preceding NOTE comments are the BlockAdditional with BlockAddID 1 as `settings LF identifier [LF comments]`.
- `D_WEBVTT/*` (WebM): the block is `identifier LF settings LF payload`; no CodecPrivate.
- Timestamps inside cue text (`<00:00:01.500>`) are relative to the start of the block, per the Matroska subtitles page. They are converted to and from absolute times in the cue model.

## wvtt (ISO)

- Sample entry: `vttC` holds the WebVTT header text; `vlab` (a source label) is kept.
- Sample: a `vttc` box per cue (`payl` text, optional `sttg` settings, `iden` identifier, `ctim` original start), `vtte` for a gap, `vtta` for comment text preceding a cue. Unknown boxes are preserved by the wire layer.
- A cue with `ctim` whose text, identifier and settings equal an open cue in the previous sample continues it. Timestamps inside the payload are left as written (absolute) because nothing settles otherwise (see gaps).

## stpp (ISO)

- Sample entry: namespace, schema location and auxiliary MIME types as three NUL-terminated strings, then boxes.
- Each sample is a complete TTML document. Times inside count from the start of the track's timeline (observed in MP4Box-written files), so a cue shown at 2 s in a sample starting at 2 s has `begin="00:00:02.000"`. The same cue repeated in later samples is read once. Empty documents cover gaps. Styles and regions come from the first document.
- Image subsamples are not read.

## Loss reports

An encoding lists `TextTrackLoss(feature, detail, cues)` for what the target cannot carry (from the sidecar layer's per-feature analysis plus the track-level losses above). A remux adds one `RemuxLoss(CodecField, NotRepresentable, …)` per entry and `changed` notes for normalisations (rounding, gap samples, track conversion).

## Observed behaviour of other tools (ffmpeg 7.0.2, MP4Box 26.07, mkvmerge 103.0)

- MP4Box and ffmpeg write tx3g with an empty two-byte sample for every gap, including the one before the first cue. MP4Box truncates overlapping SRT cues; ffmpeg keeps them as consecutive samples.
- ffmpeg 7.0.2 cannot mux or demux `wvtt` in MP4 and does not read `stpp`; MP4Box writes both. mkvmerge refuses WebVTT in WebM; ffmpeg writes `D_WEBVTT/SUBTITLES` for WebVTT in any Matroska file and drops class markup; mkvmerge writes `S_TEXT/WEBVTT`. ffmpeg does not extract cues from mkvmerge's `S_TEXT/WEBVTT` tracks.
- Differences between ours and these tools are recorded per file by `tools/text-track-compare.py`, `tools/text-track-extract-check.py` and `tools/text-track-write-check.sh`.

## Gaps black-box output could not settle

ISO/IEC 14496-30 is not available. Open: the order of `vttc` children; the use of `vsid`; `ctim` for cues split across samples (we write it and accept it, MP4Box never splits); whether payload timestamps in `wvtt` are absolute or sample-relative; whether other `stpp` writers make document times sample-relative (ours accept absolute, as MP4Box); `sbtt` text entries; `stpp` image subsamples.

## Browsers

Playwright Chromium 140 and Firefox 141 expose no in-band text tracks for MP4, WebM or Matroska. Playwright WebKit 26 lists a `subtitles` text track for MP4 with `tx3g`, MP4 with `wvtt` and Matroska with `S_TEXT/UTF8` and loads its cues; it lists none for WebM `D_WEBVTT`. `tools/playback/text-track-browser.sh` repeats the check.

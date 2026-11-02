# Teletext subtitles

DVB teletext (ETSI EN 300 472 carriage of ETS 300 706 World System Teletext) read into the shared cue model. Reading only; there is no teletext writer. Code: `RecursiveAI.OneMedia.Captions.Teletext`.

## Where teletext is found

| Container | How | Notes |
|---|---|---|
| MPEG-TS, M2TS | PES streams of type 0x06 whose PMT carries a teletext descriptor (tag 0x56, or 0x46 for VBI teletext) are tracks with codec `dvb-teletext` | The descriptor lists, per entry: ISO 639 language, type (1 initial page, 2 subtitle, 3 additional information, 4 schedule, 5 subtitle for the hearing impaired), magazine and page (BCD). Magazine 0 means 8. |
| MPEG-PS / VOB | private stream 1 packets whose payload opens with a data_identifier 0x10 to 0x1F are tracks with codec `dvb-teletext`; the first byte stays in the packet | There is no standard program-stream carriage; this is how a recorder keeps the transport PES packets. Exercised with generated streams only (no published sample exists). |
| Matroska | nothing to read | Matroska registers no codec ID for teletext, and the muxers in common use refuse the stream. |

`TeletextExtractor.ReadAll(demuxer, options, defects)` reads every teletext track and returns, per track, the subtitle pages found as `TeletextSubtitleTrack` (page, language, cues, conversion report, every page transmission). Packets of other tracks are skipped without reading their payloads. `MediaFile.Subtitles` does not list teletext pages yet; the extractor is the entry point until it does.

## Decoding

- **PES data field** (EN 300 472 §4.3): data_identifier, then data units. Units 0x02 (non-subtitle) and 0x03 (subtitle) of length 0x2C carry one teletext packet; 0xFF is stuffing. Every byte is bit-reversed first (DVB keeps teletext bytes in transmission order). A unit with an uncorrectable address, an unknown unit id or a length past the end of the PES is skipped with a typed defect (`teletext.address-hamming`, `teletext.unit-id`, `teletext.unit-truncated`; a data_identifier outside 0x10 to 0x1F is `teletext.data-identifier`).
- **Protection codes** (ETS 300 706 §8): odd parity, Hamming 8/4 and Hamming 24/18. One-bit errors are corrected; two-bit errors in a Hamming 8/4 address skip the packet, in a 24/18 triplet skip the triplet. A row byte with bad parity shows as a space and is counted as an error.
- **Pages** (§9): a page starts at a packet X/0 header and ends at the next header of the same magazine (parallel mode) or of any magazine (serial mode, control bit C11). Rows 1 to 24 are kept, plus X/26; packet 25, X/27, 8/30 and X/28 formats other than the first designation are ignored, and M/29 is read for its character-set designation only. The header time-filling page number 0xFF is not a page. Control bits C4 to C14 are decoded; the erase bit C4 starts a new page, C5 newsflash and C6 subtitle select subtitle pages, C10 inhibits display.
- **Enhancements** (§12.3): X/26 triplets apply to the page: position setting, full-row colour, G0 and G2 characters, and the diacritical-mark characters (modes 0x11 to 0x1F, composed to precomposed Unicode for Latin and for the Cyrillic Ё, Й, Ў pairs). Mosaics, DRCS, objects, the second G0 set and ESC switching are not applied.
- **Time**: every packet takes the PES presentation time. A page is stamped with its header's time. A PES without a PTS takes its DTS; without either, the time is unknown.

### Character sets

| Priority | Source |
|---|---|
| 1 | the page's X/28/0 format 1 designation |
| 2 | the magazine's M/29/0 designation |
| 3 | where neither exists and the header's C12 to C14 are zero: the PMT language of the page, else the stream's one language when all entries agree (a guess, since the header cannot say anything but English then) |
| 4 | the header's C12 to C14 as a Latin national option (C12 the most significant bit) |

Mapped G0 sets (ETS 300 706 tables 36 to 42 and 46): Latin with the thirteen national option subsets, Cyrillic options 1 (Serbian/Croatian), 2 (Russian/Bulgarian) and 3 (Ukrainian), Greek and Hebrew. **Arabic (table 44) is not mapped**: its glyphs are contextual letter forms with no one-to-one character, so letters come out as U+FFFD (counted in the report) while digits and punctuation, which share ASCII, are kept. The Latin G2 supplementary set is mapped; the Greek, Cyrillic and Arabic G2 sets are not.

## From pages to cues

A subtitle page is a page with C6 set, or one the PMT announces as a subtitle page (type 2 or 5). Pages are selected with `TeletextOptions.Pages` or `AllPages`; `AllPages` also gives the page model for pages that are not subtitles (`TeletextSubtitleTrack.Pages`: 25 by 40 cells with character, colours and attributes).

- **Visible text.** When any boxed character exists, or the page sets C5 or C6, only boxed cells are text (§12.2: start box 0/B, end box 0/A); otherwise the whole row. A row below a double-height row is skipped.
- **Timing.** A cue starts at its page's time and ends at the time of the next transmission of that page. A transmission with no visible text clears the screen. The last cue lasts until the end of the track (the last time in the stream) or, when that is unknown, `LastCueDuration` (3 s).
- **Live subtitles.** Live programmes send a page word by word, each transmission repeating the lines so far with more text. Unless `Progressive` is set, a transmission that keeps every line on screen in place and only extends it continues the same cue and replaces its text, so the cue shows the finished caption from the first word's time.
- **Styling.** Foreground colour (the eight teletext colours as RGB) and a non-black background become spans; double height is a 200 % font size span. Placement row and column are the teletext row and column. The cue extras carry `teletext/page` as `<page>:<subcode>`.
- **Page numbers** are written as the magazine digit and two hex digits (`777`).

### Conversion report

`TeletextConversionReport` counts what the cue model cannot carry: boxed mosaic cells (dropped, they read as spaces), flashing cells (shown steady), concealed cells (shown as spaces), double-height or double-width rows (approximate size), coloured cells (approximated by RGB), and characters with no mapping (U+FFFD). `IsLossless` is true when none of mosaics, flashing, concealment, double size or unmapped characters occurred.

## Differential

The corpus is eight real DVB-T and DVB-S captures from the TSDuck stream repository (Italy mux 1, 3 and 4, two Dutch multiplexes, Poland, Russia, Spain Astra), 75 (file, PID, page) tracks, no licence stated: fetch-only on the worker (`tools/corpus/manifest.tsv`), never committed. Oracles run as released binaries: ccextractor 0.96.5 (`-teletext -datapid -tpage -out=srt`) and, for a second opinion, a distro ffmpeg 8.0.1 with libzvbi on the extracted PID (`-txt_format text`). The pinned static ffmpeg 7.0.2 has libzvbi but crashes in it on every teletext stream, so it is not usable here.

ccextractor against ours, 51 tracks it produced output for: ours 148 cues, theirs 146; **132 cues equal in text and start** (start differences at most 1 ms); mid-stream end times within 42 ms (one video frame). Every difference:

| Difference | Cause |
|---|---|
| ccextractor writes junk for mosaic cells | we drop them and count them in the report |
| Polish pages garbled in ccextractor (ę ś ć ł) | it assumes the English subset when the stream gives no designation; we fall back to the PMT language |
| Cyrillic Ё: Latin Ë in ccextractor | we emit U+0451 |
| First cue of one Polish page missing in ccextractor | none found; ours is present in the page data |
| End of the last cue of each stream | ccextractor ends it at its own input end, differing per run by up to 2.6 s; ours at the stream's last time; excluded from the comparison |

ffmpeg 8.0.1 on one Polish multiplex, 6 (PID, page) combinations: its cues 15, ours 23, 10 matched by text (ASCII compare) with starts within 60 ms; it drops some cues and leaves ends unset.

Reproduce: `tools/run-teletext-check.sh`, `tools/run-teletext-oracles.sh`, `python3 tools/teletext-compare.py ours ref/cc --show 6 --ends`.

## Limits

- Only the first G0 set and its X/26 modifications; no second G0 (ESC), no G1/G3 mosaics, no DRCS or objects, no X/27 links, no 8/30 service data.
- A designation (M/29) that arrives after the first page of a magazine applies from the next page on; the first page is not re-rendered.
- Finished pages wait in a queue bounded by `TeletextOptions.MaxQueuedPages` (4096); past that the oldest is dropped with a `teletext.queue-full` defect.
- Fuzzed: `teletext-pes` (an option byte and a PES payload, pushed three times) and `teletext-file` (a TS or PS file through the demuxers and the extractor).

# Subtitles and captions

One cue model carries subtitles whatever holds them: a sidecar file, a container text track, embedded CEA-608/708 captions.
It lives in `RecursiveAI.OneMedia.Captions` (`src/OneMedia/Subtitles/Cues.cs`).

## The cue model

Everything is immutable: records with init-only members. Lists are `SubtitleList<T>`, which compares by content, so two documents
are "the same cues" when `==` says so.

| Type | Holds |
|---|---|
| `SubtitleDocument` | cues in file order; `Styles`, `Regions`, `Language`, `Title`; header `Extras`; reference frame size; the file's encoding, BOM and newline (writers reuse them) |
| `SubtitleCue` | `Start`/`End` as exact `MediaTime`; `Text` (plain, lines separated by `\n`); `Spans`; `Speaker`; `StyleName`; `Placement`; `Mode`; `Language`; `Id`; `Extras` |
| `SubtitleSpan` | a stretch of `Text` by UTF-16 offset with a `TextStyle` (bold, italic, underline, strike, colour, background, font, size, classes), `Voice`, `Language`, `Ruby`, `WordStart`/`WordEnd`, `Raw` |
| `CuePlacement` | `Region`, `Align`, `VerticalAlign`, `Line` (lines, percent or grid row), `Position` (percent), `PositionAnchor`, `Size`, `Writing`, grid `Row`/`Column` |
| `SubtitleRegion`, `SubtitleStyle` | named boxes and styles declared in the header |
| `SubtitleExtra` | `(Format, Key, Value)`: something only one format can say, kept so writing that format again loses nothing |

Rules:

- Spans index into `Text`, may nest and overlap, and a zero-length span is a point (a karaoke tick or a format marker in `Raw`).
- Karaoke and recognised words are spans with `WordStart`/`WordEnd` on the media timeline (absolute, not relative to the cue).
- A CEA-608/708 caption is an ordinary cue: `Mode` says pop-on, roll-up or paint-on; `Placement.Row` (1-15) and `Column` (0-31)
  place it on the character grid; colours, italics and underline are spans.
- Extras belong to the format named in them. Writers of other formats ignore them and the conversion report counts them as dropped.
- Time is never a `double`; positions and sizes are display values and are `double`.
- A bitmap cue (PGS, VobSub, DVB) carries `Images`: RGBA pixels (straight alpha unless `IsPremultiplied`), a position on a stated
  display size, and the source object/palette ids in `Extras`. Several images per cue are allowed; text is optional. Text formats
  drop images and the conversion report says "Image cue dropped".

## Formats

All live in `RecursiveAI.OneMedia.Captions`, found through `SubtitleFormats` (an explicit table; `SubtitleFormats.Register` adds one more,
for example Scenarist SCC from the embedded-captions layer). Content decides the format (`SubtitleFormats.Sniff`), never the extension;
the extension only picks the format to write.

| Format | Recognised by | Notes |
|---|---|---|
| SRT | a `-->` line whose times use a comma (score 85, 95 after a number line) | numbered blocks; `<b> <i> <u> <s> <font color face size>`; the common `{\an1..9}` extension becomes alignment; other `{\...}` blocks are kept as raw markers; non-sequential numbers and coordinates after the times are kept as extras |
| WebVTT | `WEBVTT` + space/tab/newline | cue settings, `REGION`, `STYLE` and `NOTE` blocks kept, `<c> <i> <b> <u> <v> <lang> <ruby>/<rt>` and timestamp tags (word timing); entities `&amp; &lt; &gt; &lrm; &rlm; &nbsp;`; the default colour classes (`.red`, `.bg_red`, …) become colours |
| ASS/SSA | `[Script Info]` or a styles section | `[Script Info]` lines, styles (the unmapped columns kept per style), events with the `Format:` line order; override tags `\b \i \u \s \c \fn \fs \an \pos \k \kf \ko \K \r` are mapped, every other tag is kept raw at its position; `Comment:` lines, unknown sections kept; SSA v4 styles and alignments are converted |
| TTML/DFXP/IMSC1 text | the TTML namespaces | styles, regions, agents, nested spans with `begin/end/dur`, ruby, `br`; time expressions clock-time (with fraction or frames), offset-time (`h m s ms f t`), frame and tick rates; written as the IMSC1 text profile (`ttp:profile`, `xml:lang`, `ttp:timeBase="media"`) |
| SBV | `H:MM:SS.mmm,H:MM:SS.mmm` first line | plain text only |
| LRC | a `[mm:ss.xx]` or `[key:value]` first line | `[ti:]` is the title, other header tags are extras, `[offset:]` is applied to the times and not kept, `[length:]` bounds the last line; `<mm:ss.xx>` word times; a cue ends where the next one starts (the last lasts 5 s unless `[length:]` says less) |

### Text encodings

Decoding order (`SubtitleReadOptions` overrides): byte order mark (UTF-8, UTF-16 and UTF-32 both ways); an explicit `Encoding`; UTF-16
without a mark when more than half of the first 128 code units have a zero high or low byte on one side only; UTF-8 when the bytes are
valid UTF-8; otherwise `FallbackEncoding`, default windows-1252 (a defect of severity Info says so). Legacy code pages come from the
BCL's `CodePagesEncodingProvider`. The document remembers its encoding, byte order mark and line ending and writers reuse them, so a
legacy file converted to the same format keeps its bytes; WebVTT and TTML are always written as UTF-8 without a mark. Characters the
target encoding cannot hold are replaced by `?` and reported.

### Defects

Readers never throw for a deviation. Each is a `MediaDefect` with the byte offset of the offending text in the original file (after
the byte order mark, in the file's own encoding) and `line N, column M` in the message. Codes: `subtitles.encoding.legacy|utf16-nobom|unknown`,
`subtitles.newline.mixed`, `subtitles.srt.stray-line|blank-in-text|end-before-start|index|tag-unclosed|tag-unmatched`,
`subtitles.webvtt.signature|stray-block|time|time-comma|end-before-start|order|setting|region-setting|timestamp|tag-unknown|tag-unmatched`,
`subtitles.ass.preamble|info|line|style-fields|time|end-before-start|karaoke`, `subtitles.ttml.xml|root|attr-ns|time|no-end|end-before-start`,
`subtitles.sbv.stray-line|end-before-start`, `subtitles.lrc.offset|negative|stray-line`.
Not well-formed XML, or a missing `<tt>` root, throws `MediaFormatException` (after logging the defect); `TryRead` returns false.
DTDs are refused. `MediaLimits.MaxCues` (default 1,000,000), `MaxAllocationBytes` (input size), `MaxStructureDepth` and
`MaxElementCount` (XML) apply.

## Conversion and the fidelity table

Writing a document in a format returns a `ConversionReport`: per cue, what was dropped (the format cannot carry it) or approximated
(carried in a nearby form). Dropped entries come from comparing what the document uses with what the format carries; approximations
are logged by the writers (time rounding, position mapping, nearest colour class, …). Entries are capped at 10,000; the rest are counted.

| Feature | SRT | WebVTT | ASS | TTML | SBV | LRC |
|---|---|---|---|---|---|---|
| Time precision | 1 ms | 1 ms | 10 ms (rounded) | exact (ms clock, or ticks) | 1 ms | 10 ms (rounded), start only |
| Bold, italic, underline | yes | yes | yes | yes | – | – |
| Strike-through | yes | – | yes | yes | – | – |
| Text colour | yes | nearest of 8 default colours | yes | yes | – | – |
| Background colour | – | nearest of 8 default colours | – | yes | – | – |
| Font family and size | yes | – | yes | yes (points become pixels) | – | – |
| Ruby | – | yes | – | yes | – | – |
| Word timing | – | yes | yes (`\k`) | yes | – | yes (10 ms) |
| Speaker/voice | – | yes | cue-level only (`Name`) | yes (agents) | – | – |
| Language | – | yes | – | yes | – | – |
| Style classes | – | yes | – | – | – | – |
| Alignment | yes (`{\an}`) | yes | yes (`\an`) | yes | – | – |
| Position and size | – | yes | `\pos` only | via generated regions | – | – |
| Regions | – | yes | – | yes | – | – |
| Vertical writing | – | yes | – | yes | – | – |
| Named styles | – | – | yes | yes | – | – |
| Cue identifiers | – | yes | – | yes | – | – |
| Title | – | – | yes | yes | – | yes |
| Caption mode, bitmap images | – | – | – | – | – | – |
| Same-format extras | yes | yes | yes | yes | – | yes |

Other approximations, always reported: an empty line inside a cue (SRT, WebVTT, SBV cannot hold one: a space or no-break space is
written), a brace in ASS text (written as a parenthesis), LRC lines (one line per text line of a cue, with the same time; the end of a
cue is the next cue's start), CEA grid rows and columns (mapped to percentages assuming 15 rows and 32 columns), a negative time
(clamped to zero), characters outside the target encoding. A bitmap cue to a text format reports "Image cue dropped".

A same-format round trip is exact: the file's own extras, order, numbering and styling survive, and a file in our canonical layout is
rewritten byte for byte (`testdata/subtitles/sample.*`, `SubtitleFormatTests`).

## Alignment

`SubtitleAlignment.Shift(document, offset)` adds an exact `MediaTime` to every cue and word time and reports cues that fell before
zero. `SubtitleAlignment.OffsetFor(media)` is where the first presented frame of the video track sits on the file's timeline (the
track's edit-list or start offset), and `ToMediaTimeline` applies it. Nothing is shifted unless asked: `Subtitles.*` never shifts.

## Method and recorded deviations

ffmpeg 7.0.2 (static build) is run as a black box: for SRT, WebVTT, ASS and LRC, ffmpeg writes each format from a generated SRT and our
reader's cues are compared with ours for the SRT; our writer writes each format and ffmpeg converts it back to SRT, read by our reader
(`tools/subtitles/ffmpeg-diff.py`). ffmpeg has no TTML or SBV reader, so those are checked by round trip and in browsers.
WebVTT written by us is loaded into `<track>` elements in Chromium, Firefox and WebKit and the cues read back through the `TextTrack`
API are compared with ours (`tools/subtitles/vtt-browser-check.py`). Observed deviations:

- ASS and LRC store centiseconds: ffmpeg and we both round to the nearest 10 ms; compared with a 10 ms tolerance.
- LRC has no end time; compared on start times and text. ffmpeg keeps enhanced `<mm:ss.xx>` word tags as text and gives the last line
  no usable end; we read them as word timing and end the last line after 5 s.
- ffmpeg shows ruby annotation text inline when converting WebVTT (`漢kan字`); we keep the base text and the annotation separately.

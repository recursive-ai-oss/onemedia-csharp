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

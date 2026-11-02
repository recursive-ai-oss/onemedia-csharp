# Embedded captions: CEA-608, CEA-708, SCC

Namespaces: `RecursiveAI.OneMedia.Codecs.Captions` (carriage: where the bytes sit in the video), `RecursiveAI.OneMedia.Captions.Cea608`,
`.Cea708`, `.Scc` (decoders, encoder, sidecar) and `RecursiveAI.OneMedia.Captions` (`EmbeddedCaptions`, `CaptionCues`: the bridge to the cue model).
Nothing here decodes a picture; captions are read from, and written into, SEI NAL units and MPEG-2 user data.

## Carriage

| Where | Layout read | Notes |
|---|---|---|
| H.264 SEI NAL (type 6) | `user_data_registered_itu_t_t35`: 0xB5, 0x0031, `GA94`, type 0x03, `cc_data()` | A/72 Part 1 |
| HEVC prefix/suffix SEI (39/40) | the same message | |
| MPEG-2 video user data (0xB2) | `GA94`, type 0x03, `cc_data()` | A/53 Part 4 |
| ISO `c608`/`c708` sample entries | not read yet | follow-up |

Length-prefixed (`avcC`/`hvcC` length size) and Annex B samples are both walked; only SEI NAL units and user-data sections are looked into.
`cc_data()` yields triplets (`cc_valid`, `cc_type`, two bytes): types 0/1 are 608 field 1/2, 2/3 are DTVCC packet data/start.
`CaptionExtractor.ReadFrames` returns them **in presentation order** (stable sort on the packet's presentation time), because decoders must see
pairs in display order, not decode order. Within a picture the pairs of one type are spread evenly over the picture's duration.

Layouts seen in files but **not read** (their user-data prefixes were observed in published samples; no description we could cite):
DVD GOP user data (`43 43 01 F8`), SCTE-20 (`03 81 …`) and the PixelTools "display order" layout (`02 09 …`). They are reported as no captions.

## CEA-608 decoding (`Cea608Decoder`)

* Parity: pairs whose bytes lack odd parity are dropped (`RequireParity`, default on). Null pairs are ignored. A control pair repeated
  immediately is acted on once (CEA-608 sends controls twice).
* Channels CC1–CC4 (field and data channel from the control code) and Text1–Text4. Modes: pop-on (RCL…EOC), roll-up 2–4 (RU2–4, CR),
  paint-on (RDC). PACs, tab offsets, mid-row codes, special and extended characters (extended characters replace the fallback character before
  them), BS, DER, EDM, ENM, EOC, FON.
* Output is a **screen**: what was displayed from `Start` to `End`, as rows with grid position (row 1–15, column 1–32) and style spans
  (colour, italic, underline, flash).
* **Screen convention.** A new screen that only extends the open one (each open row is still there at the same place and is a prefix of the
  new text) is merged into it; anything else (scroll, erase, EOC, replaced row) ends the open screen at that pair's time and starts a new one.
  So a roll-up line typed over many pairs is one caption, and each CR yields the scrolled window as a new caption. Text channels emit one
  caption per line at its CR.
* No implied roll: a roll-up command or PAC without a CR does not scroll; text written again on the same row overwrites it, as ccextractor does on
  the PixelTools `atsc_field1` sample (`RU3, PAC, text` per line, no CR: one caption, "Have a Nice Day!g Insertion").
* XDS (field 2): packets collected from start/continue/end codes with checksum verification; `ProgramName` and `NetworkName` are typed.

## CEA-708 decoding (`Cea708Decoder`)

DTVCC packets are reassembled from type 3/2 triplets; service blocks 1–63 (extended service numbers included). Modelled: DF0–DF7 (anchor, row and
column counts, visibility), CW, CLW/DSW/HDW/TGW/DLW, RST, SPL, SPA (size, italics, underline), SPC (foreground colour), BS/FF/CR/HCR, G0/G1/G2 text.
Parsed and dropped: window attributes (SWA), the other pen attributes, background and edge colours, opacity, fonts and offsets, DLY/DLC, P16, C2/C3
extension codes. A caption is the set of visible windows with text; content that only grows is merged, as for 608. `CaptionCues.FromCea708` reports
the dropped detail and the count of runs with a non-standard pen size.

## Mapping to cues (`CaptionCues`)

608 → `SubtitleCue`: text = rows joined by `\n`; italics/underline/colour become spans; `Placement.Row`/`Column` = first row's grid position (column 0-based); every
row's `row:column` is kept in the `cea608`/`rows` extra, the channel in `cea608`/`channel`; `Mode` = pop-on/roll-up/paint-on. Flashing is dropped (reported).
708 → one cue per visible window with `Line` (percent) and `Position` (percent; grid anchors scaled by 74 rows / 209 columns); extras `cea708`/`service`, `window`.
Cues → 608 pop-on (`ToCea608`): lines over 32 columns are wrapped at spaces, more than 15 rows are cut, rows go to the cue's grid position or the bottom rows
centred; characters outside the 608 sets become `?`, colours other than the seven 608 colours become white, bold/strike/background/font/size are dropped; each is counted in the loss list.

## SCC (`SccDocument`, `SccSubtitleFormat`)

`Scenarist_SCC V1.0`, then `HH:MM:SS:FF<TAB>pairs…` lines; `;` before the frame count means drop-frame. A pair occupies one frame at 29.97 fps from its line's timecode
(time = frame × 1001 / 30000, exact). Drop-frame numbering follows SMPTE 12M. **Canonical form** (what `Write` produces; re-reading and re-writing is byte-identical):
header, blank line, then each line as timecode, a tab, lowercase four-digit hex pairs separated by single spaces, and a blank line; LF line ends.
Writing from cues: each caption is loaded as pop-on pairs so the EOC pair lands on the start frame, erased (EDM) at the end frame; a caption whose load would start
before frame 0 appears late. Control codes are sent twice, as encoders do. `SccSubtitleFormat` is a `SubtitleFormat` (name `scc`, `.scc`, sniffed by header);
add `SccSubtitleFormat.Instance` to the registry's list (or call `SubtitleFormats.Register`).

## Pass-through and write-back

Remux copies video samples unchanged, so SEI and user-data captions ride along: `RemuxReport.CarriedCaptions` says, once per video track, that caption data was seen in the
samples and carried (checked on the packets written, until the first hit).

`CaptionSeiFeed` (an `IPacketFeed`, used through `RemuxOptions.Replacements`) copies an H.264/HEVC track and inserts caption SEI. **Exactly which bytes change**:

* In an access unit that gets caption data: one SEI NAL unit (`GA94` T.35 message plus trailing bits, emulation prevention applied) is inserted immediately before the first slice NAL unit, framed like its
  neighbours (the track's NAL length size, or a 4-byte start code). With `ExistingCaptionPolicy.Replace` (default) SEI NAL units that hold only caption messages are removed from those access units;
  `RemoveAll` removes them everywhere; `Keep` leaves them.
* Everything else in every access unit is copied byte for byte: slice NAL units, parameter sets, other SEI, AUDs, and every access unit that gets nothing.
* The packet's size changes; its times, flags and duration do not. The container then stores the new sizes (sample tables, block sizes) as for any remux.

`CaptionPlacement.Place` assigns byte pairs to frames (one field-1 pair per frame, the last frame at or before the pair's time, spilling forward). Triplets for 708 can be passed as frames directly.

## Differentials (tools/run-caption-diff.sh, run-caption-writeback.sh)

Oracles: ccextractor 0.96.6 release AppImage (reports "0.96.5" on its banner), ffmpeg 7.0.2 `movie=…[out0+subcc]`. Method: dump per-channel SRT with
`OneMedia.CaptionCheck srt --rebase` (times relative to the first picture, as ccextractor counts), compare with `tools/caption-compare.py`
(markup stripped, whitespace collapsed; each of the other tool's cues must be found in ours by text, then start/end within 40 ms).

Documented deviations (observed):

* ccextractor and ffmpeg date an SCC pop-on caption at its **line's timecode**; we date it at the EOC pair that shows it. Text and order are equal.
* ccextractor emits a roll-up window only when it is full (the 3-row sample: 2 cues where we emit one per scroll); we emit each scrolled window.
* ccextractor 0.96.5 reads H.264 SEI captions from MP4 only with `-in=mp4`, dates the first caption of our written MP4 at 0.001 s instead of 1.001 s, and does not read HEVC SEI captions (garbage cues); ffmpeg reads both.
* ccextractor appends `?` to some 708 lines and pads 608 lines to 32 columns; both are stripped by the comparer.

## Gaps against the standards

CTA-608-E/708-E were not available (they need an account). Code values, character tables, the PAC row table, the mid-row, tab-offset and extended-character codes were written from
FCC 47 CFR 79.101/79.102 descriptions, A/53 Part 4/A/72 and public prose, and checked black-box against ccextractor and ffmpeg output. Details to recheck against the CTA PDFs:
G0/G2 sets beyond what the samples exercise (708 `P16`, C2/C3 lengths, G3), 608 background-attribute and flash semantics, XDS packet types beyond program/network name,
roll-up window placement rules when a PAC changes the base row, the 708 delay commands and window-attribute effects, and the "implied roll" above.

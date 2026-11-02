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
| MPEG-2 video user data, other layouts | SCTE 20 style (`03 81 ..`), DVD GOP data (`43 43 01 F8`), display-order pairs (`02 09 .. .. 02 0A p1 p2`) | read from observation, see below |
| ISO `c608` sample entries (QuickTime closed-caption track) | atoms `cdat` (field 1) and `cdt2` (field 2) of byte pairs, one pair per frame | Apple QuickTime File Format, closed captioning media |
| ISO `c708` sample entries | not read | no free description of the sample layout; no oracle writes one |

Length-prefixed (`avcC`/`hvcC` length size) and Annex B samples are both walked; only SEI NAL units and user-data sections are looked into.
`cc_data()` yields triplets (`cc_valid`, `cc_type`, two bytes): types 0/1 are 608 field 1/2, 2/3 are DTVCC packet data/start.
`CaptionExtractor.ReadFrames` returns them **in presentation order** (stable sort on the packet's presentation time), because decoders must see
pairs in display order, not decode order. Within a picture the pairs of one type are spread evenly over the picture's duration.

Legacy MPEG-2 user data layouts (`CaptionData.TryGetFromLegacyMpeg2UserData`). None has a standard we may read (SCTE 20 needs an account and is
"for use by the recipient only"; the DVD and display-order layouts have no public description we could cite), so each is read from the bytes of a published
PixelTools sample and **checked against the same programme carried as ATSC `GA94`**: all six PixelTools files hold one programme, and the pairs and texts
extracted from the SCTE 20, DVD and display-order files equal those of the `GA94` file (28 captions, text equal 28/28 in each).

* **SCTE 20 style** (`03` + bits): 8 header bits (seen as `0x81`), a 5-bit element count, then 26 bits per element: priority (2), field (2; 1 = field 1, 2 = field 2),
  line offset (5; 11 for line 21), two caption bytes each sent least significant bit first (8 + 8), one more bit; trailing bits are ignored. Observed: one field 1
  element per picture. Elements of field 2 and counts above 1 are read by the same rule but were not seen in a file.
* **DVD GOP** (`43 43 01 F8`, flags byte, triplets of `FF` + two bytes): the flags byte holds the triplet count in its low 5 bits (30 = 15 frames x 2 fields); the
  triplets alternate between the two fields of each frame. In the sample the pairs sit in the second triplet of each frame and bit 6 is clear, so a clear bit 6 means the
  first triplet is field 2 (a set bit would put field 1 first: not seen). The data sits in the user data of the picture that opens the GOP; its frames are dated from that
  picture one frame duration apart. **No oracle reads this layout** (ccextractor 0.96.5 and ffmpeg 7.0.2 find nothing in `dvd_field1.mpg`), and our captions on it start
  about a third of a second later than on the other five files (a different encode of the programme, GOP-quantised start), so its timing is unverified.
* **Display order** (`02 09 80 80 02 0A p1 p2 ..`): one field 1 pair per picture at bytes 6-7; the pairs are stored in *display* order while the pictures run in decode order,
  so `ReadFrames` gives the k-th pair the k-th smallest presentation time. ccextractor reads this file and its pair sequence equals ours. The other records
  (`02 09 ..`, `08 20 ..`, `AA ..`) are ignored: only null pairs were seen in them.

The `c608` reader dates each pair at its sample's presentation time (edit lists applied) plus one NTSC frame per pair, so a pop-on caption starts at the sample
start plus its EOC pair. ffmpeg's caption decoder dates a caption at the sample start instead; texts are equal on the fixture (3/3).

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
* ccextractor 0.96.5 pads 608 lines to 32 columns (stripped by the comparer) and ends every DTVCC text row with a raw `0xFF` byte. Checked on `cea708_dtv`: each
  service block fills its packet exactly (`0x3C` = service 1, 28 bytes, in a 30-byte packet), so the byte is not in the stream. It showed as `?` when the SRT was read as UTF-8;
  the comparer now drops it. With that, `cea708_dtv` service 1: 3/3 cues equal in text and within 40 ms.
* `atsc_field1` and `cea608_field1` (roll-up, `RU3, PAC, text` without CR, rows overwritten in place): ccextractor emits one cue for the whole 5 s ("Have a Nice Day!g Insertion",
  dated from the first row written); we emit 28 screens, one per change of a row that is not a pure extension, and the last equals its cue. Both files give the same 28
  screens (28/28 text and time).

## Clean-room record for the tables

Checked entry by entry against **47 CFR 79.101** ("Closed caption decoder requirements for analog television receivers", eCFR, US federal regulation; the section text as of
2025-01-01, `ecfr.gov/api/versioner/v1/full/2025-01-01/title-47.xml?part=79&section=79.101`, retrieved 2026-11-02; the text moved there from 47 CFR 15.119, and both sections read
"[Reserved]" in the current eCFR after the analog rules were removed). Covered by it, and matching the code: the standard character set (20h-7Fh, with the nine
substitutions 2A, 5C, 5E, 5F, 60, 7B-7F; 7Fh solid block), the special characters (11h/19h 30h-3Fh; 39h transparent space), the mid-row codes (11h/19h 20h-2Fh), the
miscellaneous control codes (14h/1Ch 20h-2Fh: RCL, BS, AOF/AON reserved, DER, RU2-4, FON, RDC, TR, RTD, EDM, CR, ENM, EOC; 17h/1Fh 21h-23h tab offsets), the preamble address
code table (first bytes by row 11 / 12 / 12 / 13 / 13 / 14 / 14 / 15 / 15 / 10 / 13 / 13 / 14 / 14, second bytes 40h-5Fh and 60h-7Fh: colour, italics, indent 0-28 with underline bit),
the odd parity rule, the doubled control codes, the 32 x 15 grid, the roll-up base row default of 15, and the style rules (mid-row colour turns italics and flash off;
PACs do not alter attributes).

Found and fixed: (1) Flash On is a spacing attribute (now writes a space); (2) a first byte of 00h-0Fh is ignored alone and the second byte is used (field 1 only: on field 2 it
starts XDS); (3) first byte 10h takes only 40h-5Fh (the decoder had accepted 60h-7Fh as a row 11 PAC). The eCFR PAC table prints `68` for "Yellow Underline" on row 13, which
breaks the column's pattern (`6B`); we use `6B`.

**Not in 79.101, so resting on other evidence:**

* **Carriage.** ATSC A/53 Part 4:2009 (read 2026-11-02, section 6.2) confirms `user_data_identifier` "GA94" (0x47413934), `user_data_type_code` 0x03 for `MPEG_cc_data()` and the trailing
  `marker_bits` 0xFF; "DTG1" is AFD, not captions. It defines `cc_data()` only by reference to CEA-708 Table 2, and A/72 Part 1:2015 (section 7.4) defers to SCTE 128-1 section 8.1 for the
  SEI form; neither is available to us. So the `cc_data()` byte layout (flags byte with `process_cc_data_flag` in bit 6 and a 5-bit count, the `em_data` byte, triplets of
  marker/valid/type bits and two bytes) and the ITU-T T.35 header (0xB5, 0x0031; H.264/H.265 Annex D, free) rest on the oracles: ffmpeg and ccextractor read the SEI we write
  and we read the PixelTools files they read (`GA94` pairs equal ccextractor's raw output).
* The extended Western European sets (12h/1Ah and 13h/1Bh, 20h-3Fh) and the "extended character replaces the fallback before it" rule come from CEA-608-B/-E, which we do not have.
  They were checked against ffmpeg 7.0.2 and ccextractor 0.96.5 decoding an SCC that uses every one of the 64 codes plus the specials (`tools/run-caption-extended-check.sh`):
  the two oracles agree with the table on all but these, where they use approximations or disagree with each other: 12h 26 and 29 (ours ‘ ’; ffmpeg ´ ‘, ccextractor ASCII apostrophes),
  12h 2A and 2D (ours — •; ffmpeg - and ·, ccextractor agrees with us), 13h 37 (both oracles give ¦, so we do too; it was │), 13h 3C-3F (ours ┌ ┐ └ ┘ as ffmpeg; ccextractor
  gives corner marks). ccextractor also loses the last character of a row whose column 32 holds an extended character (we had the same defect and fixed it). The glyph choices
  for these codes are the open detail to settle against CEA-608-E.
* Field 2 control codes (15h/1Dh instead of 14h/1Ch), XDS (class/type, checksum) and Text mode details beyond TR/RTD: black-box oracle output and the CEA-608 prose in the
  ATSC A/53 Part 4 and A/72 documents, which describe only the carriage.
* Parity-failing pairs: 79.101(j) shows a solid block for a failed print character; we drop the pair (as ccextractor does on the samples).
* **CEA-708**: no free document beyond the A/53 / A/72 carriage (`cc_data`, DTVCC packet and service block framing, which we read from A/53 Part 4 §6.2.3 and A/72). The window,
  pen and command semantics (DF/CW/DSW/HDW/TGW/DLW/SPA/SPC, G0/G1/G2 mapping, C0 codes) rest on black-box oracle output only: ccextractor on the two 708 PixelTools files and ffmpeg for the
  608 side. Everything beyond what those two files exercise is untested and is listed below.

## Gaps against the standards

CTA-608-E/708-E were not available (they need an account). The tables are checked above; details to recheck against the CTA PDFs:
G0/G2 sets beyond what the samples exercise (708 `P16`, C2/C3 lengths, G3), 608 background-attribute and flash semantics, XDS packet types beyond program/network name,
roll-up window placement rules when a PAC changes the base row, the 708 delay commands and window-attribute effects, and the "implied roll" above.

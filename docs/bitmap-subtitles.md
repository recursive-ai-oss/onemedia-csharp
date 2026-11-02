# Bitmap subtitles

PGS (Blu-ray), DVD sub-pictures (VobSub) and DVB subtitles are pictures, not text. OneMedia decodes them to RGBA images with timing and
position and hands them out as image cues (`SubtitleCue.Images`, see `subtitles.md`). This is the one place the library produces pixels: a
run-length code becomes an image. It does no scaling, no text recognition and no rendering. Recognising the text is for an OCR library.

## What is read

| System | Where | Entry point |
|---|---|---|
| PGS | `.sup` files; Matroska `S_HDMV/PGS`; MPEG-TS/M2TS stream type 0x90 | `BitmapSubtitles.ReadSup`, `ReadFile`, `ReadTrack`; `PgsDecoder` |
| DVD sub-pictures | `.idx` + `.sub`; a bare `.sub` program stream; Matroska `S_VOBSUB`; VOB private stream 1 sub-streams 0x20-0x3F | `BitmapSubtitles.ReadVobSub`, `ReadFile`, `ReadTrack`; `VobSubReader`, `VobSubIndex`, `VobSubDecoder` |
| DVB subtitles | MPEG-TS PES streams (EN 300 743); Matroska `S_DVBSUB` | `BitmapSubtitles.ReadTrack`; `DvbSubtitleDecoder` |

`BitmapSubtitles.ReadTrack(file, track)` works on any track `CanDecode` accepts. It uses the packets the demuxer already produces, so
every container the library reads works the same way.

## The cue

One `SubtitleCue` per display change:

- `Start`, `End` as `MediaTime`. `End` is the start of the next display set or stop command; when nothing ends the cue it is the container
  block's duration, the DVB page time-out, or `MediaTime.Unknown`.
- `Images`: one `SubtitleImage` per PGS composition object, per DVB region, one per DVD sub-picture. `X`, `Y` are on the display of
  `DisplayWidth` x `DisplayHeight`: the PGS video size, the DVB display definition (720x576 without one), the DVD index `size:`
  (720x576 when unknown; override with `BitmapSubtitleOptions`). Pixels are 8-bit R, G, B, A with straight alpha.
- `Extras`: `system` (`pgs`, `vobsub`, `dvbsub`), `forced=true` for forced display, `stream` for a VobSub stream other than 0.
  `BitmapSubtitleCues.IsForced` reads it.
- `Language` from the track (or the idx `id:` line, or the option).

`SubtitleImagePng.Encode` writes an image as PNG, for looking at what came out (BCL `ZLibStream` only).

## Colour

PGS and DVB palettes are YCbCr with an alpha, converted with limited-range (16-235) matrices. PGS: ITU-R BT.709 for pictures taller than 576
lines and BT.601 otherwise, which is what ffmpeg's decoder does (checked on 480- and 1080-line streams: at most 1 level apart per channel).
DVB: ITU-R BT.601 always, because EN 300 743 (clause 7.2.4, note 2) defines Y, Cr and Cb that way; ffmpeg does the same on 1080-line DVB
streams. `BitmapSubtitleOptions.Matrix` overrides both. The alpha is the stream's own. A fully transparent entry keeps its
colour channels; consumers should ignore them where alpha is 0. The DVD palette is RGB in the index (a VOB has its palette in the IFO file,
which is not read: pass `BitmapSubtitleOptions.Palette`, else a grey ramp is used). Contrast 0-15 maps to alpha 0-255 (x17).

## Format notes

**PGS.** Segments PCS, WDS, PDS, ODS, END. A display set that shows objects opens a cue; the next display set closes it (a set with no objects
is the usual clear). Palette-only updates re-show the earlier objects with the new palette. Cropped objects are cut to the crop rectangle.
The forced flag of a composition object makes the cue forced. Windows are parsed past but not used to clip. Objects split over several ODS
segments are joined. PDS entries are (index, Y, Cr, Cb, alpha). An epoch start clears objects and palettes. More than 64 objects in an
epoch, objects with a zero dimension and runs past a line are defects.

**VobSub.** The unit header gives the size and the control table offset; each control sequence has a delay in units of 1024/90000 s.
`STA_DSP`/`FSTA_DSP` start (forced start marks the cue forced), `STP_DSP` stops. The picture is built from the colour, contrast, display
area and pixel address commands of the sequence that starts it. `CHG_COLCON` (colour changes inside the picture) is parsed past and
reported as an info defect, not applied. Even and odd lines come from the two fields; every line ends on a byte boundary. The idx lines
`size`, `org`, `palette`, `forced subs`, `id`/`index` and `timestamp`/`filepos` are read; `org`, `scale`, `alpha`, `smooth`, `fadein/out`,
`align`, `time offset` and `langidx` are reported or ignored, not applied. The index time stamps time the pictures, not the PES time stamps.

**DVB.** Page, region, CLUT, object and display definition segments; 2, 4 and 8-bit pixel codes with the map tables; default CLUTs of
clause 10; reduced-range CLUT entries expanded by shifting; page time-out. Regions are placed relative to the display window when the
display definition has one. Character objects, the progressive coding method and disparity segments are not decoded (an info defect for
the first two). The composition page is the first one the stream names (`CompositionPageId` overrides); data on pages that nothing
composes (the ancillary page) is shared.

## Limits and defects

Image sizes are checked before any pixel buffer exists: each side up to 16,384 and the area times four bytes against
`MediaLimits.MaxAllocationBytes`, together with the pictures already held. Run-length data only ever writes inside the declared size. Damage
is a typed defect with the byte offset (`pgs.*`, `vobsub.*`, `dvbsub.*` codes); the decoders keep going where they can. Only
`MediaLimitExceededException` can come out of a decoder for any input.

## Verification

`tools/run-bitmapsubs-diff.sh` writes crafted streams (`tools/OneMedia.BitmapSubsCheck gen`), has ffmpeg re-wrap them (Matroska copy,
M2TS, `dvbsub` in MPEG-TS, `dvdsub` in Matroska, VOB and as idx/sub) and compares our pictures with the RGBA frames ffmpeg renders on a
transparent canvas for the same file. Event times are compared after removing ffmpeg's timeline shift (it starts every subtitle file at 0),
and agree with `ffprobe -show_packets` times.

Crafted streams, ffmpeg 7.0.2 and mkvmerge 103.0 (3 events each):

| Input | Result |
|---|---|
| PGS `.sup`, Matroska (mkvmerge and ffmpeg), M2TS; SD and HD | 3/3 matched, at most 1 level per channel apart (rounding in the YCbCr to RGB step, ~1,600 to ~2,000 pixels) |
| DVD sub-pictures: Matroska, VOB (alpha and geometry only, the palette is in the IFO), idx/sub, mkvmerge `S_VOBSUB` | 3/3 pixel-identical |
| DVB in MPEG-TS | 3/3, at most 1 level apart (ffmpeg 8.0.1 only, see below) |

ffmpeg 7.0.2 (the static build) crashes on any MPEG-TS input on the test machine, so the DVB-in-TS rows were rendered with ffmpeg 8.0.1;
the M2TS PGS rows render the same stream copied to Matroska.

Published samples (samples.ffmpeg.org `sub/`, fetch-only, see `tools/corpus/manifest.tsv`):

| File | Events | Result |
|---|---|---|
| `PGS/supsample.mkv` (PGS, 1280x720) | 1 | pixel-identical |
| `largeres_vobsub.mkv` (VobSub, 1920x1080, first stream, first 60 s) | 3 | pixel-identical |
| `VOB/X.The.Movie...idx/.sub` | 685 | all event times equal ffprobe's; alpha and geometry identical in all 685; colours differ because this idx has no `palette:` line, so both decoders use their own fallback (ours a grey ramp) |
| `dvbsub/dvbsubtest.ts` (DVB, ffmpeg 8.0.1) | 36 | every visible pixel that both show is equal; 225,792 pixels are transparent for us and black for ffmpeg: CLUT entry 0 has Y = 0, which EN 300 743 defines as fully transparent whatever T says, and ffmpeg paints it opaque |
| `dvbsub/SubTitleHD.ts` (DVB, ffmpeg 8.0.1) | 8 | differs: the stream has no CLUT definition, so we use the default 16-entry CLUT of EN 300 743 clause 10.2 (entry 2 is opaque green); ffmpeg renders those pixels differently (alpha 36). Not settled by a second implementation |

Known gaps: the PGS cropping flag (taken as 0x80 in the composition object) is inferred, since ffmpeg ignores cropping and so cannot confirm it;
the published PGS sample has no cropped objects.

Fuzz: one target each for the PGS segment decoder, the sub-picture decoder, the DVB decoder and the idx parser, 300 s each on 4 cores:
4.05 M, 1.46 M, 8.40 M and 1.59 M executions, no crashes.

Set `COMPARE_DEBUG=1` or `COMPARE_CLASSES=1` for the comparison tool to print sample differing pixels or the transparent/visible split.

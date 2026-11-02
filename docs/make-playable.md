# Making a file playable

`Media.MakePlayable(input, output, profile)` turns a file that a browser profile cannot play into one it can, without decoding video, and says what it did or what stands in the way.
It acts on the verdict of `CanPlay` (`playability.md`): every blocker carries a remedy (`Remux`, `TranscodeAudio`, `VideoBlocker`, `None`), and this operation applies them.

## Goal

The output plays as a downloaded file (the *progressive* verdict) in the profile. Playing through a MediaSource needs fragmented MP4 or WebM, which is packaging, not this operation; a file
that only plays through MediaSource still gets a progressive-playable output. The result is judged by reopening the written file and asking `CanPlay` again; a file that still does not
play is deleted and reported as `Failed`.

## Decision

1. **Plays already.** Copied byte for byte (`AlreadyPlayable`, action `Copy`). When it is a plain MP4/MOV/3GP whose movie box follows the media data (warning
   `progressive.movie-at-end`), the movie box is moved to the front instead (`Faststart`, status `Made`); everything else is copied by range, as `Remuxer.Faststart`.
2. **A blocker no remux or audio transcode removes** (`VideoBlocker`: video codec, profile, level, bit depth, chroma format; `None`: encryption, no tracks) → `Refused`. Nothing is written; `Remaining` holds
   every such blocker with its code and track.
3. **Otherwise** a target container is chosen. Candidates, in order: MP4, WebM, Matroska, QuickTime, skipping those the profile does not accept for a downloaded file. Each candidate is judged *as if the same
   packets sat in it* (container name, sample entry form `avc1`/`hvc1` unless the source kept parameter sets in the stream, fast start). Two passes: first every track copied, then, if no candidate works,
   with audio tracks that still block replaced by a codec the transcoder can produce. The first candidate that leaves no blocker wins; the earlier ones are listed in `Notes`.
4. **Audio transcode is needed** and no `IAudioTranscoder` was given → `NeedsAudioTranscode`. Nothing is written; the report names the tracks (`AudioTranscodes`, from → to) and the container that would
   be used (`Target`). The core decodes nothing.
5. **Run.** The input is remuxed into the target (`remux.md`, faststart on); transcoded tracks come from a packet feed instead of the input's reader and are merged in decode-time order, so the output
   stays interleaved and memory stays flat. The remux's own report (losses, changes) is in `Remux`; tracks it left out appear as `DropTrack` actions.

Untouched tracks are remuxed packets: payload bytes copied, apart from the documented Annex B → length-prefixed and ADTS → raw reframing.

## Report

`MakePlayableReport`: `ProfileId`; `Before` and `After` (full `PlaybackVerdict`s); `Status` (`AlreadyPlayable`, `Made`, `NeedsAudioTranscode`, `Refused`, `Failed`); `Target`; `Actions` (`Copy`, `Faststart`,
`Remux`, `DropTrack`, `TranscodeAudio`, each with the track and a sentence); `AudioTranscodes` (track, from, to, bit rate, sample rate, channels, encoder delay); `Remux`; `Remaining` (blockers still
standing, with their codes); `Notes`. Refusals are results; exceptions are for unreadable input, an existing output and cancellation.

Codes added here, besides those in `playability.md`: `transcode.no-decoder` (no decoder for the source codec: AC-3, E-AC-3, ALAC, WMA), `transcode.channels` (more than two channels),
`transcode.no-target` (the profile plays nothing the transcoder produces in any candidate container; for example `generic-mp4`), `transcode.failed` (the decoder or encoder refused the stream),
`remux.refused` (the muxer would not carry a track).

## Audio transcode (`RecursiveAI.OneMedia.Audio`)

`AudioTranscoder` implements the core's `IAudioTranscoder`; `AudioTranscoder.MakePlayable(in, out, profile)` is the one-line form with it plugged in. The CLI uses it unless `--no-transcode`.

- **Decode:** OneAudio (AAC-LC, MP3, Vorbis, FLAC, Opus, AMR) through the same adapter as `Decode()`, or direct conversion for integer and float PCM (layout from the container reader or the sample entry code).
  HE-AAC with SBR/PS follows OneAudio's rules (it refuses what it cannot decode, reported as `transcode.failed`).
- **Encode:** Opus, 48 kHz, mono or stereo, 20 ms packets, OneAudio's Opus encoder (complexity 7, VBR). Default bit rate 64 kbit/s mono, 96 kbit/s stereo (`MakePlayableOptions.AudioBitRate`). Other rates are resampled to 48 kHz
  (polyphase). FLAC is not offered: the encoder writes a native stream, not packets. Opus is the one target; profiles that play neither Opus nor what the file has (`generic-mp4`) refuse.
- **Delay:** the encoder's look-ahead is the pre-skip, written in the `OpusHead` (so `dOps` and `CodecDelay`) and as the track's priming, which the ISO muxer states in the first edit-list entry. The packets
  start at the presentation time of the first decoded input sample (the first packet's time moved by the edit information, plus the input's declared priming that the decoder drops), so audio and video keep
  their alignment. The stream is padded with silence to cover the look-ahead, so the output audio can run up to one packet (20 ms) plus the look-ahead longer than the input.
- **Memory:** packets are made on request, one at a time, from a streaming decoder, resampler and encoder.

## Checked

`tools/playback/run-makeplayable.sh` runs MakePlayable over the playback matrix (or any directory, such as the sample picked by `pick-corpus-sample.sh`) for a profile, plays each written output in the
named headless browser, and requires: time advances, video frames decode, audio is heard where the input had audio, and the output kept the tracks it should have. `check-makeplayable.py` compares
untouched tracks' packets with the input's (ffprobe), transcoded tracks' duration and start time, and validates the structure with MP4Box and mkvalidator.


## Results

Playback matrix (75 files; Playwright 1.55: Chromium 140, Firefox 141, WebKit 26 on x64 Linux) and a corpus sample (48 files: MKV/WebM, TS, AVI, FLV, MOV, 3GP, MP4, Ogg, ASF, MPEG-PS; up to 40 MB each). Every written output is played in the browser of its profile.

| Profile (browser) | Input | Already playable | Made | Refused | Failed | Written outputs that failed to play |
|---|---|---|---|---|---|---|
| chromium | matrix | 22 | 13 | 40 | 0 | 0 of 35 observations |
| firefox | matrix | 43 | 20 | 12 | 0 | 0 of 63 |
| webkit-linux (WebKit) | matrix | 64 | 11 | 0 | 0 | 0 of 75 (after the PCM rule below) |
| chromium | corpus sample | 14 | 7 | 26 | 1 | 0 of 21 |
| firefox | corpus sample | 13 | 18 | 16 | 1 | 0 of 31 |
| webkit-linux | corpus sample | 28 | 10 | 9 | 1 | 1 of 38 |

- Every refusal is a video blocker (H.264, HEVC, MPEG-4 or WMV video where the profile lacks the decoder) or a missing audio decoder (`transcode.no-decoder`: WMA, AC-3, E-AC-3, ALAC), reported with its code.
- The one *Failed* per corpus run is an ASF file whose audio the decoder rejects part-way (`transcode.failed`); the partial output is deleted.
- The WebKit failure is `webm-vidyo4_720p_60fps.webm`, an unmodified copy (the profile says it plays; WebKit decoded one frame in the harness window). Not caused by MakePlayable; the same bytes were not played un-copied.
- The matrix found one profile error: WebKit (GStreamer) does not play PCM in MP4 (it does from WAV, AIFF, CAF and QuickTime). The `webkit-linux` rule for PCM now lists those containers, and the CAF PCM file goes to QuickTime.
- **Untouched tracks** (ffprobe 7.0.2 `-show_packets -show_data_hash`): 91 video and audio streams across the matrix and corpus outputs (47 + 44, of which 5 Annex B video streams compared by packet count, flags and times because the bytes are reframed by design); packet count, size, flags and SHA-256 equal, time stamps shifted by one constant per stream. 0 unexplained.
- **Transcoded tracks:** 12 matrix and 6 corpus tracks in range, 1 explained. Duration and start time within one frame of the input (AAC frame 21.3 ms). One out of range by the stream-level figure: `ts-aac-only.ts`, where ffprobe reports 3.84 s for the TS audio stream although the 189 AAC packets hold 4.032 s; decoding input and output with ffmpeg gives 193,536 and 193,608 samples (72 apart). Start times differ by the 6.5 ms pre-skip.
- **Validators:** MP4Box 26.07 `-info` and mkvalidator 0.6.0: no errors, except mkvalidator 0.6.0 rejecting AV1 in WebM (`ERR304`, "codec V_AV1 not supported for profile webm"; the validator predates AV1 in WebM, the file plays in Firefox).
- **Memory:** a 1.13 GB VP9 + AAC Matroska file (1,468 s) made playable in Chromium's profile (WebM + Opus transcode) in 13 s with a peak RSS of 92 MB.

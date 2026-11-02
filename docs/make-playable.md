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

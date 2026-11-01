# Audio adapter (`RecursiveAI.OneMedia.Audio`)

The adapter presents an audio track of any container OneMedia opens (ISO base media, Matroska/WebM) as a OneAudio
`IPacketSource`, registers the two formats with OneAudio as `IContainerFormat`, and offers `Decode()` on an opened file.
It depends on core and OneAudio only. It decodes nothing itself and has no second copy of any OneAudio code.

## Entry points

- `MediaFile.Decode(...)` (extension): the file's default audio track as OneAudio's `IAudioDecoder`. The decoder uses the
  file's own demuxer and does not close it.
- `MediaAudioExtensions.CreateRegistry()`: a OneAudio registry with the OneMedia containers first, then AAC, Opus, Vorbis, FLAC,
  AMR and MP3 codecs. `AddOneMedia()` adds just the containers; a registry opens the first container that matches, so adding
  them before `AddMp4()` makes OneMedia the MP4 reader.
- `MediaTrackPacketSource`: one track as a packet source, for hosts that drive a codec themselves.

## Track choice

The first audio track marked default, else the first audio track OneAudio has a decoder for. Tracks with codecs OneAudio lacks
(AC-3, E-AC-3, PCM, ALAC, protected tracks) raise `UnsupportedAudioFormatException` naming the codec.

## Mapping

| OneAudio member | From |
|---|---|
| `Codec` | `aac`, `opus`, `vorbis`, `flac`, `mp3`, `amr-nb`, `amr-wb` from the track's codec id |
| `TimeScale` | the track timescale (the sample rate when the timescale does not fit an int) |
| `CodecConfig` | AAC: the AudioSpecificConfig from `esds` or Matroska CodecPrivate, rebuilt from the sample entry when absent; AMR: the `damr` payload |
| header packets | Opus: `OpusHead` (from `dOps` or CodecPrivate) and a synthesized `OpusTags`; Vorbis: the three headers split from Xiph lacing; FLAC: the Ogg FLAC identification packet built from `dfLa` or CodecPrivate |
| `PrimingFrames` | ISO: leading empty edits and the first edit's start, in frames at the track rate, rounded down (the rule OneAudio's own MP4 reader applies). Matroska: CodecDelay for AAC. Opus reports none: the decoder takes the pre-skip from `OpusHead` |
| `LengthFrames` | ISO: the movie-timeline duration after the edit list. Matroska: unknown (-1): the container states a rounded duration only. Opus: the granule total |
| end of audio | Opus: from the MP4 edit list, or from Matroska DiscardPadding on the last block, as an end position on the last packet |
| `SeekToFrame` | the demuxer's keyframe seek to the last packet at or before the frame |
| MP3 | OneAudio reads MP3 from a byte stream only, so the track's packets are concatenated into one stream and handed to its MP3 reader |

## Limitations (`AudioSeamLimitations`)

Collected while packets are read, so a host can see what OneAudio's packet interface could not carry:

- `LeadingDiscardPadding`: a block asked to cut audio from its start; the audio is kept.
- `MidStreamDiscardPadding`: DiscardPadding on a block that is not the track's last. Opus receives it as an end position; other codecs keep the audio.
- `EndPaddingNotApplied`: the track ends with DiscardPadding for a codec whose OneAudio decoder takes no end position from a non-Ogg source (Vorbis). The last packet decodes in full.
- `SbrNotApplied` / `PsNotApplied`: the track is HE-AAC (SBR, and parametric stereo for HE-AACv2). OneAudio has no SBR or PS decoder, so the PCM is the core coder's: at the core sampling rate (typically half the signalled output rate), without the high band and, with PS, one channel instead of two. Set when the track is created, not while reading.

## Comparison with OneAudio's own paths (`audiodecode`)

Where OneAudio reads the same audio through its own reader, the adapter's samples are compared with it. Known, explained
differences (the adapter follows the container, the other side follows what its muxer or reader does):

- **Opus in Matroska**: ffmpeg's Ogg muxer drops the last block's DiscardPadding, so its twin is longer. The adapter honours it.
- **AAC in Matroska**: ffmpeg's copy to MP4 loses CodecDelay, so the twin decodes the priming. The adapter trims it.
- **Vorbis in Matroska**: Matroska stores no end position for Vorbis and Ogg trims the last page, so the adapter returns up to
  16 more frames.
- **MP3 in Matroska**: the adapter decodes every packet. A first packet holding the encoder's `Info` frame is dropped and the
  LAME delay and padding are trimmed by OneAudio's MP3 reader, as for an `.mp3` file; ffmpeg decodes the same track without
  that trim (`Info` frame as silence, delay kept) and cuts the final padding by duration. Aligned on that offset, the adapter
  and ffmpeg agree to float precision (largest difference 3e-6 over 17 M frames). The ffmpeg-written MP3 twin is not a usable
  reference: the muxer writes its own `Info` header with a skip of several frames, and both OneAudio and ffmpeg honour it.
- **First frames of a stream cut mid-bitstream** (`test3.mkv`): the first two granules differ between OneAudio's and ffmpeg's
  MP3 decoders, which conceal the missing bit reservoir differently; everything after is equal.

## Seam gaps for OneAudio

1. Per-packet discard padding, or a public Vorbis block-size helper, so mid-stream and Vorbis end padding can be expressed.
2. An MP3 codec over packets (today MP3 needs a byte stream).
3. Decoders and `CodecId`s for AC-3, E-AC-3, PCM variants and ALAC (typed `Unsupported` today).
4. The AAC frame-length probe.
5. HE-AAC (SBR and PS). OneAudio 1.4.0 refuses configurations that signal SBR or PS (typed exception) and decodes the core only when the signalling is in the stream and the configuration is plain AAC-LC. See "HE-AAC" below.
6. AMR samples that hold several frames (`damr` frames per sample above 1): `AmrStreamDecoder` takes one frame per packet and throws
   "frame type 7 needs 31 payload bytes, found 127" on a four-frame sample; OneAudio's own MP4 reader fails the same way.

## HE-AAC

What the file says and what the decode delivers differ for HE-AAC, so both are reported and never left to guesswork:

- **Core** (`RecursiveAI.OneMedia`): `MediaTrack.DescribeCodec()` returns `CodecParameters` with `SampleRate` (the output rate a full HE-AAC decoder produces), `CoreSampleRate`, `Channels` (output channels; two with PS), `SbrPresent` and `PsPresent` from the AudioSpecificConfig (ISO/IEC 14496-3 §1.6.2.1: explicit signalling with object type 5 or 29 first, or the backward-compatible extension after the core configuration), and the `mp4a.40.5` / `mp4a.40.29` codec string. A tolerance observed in files written by MP4Box: an explicit configuration cut one bit short of the GASpecificConfig's last flag still reads.
- **Implied SBR** (`SbrImplied`): the oldest convention signals SBR only inside the stream, leaving a plain AAC-LC configuration. The only hint outside the stream is a container that declares twice the configuration's rate; `SbrImplied` is set then, `SbrPresent` stays false, and it is a guess.
- **Decode** (`RecursiveAI.OneMedia.Audio`): `MediaFile.Decode(out AudioDecodeReport report)` and `MediaFile.DescribeDecode()` give `DecodedSampleRate` and `DecodedChannels` (what the PCM really is), `SignalledSampleRate` and `SignalledChannels` (what the file asks for, zero when equal), and `Limitations` with `SbrNotApplied` / `PsNotApplied`; `IsCoreOnly` is true when either is set. `MediaTrackPacketSource.SampleRate` is the decoded rate, so priming and lengths are counted in core frames. `Decode()` without the report still works for HE-AAC with implied SBR and returns core-only audio; its documentation says so.
- OneAudio 1.4.0 throws a typed `AudioFormatException` when the configuration itself signals SBR or PS; `DescribeDecode()` still answers for such a track.

The test files are in `testdata/heaac` (`tools/gen-heaac-testdata.sh`): plain AAC-LC frames from a synthetic sine, with MP4Box rewriting only the signalling, because no encoder available here writes real SBR.

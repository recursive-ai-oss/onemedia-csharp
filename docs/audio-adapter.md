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
| end of audio | Opus in MP4: the edit list, as an end position on the last packet. AAC/AMR in MP4: the movie timeline, as an end position |
| `DiscardEndFrames` | Matroska DiscardPadding of the block (RFC 9559 §5.1.3.5.7, nanoseconds of decoded output to drop from its end), on any block of the track: converted at the codec's output rate (Opus 48 kHz, others the track's rate), rounded to the nearest frame with halves away from zero. OneAudio decodes the whole packet and drops that many frames from the end, so codec state and overlap are unaffected. Applies to Opus, Vorbis, AAC, FLAC and AMR |
| `SeekToFrame` | the demuxer's keyframe seek to the last packet at or before the frame |
| MP3 | OneAudio reads MP3 from a byte stream only, so the track's packets are concatenated into one stream and handed to its MP3 reader |

## Limitations (`AudioSeamLimitations`)

Collected while packets are read, so a host can see what OneAudio's packet interface could not carry:

- `LeadingDiscardPadding`: a block asked to cut audio from its start (negative DiscardPadding); the audio is kept.
- `MidStreamDiscardPadding` and `EndPaddingNotApplied` are obsolete and never set: positive DiscardPadding is applied wherever it appears.
  MP3 in Matroska goes through OneAudio's byte-stream MP3 reader, which has no per-packet trims, so DiscardPadding on MP3 blocks is not
  applied (that reader trims by its own LAME data).
- `SbrNotApplied` / `PsNotApplied`: the track is HE-AAC (SBR, and parametric stereo for HE-AACv2). OneAudio has no SBR or PS decoder, so the PCM is the core coder's: at the core sampling rate (typically half the signalled output rate), without the high band and, with PS, one channel instead of two. Set when the track is created, not while reading.

## Comparison with OneAudio's own paths (`audiodecode`)

Where OneAudio reads the same audio through its own reader, the adapter's samples are compared with it. Known, explained
differences (the adapter follows the container, the other side follows what its muxer or reader does):

- **Opus in Matroska**: ffmpeg's Ogg muxer drops the last block's DiscardPadding, so its twin is longer. The adapter honours it.
- **AAC in Matroska**: ffmpeg's copy to MP4 loses CodecDelay, so the twin decodes the priming. The adapter trims it.
- **Vorbis in Matroska**: Ogg trims the last page by granule position and Matroska files from ffmpeg carry no DiscardPadding for
  Vorbis, so the adapter returns up to a block's worth of extra frames when there is none. When a block does carry DiscardPadding
  it is applied.
- **AAC in Matroska, CodecDelay**: ffmpeg decodes an AAC Matroska file without removing the declared CodecDelay; the adapter removes
  it, so it is shorter by the delay (1024 frames in ffmpeg-written files). DiscardPadding is applied by both and agrees.
- **MP3 in Matroska**: the adapter decodes every packet. A first packet holding the encoder's `Info` frame is dropped and the
  LAME delay and padding are trimmed by OneAudio's MP3 reader, as for an `.mp3` file; ffmpeg decodes the same track without
  that trim (`Info` frame as silence, delay kept) and cuts the final padding by duration. Aligned on that offset, the adapter
  and ffmpeg agree to float precision (largest difference 3e-6 over 17 M frames). The ffmpeg-written MP3 twin is not a usable
  reference: the muxer writes its own `Info` header with a skip of several frames, and both OneAudio and ffmpeg honour it.
- **First frames of a stream cut mid-bitstream** (`test3.mkv`): the first two granules differ between OneAudio's and ffmpeg's
  MP3 decoders, which conceal the missing bit reservoir differently; everything after is equal.

## Seam gaps for OneAudio

1. A public Vorbis block-size helper (the per-packet trim now covers end padding, but not a container that wants positions).
2. An MP3 codec over packets (today MP3 needs a byte stream), which would also let DiscardPadding reach MP3.
3. Decoders and `CodecId`s for AC-3, E-AC-3, PCM variants and ALAC (typed `Unsupported` today).
4. HE-AAC (SBR and PS). OneAudio refuses configurations that signal SBR or PS (typed exception) and decodes the core only when the signalling is in the stream and the configuration is plain AAC-LC. See "HE-AAC" below.

Closed by OneAudio 1.5.0: per-packet trim (`PacketView.DiscardEndFrames`), multi-frame AMR samples, and the AAC frame-length probe
(`AacFrameProbe`, built from the AudioSpecificConfig in `CodecConfig`; truncated-file reconstruction will call it).

## HE-AAC

What the file says and what the decode delivers differ for HE-AAC, so both are reported and never left to guesswork:

- **Core** (`RecursiveAI.OneMedia`): `MediaTrack.DescribeCodec()` returns `CodecParameters` with `SampleRate` (the output rate a full HE-AAC decoder produces), `CoreSampleRate`, `Channels` (output channels; two with PS), `SbrPresent` and `PsPresent` from the AudioSpecificConfig (ISO/IEC 14496-3 §1.6.2.1: explicit signalling with object type 5 or 29 first, or the backward-compatible extension after the core configuration), and the `mp4a.40.5` / `mp4a.40.29` codec string. A tolerance observed in files written by MP4Box: an explicit configuration cut one bit short of the GASpecificConfig's last flag still reads.
- **Container rate hint** (`ContainerDeclaresDoubleRate`, first published as `SbrImplied`, now obsolete): the oldest convention signals SBR only inside the stream, leaving a plain AAC-LC configuration. The only hint outside the stream is a container that declares twice the configuration's rate (Matroska `OutputSamplingFrequency`, an MP4 sample entry). The flag is set then, `SbrPresent` stays false and `SbrSignalling` is `ContainerRate`. It is a hint, never evidence: some files declare the doubled rate for a stream that carries no SBR (a Matroska test file's commentary track is plain AAC-LC at 22,050 Hz under a declared 44,100 Hz), and nothing outside the stream can tell the two apart.
- **Signalling** (`CodecParameters.SbrSignalling`, `AudioDecodeReport.Signalling`, `MediaTrackPacketSource.SbrSignalling`): `Explicit` (the configuration starts with audio object type 5 or 29), `BackwardCompatible` (the 0x2b7 extension after the core configuration), `ContainerRate` (the hint above) or `None`. Messages name it.
- **Decode** (`RecursiveAI.OneMedia.Audio`): `MediaFile.Decode(out AudioDecodeReport report)` and `MediaFile.DescribeDecode()` give `DecodedSampleRate` and `DecodedChannels` (what the PCM really is), `SignalledSampleRate` and `SignalledChannels` (what the file asks for, zero when equal), and `Limitations` with `SbrNotApplied` / `PsNotApplied`; `IsCoreOnly` is true when either is set. `MediaTrackPacketSource.SampleRate` is the decoded rate, so priming and lengths are counted in core frames. `Decode()` without the report still works for HE-AAC with implied SBR and returns core-only audio; its documentation says so.
- **"Possibly core-only"** (`AudioSeamLimitations.SbrPossiblyNotApplied`, `AudioDecodeReport.IsPossiblyCoreOnly`): a track whose only hint is the container's doubled rate (`SbrSignalling.ContainerRate`). The PCM is still the core coder's, at the core rate, but nothing proves audio is missing, so `IsCoreOnly` and `SbrNotApplied` stay false. They are set on evidence only: SBR or PS in the configuration, or OneAudio skipping SBR data, which `report.AfterReading(decoder)` folds into the report once the stream has been read.
- `decoder.SbrDataIgnored()` (extension) reads OneAudio's `AacDecoder.SbrDataIgnored`: true once SBR data in a plain-LC stream has been skipped. That is the only certain evidence for SBR signalled inside the stream, and it is known only after reading; before that, `ContainerDeclaresDoubleRate` and `IsCoreOnly` for a `ContainerRate` track are a guess from the declared rate.
- `Decode` throws `UnsupportedAudioFormatException` for every track whose configuration signals SBR or PS, explicitly or by the extension, and says which. OneAudio 1.5.0 reports some of them as an invalid configuration instead (MP4Box writes explicit signalling one bit short of the GASpecificConfig's last flag), so the adapter converts any OneAudio failure on such a track. The conversion follows a failure only, so a OneAudio that decodes SBR later is not blocked. `DescribeDecode()` still answers for such a track.

## Reading a file more than once

A `MediaFile` has one read position. The first read starts where `Open` left it. Every later read (`Packets`, `PacketsAsync`, `Decode`, in any order, also for another track) first returns to the start with a seek, so it needs a source that can seek; a file that cannot rewind throws `MediaReadStateException` (`CannotRewind`). That is the normal case for a file opened with `Media.Open(Stream)` from a stream that cannot seek: it opens when its format can be read front to back (Matroska/WebM, MP4 laid out for it, ASF, MPEG-TS and program streams, elementary streams), reads once, and refuses a second pass; a format that needs to seek throws `UnsupportedMediaException` naming it. A synchronous read (`Packets`, `Decode`, `DescribeDecode`) of a file opened with `OpenAsync` throws `MediaReadStateException` (`AsynchronousSource`); use `PacketsAsync`. A read that is still alive when a newer one starts is superseded and throws `MediaReadStateException` (`Superseded`) on its next call, so two readers never interleave and nothing returns silently empty. Seeking to a time before the start lands on the first sample (ISO tables start at decode time zero). Reading `media.Demuxer` directly is not tracked: a caller that does that owns the position.

The test files are in `testdata/heaac` (`tools/gen-heaac-testdata.sh`): plain AAC-LC frames from a synthetic sine, with MP4Box rewriting only the signalling, because no encoder available here writes real SBR.

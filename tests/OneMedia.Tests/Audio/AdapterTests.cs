using OneAudio;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Tests.Audio;

public class AdapterTests
{
    private static string Fixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio", name);

    private static string IsoFixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "isobmff", name);

    private static AudioCodecRegistry NativeRegistry() => new AudioCodecRegistry().AddOgg().AddVorbis().AddOpus().AddFlac().AddMp4().AddAac();

    private static (float[] Samples, AudioFormat Format) ReadAll(IAudioDecoder d)
    {
        using (d)
        {
            List<float> all = [];
            float[] buf = new float[4096 * d.Format.Channels];
            int got;
            while ((got = d.Read(buf)) > 0)
            {
                all.AddRange(buf.AsSpan(0, got * d.Format.Channels).ToArray());
            }

            return ([.. all], d.Format);
        }
    }

    [Fact]
    public void MediaOpenReadsTracksAndPackets()
    {
        using MediaFile media = Media.Open(IsoFixture("bframes_aac.mp4"));
        Assert.Equal(MediaFormatId.IsoBmff, media.Format);
        Assert.Equal(2, media.Tracks.Count);
        Assert.NotNull(media.Video);
        MediaTrack audio = media.Audio!;
        Assert.Equal(CodecId.Aac, audio.Codec);
        Assert.True(media.Duration > TimeSpan.Zero);
        int n = 0;
        foreach (OwnedPacket p in media.Packets(audio))
        {
            using (p)
            {
                Assert.Equal(audio.Index, p.Info.TrackIndex);
                n++;
            }
        }

        Assert.Equal(audio.PacketCount, n);
    }

    [Fact]
    public void MediaOpenFromMemoryRecognisesMatroska()
    {
        using MediaFile media = Media.Open(File.ReadAllBytes(Fixture("opus.webm")));
        Assert.Equal(MediaFormatId.Matroska, media.Format);
        Assert.Equal(CodecId.Opus, media.Audio!.Codec);
        Assert.Null(media.Video);
    }

    [Fact]
    public async Task MediaOpenAsyncReadsPackets()
    {
        await using MediaFile media = await Media.OpenAsync(Fixture("aac.mka"));
        int n = 0;
        await foreach (OwnedPacket p in media.PacketsAsync(media.Audio!))
        {
            using (p)
            {
                n++;
            }
        }

        Assert.True(n > 5);
    }

    [Theory]
    [InlineData("flac.mka", "flac.flac")]
    public void DecodesLikeTheNativePath(string file, string twin)
    {
        (float[] ours, AudioFormat fo) = ReadAll(AudioDecoder.OpenFile(Fixture(file), MediaAudioExtensions.CreateRegistry()));
        (float[] theirs, AudioFormat fn) = ReadAll(AudioDecoder.OpenFile(Fixture(twin), NativeRegistry()));
        Assert.Equal(fn.SampleRate, fo.SampleRate);
        Assert.Equal(fn.Channels, fo.Channels);
        Assert.Equal(theirs.Length, ours.Length);
        Assert.True(ours.AsSpan().SequenceEqual(theirs));
        Assert.Contains(ours, s => Math.Abs(s) > 0.05f);
    }

    [Theory]
    [InlineData("opus.mka")]
    [InlineData("opus.webm")]
    public void OpusHonoursDiscardPaddingThatTheOggTwinLost(string file)
    {
        (float[] ours, _) = ReadAll(AudioDecoder.OpenFile(Fixture(file), MediaAudioExtensions.CreateRegistry()));
        (float[] theirs, _) = ReadAll(AudioDecoder.OpenFile(Fixture("opus.ogg"), NativeRegistry()));
        // The Matroska file trims the final packet with DiscardPadding; ffmpeg's Ogg muxer does not carry that over, so the twin is longer.
        Assert.True(ours.Length < theirs.Length);
        Assert.True(ours.AsSpan().SequenceEqual(theirs.AsSpan(0, ours.Length)));
    }

    [Theory]
    [InlineData("opus.mka")]
    [InlineData("opus.webm")]
    public void OpusLengthIsBlockTimeMinusPreSkipAndDiscardPadding(string file)
    {
        // 21 packets of 960 frames, less the 312-frame pre-skip (CodecDelay) and 648 frames of DiscardPadding on the last block: 19,200 mono frames,
        // the length a reference decoder produces. The block times are in whole milliseconds, so the 6.5 ms delay must not leak into the end position.
        (float[] ours, AudioFormat f) = ReadAll(AudioDecoder.OpenFile(Fixture(file), MediaAudioExtensions.CreateRegistry()));
        Assert.Equal(1, f.Channels);
        Assert.Equal(19200, ours.Length);
    }

    // Fixtures written by tools/matroska-discard-padding.py with DiscardPadding on blocks in the middle of the track. The lengths are what ffmpeg 8.0
    // decodes the same files to (ffmpeg -i FILE -f f32le), which applies every block's padding: Opus 20160 - 312 pre-skip - (480 + 240 + 648) = 18480 mono
    // frames at 48 kHz; Vorbis 6656 - (16 + 40 + 50) = 6550; AAC 8192 - (64 + 16) = 8112. ffmpeg keeps the 1024-frame CodecDelay that the adapter drops (see AacDropsTheCodecDelay...), so the adapter's AAC figure is 8112 - 1024 = 7088.
    [Theory]
    [InlineData("opus-midpad.mka", 18480)]
    [InlineData("vorbis-midpad.mka", 6550)]
    [InlineData("aac-midpad.mka", 7088)]
    public void DiscardPaddingInTheMiddleOfATrackShortensTheDecode(string file, int frames)
    {
        (float[] ours, _) = ReadAll(AudioDecoder.OpenFile(Fixture(file), MediaAudioExtensions.CreateRegistry()));
        Assert.Equal(frames, ours.Length);
    }

    [Fact]
    public void OpusMidStreamDiscardRemovesExactlyThePaddedFramesAndNothingElse()
    {
        (float[] whole, _) = ReadAll(AudioDecoder.OpenFile(Fixture("opus.mka"), MediaAudioExtensions.CreateRegistry()));
        (float[] cut, _) = ReadAll(AudioDecoder.OpenFile(Fixture("opus-midpad.mka"), MediaAudioExtensions.CreateRegistry()));
        // Output frame 0 is packet 0's frame 312. Block 5 ends at 6 * 960 - 312 = 5448 and loses its last 480 frames; block 12 ends at 13 * 960 - 312
        // = 12168 and loses 240. Everything else, including the packets after the cut, is bit-identical because the codec still decodes whole packets.
        float[] expected = [.. whole.AsSpan(0, 5448 - 480), .. whole.AsSpan(5448, 12168 - 240 - 5448), .. whole.AsSpan(12168)];
        Assert.Equal(expected.Length, cut.Length);
        Assert.True(expected.AsSpan().SequenceEqual(cut));
    }

    [Fact]
    public void AdapterReportsOnlyWhatItCouldNotApply()
    {
        using MediaFile media = Media.Open(Fixture("opus-midpad.mka"));
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(media.Demuxer!, media.Audio!);
        while (source.TryReadPacket(out OneAudio.PacketView _))
        {
        }

        Assert.Equal(AudioSeamLimitations.None, source.Limitations);
    }

    [Fact]
    public void DiscardEndFramesCarriesThePaddingPerPacket()
    {
        using MediaFile media = Media.Open(Fixture("vorbis-midpad.mka"));
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(media.Demuxer!, media.Audio!);
        List<int> discards = [];
        while (source.TryReadPacket(out OneAudio.PacketView p))
        {
            if (!p.IsHeader)
            {
                discards.Add(p.DiscardEndFrames);
            }
        }

        Assert.Equal(14, discards.Count);
        Assert.Equal(16, discards[3]);
        Assert.Equal(40, discards[7]);
        Assert.Equal(50, discards[13]);
        Assert.Equal(106, discards.Sum());
    }

    [Fact]
    public void AacDropsTheCodecDelayTheMatroskaFileDeclares()
    {
        (float[] ours, _) = ReadAll(AudioDecoder.OpenFile(Fixture("aac.mka"), MediaAudioExtensions.CreateRegistry()));
        (float[] theirs, _) = ReadAll(AudioDecoder.OpenFile(Fixture("aac-twin.m4a"), NativeRegistry()));
        // The copy into MP4 lost the 1024-frame delay, so the twin decodes the priming frames that Matroska's CodecDelay says to drop.
        Assert.Equal(theirs.Length - 1024, ours.Length);
        Assert.True(ours.AsSpan().SequenceEqual(theirs.AsSpan(1024)));
    }

    [Fact]
    public void VorbisDecodesToTheNativeSamplesExceptTheFinalTrim()
    {
        (float[] ours, _) = ReadAll(AudioDecoder.OpenFile(Fixture("vorbis.mka"), MediaAudioExtensions.CreateRegistry()));
        (float[] theirs, _) = ReadAll(AudioDecoder.OpenFile(Fixture("vorbis.ogg"), NativeRegistry()));
        int common = Math.Min(ours.Length, theirs.Length);
        Assert.True(common > 4000);
        // Matroska stores no end position for Vorbis, so the last packet decodes in full while Ogg trims it (a few hundred frames at most).
        Assert.True(ours.Length >= theirs.Length);
        Assert.True(ours.Length - theirs.Length < 2048);
        Assert.True(ours.AsSpan(0, common - 1024).SequenceEqual(theirs.AsSpan(0, common - 1024)));
    }

    [Fact]
    public void DecodesAacInMp4LikeOneAudiosOwnReader()
    {
        AudioCodecRegistry ours = new AudioCodecRegistry().AddOneMedia().AddAac();
        AudioCodecRegistry native = new AudioCodecRegistry().AddMp4().AddAac();
        (float[] a, _) = ReadAll(AudioDecoder.OpenFile(IsoFixture("bframes_aac.mp4"), ours));
        (float[] b, _) = ReadAll(AudioDecoder.OpenFile(IsoFixture("bframes_aac.mp4"), native));
        Assert.Equal(b.Length, a.Length);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    // tools/gen-isobmff-testdata.sh: a fragmented AAC file whose movie box states 4096 ticks (a quarter second of 16 kHz) for a track that holds 33
    // packets in fragments. That figure is no length: the adapter used to stop the decode after it. ffmpeg 7.0.2 decodes all 33 * 1024 frames.
    [Fact]
    public void FragmentedFileWithStaleMovieDurationDecodesEveryFragment()
    {
        using MediaFile media = Media.Open(IsoFixture("frag_stale_duration.m4a"));
        Assert.True(media.Audio!.DurationIsPartial);
        (float[] samples, _) = ReadAll(AudioDecoder.OpenFile(IsoFixture("frag_stale_duration.m4a"), MediaAudioExtensions.CreateRegistry()));
        Assert.Equal(33 * 1024, samples.Length);
    }

    private static string HeAacFixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "heaac", name);

    // The fixtures hold plain AAC-LC frames at 22.05 kHz whose configuration or sample entry says HE-AAC, the way HE-AAC files do.
    // See tools/gen-heaac-testdata.sh.
    [Theory]
    [InlineData("sbr-compatible.m4a", false, "mp4a.40.5")]
    [InlineData("sbr-explicit.m4a", false, "mp4a.40.5")]
    [InlineData("sbrps-explicit.m4a", true, "mp4a.40.29")]
    public void CoreReportsTheSignalledOutputRateAndSbrAndPs(string file, bool ps, string rfc6381)
    {
        using MediaFile media = Media.Open(HeAacFixture(file));
        CodecParameters p = media.Audio!.DescribeCodec()!;
        Assert.True(p.SbrPresent);
        Assert.False(p.ContainerDeclaresDoubleRate);
        Assert.Equal(ps, p.PsPresent);
        Assert.Equal(44100, p.SampleRate);
        Assert.Equal(22050, p.CoreSampleRate);
        Assert.Equal(ps ? 2 : 1, p.Channels);
        Assert.Equal(rfc6381, p.Rfc6381);
    }

    [Fact]
    public void CoreFlagsAContainerRateTwiceTheConfigurationsWhenItShowsNoSbr()
    {
        using MediaFile media = Media.Open(HeAacFixture("sbr-implied.m4a"));
        Assert.Equal(44100, media.Audio!.Audio!.Value.SampleRate);
        CodecParameters p = media.Audio.DescribeCodec()!;
        Assert.False(p.SbrPresent);
        Assert.True(p.ContainerDeclaresDoubleRate);
        Assert.Equal(44100, p.SampleRate);
        Assert.Equal(22050, p.CoreSampleRate);
    }

    // AAC in MPEG-TS arrives as ADTS frames; the decoder is configured by the AudioSpecificConfig, so the adapter must hand it raw data blocks.
    [Theory]
    [InlineData("h264-aac.ts")]
    [InlineData("h264-aac.m2ts")]
    public void AacInTransportStreamDecodes(string file)
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "mpeg", file));
        (float[] samples, AudioFormat format) = ReadAll(media.Decode());
        Assert.True(samples.Length / format.Channels > 1024);
        Assert.Contains(samples, v => v != 0f);
    }

    // DescribeDecode asks the decoder, so it can not disagree with Decode: a container's doubled-rate hint with no SBR in the stream decodes at
    // the core rate (test5.mkv's Commentary track: 22,050 Hz under a declared 44,100 Hz), and the description says so too.
    [Theory]
    [InlineData("sbr-implied.m4a")]
    [InlineData("sbr-compatible.m4a")]
    [InlineData("sbr-explicit.m4a")]
    [InlineData("sbrps-explicit.m4a")]
    public void DescribeDecodeAgreesWithDecode(string file)
    {
        using MediaFile media = Media.Open(HeAacFixture(file));
        AudioDecodeReport described = media.DescribeDecode();
        using IAudioDecoder d = media.Decode(out AudioDecodeReport decoded);
        Assert.Equal(decoded, described);
        Assert.Equal(d.Format.SampleRate, described.DecodedSampleRate);
        Assert.Equal(d.Format.Channels, described.DecodedChannels);
    }

    [Fact]
    public void PlainAacIsNotSbr()
    {
        using MediaFile media = Media.Open(IsoFixture("bframes_aac.mp4"));
        CodecParameters p = media.Audio!.DescribeCodec()!;
        Assert.False(p.SbrPresent);
        Assert.False(p.ContainerDeclaresDoubleRate);
        Assert.False(p.PsPresent);
        using IAudioDecoder d = media.Decode(out AudioDecodeReport report);
        Assert.False(report.IsCoreOnly);
        Assert.Equal(AudioSeamLimitations.None, report.Limitations);
        Assert.Equal(0, report.SignalledSampleRate);
        Assert.Equal(media.Audio.Audio!.Value.SampleRate, report.DecodedSampleRate);
    }

    [Fact]
    public void ContainerRateHintWithNoSbrInTheStreamDecodesAtTheCoreRateAndTheReportSettlesIt()
    {
        using MediaFile media = Media.Open(HeAacFixture("sbr-implied.m4a"));
        (float[] samples, AudioFormat format) = ReadAll(media.Decode(out AudioDecodeReport report));
        // Nothing is core-only before reading: the container's doubled rate is a hint and OneAudio looks for SBR in the stream itself.
        Assert.False(report.IsCoreOnly);
        Assert.Equal(AudioSeamLimitations.None, report.Limitations);
        Assert.Equal(SbrSignalling.ContainerRate, report.Signalling);
        // The fixture's frames hold no SBR data, so the decoder outputs the core rate and the report says so, with the declared rate beside it.
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(22050, report.DecodedSampleRate);
        Assert.Equal(44100, report.SignalledSampleRate);
        Assert.False(report.SbrActive);
        using IAudioDecoder again = media.Decode();
        AudioDecodeReport settled = report.AfterReading(again);
        Assert.Equal(report, settled);
        // About half a second at the core rate, the same as ffmpeg decodes it to (12288 frames).
        Assert.Equal(12288, samples.Length / format.Channels);
    }

    // ffmpeg 7.0.2 decodes these to 44.1 kHz and 24576 frames per channel (the fixtures' frames are plain AAC-LC, so there is no real high band).
    [Theory]
    [InlineData("sbr-compatible.m4a", 1)]
    [InlineData("sbr-explicit.m4a", 1)]
    [InlineData("sbrps-explicit.m4a", 2)]
    public void SignalledHeAacDecodesAtTheOutputRate(string file, int channels)
    {
        using MediaFile media = Media.Open(HeAacFixture(file));
        (float[] samples, AudioFormat format) = ReadAll(media.Decode(out AudioDecodeReport report));
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(report.DecodedSampleRate, format.SampleRate);
        Assert.Equal(report.DecodedChannels, format.Channels);
        Assert.Equal(AudioSeamLimitations.None, report.Limitations);
        Assert.Equal(0, report.SignalledSampleRate);
        Assert.Equal(0, report.SignalledChannels);
        Assert.True(report.SbrActive);
        // The PS fixture holds a stereo core (plain AAC-LC frames), so the decoder runs SBR without parametric stereo even though the file
        // signals PS: the report says what ran, not what was signalled.
        Assert.False(report.PsActive);
        Assert.Equal(24576, samples.Length / format.Channels);
        Assert.Equal(channels, format.Channels);
        Assert.NotEqual(SbrSignalling.None, report.Signalling);
    }

    [Fact]
    public void DecodeOnAnOpenFileDoesNotCloseIt()
    {
        using MediaFile media = Media.Open(Fixture("flac.mka"));
        (float[] samples, _) = ReadAll(media.Decode());
        Assert.NotEmpty(samples);
        Assert.Equal(CodecId.Flac, media.Audio!.Codec);
    }

    [Fact]
    public void UnsupportedCodecsAreTypedResults()
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", "ac3.ac3"));
        Assert.Throws<UnsupportedAudioFormatException>(() => media.Decode());
    }

    [Fact]
    public void SeekToFrameLandsOnOrBeforeTheTarget()
    {
        using MediaFile media = Media.Open(IsoFixture("bframes_aac.mp4"));
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(media.Demuxer!, media.Audio!);
        source.SeekToFrame(8000, out long start);
        Assert.InRange(start, 8000 - 1024, 8000);
        Assert.True(source.TryReadPacket(out OneAudio.PacketView first));
        Assert.False(first.IsHeader);
    }

    // Frame counts are what ffmpeg 8.0.1 decodes from the same files (-f f32le): a LAME file is trimmed to its gapless length, the others are whole frames.
    [Theory]
    [InlineData("lame-info.mp3", 13230)]
    [InlineData("plain.mp3", 14976)]
    [InlineData("mpeg2-mono.mp3", 6615)]
    [InlineData("lc.aac", 14336)]
    [InlineData("nb.amr", 6400)]
    [InlineData("wb.awb", 12800)]
    public void ElementaryStreamsDecodeToTheLengthFfmpegGives(string name, int frames)
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", name));
        (float[] samples, AudioFormat format) = ReadAll(media.Decode());
        Assert.Equal(frames, samples.Length / format.Channels);
    }

    [Fact]
    public void PcmFilesAreOpenedByOneMediaButOneAudioHasNoPacketDecoderForThem()
    {
        // WAVE, AIFF and CAF files open and demux in OneMedia; OneAudio decodes WAVE with its own whole-file reader and has no PCM or ALAC
        // codec that takes packets, so the adapter refuses the track with a typed error instead of guessing.
        byte[] wav = new byte[44 + 400];
        "RIFF"u8.CopyTo(wav);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 8000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 16000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 400);
        using MemoryStream ms = new(wav);
        using MediaFile media = MediaFile.Open(new StreamMediaSource(ms, leaveOpen: true));
        Assert.Equal(CodecId.Pcm, media.Audio!.Codec);
        Assert.False(MediaTrackPacketSource.IsSupported(CodecId.Pcm));
        Assert.False(MediaTrackPacketSource.IsSupported(CodecId.Alac));
        Assert.Throws<global::OneAudio.UnsupportedAudioFormatException>(() => media.Decode());
    }
}

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
        Assert.False(p.SbrImplied);
        Assert.Equal(ps, p.PsPresent);
        Assert.Equal(44100, p.SampleRate);
        Assert.Equal(22050, p.CoreSampleRate);
        Assert.Equal(ps ? 2 : 1, p.Channels);
        Assert.Equal(rfc6381, p.Rfc6381);
    }

    [Fact]
    public void CoreSeesSbrImpliedByTheDeclaredRateWhenTheConfigurationShowsNone()
    {
        using MediaFile media = Media.Open(HeAacFixture("sbr-implied.m4a"));
        Assert.Equal(44100, media.Audio!.Audio!.Value.SampleRate);
        CodecParameters p = media.Audio.DescribeCodec()!;
        Assert.False(p.SbrPresent);
        Assert.True(p.SbrImplied);
        Assert.Equal(44100, p.SampleRate);
        Assert.Equal(22050, p.CoreSampleRate);
    }

    [Fact]
    public void PlainAacIsNotSbr()
    {
        using MediaFile media = Media.Open(IsoFixture("bframes_aac.mp4"));
        CodecParameters p = media.Audio!.DescribeCodec()!;
        Assert.False(p.SbrPresent);
        Assert.False(p.SbrImplied);
        Assert.False(p.PsPresent);
        using IAudioDecoder d = media.Decode(out AudioDecodeReport report);
        Assert.False(report.IsCoreOnly);
        Assert.Equal(AudioSeamLimitations.None, report.Limitations);
        Assert.Equal(0, report.SignalledSampleRate);
        Assert.Equal(media.Audio.Audio!.Value.SampleRate, report.DecodedSampleRate);
    }

    [Fact]
    public void ImpliedHeAacDecodesAsTheCoreAndSaysSo()
    {
        using MediaFile media = Media.Open(HeAacFixture("sbr-implied.m4a"));
        (float[] samples, AudioFormat format) = ReadAll(media.Decode(out AudioDecodeReport report));
        Assert.True(report.IsCoreOnly);
        Assert.True(report.Limitations.HasFlag(AudioSeamLimitations.SbrNotApplied));
        Assert.False(report.Limitations.HasFlag(AudioSeamLimitations.PsNotApplied));
        Assert.Equal(44100, report.SignalledSampleRate);
        // The PCM really is at the core rate, and the report says so.
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(22050, report.DecodedSampleRate);
        Assert.Equal(format.Channels, report.DecodedChannels);
        Assert.Equal(0, report.SignalledChannels);
        // The fixture's frames hold no SBR data, so the stream itself never confirms the guess.
        IAudioDecoder again = media.Decode();
        using (again)
        {
            Assert.False(again.SbrDataIgnored());
        }

        // About half a second at the core rate; at the signalled rate it would be twice as many frames.
        Assert.InRange(samples.Length / format.Channels, 10000, 12500);
    }

    [Theory]
    [InlineData("sbr-compatible.m4a", false)]
    [InlineData("sbr-explicit.m4a", false)]
    [InlineData("sbrps-explicit.m4a", true)]
    public void SignalledHeAacIsRefusedByOneAudioButStillReported(string file, bool ps)
    {
        using MediaFile media = Media.Open(HeAacFixture(file));
        // OneAudio 1.4.0 refuses configurations that signal SBR or PS with a typed exception rather than decoding the core (the explicit
        // fixtures also end one bit early, as MP4Box wrote them, which its stricter parser reports as an invalid configuration).
        Assert.ThrowsAny<AudioFormatException>(() => media.Decode(out _));
        AudioDecodeReport report = media.DescribeDecode();
        Assert.True(report.IsCoreOnly);
        Assert.Equal(ps, report.Limitations.HasFlag(AudioSeamLimitations.PsNotApplied));
        Assert.Equal(44100, report.SignalledSampleRate);
        Assert.Equal(22050, report.DecodedSampleRate);
        Assert.Equal(1, report.DecodedChannels);
        Assert.Equal(ps ? 2 : 0, report.SignalledChannels);
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
}

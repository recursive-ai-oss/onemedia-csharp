using OneAudio;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Tests.Audio;

// A second read of an opened file starts again from the beginning, and never returns silence: where it cannot, it throws.
public class RereadTests
{
    private static string Path(string dir, string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

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

    private static List<byte[]> PacketBytes(MediaFile media, MediaTrack track)
    {
        List<byte[]> all = [];
        foreach (OwnedPacket p in media.Packets(track))
        {
            using (p)
            {
                all.Add(p.Data.ToArray());
            }
        }

        return all;
    }

    private static void AssertSame(List<byte[]> a, List<byte[]> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(a[i].AsSpan().SequenceEqual(b[i]), $"packet {i} differs");
        }
    }

    [Theory]
    [InlineData("isobmff", "bframes_aac.mp4")]
    [InlineData("audio", "aac.mka")]
    [InlineData("audio", "opus.webm")]
    public void DecodeAfterPacketsGivesTheSameAudioAsAFreshFile(string dir, string file)
    {
        (float[] expected, _) = ReadAll(Media.Open(Path(dir, file)).Decode());

        using MediaFile media = Media.Open(Path(dir, file));
        int n = media.Packets(media.Audio!).Count();
        Assert.True(n > 0);
        (float[] samples, _) = ReadAll(media.Decode());
        Assert.NotEmpty(samples);
        Assert.True(expected.AsSpan().SequenceEqual(samples));
    }

    [Theory]
    [InlineData("isobmff", "bframes_aac.mp4")]
    [InlineData("audio", "aac.mka")]
    public void PacketsAfterDecodeAndDecodeAfterDecodeStillWork(string dir, string file)
    {
        using MediaFile media = Media.Open(Path(dir, file));
        (float[] first, _) = ReadAll(media.Decode());
        (float[] second, _) = ReadAll(media.Decode());
        Assert.NotEmpty(first);
        Assert.True(first.AsSpan().SequenceEqual(second));

        using MediaFile fresh = Media.Open(Path(dir, file));
        AssertSame(PacketBytes(fresh, fresh.Audio!), PacketBytes(media, media.Audio!));
    }

    [Theory]
    [InlineData("isobmff", "bframes_aac.mp4")]
    [InlineData("isobmff", "frag.mp4")]
    [InlineData("isobmff", "video.3gp")]
    [InlineData("audio", "aac.mka")]
    [InlineData("audio", "vorbis.mka")]
    public void TwoPacketsPassesGiveTheSamePackets(string dir, string file)
    {
        using MediaFile media = Media.Open(Path(dir, file));
        MediaTrack track = media.Tracks[0];
        List<byte[]> a = PacketBytes(media, track);
        Assert.NotEmpty(a);
        AssertSame(a, PacketBytes(media, track));

        // A pass that stopped early does not leave the next one short.
        using (IEnumerator<OwnedPacket> e = media.Packets(track).GetEnumerator())
        {
            Assert.True(e.MoveNext());
            e.Current.Dispose();
        }

        AssertSame(a, PacketBytes(media, track));
    }

    [Fact]
    public void EnumeratingTheSameEnumerableTwiceWorks()
    {
        using MediaFile media = Media.Open(Path("isobmff", "bframes_aac.mp4"));
        IEnumerable<OwnedPacket> packets = media.Packets(media.Audio!);
        int a = 0, b = 0;
        foreach (OwnedPacket p in packets) { using (p) { a++; } }
        foreach (OwnedPacket p in packets) { using (p) { b++; } }
        Assert.True(a > 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task AsyncPacketsAfterSyncPacketsStartAgain()
    {
        using MediaFile media = Media.Open(Path("isobmff", "bframes_aac.mp4"));
        int a = media.Packets(media.Audio!).Select(p => { p.Dispose(); return 0; }).Count();
        int b = 0;
        await foreach (OwnedPacket p in media.PacketsAsync(media.Audio!))
        {
            p.Dispose();
            b++;
        }

        Assert.True(a > 0);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("two-audio.mp4")]
    [InlineData("two-audio.mkv")]
    public void DecodingOneTrackAndThenAnotherGivesBoth(string file)
    {
        string path = Path("audio", file);
        using MediaFile media = Media.Open(path);
        List<MediaTrack> audio = [.. media.Tracks.Where(t => t.Kind == TrackKind.Audio)];
        Assert.True(audio.Count >= 2);
        using MediaFile fresh = Media.Open(path);
        for (int i = 0; i < audio.Count; i++)
        {
            (float[] expected, _) = ReadAll(fresh.Decode(audio[i]));
            (float[] got, _) = ReadAll(media.Decode(audio[i]));
            Assert.NotEmpty(got);
            Assert.True(expected.AsSpan().SequenceEqual(got), $"track {i}");
        }

        // And back to the first.
        (float[] again, _) = ReadAll(media.Decode(audio[0]));
        (float[] firstFresh, _) = ReadAll(fresh.Decode(audio[0]));
        Assert.True(firstFresh.AsSpan().SequenceEqual(again));
    }

    [Fact]
    public void AReaderThatWasSupersededThrowsInsteadOfReturningNothing()
    {
        using MediaFile media = Media.Open(Path("isobmff", "bframes_aac.mp4"));
        using IEnumerator<OwnedPacket> old = media.Packets(media.Audio!).GetEnumerator();
        Assert.True(old.MoveNext());
        old.Current.Dispose();

        IAudioDecoder decoder = media.Decode();
        MediaReadStateException e = Assert.Throws<MediaReadStateException>(() => old.MoveNext());
        Assert.Equal(MediaReadStateReason.Superseded, e.Reason);

        // The same for a decoder overtaken by a later pass.
        using (decoder)
        {
            Assert.True(media.Packets(media.Audio!).Any());
            float[] buf = new float[4096 * decoder.Format.Channels];
            // OneAudio 1.6.0 reads the first access units when it opens an AAC decoder (to look for SBR) and replays them, so reading
            // alone may never reach the source; a seek does.
            Assert.Throws<MediaReadStateException>(() =>
            {
                decoder.Seek(0);
                decoder.Read(buf);
            });
        }
    }

    // A demuxer that cannot seek, standing in for a forward-only source.
    private sealed class NoSeekFormat(IMediaFormat inner) : IMediaFormat
    {
        public MediaFormatId Id => inner.Id;

        public string DisplayName => inner.DisplayName;

        public SniffResult Sniff(ReadOnlySpan<byte> head) => inner.Sniff(head);

        public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
            new NoSeekDemuxer(inner.Open(source, options, cancellationToken));
    }

    private sealed class NoSeekDemuxer(IDemuxer inner) : IDemuxer
    {
        public MediaFormatId Format => inner.Format;

        public IReadOnlyList<MediaTrack> Tracks => inner.Tracks;

        public MediaTime Duration => inner.Duration;

        public DefectLog Defects => inner.Defects;

        public bool CanSeek => false;

        public void SetTrackEnabled(int trackIndex, bool enabled) => inner.SetTrackEnabled(trackIndex, enabled);

        public bool TryReadPacket(out PacketView packet) => inner.TryReadPacket(out packet);

        public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) => throw new NotSupportedException();

        public void Dispose() => inner.Dispose();
    }

    private static MediaFile OpenWithoutSeek(string path)
    {
        IMediaFormat iso = MediaFormats.Default.Formats.OfType<IMediaFormat>().First(f => f.Id == MediaFormatId.IsoBmff);
        MediaFormatRegistry registry = new();
        registry.Add(new NoSeekFormat(iso));
        return MediaFile.Open(FileMediaSource.Open(path), registry);
    }

    [Fact]
    public void ASecondReadOfAFileThatCannotSeekThrows()
    {
        string path = Path("isobmff", "bframes_aac.mp4");
        using (MediaFile media = OpenWithoutSeek(path))
        {
            Assert.True(PacketBytes(media, media.Audio!).Count > 0);
            MediaReadStateException e = Assert.Throws<MediaReadStateException>(() => media.Packets(media.Audio!).Count());
            Assert.Equal(MediaReadStateReason.CannotRewind, e.Reason);
            Assert.Equal(MediaReadStateReason.CannotRewind, Assert.Throws<MediaReadStateException>(() => media.Decode()).Reason);
        }

        // The first read, whichever kind, works.
        using MediaFile once = OpenWithoutSeek(path);
        (float[] samples, _) = ReadAll(once.Decode());
        Assert.NotEmpty(samples);
        Assert.Throws<MediaReadStateException>(() => once.Packets(once.Audio!).Count());
    }

    // OneAudio 1.6.0 decodes signalled HE-AAC; this library reports how the file signals it.
    [Theory]
    [InlineData("sbr-compatible.m4a", SbrSignalling.BackwardCompatible)]
    [InlineData("sbr-explicit.m4a", SbrSignalling.Explicit)]
    [InlineData("sbrps-explicit.m4a", SbrSignalling.Explicit)]
    public void SignallingIsReportedForDecodableHeAac(string file, SbrSignalling signalling)
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "heaac", file));
        using IAudioDecoder d = media.Decode(out AudioDecodeReport decoded);
        Assert.Equal(signalling, decoded.Signalling);
        AudioDecodeReport report = media.DescribeDecode();
        Assert.Equal(signalling, report.Signalling);
        Assert.Equal(report.Signalling, media.Audio!.DescribeCodec()!.SbrSignalling);
    }

    [Fact]
    public void ContainerRateAloneIsReportedAsAHintNotAsSignalling()
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "heaac", "sbr-implied.m4a"));
        CodecParameters p = media.Audio!.DescribeCodec()!;
        Assert.True(p.ContainerDeclaresDoubleRate);
        Assert.False(p.SbrPresent);
        Assert.Equal(SbrSignalling.ContainerRate, p.SbrSignalling);
        using IAudioDecoder d = media.Decode(out AudioDecodeReport report);
        Assert.Equal(SbrSignalling.ContainerRate, report.Signalling);
    }
}

public class PcmRereadTests
{
    [Theory]
    [InlineData("bwf-v0.wav")]
    [InlineData("aifc-sowt.aifc")]
    [InlineData("caf-info-chan.caf")]
    public void PacketsTwiceThenDecodeGivesTheTypedRefusalForPcm(string file)
    {
        using MediaFile media = Media.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "pcm", file));
        MediaTrack track = media.Audio!;
        List<byte[]> first = [];
        foreach (OwnedPacket p in media.Packets(track))
        {
            using (p)
            {
                first.Add(p.Data.ToArray());
            }
        }

        List<byte[]> second = [];
        foreach (OwnedPacket p in media.Packets(track))
        {
            using (p)
            {
                second.Add(p.Data.ToArray());
            }
        }

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.True(first[i].AsSpan().SequenceEqual(second[i]), $"packet {i} differs");
        }

        // OneAudio has no packet decoder for PCM: the refusal is typed, not silence.
        Assert.Throws<UnsupportedAudioFormatException>(() => media.Decode());
    }
}

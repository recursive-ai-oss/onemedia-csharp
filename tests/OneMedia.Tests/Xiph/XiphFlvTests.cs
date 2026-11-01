using RecursiveAI.OneMedia.Flac;
using RecursiveAI.OneMedia.Flv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Ogg;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Ogg, native FLAC and FLV: small files written by ffmpeg (tools/xiph-flv/gen-testdata.sh), then damaged and re-served in other ways.</summary>
public class XiphFlvTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "xiph-flv", name);

    private static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static List<(PacketInfo Info, string Hash)> ReadAll(IDemuxer d)
    {
        List<(PacketInfo, string)> list = [];
        while (d.TryReadPacket(out PacketView p))
        {
            list.Add((p.Info, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p.Data))));
        }

        return list;
    }

    private static IDemuxer OpenSync(string name, byte[] data) => name.EndsWith(".flac", StringComparison.Ordinal) ? FlacDemuxer.Open(new MemoryMediaSource(data))
        : name.EndsWith(".flv", StringComparison.Ordinal) ? FlvDemuxer.Open(new MemoryMediaSource(data)) : OggDemuxer.Open(new MemoryMediaSource(data));

    private static IDemuxer OpenForward(string name, byte[] data) => name.EndsWith(".flac", StringComparison.Ordinal) ? FlacDemuxer.Open(new StreamForwardSource(new NonSeekable(data)))
        : name.EndsWith(".flv", StringComparison.Ordinal) ? FlvDemuxer.Open(new StreamForwardSource(new NonSeekable(data))) : OggDemuxer.Open(new StreamForwardSource(new NonSeekable(data)));

    private static async Task<List<(PacketInfo Info, string Hash)>> ReadAllAsync(string name, byte[] data)
    {
        IMediaFormat format = name.EndsWith(".flac", StringComparison.Ordinal) ? new FlacFormat() : name.EndsWith(".flv", StringComparison.Ordinal) ? new FlvFormat() : new OggFormat();
        await using IAsyncDemuxer d = await format.OpenAsync(new MemoryMediaSource(data), MediaOpenOptions.Default);
        List<(PacketInfo, string)> list = [];
        while (await d.ReadPacketAsync())
        {
            list.Add((d.Current.Info, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d.Current.Data))));
        }

        return list;
    }

    public static TheoryData<string> Files() => new() { "opus.ogg", "vorbis.ogg", "tags.ogg", "tags.opus", "tags.flac", "h264-aac.flv", "flv1-mp3.flv", "vorbis-6ch.ogg", "theora.ogv" };

    private static string FileFor(string name) => name is "opus.ogg" or "vorbis.ogg" ? System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio", name) : Path(name);

    [Theory]
    [MemberData(nameof(Files))]
    public async Task SeekableForwardAndAsyncSourcesGiveTheSamePackets(string name)
    {
        byte[] data = File.ReadAllBytes(FileFor(name));
        List<(PacketInfo Info, string Hash)> a;
        using (IDemuxer d = OpenSync(name, data))
        {
            a = ReadAll(d);
            Assert.NotEmpty(d.Tracks);
        }

        Assert.NotEmpty(a);
        using (IDemuxer f = OpenForward(name, data))
        {
            Assert.Equal(a, ReadAll(f));
            Assert.False(f.CanSeek);
        }

        Assert.Equal(a, await ReadAllAsync(name, data));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TruncationAndBitFlipsNeverThrowAnythingUntyped(string name)
    {
        byte[] data = File.ReadAllBytes(FileFor(name));
        for (int cut = 0; cut < data.Length; cut += Math.Max(1, data.Length / 60))
        {
            Probe(name, data.AsSpan(0, cut).ToArray());
        }

        Random rng = new(1234);
        for (int i = 0; i < 60; i++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int k = 0; k < 4; k++)
            {
                copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
            }

            Probe(name, copy);
        }
    }

    private static void Probe(string name, byte[] data)
    {
        try
        {
            using IDemuxer d = OpenSync(name, data);
            int n = 0;
            while (d.TryReadPacket(out _) && n++ < 100_000)
            {
            }

            if (d is IMetadataProvider m)
            {
                _ = m.ReadMetadata().NativeTags;
            }
        }
        catch (MediaException)
        {
        }
    }

    [Fact]
    public void OggOpusIsOneTrackWithPreSkipAndGranuleDuration()
    {
        using OggDemuxer d = OggDemuxer.Open(FileMediaSource.Open(FileFor("opus.ogg")));
        MediaTrack t = Assert.Single(d.Tracks);
        Assert.Equal(TrackKind.Audio, t.Kind);
        Assert.Equal("opus", t.Codec.Name);
        Assert.Equal(48000, t.Timescale);
        Assert.True(d.Duration.IsKnown);
        MediaTime previous = MediaTime.Unknown;
        while (d.TryReadPacket(out PacketView p))
        {
            if (previous.IsKnown)
            {
                Assert.True(p.Info.Pts.CompareTo(previous) > 0);
            }

            previous = p.Info.Pts;
        }
    }

    [Fact]
    public void VorbisSetupWithOrderedCodebooksGivesPacketDurations()
    {
        // A six-channel stream written by libvorbis has ordered codebooks whose code lengths skip values.
        using OggDemuxer d = OggDemuxer.Open(new MemoryMediaSource(Bytes("vorbis-6ch.ogg")));
        Assert.DoesNotContain(d.Defects, x => x.Code == "ogg.vorbis-setup");
        MediaTime end = MediaTime.Zero;
        int n = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            Assert.True(p.Info.Duration.Ticks > 0);
            end = p.Info.Pts + p.Info.Duration;
            n++;
        }

        Assert.True(n > 10);
        Assert.Equal(0.4, end.Ticks / (double)end.Timescale, 2);
    }

    [Fact]
    public void TheoraFramesAreNumberedFromZero()
    {
        using OggDemuxer d = OggDemuxer.Open(new MemoryMediaSource(Bytes("theora.ogv")));
        MediaTrack t = Assert.Single(d.Tracks);
        Assert.Equal("theora", t.Codec.Name);
        long expected = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            Assert.Equal(expected++, p.Info.Pts.Ticks / t.Video!.Value.FrameRateDenominator);
        }

        Assert.Equal(15, expected);
    }

    [Fact]
    public void ACorruptedPageIsReportedAndReadingContinues()
    {
        byte[] data = Bytes("tags.ogg");
        int last = data.AsSpan().LastIndexOf("OggS"u8);
        data[^3] ^= 0x55;
        using OggDemuxer d = OggDemuxer.Open(new MemoryMediaSource(data));
        _ = ReadAll(d);
        Assert.Contains(d.Defects, x => x.Kind == DefectKind.ChecksumMismatch && x.Offset >= last);
    }

    [Fact]
    public void VorbisCommentsMapToTheCommonView()
    {
        foreach (string name in new[] { "tags.ogg", "tags.opus", "tags.flac" })
        {
            using IDemuxer d = OpenSync(name, Bytes(name));
            MediaMetadata m = ((IMetadataProvider)d).ReadMetadata();
            Assert.Equal("Tagged Title", m.Tags.Title);
            Assert.Equal("Some Artist", m.Tags.Artist);
            Assert.Equal("An Album", m.Tags.Album);
            Assert.Contains(m.NativeTags, t => t.System == TagSystems.VorbisComment && t.Key.ToUpperInvariant() is "TRACKNUMBER" or "TRACK");
            Assert.NotNull(m.Tags.Date);
        }
    }

    [Fact]
    public void FlacReportsBlocksFramesAndSeeks()
    {
        using FlacDemuxer d = FlacDemuxer.Open(new MemoryMediaSource(Bytes("tags.flac")));
        Assert.Equal(0, d.MetadataBlocks[0].TypeCode);
        Assert.True(d.StreamInfo.TotalSamples > 0);
        long samples = 0;
        int frames = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            Assert.Equal(samples, p.Info.Pts.Ticks);
            samples += p.Info.Duration.Ticks;
            frames++;
        }

        Assert.Equal(d.StreamInfo.TotalSamples, samples);
        Assert.DoesNotContain(d.Defects, x => x.Severity >= DefectSeverity.Warning);
        MediaTime landed = d.Seek(new MediaTime(samples / 2, d.StreamInfo.SampleRate));
        Assert.True(landed.Ticks <= samples / 2);
        Assert.True(d.TryReadPacket(out PacketView after));
        Assert.Equal(landed.Ticks, after.Info.Pts.Ticks);
        Assert.True(frames > 2);
    }

    [Fact]
    public void AnId3PrefixInFrontOfFlacIsSkippedAndKeptAsABinaryTag()
    {
        byte[] flac = Bytes("tags.flac");
        byte[] id3 = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 20, .. new byte[20]];
        byte[] data = [.. id3, .. flac];
        Assert.Equal(SniffConfidence.Certain, new FlacFormat().Sniff(data.AsSpan(0, 64)).Confidence);
        using IDemuxer plain = OpenSync("a.flac", flac);
        using IDemuxer prefixed = OpenSync("a.flac", data);
        Assert.Equal(ReadAll(plain).Select(x => x.Hash), ReadAll(prefixed).Select(x => x.Hash));
        MediaMetadata m = ((IMetadataProvider)prefixed).ReadMetadata();
        NativeTag raw = Assert.Single(m.NativeTags, t => t.System == RawId3Prefix.SystemName);
        Assert.Equal(30, raw.ReadValue().Length);
        Assert.Equal("Tagged Title", m.Tags.Title);
    }

    [Fact]
    public void FlvTracksCarryConfigurationAndCompositionTimes()
    {
        using FlvDemuxer d = FlvDemuxer.Open(new MemoryMediaSource(Bytes("h264-aac.flv")));
        Assert.Equal(2, d.Tracks.Count);
        MediaTrack v = d.Tracks.Single(t => t.Kind == TrackKind.Video);
        MediaTrack a = d.Tracks.Single(t => t.Kind == TrackKind.Audio);
        Assert.Equal("h264", v.Codec.Name);
        Assert.False(v.Configuration.Data.IsEmpty);
        Assert.Equal(160, v.Video!.Value.Width);
        Assert.Equal("aac", a.Codec.Name);
        Assert.False(a.Configuration.Data.IsEmpty);
        int reordered = 0;
        int keys = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            if (p.Info.TrackIndex == v.Index)
            {
                keys += p.Info.IsKeyframe ? 1 : 0;
                reordered += p.Info.Pts.CompareTo(p.Info.Dts) != 0 ? 1 : 0;
            }
        }

        Assert.True(keys >= 1);
        Assert.True(reordered > 0, "B-frames give a presentation time after the decode time");
        Assert.True(d.Duration.IsKnown);
    }

    [Fact]
    public void FlvSeekLandsOnAKeyframeAtOrBeforeTheTarget()
    {
        using FlvDemuxer d = FlvDemuxer.Open(new MemoryMediaSource(Bytes("h264-aac.flv")));
        MediaTime landed = d.Seek(new MediaTime(700, 1000));
        Assert.True(landed.CompareTo(new MediaTime(700, 1000)) <= 0);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.True(p.Info.IsKeyframe || p.Info.TrackIndex != d.Tracks.Single(t => t.Kind == TrackKind.Video).Index);
    }

    [Theory]
    [InlineData("opus.ogg")]
    [InlineData("tags.flac")]
    [InlineData("h264-aac.flv")]
    public void PullPathAllocatesNothingPerPacket(string name)
    {
        byte[] data = File.ReadAllBytes(FileFor(name));
        using (IDemuxer warm = OpenSync(name, data))
        {
            Drain(warm);
        }

        using IDemuxer d = OpenSync(name, data);
        for (int i = 0; i < 3 && d.TryReadPacket(out _); i++)
        {
        }

        Assert.Equal(0, Drain(d));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long Drain(IDemuxer d)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            sum += p.Data.Length;
        }

        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotEqual(0, sum);
        return used;
    }
}

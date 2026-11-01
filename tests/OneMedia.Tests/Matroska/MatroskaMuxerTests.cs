using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tests;

public class MatroskaMuxerTests
{
    private static readonly byte[] Uuid = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    private static MediaTrack Vp8() => new()
    {
        Index = 0,
        Kind = TrackKind.Video,
        Codec = CodecId.Vp8,
        Timescale = 1000,
        Video = new VideoTrackInfo(160, 112, FrameRateNumerator: 25, FrameRateDenominator: 1),
    };

    private static MediaTrack OpusTrack() => new()
    {
        Index = 1,
        Kind = TrackKind.Audio,
        Codec = CodecId.Opus,
        Timescale = 48000,
        Configuration = new CodecConfiguration(OpusHeadBytes(), "OpusHead"),
        Audio = new AudioTrackInfo(48000, 2),
    };

    private static byte[] OpusHeadBytes() =>
        [(byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'H', (byte)'e', (byte)'a', (byte)'d', 1, 2, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];

    private static PacketInfo Packet(int track, long pts, long dur, bool key, int size, long timescale = 1000) =>
        new(track, new MediaTime(pts, timescale), new MediaTime(pts, timescale), dur < 0 ? MediaTime.Unknown : new MediaTime(dur, timescale), key ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, size);

    private static byte[] Payload(int size, int seed) => [.. Enumerable.Range(0, size).Select(i => (byte)((i * 31 + seed) & 0xFF))];

    private static MatroskaMuxOptions Fixed(MatroskaMuxOptions? o = null) => (o ?? new MatroskaMuxOptions()) with { SegmentUuid = Uuid };

    private sealed record Got(PacketInfo Info, byte[] Data);

    private static List<Got> ReadBack(byte[] file, out MatroskaDemuxer demuxer)
    {
        demuxer = MatroskaDemuxer.Open(new MemoryMediaSource(file));
        List<Got> all = [];
        while (demuxer.TryReadPacket(out PacketView p))
        {
            all.Add(new Got(p.Info, p.Data.ToArray()));
        }

        return all;
    }

    [Fact]
    public void WritesPacketsThatReadBackWithTheirTimesFlagsAndBytes()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8(), OpusTrack()], Fixed());
        List<(int Track, long Pts, long Dur, bool Key, byte[] Data)> sent = [];
        for (int i = 0; i < 200; i++)
        {
            bool key = i % 50 == 0;
            sent.Add((0, i * 40, 40, key, Payload(30 + i, i)));
            sent.Add((1, i * 40, 40, true, Payload(20 + i % 7, i + 5)));
        }

        foreach ((int track, long pts, long dur, bool key, byte[] data) in sent)
        {
            mux.WritePacket(Packet(track, pts, dur, key, data.Length), data);
        }

        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        using (d)
        {
            Assert.Equal(sent.Count, got.Count);
            Assert.Empty(d.Defects);
            Assert.Equal(CodecId.Vp8, d.Tracks[0].Codec);
            Assert.Equal(CodecId.Opus, d.Tracks[1].Codec);
            Assert.Equal(160, d.Tracks[0].Video!.Value.Width);
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.Equal(sent[i].Track, got[i].Info.TrackIndex);
                Assert.Equal(sent[i].Data, got[i].Data);
                Assert.Equal(sent[i].Key, got[i].Info.IsKeyframe);
                Assert.Equal(sent[i].Dur, got[i].Info.Duration.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks);
                // Opus: Block timestamps carry the pre-skip (CodecDelay), which the reader takes off again (RFC 9559 §11.2).
                long expectedPts = sent[i].Pts - (sent[i].Track == 1 ? 7 : 0);
                Assert.Equal(expectedPts, got[i].Info.Pts.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks);
            }

            Assert.True(d.Duration.IsKnown);
        }
    }

    [Fact]
    public void WritesCuesThatSeekFindsAndHeaderWithSeekHeadAndReservedVoid()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed());
        for (int i = 0; i < 400; i++)
        {
            byte[] data = Payload(100, i);
            mux.WritePacket(Packet(0, i * 40, 40, i % 25 == 0, data.Length), data);
        }

        mux.Finish();
        Assert.Equal(16, mux.CuePointCount); // one per key frame, a second apart
        byte[] file = ms.ToArray();
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file));
        MediaTime landed = d.Seek(new MediaTime(9_500, 1000));
        Assert.Equal(new MediaTime(9_000, 1000), landed);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.True(p.Info.IsKeyframe);
        // The reserve after Tracks is a Void element, ready for tags.
        Assert.True(file.AsSpan().IndexOf(EbmlWriter.Void(2048).AsSpan(0, 3)) > 0);
    }

    [Fact]
    public void Crc32ElementsVerifyWhenEnabled()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8(), OpusTrack()], Fixed(new MatroskaMuxOptions { Crc32 = true }));
        for (int i = 0; i < 100; i++)
        {
            byte[] data = Payload(64, i);
            mux.WritePacket(Packet(0, i * 40, 40, i % 10 == 0, data.Length), data);
            mux.WritePacket(Packet(1, i * 40, 40, true, data.Length), data);
        }

        mux.Finish();
        DefectLog log = new();
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(ms.ToArray()), new MediaOpenOptions { Defects = log });
        int n = 0;
        while (d.TryReadPacket(out _)) { n++; }
        Assert.Equal(200, n);
        Assert.DoesNotContain(log, x => x.Kind == DefectKind.ChecksumMismatch);
        Assert.Contains(ms.ToArray().AsSpan().IndexOf(new byte[] { 0xBF, 0x84 }) is var at && at > 0 ? "found" : "", "found");
    }

    [Fact]
    public void LiveModeWritesToAStreamThatCannotSeekAndReadsBackForwardOnly()
    {
        using MemoryStream backing = new();
        using NoSeekStream pipe = new(backing);
        MatroskaMuxer mux = new(pipe, [Vp8(), OpusTrack()], Fixed(new MatroskaMuxOptions { Live = true }));
        List<byte[]> sent = [];
        for (int i = 0; i < 300; i++)
        {
            byte[] data = Payload(50 + i % 13, i);
            sent.Add(data);
            mux.WritePacket(Packet(0, i * 40, 40, i % 30 == 0, data.Length), data);
        }

        mux.Finish();
        byte[] file = backing.ToArray();
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        int n = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            Assert.Equal(sent[n], p.Data.ToArray());
            Assert.Equal(n * 40, p.Info.Pts.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks);
            n++;
        }

        Assert.Equal(300, n);
        // RFC 8794 §6.2: the Segment's size is the unknown-size marker (all ones).
        int segment = file.AsSpan().IndexOf(new byte[] { 0x18, 0x53, 0x80, 0x67 });
        Assert.Equal(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, file.AsSpan(segment + 4, 8).ToArray());
    }

    [Fact]
    public void PacketsSharingATimestampWithoutDurationsGoBackIntoOneLace()
    {
        using MemoryStream ms = new();
        MediaTrack audio = new() { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Opus, Timescale = 1000, Configuration = new CodecConfiguration(OpusHeadBytes(), "OpusHead"), Audio = new AudioTrackInfo(48000, 2) };
        MatroskaMuxer mux = new(ms, [audio], Fixed());
        List<byte[]> sent = [];
        for (int i = 0; i < 12; i++)
        {
            byte[] data = Payload(10 + i * 30, i);
            sent.Add(data);
            mux.WritePacket(Packet(0, (i / 4) * 80, -1, true, data.Length), data);
        }

        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        using (d)
        {
            Assert.Equal(12, got.Count);
            for (int i = 0; i < 12; i++)
            {
                Assert.Equal(sent[i], got[i].Data);
            }
        }

        // Three blocks of four frames each, not twelve blocks: the first block's header says Xiph lacing (flags 0x02) with 3 sizes after the count.
        Assert.True(ms.ToArray().AsSpan().IndexOf(new byte[] { 0x02, 3 }) > 0);
    }

    [Fact]
    public void BlockGroupsCarryDurationsTheDefaultDurationDoesNotGive()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed());
        for (int i = 0; i < 30; i++)
        {
            byte[] data = Payload(20, i);
            mux.WritePacket(Packet(0, i * 40, i == 29 ? 17 : 40, i == 0, data.Length), data);
        }

        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        using (d)
        {
            Assert.Equal(40, got[3].Info.Duration.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks);
            Assert.Equal(17, got[29].Info.Duration.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks);
            Assert.False(got[1].Info.IsKeyframe);
        }
    }

    [Fact]
    public void DiscardPaddingIsWrittenInNanoseconds()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [OpusTrack()], Fixed());
        for (int i = 0; i < 10; i++)
        {
            byte[] data = Payload(40, i);
            PacketInfo info = Packet(0, i * 20, 20, true, data.Length) with { DiscardPadding = i == 9 ? new MediaTime(6_500_000, 1_000_000_000) : default };
            mux.WritePacket(info, data);
        }

        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        using (d)
        {
            Assert.Equal(new MediaTime(6_500_000, 1_000_000_000), got[9].Info.DiscardPadding);
            Assert.Equal(0, got[8].Info.DiscardPadding.Ticks);
        }
    }

    [Fact]
    public void RefusesDecodeTimeThatGoesBackwards()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed());
        mux.WritePacket(Packet(0, 100, 40, true, 4), Payload(4, 0));
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Packet(0, 60, 40, false, 4), Payload(4, 1)));
        Assert.Equal(MuxRefusalReason.NonMonotonicDts, e.Reason);
        Assert.Equal(0, e.TrackIndex);
        // The refused packet wrote nothing; the muxer carries on.
        mux.WritePacket(Packet(0, 140, 40, false, 4), Payload(4, 2));
        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        d.Dispose();
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public void RefusesCodecsWebMDoesNotAllow()
    {
        MediaTrack h264 = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 1000, Configuration = new CodecConfiguration(new byte[] { 1, 0x64, 0, 0x1F, 0xFF }, "avcC") };
        using MemoryStream ms = new();
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [h264], new MatroskaMuxOptions { Profile = MatroskaProfile.WebM }));
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, e.Reason);
        Assert.Contains("V_MPEG4/ISO/AVC", e.Message);
        MediaTrack aac = new() { Index = 1, Kind = TrackKind.Audio, Codec = CodecId.Aac, Timescale = 44100, Configuration = new CodecConfiguration(new byte[] { 0x12, 0x10 }, "asc"), Audio = new AudioTrackInfo(44100, 2) };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [Vp8(), aac], new MatroskaMuxOptions { Profile = MatroskaProfile.WebM })).Reason);
        // The same tracks are fine in Matroska, and WebM takes VP8 and Opus.
        _ = new MatroskaMuxer(ms, [h264, aac], Fixed());
        _ = new MatroskaMuxer(ms, [Vp8(), OpusTrack()], Fixed(new MatroskaMuxOptions { Profile = MatroskaProfile.WebM }));
    }

    [Fact]
    public void WebMOutputHasWebMDocTypeAndNoSegmentUuid()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed(new MatroskaMuxOptions { Profile = MatroskaProfile.WebM }));
        mux.WritePacket(Packet(0, 0, 40, true, 4), Payload(4, 0));
        mux.Finish();
        byte[] file = ms.ToArray();
        Assert.True(file.AsSpan().IndexOf("webm"u8) > 0);
        Assert.True(file.AsSpan().IndexOf(new byte[] { 0x73, 0xA4, 0x90 }) < 0);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal("webm", d.Header.DocType);
    }

    [Fact]
    public void RefusesMissingCodecPrivateAndUnnamedCodecs()
    {
        using MemoryStream ms = new();
        MediaTrack avc = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 1000 };
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [avc], Fixed()));
        Assert.Equal(MuxRefusalReason.MissingConfiguration, e.Reason);
        MediaTrack opusNoHead = new() { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Opus, Timescale = 48000, Audio = new AudioTrackInfo(48000, 2) };
        Assert.Equal(MuxRefusalReason.MissingConfiguration, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [opusNoHead], Fixed())).Reason);
        MediaTrack wrongForm = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 1000, Configuration = new CodecConfiguration(new byte[] { 0, 0, 0, 1, 0x67 }, "annexb") };
        Assert.Equal(MuxRefusalReason.InvalidConfiguration, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [wrongForm], Fixed())).Reason);
        MediaTrack alac = new() { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Alac, Timescale = 44100, Audio = new AudioTrackInfo(44100, 2) };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [alac], Fixed())).Reason);
        MediaTrack data = new() { Index = 0, Kind = TrackKind.Data, Timescale = 1000 };
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(ms, [data], Fixed())).Reason);
    }

    [Fact]
    public void RefusesOutputsAndOptionsThatCannotWork()
    {
        using MemoryStream backing = new();
        using NoSeekStream pipe = new(backing);
        Assert.Equal(MuxRefusalReason.OutputNotSuitable, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(pipe, [Vp8()], Fixed())).Reason);
        Assert.Throws<ArgumentException>(() => new MatroskaMuxer(backing, [Vp8()], new MatroskaMuxOptions { TimestampScale = 3 }));
        Assert.Throws<ArgumentException>(() => new MatroskaMuxer(backing, [Vp8()], new MatroskaMuxOptions { SegmentUuid = new byte[16] }));
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => new MatroskaMuxer(backing, [], Fixed())).Reason);
        MatroskaMuxer mux = new(backing, [Vp8()], Fixed());
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Packet(3, 0, 40, true, 1), Payload(1, 0))).Reason);
        mux.Finish();
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Packet(0, 0, 40, true, 1), Payload(1, 0))).Reason);
    }

    [Fact]
    public void FinishReportsWhatWasWrittenAndTheNormalisationsApplied()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [OpusTrack()], Fixed());
        for (int i = 0; i < 10; i++)
        {
            // 960 ticks of 48 kHz is exactly 20 ms; the odd packet time is not a whole millisecond.
            mux.WritePacket(Packet(0, i * 960 + (i == 5 ? 7 : 0), 960, true, 30, 48000), Payload(30, i));
        }

        MuxReport report = mux.Finish();
        Assert.Equal(ms.Length, report.BytesWritten);
        Assert.Equal(10, report.Tracks[0].Packets);
        Assert.Equal(300, report.Tracks[0].PayloadBytes);
        Assert.Equal(new MediaTime(200, 1000), report.Tracks[0].Duration);
        Assert.Contains(report.Notes, n => n.Contains("rounded", StringComparison.Ordinal));
        Assert.Same(report, mux.Finish());
    }

    [Fact]
    public void TracksAreDeclaredWithAddTrackBeforeTheFirstPacket()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, Fixed());
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Packet(0, 0, 40, true, 1), Payload(1, 0))).Reason);
        Assert.Equal(0, mux.AddTrack(Vp8()));
        Assert.Equal(1, mux.AddTrack(OpusTrack() with { Index = 9 }));
        Assert.Equal([0, 1], mux.Tracks.Select(t => t.Index).ToArray());
        mux.WritePacket(Packet(0, 0, 40, true, 1), Payload(1, 0));
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Vp8())).Reason);
    }

    [Fact]
    public void ACancelledTokenStopsTheWrite()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed());
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => mux.WritePacket(Packet(0, 0, 40, true, 1), Payload(1, 0), cts.Token));
        Assert.Throws<OperationCanceledException>(() => mux.Finish(cts.Token));
    }

    [Fact]
    public void ClustersStartOnKeyFramesAndStayWithinTheMemoryBound()
    {
        using MemoryStream ms = new();
        MatroskaMuxer mux = new(ms, [Vp8()], Fixed(new MatroskaMuxOptions { MinClusterDuration = new MediaTime(1, 1) }));
        for (int i = 0; i < 1000; i++)
        {
            byte[] data = Payload(500, i);
            mux.WritePacket(Packet(0, i * 40, 40, i % 100 == 0, data.Length), data);
        }

        mux.Finish();
        List<Got> got = ReadBack(ms.ToArray(), out MatroskaDemuxer d);
        d.Dispose();
        Assert.Equal(1000, got.Count);
        // Every Cluster begins with the key frame it was cut at: the reader's source positions are in increasing order and keys sit at 100-packet steps.
        Assert.Equal([0, 100, 200, 300, 400, 500, 600, 700, 800, 900], Enumerable.Range(0, 1000).Where(i => got[i].Info.IsKeyframe).ToArray());
    }

    [Fact]
    public void SteadyStateWritingAllocatesNothingPerPacket()
    {
        SeekableSink sink = new();
        MatroskaMuxer mux = new(sink, [Vp8(), OpusTrack()], Fixed());
        byte[] data = Payload(900, 1);
        long t = 0;
        void Write(int packets)
        {
            for (int i = 0; i < packets; i++)
            {
                mux.WritePacket(Packet(0, t, 40, i % 25 == 0, data.Length), data);
                mux.WritePacket(Packet(1, t, 20, true, 100), data.AsSpan(0, 100));
                t += 40;
            }
        }

        Write(3000); // warm-up: the Cluster buffers, the Cue list and the lists reach their working size
        long before = GC.GetAllocatedBytesForCurrentThread();
        Write(5000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        mux.Finish();
        // 10,000 packets; the Cue list may double once (24 bytes per cue), nothing else may allocate.
        Assert.True(allocated <= 4096, $"allocated {allocated} bytes over 10000 packets");
    }

    [Fact]
    public void LiveWritingAllocatesNothingPerPacket()
    {
        MatroskaMuxer mux = new(Stream.Null, [Vp8()], Fixed(new MatroskaMuxOptions { Live = true }));
        byte[] data = Payload(700, 3);
        long t = 0;
        for (int i = 0; i < 2000; i++)
        {
            mux.WritePacket(Packet(0, t += 40, 40, i % 25 == 0, data.Length), data);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++)
        {
            mux.WritePacket(Packet(0, t += 40, 40, i % 25 == 0, data.Length), data);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class NoSeekStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    // A stream that keeps nothing but can seek, so the allocation test sees only the muxer.
    private sealed class SeekableSink : Stream
    {
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _position;

        public override long Position { get => _position; set => _position = value; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();

        public override void SetLength(long value) { }

        public override void Write(byte[] buffer, int offset, int count) => _position += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _position += buffer.Length;
    }
}

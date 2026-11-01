using RecursiveAI.OneMedia.Matroska;
using static RecursiveAI.OneMedia.Tests.Ebl;

namespace RecursiveAI.OneMedia.Tests;

public class MatroskaDemuxTests
{
    private sealed record Pkt(PacketInfo Info, byte[] Data);

    private static List<Pkt> ReadAll(MatroskaDemuxer d)
    {
        List<Pkt> all = [];
        while (d.TryReadPacket(out PacketView p))
        {
            all.Add(new Pkt(p.Info, p.Data.ToArray()));
        }

        return all;
    }

    private static MatroskaDemuxer Open(byte[] file, DefectLog? defects = null, MediaLimits? limits = null) =>
        MatroskaDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Defects = defects, Limits = limits ?? MediaLimits.Default });

    private static byte[] Simple1(short rel, bool key, byte[] payload) => Simple(1, rel, (byte)(key ? 0x80 : 0), payload);

    private static byte[] BasicFile(out byte[] frame0)
    {
        frame0 = Fill(10, 1);
        return Cat(
            Header(),
            SegmentElement(
                InfoElement(),
                TracksElement(Track(1, 1, "V_VP8", U(0x23E383, 40_000_000), El(0xE0, U(0xB0, 160), U(0xBA, 112))), Track(2, 2, "A_VORBIS", El(0xE1, F64(0xB5, 44100), U(0x9F, 2)))),
                ClusterElement(1000, Simple1(0, true, frame0), Simple(2, 5, 0x80, Fill(4, 9)), Simple1(40, false, Fill(6, 2)))));
    }

    [Fact]
    public void ReadsTracksAndPacketsWithExactTimes()
    {
        byte[] file = BasicFile(out byte[] frame0);
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(MediaFormatId.Matroska, d.Format);
        Assert.Equal(2, d.Tracks.Count);
        MediaTrack v = d.Tracks[0];
        Assert.Equal(TrackKind.Video, v.Kind);
        Assert.Equal(CodecId.Vp8, v.Codec);
        Assert.Equal(1000, v.Timescale);
        Assert.Equal(160, v.Video!.Value.Width);
        Assert.Equal(25, v.Video!.Value.FrameRateNumerator);
        Assert.Equal(1, v.Video!.Value.FrameRateDenominator);
        MediaTrack a = d.Tracks[1];
        Assert.Equal(CodecId.Vorbis, a.Codec);
        Assert.Equal(44100, a.Audio!.Value.SampleRate);
        Assert.Equal(2, a.Audio!.Value.Channels);
        Assert.Equal(new MediaTime(3000, 1000), d.Duration);

        List<Pkt> packets = ReadAll(d);
        Assert.Equal(3, packets.Count);
        Assert.Equal(0, packets[0].Info.TrackIndex);
        Assert.Equal(new MediaTime(1000, 1000), packets[0].Info.Pts);
        Assert.Equal(new MediaTime(40, 1000), packets[0].Info.Duration); // DefaultDuration 40 ms
        Assert.True(packets[0].Info.IsKeyframe);
        Assert.False(packets[0].Info.Dts.IsKnown); // video: decode time is not stored
        Assert.Equal(frame0, packets[0].Data);
        // The source range points at the frame's bytes in the file.
        Assert.Equal(frame0, file.AsSpan((int)packets[0].Info.SourceRange.Offset, (int)packets[0].Info.SourceRange.Length).ToArray());
        Assert.Equal(1, packets[1].Info.TrackIndex);
        Assert.Equal(new MediaTime(1005, 1000), packets[1].Info.Pts);
        Assert.Equal(packets[1].Info.Pts, packets[1].Info.Dts); // audio: no reordering
        Assert.False(packets[2].Info.IsKeyframe);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void TracksAfterTheFirstClusterAreFoundByWalkingPastIt()
    {
        // No SeekHead: the reader meets a Cluster first, tries the (empty) SeekHead lookup, which moves the input, then walks on.
        byte[] file = Cat(Header(), SegmentElement(InfoElement(),
            ClusterElement(0, Simple(1, 0, 0x80, Fill(6, 1)), Simple(1, 40, 0, Fill(4, 2))),
            ClusterElement(80, Simple(1, 0, 0x80, Fill(5, 3))),
            TracksElement(Track(1, 1, "V_VP8"))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(CodecId.Vp8, d.Tracks[0].Codec);
        Assert.Equal(3, ReadAll(d).Count);
    }

    [Fact]
    public void SimpleBlockFlagsMapToPacketAttributes()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP9")),
            ClusterElement(0, Simple(1, 0, 0x80 | 0x08 | 0x01, Fill(3, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.True(p.Info.Flags.HasFlag(PacketAttributes.Keyframe));
        Assert.True(p.Info.Flags.HasFlag(PacketAttributes.Discard)); // invisible
        Assert.True(p.Info.Flags.HasFlag(PacketAttributes.Disposable)); // discardable
    }

    [Fact]
    public void BlockGroupsGiveDurationKeyframeAndDiscardPadding()
    {
        byte[] opus = Track(1, 2, "A_OPUS", U(0x56AA, 6_500_000), U(0x56BB, 80_000_000), El(0xE1, F64(0xB5, 48000), U(0x9F, 2)));
        // DiscardPadding is a signed integer; build it with the right length.
        byte[] dp = El(0x75A2, [0x00, 0x4C, 0x4B, 0x40]); // 5,000,000 ns
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(opus, Track(2, 1, "V_VP8")),
            ClusterElement(100,
                El(BlockGroup, BlockBytes(1, 0, 0, Fill(8, 1)), U(0x9B, 20)),
                El(BlockGroup, BlockBytes(2, 0, 0, Fill(5, 2)), El(0xFB, [0x81])),
                El(BlockGroup, BlockBytes(2, 10, 0, Fill(5, 3))),
                El(BlockGroup, BlockBytes(1, 20, 0, Fill(8, 4)), U(0x9B, 14), dp))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(new MediaTime(6_500_000, 1_000_000_000), d.Tracks[0].Edit.Priming);
        List<Pkt> p = ReadAll(d);
        Assert.Equal(4, p.Count);
        // CodecDelay is subtracted from the block timestamp (6.5 ms rounds to 7 ticks at 1 ms).
        Assert.Equal(new MediaTime(100 - 7, 1000), p[0].Info.Pts);
        Assert.Equal(new MediaTime(20, 1000), p[0].Info.Duration);
        Assert.False(p[1].Info.IsKeyframe);
        Assert.True(p[2].Info.IsKeyframe);
        Assert.Equal(new MediaTime(14, 1000), p[3].Info.Duration);
        Assert.Equal(new MediaTime(5_000_000, 1_000_000_000), p[3].Info.DiscardPadding);
        Assert.Equal(default, p[0].Info.DiscardPadding);
    }

    [Fact]
    public void XiphLacingSplitsFramesAndSharesTheDuration()
    {
        byte[] f1 = Fill(3, 1), f2 = Fill(300, 2), f3 = Fill(4, 3);
        byte[] lace = Cat([2], [3], [255, 45], f1, f2, f3);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_AAC", U(0x23E383, 21_333_333))),
            ClusterElement(0, Simple(1, 0, 0x02, lace))));
        using MatroskaDemuxer d = Open(file);
        List<Pkt> p = ReadAll(d);
        Assert.Equal(3, p.Count);
        Assert.Equal(f1, p[0].Data);
        Assert.Equal(f2, p[1].Data);
        Assert.Equal(f3, p[2].Data);
        // Frame source ranges are the frames' own bytes.
        Assert.Equal(f2, file.AsSpan((int)p[1].Info.SourceRange.Offset, 300).ToArray());
        // Lace total = 3 x 21.333333 ms = 63 ms (floor), split by cumulative floor: 0, 21, 42, 63.
        Assert.Equal([0L, 21, 42], p.Select(x => x.Info.Pts.Ticks).ToArray());
        Assert.Equal([21L, 21, 21], p.Select(x => x.Info.Duration.Ticks).ToArray());
    }

    [Fact]
    public void EbmlLacingDecodesSignedSizeDifferences()
    {
        // Frames of 800, 500 and 1000 bytes: the RFC 9559 example (sizes 0x4320 and 0x5ED3).
        byte[] f1 = Fill(800, 1), f2 = Fill(500, 2), f3 = Fill(1000, 3);
        byte[] lace = Cat([2], [0x43, 0x20], [0x5E, 0xD3], f1, f2, f3);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_AAC")), ClusterElement(0, Simple(1, 0, 0x06, lace))));
        using MatroskaDemuxer d = Open(file);
        List<Pkt> p = ReadAll(d);
        Assert.Equal(3, p.Count);
        Assert.Equal(f1, p[0].Data);
        Assert.Equal(f2, p[1].Data);
        Assert.Equal(f3, p[2].Data);
        Assert.All(p, x => Assert.False(x.Info.Duration.IsKnown)); // nothing in the file says how long they are
    }

    [Fact]
    public void FixedSizeLacingDividesEvenly()
    {
        byte[] data = Fill(30, 5);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_PCM/INT/LIT")),
            ClusterElement(0, Simple(1, 0, 0x04, Cat([2], data)), Simple(1, 30, 0x04, Cat([2], Fill(31, 1))))));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        List<Pkt> p = ReadAll(d);
        Assert.Equal(3, p.Count); // the second block does not divide evenly and is dropped, with a defect
        Assert.Equal(data.AsSpan(0, 10).ToArray(), p[0].Data);
        Assert.Equal(data.AsSpan(20, 10).ToArray(), p[2].Data);
        Assert.Contains(defects, x => x.Kind == DefectKind.Inconsistent);
    }

    [Fact]
    public void BlockDurationOfALaceIsSharedOut()
    {
        byte[] lace = Cat([3], Fill(8, 1), Fill(8, 2), Fill(8, 3), Fill(8, 4)); // fixed size, four frames
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_OPUS")),
            ClusterElement(10, El(BlockGroup, BlockBytes(1, 0, 0x04, lace), U(0x9B, 90)))));
        using MatroskaDemuxer d = Open(file);
        List<Pkt> p = ReadAll(d);
        Assert.Equal([10L, 32, 55, 77], p.Select(x => x.Info.Pts.Ticks).ToArray()); // 0, 22, 45, 67 (floor of 22.5 steps)
        Assert.Equal(90, p.Sum(x => x.Info.Duration.Ticks));
    }

    [Fact]
    public void HeaderStrippingPutsTheRemovedBytesBack()
    {
        byte[] prefix = [0, 0, 1, 0xB3];
        byte[] encodings = El(0x6D80, El(0x6240, U(0x5031, 0), U(0x5032, 1), U(0x5033, 0), El(0x5034, U(0x4254, 3), Bin(0x4255, prefix))));
        byte[] stored = Fill(6, 9);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_MPEG2", encodings)), ClusterElement(0, Simple1(0, true, stored))));
        using MatroskaDemuxer d = Open(file);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(Cat(prefix, stored), p.Data.ToArray());
        Assert.Equal(prefix.Length + stored.Length, p.Info.Size);
        Assert.Equal(stored.Length, p.Info.SourceRange.Length); // the range names the stored bytes, not the restored ones
        Assert.Equal(prefix, d.Entries[0].HeaderStripPrefix.ToArray());
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream ms = new();
        using (System.IO.Compression.ZLibStream z = new(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] ZlibTrack() => El(0x6D80, El(0x6240, U(0x5033, 0), El(0x5034, U(0x4254, 0))));

    [Fact]
    public void ZlibFramesAreInflatedAndKeepTheirStoredRange()
    {
        byte[] plain = Fill(300, 7);
        byte[] stored = Deflate(plain);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 17, "S_TEXT/UTF8", ZlibTrack())), ClusterElement(0, Simple1(0, true, stored))));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        Assert.False(d.Entries[0].HasUnsupportedFrameCompression);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(plain, p.Data.ToArray());
        Assert.Equal(plain.Length, p.Info.Size);
        Assert.Equal(stored.Length, p.Info.SourceRange.Length);
        Assert.True(file.AsSpan((int)p.Info.SourceRange.Offset, stored.Length).SequenceEqual(stored));
        Assert.DoesNotContain(defects, x => x.Kind == DefectKind.InvalidValue);
    }

    [Fact]
    public void ABadZlibFrameIsDeliveredAsStoredWithAWarning()
    {
        byte[] junk = Fill(20, 3);
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 17, "S_TEXT/UTF8", ZlibTrack())), ClusterElement(0, Simple1(0, true, junk))));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(junk, p.Data.ToArray());
        Assert.Contains(defects, x => x.Kind == DefectKind.InvalidValue && x.Severity == DefectSeverity.Warning);
    }

    [Fact]
    public void InflatedSizeIsLimited()
    {
        byte[] stored = Deflate(new byte[100_000]); // a few hundred bytes that inflate to 100 kB
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 17, "S_TEXT/UTF8", ZlibTrack())), ClusterElement(0, Simple1(0, true, stored))));
        MediaLimits limits = MediaLimits.Default with { MaxPacketBytes = 4096 };
        using MatroskaDemuxer d = Open(file, limits: limits);
        Assert.Throws<MediaLimitExceededException>(() => d.TryReadPacket(out _));
    }

    [Fact]
    public void EncryptionIsReported()
    {
        byte[] enc = El(0x6D80, El(0x6240, U(0x5033, 1), El(0x5035, U(0x47E1, 5))));
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_OPUS", enc)), ClusterElement(0, Simple1(0, true, Fill(5, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.True(d.Tracks[0].IsProtected);
    }

    [Fact]
    public void OutputSamplingFrequencyIsTheTrackRateForSbr()
    {
        byte[] audio = El(0xE1, F64(0xB5, 24000), F64(0x78B5, 48000), U(0x9F, 2));
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_AAC", audio)), ClusterElement(0, Simple1(0, true, Fill(5, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(48000, d.Tracks[0].Audio!.Value.SampleRate);
    }

    [Fact]
    public void VfwFourCcNamesTheCodec()
    {
        byte[] bih = new byte[40];
        bih[0] = 40;
        "H264"u8.CopyTo(bih.AsSpan(16));
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_MS/VFW/FOURCC", Bin(0x63A2, bih), El(0xE0, U(0xB0, 64), U(0xBA, 48)))), ClusterElement(0, Simple1(0, true, Fill(5, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(CodecId.H264, d.Tracks[0].Codec);
        Assert.Equal("H264", d.Tracks[0].CodecTag);
    }

    [Fact]
    public void TimestampScaleThatDoesNotDivideASecondUsesNanosecondTicks()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(1_000_333, 1000), TracksElement(Track(1, 1, "V_VP8")), ClusterElement(3, Simple1(2, true, Fill(3, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(1_000_000_000, d.Tracks[0].Timescale);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(new MediaTime(5L * 1_000_333, 1_000_000_000), p.Info.Pts); // exact
        Assert.Equal(new MediaTime(1000L * 1_000_333, 1_000_000_000), d.Duration);
    }

    [Fact]
    public void ScaleThatDividesASecondKeepsOneTickPerUnit()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(500_000, 6000), TracksElement(Track(1, 1, "V_VP8")), ClusterElement(3, Simple1(2, true, Fill(3, 1)))));
        using MatroskaDemuxer d = Open(file);
        Assert.Equal(2000, d.Tracks[0].Timescale);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(new MediaTime(5, 2000), p.Info.Pts);
    }

    [Fact]
    public void DisabledTracksAreSkippedAndUnknownTracksReported()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP8"), Track(2, 2, "A_OPUS")),
            ClusterElement(0, Simple1(0, true, Fill(3, 1)), Simple(2, 1, 0x80, Fill(3, 2)), Simple(9, 2, 0x80, Fill(3, 3)))));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        d.SetTrackEnabled(1, false);
        List<Pkt> p = ReadAll(d);
        Assert.Single(p);
        Assert.Contains(defects, x => x.Kind == DefectKind.Inconsistent && x.Message.Contains("track 9", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetTrackEnabled(5, true));
    }

    [Fact]
    public void UnknownSizeSegmentAndClustersReadSeekableAndForwardOnly()
    {
        byte[] file = Cat(Header(), SegmentUnknown(InfoElement(), TracksElement(Track(1, 1, "V_VP8"), Track(2, 2, "A_OPUS")),
            ElUnknown(Cluster, U(0xE7, 0), Simple1(0, true, Fill(5, 1)), Simple(2, 3, 0x80, Fill(4, 2))),
            ElUnknown(Cluster, U(0xE7, 1000), Simple1(0, true, Fill(5, 3)), Simple(2, 3, 0x80, Fill(4, 4))),
            El(0x1254C367, El(0x7373, El(0x63C0, U(0x68CA, 50))))));
        using MatroskaDemuxer seekable = Open(file);
        List<Pkt> a = ReadAll(seekable);
        Assert.Equal(4, a.Count);
        Assert.Equal(new MediaTime(1003, 1000), a[3].Info.Pts);

        using MatroskaDemuxer forward = MatroskaDemuxer.Open(new StreamForwardSource(new NonSeekableStream(file)));
        Assert.False(forward.CanSeek);
        List<Pkt> b = ReadAll(forward);
        Assert.Equal(a.Select(x => (x.Info.TrackIndex, x.Info.Pts, x.Info.Duration, x.Info.Flags, x.Data.Length)).ToArray(), b.Select(x => (x.Info.TrackIndex, x.Info.Pts, x.Info.Duration, x.Info.Flags, x.Data.Length)).ToArray());
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Data, b[i].Data);
        }

        Assert.Throws<NotSupportedException>(() => forward.Seek(MediaTime.Zero));
    }

    [Fact]
    public void ForwardOnlyNeedsTracksBeforeTheFirstCluster()
    {
        byte[] file = Cat(Header(), SegmentUnknown(InfoElement(), ClusterElement(0, Simple1(0, true, Fill(3, 1))), TracksElement(Track(1, 1, "V_VP8"))));
        MediaFormatException ex = Assert.Throws<MediaFormatException>(() => MatroskaDemuxer.Open(new StreamForwardSource(new NonSeekableStream(file))));
        Assert.Equal(DefectKind.InvalidOrder, ex.Kind);
        // A seekable source can go back for them.
        using MatroskaDemuxer d = Open(file);
        Assert.Single(d.Tracks);
        Assert.True(d.TryReadPacket(out _));
    }

    // Four-byte SeekPositions keep the SeekHead the same size whatever it points at.
    private static byte[] Fixed4(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    [Fact]
    public void SeekHeadLocatesTracksAfterTheClusters()
    {
        byte[] info = InfoElement();
        byte[] tracks = TracksElement(Track(1, 1, "V_VP8"));
        byte[] cluster = ClusterElement(0, Simple1(0, true, Fill(3, 1)));
        // Positions are relative to the Segment data; the SeekHead is fixed-size so they can be computed.
        byte[] SeekHead(long infoPos, long tracksPos) => El(0x114D9B74,
            El(0x4DBB, Bin(0x53AB, [0x15, 0x49, 0xA9, 0x66]), Bin(0x53AC, Fixed4(infoPos))),
            El(0x4DBB, Bin(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), Bin(0x53AC, Fixed4(tracksPos))));
        int seekLen = SeekHead(1000, 1000).Length;
        long infoAt = seekLen;
        long clusterAt = infoAt + info.Length;
        long tracksAt = clusterAt + cluster.Length;
        byte[] file = Cat(Header(), SegmentElement(SeekHead(infoAt, tracksAt), info, cluster, tracks));
        using MatroskaDemuxer d = Open(file);
        Assert.Single(d.Tracks);
        Assert.Equal(2, d.SeekEntries.Count);
        Assert.Empty(d.Defects.Select(x => x.ToString()));
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(3, p.Data.Length);
    }

    [Fact]
    public void SeekUsesCuesAndFallsBackToClusters()
    {
        // Three clusters of one video keyframe each, at 0, 1000 and 2000 ms.
        byte[] c0 = ClusterElement(0, Simple1(0, true, Fill(3, 1)));
        byte[] c1 = ClusterElement(1000, Simple1(0, true, Fill(3, 2)));
        byte[] c2 = ClusterElement(2000, Simple1(0, true, Fill(3, 3)));
        byte[] info = InfoElement();
        byte[] tracks = TracksElement(Track(1, 1, "V_VP8"));
        long first = info.Length + tracks.Length;
        byte[] Cue(long time, long pos) => El(0xBB, U(0xB3, (ulong)time), El(0xB7, U(0xF7, 1), U(0xF1, (ulong)pos)));
        byte[] cues = El(Cues, Cue(0, first), Cue(1000, first + c0.Length), Cue(2000, first + c0.Length + c1.Length));
        byte[] withCues = Cat(Header(), SegmentElement(info, tracks, c0, c1, c2, cues));
        byte[] withoutCues = Cat(Header(), SegmentElement(info, tracks, c0, c1, c2));

        foreach (byte[] file in new[] { withCues, withoutCues })
        {
            using MatroskaDemuxer d = Open(file);
            Assert.True(d.CanSeek);
            Assert.Equal(new MediaTime(1000, 1000), d.Seek(new MediaTime(1500, 1000)));
            Assert.True(d.TryReadPacket(out PacketView p));
            Assert.Equal(new MediaTime(1000, 1000), p.Info.Pts);
            Assert.Equal(new MediaTime(2000, 1000), d.Seek(new MediaTime(1500, 1000), SeekMode.NextKeyframe));
            Assert.True(d.TryReadPacket(out p));
            Assert.Equal(new MediaTime(2000, 1000), p.Info.Pts);
            Assert.Equal(MediaTime.Unknown, d.Seek(new MediaTime(5000, 1000), SeekMode.NextKeyframe));
            Assert.False(d.TryReadPacket(out _));
            Assert.Equal(new MediaTime(0, 1000), d.Seek(new MediaTime(-5, 1000)));
            Assert.Equal(3, ReadAll(d).Count);
        }
    }

    [Fact]
    public void ChecksumMismatchIsADefectNotAnException()
    {
        byte[] Build(bool corrupt) => Cat(Header(), SegmentElement(WithCrc(Info, corrupt, U(0x2AD7B1, 1_000_000), F64(0x4489, 10)), TracksElement(Track(1, 1, "V_VP8")), ClusterElement(0, Simple1(0, true, Fill(3, 1)))));
        DefectLog good = new();
        using (MatroskaDemuxer d = Open(Build(false), good))
        {
            Assert.Equal(new MediaTime(10, 1000), d.Duration);
        }

        Assert.Empty(good);
        DefectLog bad = new();
        using MatroskaDemuxer d2 = Open(Build(true), bad);
        Assert.Equal(new MediaTime(10, 1000), d2.Duration);
        MediaDefect defect = Assert.Single(bad);
        Assert.Equal(DefectKind.ChecksumMismatch, defect.Kind);
        Assert.True(d2.TryReadPacket(out _));
    }

    [Fact]
    public void DamageBetweenClustersResyncsAtTheNextCluster()
    {
        byte[] c0 = ClusterElement(0, Simple1(0, true, Fill(3, 1)));
        byte[] c1 = ClusterElement(1000, Simple1(0, true, Fill(3, 2)));
        byte[] head = Cat(Header(), SegmentUnknown(InfoElement(), TracksElement(Track(1, 1, "V_VP8"))));
        byte[] junk = [0x00, 0x00, 0x00, 0x12, 0x34, 0x00, 0x00, 0x00];
        byte[] file = Cat(head, c0, junk, c1);
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        List<Pkt> p = ReadAll(d);
        Assert.Equal(2, p.Count);
        Assert.Equal(new MediaTime(1000, 1000), p[1].Info.Pts);
        MediaDefect resync = Assert.Single(defects, x => x.Kind == DefectKind.UnaccountedBytes);
        Assert.Equal(head.Length + c0.Length, resync.Offset);
        Assert.Contains(defects, x => x.Kind == DefectKind.InvalidSize && x.Offset == head.Length + c0.Length);
    }

    [Fact]
    public void ElementPastTheClusterEndResyncs()
    {
        // The second block claims to run far beyond its Cluster.
        byte[] bad = Cat([0xA3, 0x4F, 0xFF], Fill(10, 1));
        byte[] c0 = El(Cluster, U(0xE7, 0), Simple1(0, true, Fill(3, 1)), bad);
        byte[] c1 = ClusterElement(1000, Simple1(0, true, Fill(3, 2)));
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP8")), c0, c1));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        Assert.Equal(2, ReadAll(d).Count);
        Assert.Contains(defects, x => x.Kind == DefectKind.SizeExceedsParent);
    }

    [Fact]
    public void TruncationAtEveryOffsetNeverThrowsUntyped()
    {
        byte[] file = BasicFile(out _);
        for (int cut = 0; cut < file.Length; cut++)
        {
            byte[] part = file.AsSpan(0, cut).ToArray();
            try
            {
                using MatroskaDemuxer d = Open(part);
                ReadAll(d);
            }
            catch (MediaException)
            {
                // typed: fine
            }
        }
    }

    [Fact]
    public void EverySingleByteCorruptionStaysTyped()
    {
        byte[] file = Cat(BasicFile(out _));
        byte[] copy = new byte[file.Length];
        for (int i = 0; i < file.Length; i++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF, 0x7F, 0x80 })
            {
                file.CopyTo(copy, 0);
                copy[i] = value;
                try
                {
                    using MatroskaDemuxer d = Open(copy);
                    ReadAll(d);
                }
                catch (MediaException)
                {
                }
            }
        }
    }

    [Fact]
    public void LimitsApplyBeforeAllocating()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP8"), Track(2, 1, "V_VP8"), Track(3, 1, "V_VP8")), ClusterElement(0, Simple1(0, true, Fill(100, 1)))));
        Assert.Throws<MediaLimitExceededException>(() => Open(file, limits: new MediaLimits { MaxTracks = 2 }));
        using MatroskaDemuxer d = Open(file, limits: new MediaLimits { MaxPacketBytes = 50 });
        Assert.Throws<MediaLimitExceededException>(() => d.TryReadPacket(out _));

        // A lace announcing 255 frames when the limit is 4 is a limit error, not an allocation.
        byte[] lace = Cat([254], Fill(300, 1));
        byte[] laced = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 2, "A_AAC")), ClusterElement(0, Simple(1, 0, 0x04, lace))));
        using MatroskaDemuxer d2 = Open(laced, limits: new MediaLimits { MaxLacedFrames = 4 });
        Assert.Throws<MediaLimitExceededException>(() => d2.TryReadPacket(out _));
    }

    [Fact]
    public void ABlockSizeFarPastTheEndOfTheFileDoesNotAllocateIt()
    {
        // A block that claims 100 MB (under the packet limit) in a file that ends right after it.
        byte[] hostile = Cat([0xA3], Size(100_000_000, 8), Fill(5, 1));
        byte[] file = Cat(Header(), SegmentUnknown(InfoElement(), TracksElement(Track(1, 1, "V_VP8")), ElUnknown(Cluster, U(0xE7, 0), hostile)));
        DefectLog defects = new();
        using MatroskaDemuxer d = Open(file, defects);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(d.TryReadPacket(out _));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2_000_000);
        Assert.Contains(defects, x => x.Kind == DefectKind.Truncated);

        // The same through a forward-only source, where the length is not known up front.
        DefectLog forwardDefects = new();
        using MatroskaDemuxer f = MatroskaDemuxer.Open(new StreamForwardSource(new NonSeekableStream(file)), new MediaOpenOptions { Defects = forwardDefects });
        long before2 = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(f.TryReadPacket(out _));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before2 < 2_000_000);
        Assert.Contains(forwardDefects, x => x.Kind == DefectKind.Truncated);

        // Beyond the packet limit it is a limit error before anything is read.
        byte[] huge = Cat([0xA3], Size(200_000_000_000L, 8), Fill(5, 1));
        byte[] file2 = Cat(Header(), SegmentUnknown(InfoElement(), TracksElement(Track(1, 1, "V_VP8")), ElUnknown(Cluster, U(0xE7, 0), huge)));
        using MatroskaDemuxer g = Open(file2);
        Assert.Throws<MediaLimitExceededException>(() => g.TryReadPacket(out _));
    }

    [Fact]
    public void RefusesDataThatIsNotMatroska()
    {
        Assert.Throws<MediaFormatException>(() => Open("this is not an EBML file at all"u8.ToArray()));
        Assert.Throws<MediaFormatException>(() => Open([]));
        byte[] newer = Cat(El(EbmlHeader, U(0x42F7, 2), S(0x4282, "matroska")), SegmentElement());
        Assert.Throws<UnsupportedMediaException>(() => Open(newer));
        byte[] noTracks = Cat(Header(), SegmentElement(InfoElement(), ClusterElement(0, Simple1(0, true, Fill(3, 1)))));
        Assert.Throws<MediaFormatException>(() => Open(noTracks));
    }

    [Fact]
    public void TheFormatSniffsAndOpensThroughTheRegistry()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new MatroskaFormat());
        using IDemuxer d = registry.Open(new MemoryMediaSource(BasicFile(out _)));
        Assert.Equal(2, d.Tracks.Count);
        Assert.Equal(SniffConfidence.Certain, new MatroskaFormat().Sniff(BasicFile(out _)).Confidence);
    }

    [Fact]
    public async Task TheAsyncDemuxerReadsTheSamePackets()
    {
        byte[] file = BasicFile(out _);
        using MatroskaDemuxer sync = Open(file);
        List<Pkt> expected = ReadAll(sync);
        await using MatroskaAsyncDemuxer d = await MatroskaAsyncDemuxer.OpenAsync(new MemoryMediaSource(file));
        List<Pkt> got = [];
        while (await d.ReadPacketAsync())
        {
            got.Add(new Pkt(d.Current.Info, d.Current.Data.ToArray()));
        }

        Assert.Equal(expected.Select(x => (x.Info, x.Data.Length)).ToArray(), got.Select(x => (x.Info, x.Data.Length)).ToArray());
        await using MatroskaAsyncDemuxer fwd = await MatroskaAsyncDemuxer.OpenAsync(new StreamForwardSource(new NonSeekableStream(file)));
        int n = 0;
        while (await fwd.ReadPacketAsync())
        {
            n++;
        }

        Assert.Equal(expected.Count, n);
    }

    [Fact]
    public void PullPathAllocatesNothingPerPacketLacedOrNot()
    {
        List<byte[]> blocks = [];
        for (int i = 0; i < 4000; i++)
        {
            blocks.Add(Simple1((short)(i % 100), i % 25 == 0, Fill(200 + (i % 7), (byte)i)));
            blocks.Add(Simple(2, (short)(i % 100), 0x82, Cat([3], [10, 11, 12], Fill(10, 1), Fill(11, 2), Fill(12, 3), Fill(13, 4)))); // Xiph lace of 4
            blocks.Add(El(BlockGroup, BlockBytes(1, (short)(i % 100), 0, Fill(50, 7)), U(0x9B, 40), El(0xFB, [0x81])));
        }

        byte[] file = Cat(Header(), SegmentElement(InfoElement(), TracksElement(Track(1, 1, "V_VP8"), Track(2, 2, "A_AAC", U(0x23E383, 21_000_000))), ClusterElement(0, blocks.ToArray())));
        using MatroskaDemuxer d = Open(file);
        for (int i = 0; i < 300; i++)
        {
            Assert.True(d.TryReadPacket(out _));
        }

        // Async state machines are heap objects in an unoptimised build; the zero-allocation guarantee is for optimised builds.
        Assert.SkipWhen(System.Diagnostics.Debugger.IsAttached || typeof(MatroskaDemuxer).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).Cast<System.Diagnostics.DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled), "needs an optimised build");
        long sum = 0;
        int count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long last = before;
        int[] where = new int[16];
        long[] sizes = new long[16];
        int hits = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            sum += p.Data.Length + p.Info.Pts.Ticks;
            count++;
            long now = GC.GetAllocatedBytesForCurrentThread();
            if (now != last && hits < where.Length)
            {
                where[hits] = count;
                sizes[hits] = now - last;
                hits++;
            }

            last = now;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(count > 10_000);
        Assert.NotEqual(0, sum);
        // On failure, name the packets after which the allocation counter moved, so a one-off allocation can be told from a per-packet one.
        var detail = new System.Text.StringBuilder();
        for (int i = 0; i < hits; i++)
        {
            detail.Append("packet ").Append(where[i]).Append(": ").Append(sizes[i]).Append(" B; ");
        }

        Assert.True(allocated == 0, $"{allocated} B allocated over {count} packets; {detail}");
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        // Hand out at most 7 bytes at a time, like a pipe, so short reads are exercised.
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7));

        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 7)]);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

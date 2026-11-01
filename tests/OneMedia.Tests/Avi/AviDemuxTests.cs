using System.Text;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tests.Avi;

public class AviDemuxTests
{
    private static byte[] Payload(int length, int seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)((seed * 31) + i);
        }

        return b;
    }

    // Ten video frames at 25 fps (key frames at 0 and 5) interleaved with PCM chunks of 1,920 samples (48 kHz, 16-bit mono).
    private static AviBuilder Sample()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "FMP4", 1, 25, 0, AviBuilder.Bitmap(320, 240, "FMP4")));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 48000, 2, AviBuilder.Wave(1, 1, 48000, 2, 16), "Sound"));
        for (int i = 0; i < 10; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(100 + i, i), i % 5 == 0));
            b.Chunks.Add(new AviBuilder.ChunkSpec(1, Payload(3840, 100 + i), true, "wb"));
        }

        return b;
    }

    private static List<(PacketInfo Info, byte[] Data)> ReadAll(AviDemuxer d)
    {
        List<(PacketInfo, byte[])> all = [];
        while (d.TryReadPacket(out PacketView p))
        {
            all.Add((p.Info, p.Data.ToArray()));
        }

        return all;
    }

    [Fact]
    public void ReadsTracksAndPacketsInFileOrderWithIndexFlags()
    {
        AviBuilder b = Sample();
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal(MediaFormatId.Avi, d.Format);
        Assert.Equal(2, d.Tracks.Count);
        MediaTrack v = d.Tracks[0];
        Assert.Equal(TrackKind.Video, v.Kind);
        Assert.Equal("mpeg4", v.Codec.Name);
        Assert.Equal(320, v.Video!.Value.Width);
        Assert.Equal(25, v.Timescale);
        MediaTrack a = d.Tracks[1];
        Assert.Equal(TrackKind.Audio, a.Kind);
        Assert.Equal(CodecId.Pcm, a.Codec);
        Assert.Equal(48000, a.Audio!.Value.SampleRate);
        Assert.Equal("Sound", a.Name);

        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(20, packets.Count);
        for (int i = 0; i < 10; i++)
        {
            (PacketInfo vi, byte[] vd) = packets[2 * i];
            Assert.Equal(0, vi.TrackIndex);
            Assert.Equal(new MediaTime(i, 25), vi.Pts);
            Assert.Equal(vi.Pts, vi.Dts);
            Assert.Equal(new MediaTime(1, 25), vi.Duration);
            Assert.Equal(i % 5 == 0, vi.IsKeyframe);
            Assert.Equal(Payload(100 + i, i), vd);
            (PacketInfo ai, byte[] ad) = packets[(2 * i) + 1];
            Assert.Equal(new MediaTime(i * 1920, 48000), ai.Pts);
            Assert.Equal(new MediaTime(1920, 48000), ai.Duration);
            Assert.Equal(Payload(3840, 100 + i), ad);
        }

        Assert.Empty(d.Defects);
    }

    [Fact]
    public void SourceRangePointsAtThePayload()
    {
        byte[] file = Sample().Build();
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        foreach ((PacketInfo info, byte[] data) in ReadAll(d))
        {
            Assert.Equal(data, file.AsSpan((int)info.SourceRange.Offset, (int)info.SourceRange.Length).ToArray());
        }
    }

    [Fact]
    public void AbsoluteIndexOffsetsAreRecognised()
    {
        AviBuilder b = Sample();
        b.AbsoluteIdx1 = true;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.True(d.HasIndex);
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal([true, false, false, false, false, true, false, false, false, false], packets.Where(p => p.Info.TrackIndex == 0).Select(p => p.Info.IsKeyframe));
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void RecListsAreEntered()
    {
        AviBuilder b = Sample();
        b.RecLists = true;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal(20, ReadAll(d).Count);
    }

    [Fact]
    public void FileWithoutIndexReportsEveryPacketAsKey()
    {
        AviBuilder b = Sample();
        b.WriteIdx1 = false;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.False(d.HasIndex);
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(20, packets.Count);
        Assert.All(packets, p => Assert.True(p.Info.IsKeyframe));
    }

    [Fact]
    public void IndexPointingAtNothingIsReportedAndIgnored()
    {
        byte[] file = Sample().Build();
        int idx = file.AsSpan().LastIndexOf("idx1"u8);
        for (int i = idx + 8; i < file.Length; i += 16)
        {
            file[i + 8] ^= 0x55; // corrupt each entry's offset
        }

        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(20, ReadAll(d).Count);
        Assert.Contains(d.Defects, x => x.Code == "avi.idx1-offsets" && x.Offset == idx + 8);
    }

    [Fact]
    public void TruncatedChunkGivesACorruptPacketAndADefectWithItsOffset()
    {
        byte[] file = Sample().Build();
        int cut = file.AsSpan().IndexOf(Encoding.ASCII.GetBytes("00dc")) + 8; // start of the first video payload
        int third = cut;
        for (int i = 0; i < 5; i++)
        {
            third = file.AsSpan(third).IndexOf(Encoding.ASCII.GetBytes("00dc")) + third + 4;
        }

        byte[] shortFile = file[..(third + 4 + 30)];
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(shortFile));
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        PacketInfo last = packets[^1].Info;
        Assert.True((last.Flags & PacketAttributes.Corrupt) != 0);
        Assert.Contains(d.Defects, x => x.Kind == DefectKind.Truncated);
    }

    [Fact]
    public void ForwardOnlyReadingGivesTheSamePacketsWithoutKeyFlags()
    {
        byte[] file = Sample().Build();
        using AviDemuxer seekable = AviDemuxer.Open(new MemoryMediaSource(file));
        using AviDemuxer forward = AviDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        List<(PacketInfo Info, byte[] Data)> a = ReadAll(seekable);
        List<(PacketInfo Info, byte[] Data)> f = ReadAll(forward);
        Assert.Equal(a.Count, f.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Info.Pts, f[i].Info.Pts);
            Assert.Equal(a[i].Info.TrackIndex, f[i].Info.TrackIndex);
            Assert.Equal(a[i].Data, f[i].Data);
            Assert.Equal(a[i].Info.SourceRange, f[i].Info.SourceRange);
        }

        Assert.False(forward.CanSeek);
        Assert.Throws<NotSupportedException>(() => forward.Seek(MediaTime.Zero));
    }

    [Fact]
    public void SeekFindsThePreviousAndNextKeyFrame()
    {
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(Sample().Build()));
        MediaTime got = d.Seek(new MediaTime(8, 25), SeekMode.PreviousKeyframe);
        Assert.Equal(new MediaTime(5, 25), got);
        Assert.True(d.TryReadPacket(out PacketView p));
        // The next chunk in the file from the key frame at 5 is that frame itself.
        Assert.Equal(new MediaTime(5, 25), p.Info.Pts);
        Assert.True(p.Info.IsKeyframe);
        Assert.True(d.TryReadPacket(out PacketView audio));
        Assert.Equal(1, audio.Info.TrackIndex);
        Assert.Equal(new MediaTime(5 * 1920, 48000), audio.Info.Pts);

        Assert.Equal(new MediaTime(5, 25), d.Seek(new MediaTime(3, 25), SeekMode.NextKeyframe));
        Assert.Equal(MediaTime.Unknown, d.Seek(new MediaTime(9, 25), SeekMode.NextKeyframe));
    }

    [Fact]
    public void DisabledTracksAreSkippedButTheirClocksAdvance()
    {
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(Sample().Build()));
        d.SetTrackEnabled(0, false);
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(10, packets.Count);
        Assert.All(packets, p => Assert.Equal(1, p.Info.TrackIndex));
        Assert.Equal(new MediaTime(9 * 1920, 48000), packets[^1].Info.Pts);
    }

    [Fact]
    public void ZeroLengthChunksAreDroppedFramesThatTakeATickOnVideoOnly()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 10, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1152, 44100, 0, AviBuilder.Wave(0x55, 2, 44100, 1, 0)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(10, 1)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, [], false));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(10, 2)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(1, Payload(20, 1), true, "wb"));
        b.Chunks.Add(new AviBuilder.ChunkSpec(1, [], true, "wb"));
        b.Chunks.Add(new AviBuilder.ChunkSpec(1, Payload(20, 2), true, "wb"));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(4, packets.Count);
        Assert.Equal(new MediaTime(2, 10), packets[1].Info.Pts);
        Assert.Equal(new MediaTime(1152, 44100), packets[3].Info.Pts);
    }

    [Fact]
    public void OddSizedChunksArePaddedToEvenOffsets()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 10, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        for (int i = 0; i < 5; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(11 + (2 * i), i)));
        }

        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(Payload(11 + (2 * i), i), packets[i].Data);
        }
    }

    [Fact]
    public void OpenDmlFileContinuesInAvixAndTakesKeyFlagsFromStandardIndexes()
    {
        AviBuilder b = Sample();
        byte[] file = b.BuildOpenDml(chunksPerRiff: 6);
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.True(d.HasIndex);
        Assert.NotNull(d.Headers.Streams[0].SuperIndex);
        Assert.Equal(4, d.Headers.Streams[0].SuperIndex!.Count);
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(20, packets.Count);
        Assert.Equal([true, false, false, false, false, true, false, false, false, false], packets.Where(p => p.Info.TrackIndex == 0).Select(p => p.Info.IsKeyframe));
        Assert.Equal(Payload(100 + 9, 9), packets.Last(p => p.Info.TrackIndex == 0).Data);
        Assert.Equal(new MediaTime(9 * 1920, 48000), packets[^1].Info.Pts);
        Assert.Empty(d.Defects);

        // The structure tree lists the first RIFF's lists and sees the chunk after the end of it as a second RIFF at top level.
        IStructureNode root = d.Structure!;
        Assert.Contains(root.Children, c => c is RiffChunk { ListType: "movi" });
    }

    [Fact]
    public void OpenDmlSeekWorksAcrossRiffChunks()
    {
        byte[] file = Sample().BuildOpenDml(chunksPerRiff: 6);
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(new MediaTime(5, 25), d.Seek(new MediaTime(7, 25)));
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(new MediaTime(5, 25), p.Info.Pts);
    }

    [Fact]
    public void RiffSizeThatClaimsMoreThanTheFileIsClamped()
    {
        byte[] file = Sample().Build();
        file[4] = 0xFF;
        file[5] = 0xFF;
        file[6] = 0xFF;
        file[7] = 0xFF;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(20, ReadAll(d).Count);
        Assert.Contains(d.Defects, x => x.Code == "riff.unbounded-size" && x.Offset == 0);
    }

    [Fact]
    public void ChunksAfterAMoviListThatEndsTooSoonAreStillRead()
    {
        byte[] file = Sample().Build();
        int movi = file.AsSpan().IndexOf("movi"u8) - 8;
        file[movi + 4] = 20; // movi claims 16 bytes of data
        file[movi + 5] = 0;
        file[movi + 6] = 0;
        file[movi + 7] = 0;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(20, ReadAll(d).Count);
        Assert.Contains(d.Defects, x => x.Code == "avi.movi-size-too-small");
    }

    [Fact]
    public void ChunkForAnUndeclaredStreamIsSkippedWithOneDefect()
    {
        AviBuilder b = Sample();
        b.Chunks.Insert(3, new AviBuilder.ChunkSpec(5, Payload(10, 9)));
        b.Chunks.Insert(4, new AviBuilder.ChunkSpec(5, Payload(10, 9)));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal(20, ReadAll(d).Count);
        Assert.Equal(1, d.Defects.Count(x => x.Code == "avi.unknown-stream"));
    }

    [Fact]
    public void FileThatIsNotAviIsRejectedWithATypedException()
    {
        byte[] file = Sample().Build();
        file[8] = (byte)'W';
        Assert.Throws<MediaFormatException>(() => AviDemuxer.Open(new MemoryMediaSource(file)));
        Assert.Throws<MediaFormatException>(() => AviDemuxer.Open(new MemoryMediaSource(new byte[64])));
    }

    [Fact]
    public void HeaderListIsRequired()
    {
        byte[] file = AviBuilder.List("RIFF", "AVI ", AviBuilder.Chunk("JUNK", new byte[20]));
        MediaFormatException e = Assert.Throws<MediaFormatException>(() => AviDemuxer.Open(new MemoryMediaSource(file)));
        Assert.Equal(12, e.Offset);
    }

    [Fact]
    public void HugeStreamFormatIsNotAllocatedBeyondTheCap()
    {
        AviBuilder b = Sample();
        byte[] file = b.Build();
        int strf = file.AsSpan().IndexOf("strf"u8);
        file[strf + 4] = 0xFF;
        file[strf + 5] = 0xFF;
        file[strf + 6] = 0xFF;
        file[strf + 7] = 0x7F;
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        Assert.True(d.Tracks.Count >= 1);
        Assert.NotEmpty(d.Defects);
    }

    [Fact]
    public void StructureTreeKeepsUnknownChunksAndJunk()
    {
        AviBuilder b = Sample();
        b.InfoList = AviBuilder.List("LIST", "INFO", AviBuilder.Chunk("INAM", Encoding.ASCII.GetBytes("Title\0")), AviBuilder.Chunk("JUNK", new byte[7]), AviBuilder.Chunk("ZZZZ", [1, 2, 3]));
        byte[] file = b.Build();
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        RiffChunk info = (RiffChunk)d.Structure!.Children.Single(c => c is RiffChunk { ListType: "INFO" });
        Assert.Equal(["INAM", "JUNK", "ZZZZ"], info.Children.Select(c => ((RiffChunk)c).FourCc));
        Assert.Equal(1, ((RiffChunk)info.Children[1]).PayloadSize & 1);
        Assert.Equal("RIFF", ((RiffChunk)d.Structure!).FourCc);
    }

    [Fact]
    public void RiffInfoAndIditAreReadAsNativeTagsAndFacts()
    {
        AviBuilder b = Sample();
        b.InfoList = AviBuilder.List("LIST", "INFO", AviBuilder.Chunk("INAM", Encoding.ASCII.GetBytes("Deposition\0")), AviBuilder.Chunk("ISFT", Encoding.ASCII.GetBytes("Lavf\0")),
            AviBuilder.Chunk("ICMT", [0x63, 0x61, 0x66, 0xE9, 0]));
        b.Idit = AviBuilder.Chunk("IDIT", Encoding.ASCII.GetBytes("Tue Mar 08 21:11:35 2005\n\0"));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Metadata.MediaMetadata m = d.ReadMetadata();
        Assert.Equal("Deposition", m["riff-info", "INAM"]!.Text);
        Assert.Equal("caf\u00E9", m["riff-info", "ICMT"]!.Text); // Latin-1 when the bytes are not UTF-8
        Assert.Equal("Deposition", m.Tags.Title);
        Assert.Equal(new DateTime(2005, 3, 8, 21, 11, 35), m.Facts.CaptureTime.Candidates[0].Value.Value);
        Assert.Equal(TimeZoneCertainty.Unknown, m.Facts.CaptureTime.Candidates[0].Value.Certainty);
        Assert.Equal("Lavf", m.Facts.Software.Candidates[0].Value);
    }

    [Fact]
    public void PullPathAllocatesNothingPerPacket()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 25, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 48000, 2, AviBuilder.Wave(1, 1, 48000, 2, 16)));
        for (int i = 0; i < 6000; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(900, i), i % 10 == 0));
            b.Chunks.Add(new AviBuilder.ChunkSpec(1, Payload(3840, i), true, "wb"));
        }

        byte[] file = b.Build();
        // Steady state: warm up, then at least one of a few full passes on fresh demuxers must allocate nothing (a pool refill or tiering
        // allocation on a pass is not a per-packet cost).
        long best = long.MaxValue;
        for (int pass = 0; pass < 5 && best != 0; pass++)
        {
            using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
            Assert.True(d.TryReadPacket(out _));
            long allocated = Drain(d, out long count);
            Assert.Equal(11_999, count);
            if (pass > 0)
            {
                best = Math.Min(best, allocated);
            }
        }

        Assert.Equal(0, best);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long Drain(AviDemuxer d, out long count)
    {
        count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (d.TryReadPacket(out PacketView view))
        {
            count += view.Data.Length > 0 ? 1 : 0;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void WithoutAnIndexKeyFramesComeFromThePictureHeader()
    {
        // MPEG-4 Part 2: VOP start code, then vop_coding_type in the top two bits (0 = I, 1 = P, 2 = B).
        static byte[] Vop(int type) => [0, 0, 1, 0xB6, (byte)(type << 6), 0x55, 0x55, 0x55];
        // H.264 Annex B: an IDR slice (type 5) or a non-IDR slice (type 1) behind an access unit delimiter.
        static byte[] Nal(int type) => [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, (byte)(0x60 | type), 0x88, 0x84];
        AviBuilder b = new() { WriteIdx1 = false, MainFlags = 0 };
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "FMP4", 1, 25, 0, AviBuilder.Bitmap(8, 8, "FMP4")));
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "H264", 1, 25, 0, AviBuilder.Bitmap(8, 8, "H264")));
        foreach (int t in new[] { 0, 1, 2, 1, 0 })
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, Vop(t)));
        }

        foreach (int t in new[] { 5, 1, 1, 5 })
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(1, Nal(t)));
        }

        foreach (bool forwardOnly in new[] { false, true })
        {
            byte[] file = b.Build();
            using AviDemuxer d = forwardOnly ? AviDemuxer.Open(new StreamForwardSource(new MemoryStream(file))) : AviDemuxer.Open(new MemoryMediaSource(file));
            List<(int Track, bool Key)> seen = [];
            while (d.TryReadPacket(out PacketView p))
            {
                seen.Add((p.Info.TrackIndex, p.Info.IsKeyframe));
            }

            Assert.Equal([true, false, false, false, true], seen.Where(s => s.Track == 0).Select(s => s.Key));
            Assert.Equal([true, false, false, true], seen.Where(s => s.Track == 1).Select(s => s.Key));
        }
    }

    [Fact]
    public void AudioWithoutASampleSizeCountsChunksAndAacCarriesItsConfig()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1152, 44100, 0, AviBuilder.Wave(0x55, 2, 44100, 1, 0)));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1024, 44100, 0, AviBuilder.Wave(0xFF, 2, 44100, 1, 0, [0x12, 0x10])));
        for (int i = 0; i < 3; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(417, i), true, "wb"));
            b.Chunks.Add(new AviBuilder.ChunkSpec(1, Payload(200, i), true, "wb"));
        }

        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal(CodecId.Mp3, d.Tracks[0].Codec);
        Assert.Equal(CodecId.Aac, d.Tracks[1].Codec);
        Assert.Equal("asc", d.Tracks[1].Configuration.Format);
        Assert.Equal([0x12, 0x10], d.Tracks[1].Configuration.Data.ToArray());
        List<(PacketInfo Info, byte[] Data)> packets = ReadAll(d);
        Assert.Equal(new MediaTime(2 * 1152, 44100), packets[4].Info.Pts);
        Assert.Equal(new MediaTime(1152, 44100), packets[4].Info.Duration);
    }

    [Fact]
    public void DeclaredStreamStartOffsetsTimes()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 25, 0, AviBuilder.Bitmap(8, 8, "MJPG"), Start: 50));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, Payload(10, 1)));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal(new MediaTime(50, 25), d.Tracks[0].Edit.StartTime);
    }

    [Fact]
    public async Task RangedSourcesOpenAsynchronouslyAndGiveTheSamePackets()
    {
        byte[] file = Sample().Build();
        using AviDemuxer sync = AviDemuxer.Open(new MemoryMediaSource(file));
        List<(PacketInfo Info, byte[] Data)> expected = ReadAll(sync);
        await using MediaFile media = await MediaFile.OpenAsync(new RangedMediaSource(file.Length, (offset, buffer, _) =>
        {
            int n = (int)Math.Min(buffer.Length, file.Length - offset);
            file.AsMemory((int)offset, n).CopyTo(buffer);
            return new ValueTask<int>(n);
        }));
        Assert.Equal(MediaFormatId.Avi, media.Format);
        IAsyncDemuxer d = media.AsyncDemuxer!;
        int i = 0;
        while (await d.ReadPacketAsync())
        {
            Assert.Equal(expected[i].Info, d.Current.Info);
            Assert.True(d.Current.Data.SequenceEqual(expected[i].Data));
            i++;
        }

        Assert.Equal(expected.Count, i);
    }
}

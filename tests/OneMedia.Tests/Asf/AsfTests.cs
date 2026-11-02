using RecursiveAI.OneMedia.Asf;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using static RecursiveAI.OneMedia.Tests.AsfBuilder;

namespace RecursiveAI.OneMedia.Tests;

[Collection(AllocationMeasurements.Name)]
public class AsfTests
{
    private static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(seed + i))];

    // Audio on stream 1, video on stream 2: three whole objects, one video object in two fragments, one compressed audio payload.
    private static byte[] Sample(Action<AsfBuilder>? extra = null, bool protectedStream = false)
    {
        AsfBuilder b = new();
        b.AddFileProperties().AddAudioStream(1).AddVideoStream(2, "WMV3", 320, 240, protectedStream);
        extra?.Invoke(b);
        b.AddPacket(true, new Payload(2, true, 1, 0, 100, 1000, Bytes(100, 1)), new Payload(1, false, 1, 0, 40, 1100, Bytes(40, 2)));
        b.AddPacket(false, new Payload(2, false, 2, 0, 300, 1200, Bytes(200, 3)));
        b.AddPacket(false, new Payload(2, false, 2, 200, 300, 1200, Bytes(100, 203)), new Payload(1, false, 2, 0, 0, 1300, [2, 1, 1, 2, 2, 2, 2, 3, 3], Compressed: true, Delta: 20));
        b.AddPacket(false, new Payload(2, true, 3, 0, 50, 2000, Bytes(50, 4)));
        return b.Build();
    }

    private static List<(PacketInfo Info, byte[] Data)> ReadAll(IDemuxer d)
    {
        List<(PacketInfo, byte[])> all = [];
        while (d.TryReadPacket(out PacketView v))
        {
            all.Add((v.Info, v.Data.ToArray()));
        }

        return all;
    }

    [Fact]
    public void ReadsTracksPacketsAndTimes()
    {
        byte[] file = Sample();
        using IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(file));
        Assert.Equal(MediaFormatId.Asf, d.Format);
        Assert.Equal(2, d.Tracks.Count);
        Assert.Equal(CodecId.WmaV2, d.Tracks[0].Codec);
        Assert.Equal(44100, d.Tracks[0].Audio!.Value.SampleRate);
        Assert.Equal([1, 2, 3, 4], d.Tracks[0].Configuration.Data.ToArray());
        Assert.Equal(CodecId.Wmv3, d.Tracks[1].Codec);
        Assert.Equal(320, d.Tracks[1].Video!.Value.Width);
        Assert.Equal(new MediaTime(9000, 1000), d.Duration); // play duration 10 s less the 1 s preroll

        var packets = ReadAll(d);
        // Completion order: video 1, audio 1, (video 2 is still partial), video 2 completes, then the three compressed audio pieces, video 3.
        Assert.Equal([(1, 0L), (0, 100L), (1, 200L), (0, 300L), (0, 320L), (0, 340L), (1, 1000L)], packets.Select(p => (p.Info.TrackIndex, p.Info.Pts.Ticks)));
        Assert.True(packets[0].Info.IsKeyframe);
        Assert.True(packets[1].Info.IsKeyframe); // audio frames always stand alone
        Assert.False(packets[2].Info.IsKeyframe);
        Assert.Equal(Bytes(100, 1), packets[0].Data);
        Assert.Equal([.. Bytes(200, 3), .. Bytes(100, 203)], packets[2].Data);
        Assert.True(packets[2].Info.SourceRange.IsNone);
        Assert.False(packets[0].Info.SourceRange.IsNone);
        Assert.Equal(Bytes(100, 1), file.AsSpan((int)packets[0].Info.SourceRange.Offset, 100).ToArray());
        Assert.Equal(2, packets[3].Data.Length);
        Assert.DoesNotContain(d.Defects, x => x.Severity != DefectSeverity.Info);
    }

    [Fact]
    public void ForwardOnlyAndAsyncReadTheSamePackets()
    {
        byte[] file = Sample(b => b.AddContentDescription("Title", "Artist"));
        List<(PacketInfo Info, byte[] Data)> expected;
        using (IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(file)))
        {
            expected = ReadAll(d);
        }

        using IDemuxer forward = new AsfFormat().Open(new StreamForwardSource(new MemoryStream(file)), MediaOpenOptions.Default);
        List<(PacketInfo Info, byte[] Data)> got = ReadAll(forward);
        Assert.Equal(expected.Select(p => (p.Info.TrackIndex, p.Info.Pts, p.Data.Length)), got.Select(p => (p.Info.TrackIndex, p.Info.Pts, p.Data.Length)));
        Assert.False(forward.CanSeek);
        Assert.Equal("Title", ((IMetadataProvider)forward).ReadMetadata().Tags.Title);

        MemoryMediaSource source = new(file);
        IAsyncDemuxer asyncDemuxer = new AsfFormat().OpenAsync(source, MediaOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        int count = 0;
        while (asyncDemuxer.ReadPacketAsync().AsTask().GetAwaiter().GetResult())
        {
            Assert.Equal(expected[count].Data, asyncDemuxer.Current.Data.ToArray());
            count++;
        }

        Assert.Equal(expected.Count, count);
        Assert.Equal("Title", ((IAsyncMetadataProvider)asyncDemuxer).ReadMetadataAsync().AsTask().GetAwaiter().GetResult().Tags.Title);
    }

    [Fact]
    public void DisabledTracksAreSkipped()
    {
        using IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(Sample()));
        d.SetTrackEnabled(1, false);
        Assert.All(ReadAll(d), p => Assert.Equal(0, p.Info.TrackIndex));
    }

    [Fact]
    public void ProtectedFilesListTracksAndNoPackets()
    {
        using IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(Sample(b => b.AddEncryptionObject())));
        Assert.True(d.IsProtected);
        Assert.Equal(AsfProtection.ContentEncryption, ((AsfDemuxer)d).Protection);
        Assert.Equal(2, d.Tracks.Count);
        Assert.All(d.Tracks, t => Assert.True(t.IsProtected));
        Assert.False(d.TryReadPacket(out _));

        using IDemuxer e = MediaFormats.Create().Open(new MemoryMediaSource(Sample(protectedStream: true)));
        Assert.True(e.IsProtected);
        Assert.Equal(AsfProtection.EncryptedStream, ((AsfDemuxer)e).Protection);
    }

    // Found by the ASF fuzz target: a video format whose bitmap height is int.MinValue made Math.Abs throw.
    [Fact]
    public void BitmapHeightOfMinValueIsNotAnOverflow()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "asf", "asf-bitmap-height-min-value.bin"));
        try
        {
            using IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(input));
            while (d.TryReadPacket(out _))
            {
            }
        }
        catch (MediaException)
        {
            // A typed rejection is fine; any other exception fails the test.
        }
    }

    // Found by the ASF fuzz target: the length field of a payload reached into the padding, leaving a negative data length.
    [Fact]
    public void PayloadLengthFieldInsidePaddingIsNotAnException()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "asf", "asf-payload-length-in-padding.bin"));
        try
        {
            using IDemuxer d = MediaFormats.Create().Open(new MemoryMediaSource(input));
            while (d.TryReadPacket(out _))
            {
            }
        }
        catch (MediaException)
        {
            // A typed rejection is fine; any other exception fails the test.
        }
    }

    [Fact]
    public void TagsAreReadWithRanges()
    {
        byte[] image = Bytes(300, 7);
        byte[] file = Sample(b => b
            .AddContentDescription("Song", "Singer", "(c) me", "a comment")
            .AddExtendedContentDescription(Text("WM/AlbumTitle", "Album"), Text("WM/Genre", "Rock"), Dword("WM/Year", 1999), Text("WM/TrackNumber", "3/12"), new Attr("WM/Picture", 1, PictureValue(3, "image/jpeg", image)))
            .AddMetadataLibrary(Text("WM/Composer", "Composer")));
        using MediaFile m = MediaFile.Open(new MemoryMediaSource(file));
        CommonTags t = m.Tags;
        Assert.Equal("Song", t.Title);
        Assert.Equal("Singer", t.Artist);
        Assert.Equal("Album", t.Album);
        Assert.Equal("Rock", t.Genre);
        Assert.Equal("a comment", t.Description);
        Assert.Equal("(c) me", t.Copyright);
        Assert.Equal("Composer", t.Composer);
        Assert.Equal("1999", t.Date);
        Assert.Equal(3, t.TrackNumber);
        Assert.Equal(12, t.TrackTotal);
        NativeTag year = m.Native["asf", "WM/Year"]!;
        Assert.Equal(TagValueKind.Number, year.Kind);
        Assert.Equal(4, year.ValueRange.Length);
        TagPicture pic = Assert.Single(t.Pictures);
        Assert.Equal(PictureType.FrontCover, pic.Type);
        Assert.Equal("image/jpeg", pic.MimeType);
        Assert.Equal(image, pic.ReadData());
        Assert.Equal(image, file.AsSpan((int)pic.Source.ValueRange.Offset, image.Length).ToArray());
        Assert.NotNull(m.CaptureTime.Best); // File Properties creation date
    }

    [Fact]
    public void SeekFindsTheKeyframeBefore()
    {
        // Video keyframes in packets 0 and 3: presentation times 1000 and 2500, so 0 and 1500 ms after the 1000 ms preroll. The index counts
        // from the same zero and names the packet of the keyframe at or before each half second.
        AsfBuilder with = new();
        with.AddFileProperties().AddAudioStream(1).AddVideoStream(2);
        for (int i = 0; i < 6; i++)
        {
            with.AddPacket(false, new Payload(2, i % 3 == 0, (uint)i, 0, 10, (uint)(1000 + i * 500), Bytes(10, (byte)i)));
        }

        with.AddSimpleIndex(5_000_000, 0, 0, 0, 3, 3, 3, 3);
        using IDemuxer d = new AsfFormat().Open(new MemoryMediaSource(with.Build()), MediaOpenOptions.Default);
        Assert.True(d.CanSeek);
        Assert.Equal(new MediaTime(1500, 1000), d.Seek(new MediaTime(1600, 1000)));
        Assert.True(d.TryReadPacket(out PacketView first));
        Assert.True(first.Info.IsKeyframe);
        Assert.Equal(1500, first.Info.Pts.Ticks);
        Assert.Equal(3, first.Data[0]);
        Assert.Equal(new MediaTime(0, 1000), d.Seek(new MediaTime(1400, 1000)));
        Assert.True(d.TryReadPacket(out first));
        Assert.Equal(0, first.Info.Pts.Ticks);
    }

    [Fact]
    public void Mp3InAsfDecodesThroughTheAdapter()
    {
        // testdata/asf/mp3.wma: one second of 22.05 kHz mono MP3 in an ASF audio file (made with an ASF muxer from synthetic input).
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "asf", "mp3.wma");
        using OneAudio.IAudioDecoder d = OneAudio.AudioDecoder.OpenFile(path, RecursiveAI.OneMedia.Audio.MediaAudioExtensions.CreateRegistry());
        Assert.Equal(22050, d.Format.SampleRate);
        Assert.Equal(1, d.Format.Channels);
        float[] buffer = new float[4096];
        long frames = 0;
        int got;
        while ((got = d.Read(buffer)) > 0)
        {
            frames += got;
        }

        // 41 frames of 576 samples: the length another decoder gives for the same packets, to the frame.
        Assert.Equal(23616, frames);
    }

    [Fact]
    public void SeekWithoutAnIndexScansForKeyframes()
    {
        AsfBuilder b = new();
        b.AddFileProperties().AddVideoStream(2);
        for (int i = 0; i < 6; i++)
        {
            b.AddPacket(false, new Payload(2, i % 3 == 0, (uint)i, 0, 10, (uint)(1000 + i * 500), Bytes(10, (byte)i)));
        }

        using IDemuxer d = new AsfFormat().Open(new MemoryMediaSource(b.Build()), MediaOpenOptions.Default);
        Assert.Equal(new MediaTime(1500, 1000), d.Seek(new MediaTime(2400, 1000)));
        Assert.True(d.TryReadPacket(out PacketView v));
        Assert.Equal(3, v.Data[0]);
        Assert.Equal(new MediaTime(1500, 1000), d.Seek(new MediaTime(1400, 1000), SeekMode.NextKeyframe));
    }

    [Fact]
    public void DamagedFilesReportDefectsAndKeepWhatIsReadable()
    {
        byte[] file = Sample();
        // Cut inside the third packet.
        byte[] cut = file[..(file.Length - 2 * PacketSize - 100)];
        DefectLog log = new();
        using IDemuxer d = new AsfFormat().Open(new MemoryMediaSource(cut), new MediaOpenOptions { Defects = log });
        int n = ReadAll(d).Count;
        Assert.True(n >= 2);
        Assert.Contains(log, x => x.Kind == DefectKind.Truncated);

        // A header that claims more bytes than the file has.
        byte[] big = (byte[])file.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(big.AsSpan(16), 1UL << 40);
        DefectLog log2 = new();
        try
        {
            using IDemuxer d2 = new AsfFormat().Open(new MemoryMediaSource(big), new MediaOpenOptions { Defects = log2 });
            ReadAll(d2);
        }
        catch (MediaException)
        {
        }

        Assert.Contains(log2, x => x.Kind == DefectKind.SizeExceedsFile);

        Assert.Throws<MediaFormatException>(() => new AsfFormat().Open(new MemoryMediaSource(new byte[40]), MediaOpenOptions.Default));
    }

    [Fact]
    public void ObjectTreeHoldsRangesAndUnknownObjects()
    {
        byte[] file = Sample(b => b.AddMetadataLibrary(Text("WM/Composer", "x")).HeaderObjects.Add(Obj(Guid.Parse("01020304-0506-0708-090A-0B0C0D0E0F10"), [1, 2, 3])));
        using AsfDemuxer d = AsfDemuxer.Open(new MemoryMediaSource(file));
        AsfObjectNode root = d.Header.Root;
        Assert.Equal(0, root.Offset);
        Assert.Equal(30, root.HeaderSize);
        AsfObjectNode unknown = Assert.Single(root.Children.OfType<AsfObjectNode>(), c => c.Kind == AsfObjectKind.Unknown);
        Assert.Equal(27, ((IStructureNode)unknown).Range.Length);
        AsfObjectNode ext = Assert.Single(root.Children.OfType<AsfObjectNode>(), c => c.Kind == AsfObjectKind.HeaderExtension);
        Assert.Single(ext.Children);
        Assert.Equal(AsfObjectKind.MetadataLibrary, ((AsfObjectNode)ext.Children[0]).Kind);
    }

    [Trait("Browser", "allocation")]
    [Fact]
    public void PullPathAllocatesNothingPerPacket()
    {
        AllocationGuard.RequireOptimised();
        AsfBuilder b = new();
        b.AddFileProperties().AddAudioStream(1).AddVideoStream(2);
        for (int i = 0; i < 400; i += 2)
        {
            // fragmented objects first, so the reassembly buffers exist before the measured loop starts
            b.AddPacket(false, new Payload(2, false, (uint)i, 0, 300, (uint)(1000 + i * 33), Bytes(150, 1)));
            b.AddPacket(false, new Payload(2, false, (uint)i, 150, 300, (uint)(1000 + i * 33), Bytes(150, 151)));
        }

        for (int i = 0; i < 400; i++)
        {
            b.AddPacket(false, new Payload(2, true, (uint)i, 0, 100, (uint)(1000 + i * 33), Bytes(100, (byte)i)), new Payload(1, false, (uint)i, 0, 50, (uint)(1000 + i * 33), Bytes(50, 1)));
        }

        byte[] file = b.Build();
        using (IDemuxer warm = new AsfFormat().Open(new MemoryMediaSource(file), MediaOpenOptions.Default))
        {
            Drain(warm, out _);
        }

        long allocated = AllocationGuard.Best(() =>
        {
            using IDemuxer d = new AsfFormat().Open(new MemoryMediaSource(file), MediaOpenOptions.Default);
            for (int i = 0; i < 6; i++)
            {
                Assert.True(d.TryReadPacket(out _));
            }

            long used = Drain(d, out long checksum);
            Assert.NotEqual(0, checksum);
            return used;
        });
        Assert.Equal(0, allocated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long Drain(IDemuxer demuxer, out long checksum)
    {
        checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (demuxer.TryReadPacket(out PacketView view))
        {
            checksum += view.Data[0] + view.Info.Pts.Ticks;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

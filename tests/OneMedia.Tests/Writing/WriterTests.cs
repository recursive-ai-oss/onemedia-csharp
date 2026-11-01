using System.Security.Cryptography;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tests;

internal static class WriterTestSupport
{
    public static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", .. parts]);

    /// <summary>SHA-256 of every packet's bytes per track, read through our demuxer.</summary>
    public static SortedDictionary<int, string> PayloadHash(byte[] file)
    {
        using MemoryMediaSource src = new(file);
        using MediaFile mf = MediaFile.Open(src);
        IDemuxer d = mf.Demuxer!;
        Dictionary<int, IncrementalHash> hashes = [];
        Dictionary<int, long> counts = [];
        while (d.TryReadPacket(out PacketView v))
        {
            int t = v.Info.TrackIndex;
            if (!hashes.TryGetValue(t, out IncrementalHash? h))
            {
                hashes[t] = h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            h.AppendData(v.Data);
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }

        SortedDictionary<int, string> result = [];
        foreach ((int t, IncrementalHash h) in hashes)
        {
            result[t] = $"{counts[t]}:{Convert.ToHexString(h.GetHashAndReset())}";
        }

        return result;
    }

    public static byte[] Apply(EditPlan plan, byte[] original)
    {
        using MemoryMediaSource src = new(original);
        using MemoryStream ms = new();
        SafeWriter.Write(plan, src, ms);
        return ms.ToArray();
    }

    public static byte[] IlstUdta(string title)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(title);
        byte[] dataPayload = new byte[8 + text.Length];
        dataPayload[3] = 1; // well-known type 1: UTF-8 text
        text.CopyTo(dataPayload, 8);
        byte[] data = BoxBuilder.Create(BoxTypes.Make("data"), dataPayload);
        byte[] item = BoxBuilder.Create(BoxTypes.Make("\u00a9nam"), data);
        byte[] ilst = BoxBuilder.Create(BoxTypes.Ilst, item);
        byte[] hdlr = BoxBuilder.CreateFull(BoxTypes.Hdlr, 0, 0, [0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'i', (byte)'r', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        byte[] meta = BoxBuilder.CreateFull(BoxTypes.Meta, 0, 0, [.. hdlr, .. ilst]);
        return BoxBuilder.Create(BoxTypes.Udta, meta);
    }
}

public class IsoWriterTests
{
    private static (BoxTree Tree, MemoryMediaSource Source) Open(byte[] file)
    {
        MemoryMediaSource src = new(file);
        return (new BoxTree(src), src);
    }

    [Theory]
    [InlineData("faststart.mp4")]
    [InlineData("frag.mp4")]
    [InlineData("bframes_aac.mov")]
    [InlineData("video.3gp")]
    public void UntouchedFileRoundTripsThroughBothPaths(string name)
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture("isobmff", name));
        (BoxTree tree, MemoryMediaSource src) = Open(file);
        using (src)
        {
            EditPlan plain = new BoxEditor(tree, src).Build();
            Assert.True(plain.IsSameLayout);
            Assert.Equal(file, WriterTestSupport.Apply(plain, file));
            EditPlan strict = new BoxEditor(tree, src) { RewriteHeaders = true }.Build();
            Assert.Equal(file, WriterTestSupport.Apply(strict, file));
        }
    }

    [Theory]
    [InlineData("faststart.mp4")]
    [InlineData("frag.mp4")]
    [InlineData("bframes_aac.mov")]
    [InlineData("video.3gp")]
    public void GrowingMoovKeepsEveryPacket(string name)
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture("isobmff", name));
        (BoxTree tree, MemoryMediaSource src) = Open(file);
        byte[] output;
        EditPlan plan;
        using (src)
        {
            BoxEditor editor = new(tree, src);
            BoxNode moov = tree.Find(BoxTypes.Moov)!;
            editor.AppendChild(moov, WriterTestSupport.IlstUdta("Deposition, day 2"));
            plan = editor.Build();
            Assert.False(plan.IsSameLayout);
            output = WriterTestSupport.Apply(plan, file);
        }

        Assert.Equal(plan.Length, output.Length);
        Assert.Equal(WriterTestSupport.PayloadHash(file), WriterTestSupport.PayloadHash(output));
        using MemoryMediaSource outSrc = new(output);
        BoxTree outTree = new(outSrc);
        Assert.NotNull(outTree.Find(BoxTypes.Moov)!.Find(BoxTypes.Udta));
        Assert.False(outTree.Defects.HasErrors);
    }

    [Fact]
    public void ReplacementThatFitsPaddingIsAnInPlaceEdit()
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture("isobmff", "faststart.mp4"));
        // First pass: make room — a udta plus 200 bytes of free box inside moov.
        byte[] prepared;
        (BoxTree tree, MemoryMediaSource src) = Open(file);
        using (src)
        {
            BoxEditor e = new(tree, src);
            BoxNode moov = tree.Find(BoxTypes.Moov)!;
            e.AppendChild(moov, WriterTestSupport.IlstUdta("A"));
            e.AppendChild(moov, BoxBuilder.CreateFree(200));
            prepared = WriterTestSupport.Apply(e.Build(), file);
        }

        string path = Path.Combine(Path.GetTempPath(), $"writer-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, prepared);
        try
        {
            using FileMediaSource fs = FileMediaSource.Open(path);
            BoxTree t2 = new(fs);
            BoxNode udta = t2.Find(BoxTypes.Moov)!.Children.Last(c => c.Type == BoxTypes.Udta);
            BoxEditor e2 = new(t2, fs);
            e2.Replace(udta, WriterTestSupport.IlstUdta("A much longer title than before, still within the padding"));
            EditPlan plan = e2.Build();
            Assert.True(plan.IsSameLayout);
            Assert.True(plan.PaddingUsed > 0);
            fs.Dispose();
            WriteReport report = SafeWriter.SaveInPlace(plan, path);
            Assert.Equal(WriteMode.InPlace, report.Mode);
            Assert.Equal(0, report.BytesCopied);
            byte[] after = File.ReadAllBytes(path);
            Assert.Equal(prepared.Length, after.Length);
            // Only the udta slot and the free box that followed it differ.
            int lo = 0;
            while (prepared[lo] == after[lo])
            {
                lo++;
            }

            int hi = after.Length - 1;
            while (prepared[hi] == after[hi])
            {
                hi--;
            }

            Assert.True(lo >= udta.Offset && hi < udta.Offset + 8 + 4 + 200 + (udta.Header.End - udta.Offset));
            Assert.Equal(WriterTestSupport.PayloadHash(prepared), WriterTestSupport.PayloadHash(after));

            // Far too big for the padding: refused, file untouched.
            using FileMediaSource fs2 = FileMediaSource.Open(path);
            BoxTree t3 = new(fs2);
            BoxEditor e3 = new(t3, fs2);
            e3.Replace(t3.Find(BoxTypes.Moov)!.Children.Last(c => c.Type == BoxTypes.Udta), WriterTestSupport.IlstUdta(new string('x', 5000)));
            EditPlan big = e3.Build();
            Assert.False(big.IsSameLayout);
            fs2.Dispose();
            PaddingRequiredException ex = Assert.Throws<PaddingRequiredException>(() => SafeWriter.SaveInPlace(big, path));
            Assert.True(ex.Required > 0);
            Assert.Equal(after, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveReplacesAtomicallyAndKeepsTheOriginalOnCancel()
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture("isobmff", "faststart.mp4"));
        string dir = Path.Combine(Path.GetTempPath(), $"writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.mp4");
        File.WriteAllBytes(path, file);
        try
        {
            EditPlan plan;
            using (FileMediaSource fs = FileMediaSource.Open(path))
            {
                BoxTree tree = new(fs);
                BoxEditor e = new(tree, fs);
                e.AppendChild(tree.Find(BoxTypes.Moov), WriterTestSupport.IlstUdta("T"));
                plan = e.Build();
            }

            using CancellationTokenSource cts = new();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => SafeWriter.Save(plan, path, cts.Token));
            Assert.Equal(file, File.ReadAllBytes(path));
            Assert.Equal(["a.mp4"], Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());

            WriteReport report = SafeWriter.Save(plan, path);
            Assert.Equal(WriteMode.Save, report.Mode);
            Assert.Equal(plan.Length, new FileInfo(path).Length);
            Assert.Equal(["a.mp4"], Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
            Assert.Equal(WriterTestSupport.PayloadHash(file), WriterTestSupport.PayloadHash(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class MatroskaWriterTests
{
    [Theory]
    [InlineData("metadata", "mkvmerge_global.mkv")]
    [InlineData("metadata", "mkvmerge_track_tags.mkv")]
    [InlineData("metadata", "mkvmerge_webm.webm")]
    [InlineData("audio", "opus.mka")]
    [InlineData("audio", "two-audio.mkv")]
    public void UntouchedFileRoundTripsThroughBothPaths(string dir, string name)
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture(dir, name));
        using MemoryMediaSource src = new(file);
        EbmlDocument doc = EbmlDocument.Open(src);
        EditPlan plain = new EbmlEditor(doc, src).Build();
        Assert.True(plain.IsSameLayout);
        Assert.Equal(file, WriterTestSupport.Apply(plain, file));
        EditPlan strict = new EbmlEditor(doc, src) { RewriteHeaders = true }.Build();
        Assert.Equal(file, WriterTestSupport.Apply(strict, file));
    }

    [Theory]
    [InlineData("metadata", "mkvmerge_global.mkv")]
    [InlineData("metadata", "mkvmerge_track_tags.mkv")]
    [InlineData("audio", "opus.mka")]
    [InlineData("audio", "two-audio.mkv")]
    public void GrowingTheSegmentBeforeTheClustersKeepsPacketsAndPositions(string dir, string name)
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture(dir, name));
        using MemoryMediaSource src = new(file);
        EbmlDocument doc = EbmlDocument.Open(src);
        EbmlElement seg = doc.Segment!;
        EbmlEditor editor = new(doc, src);
        // Add a Title to the Segment Info, which sits in front of the Clusters: everything after it moves.
        EbmlElement info = seg.ChildElements.First(e => e.Id == 0x1549A966);
        byte[] infoPayload = new byte[info.PayloadSize];
        info.ReadPayload(infoPayload);
        byte[] title = EbmlWriter.Element(0x7BA9, "Deposition, day 2"u8);
        editor.Replace(info, EbmlWriter.Element(0x1549A966, [.. infoPayload, .. title]));
        EditPlan plan = editor.Build();
        byte[] output = WriterTestSupport.Apply(plan, file);
        Assert.Equal(WriterTestSupport.PayloadHash(file), WriterTestSupport.PayloadHash(output));
        using MemoryMediaSource outSrc = new(output);
        EbmlDocument outDoc = EbmlDocument.Open(outSrc);
        Assert.False(outDoc.Defects.HasErrors);
        // Every SeekHead entry still points at the element it names.
        AssertSeekHeadPointsRight(outDoc, outSrc);
    }

    private static void AssertSeekHeadPointsRight(EbmlDocument doc, MemoryMediaSource src)
    {
        EbmlElement seg = doc.Segment!;
        long segStart = seg.Offset + seg.HeaderSize;
        foreach (EbmlElement sh in seg.ChildElements.Where(e => e.Id == MatroskaIds.SeekHead))
        {
            foreach (EbmlElement seek in sh.ChildElements.Where(e => e.Id == 0x4DBB))
            {
                uint id = 0;
                long pos = -1;
                foreach (EbmlElement f in seek.ChildElements)
                {
                    byte[] b = new byte[(int)f.PayloadSize];
                    f.ReadPayload(b);
                    if (f.Id == 0x53AB)
                    {
                        foreach (byte x in b)
                        {
                            id = (id << 8) | x;
                        }
                    }
                    else if (f.Id == 0x53AC)
                    {
                        Ebml.TryReadUnsigned(b, out ulong p);
                        pos = (long)p;
                    }
                }

                byte[] head = new byte[4];
                src.Read(segStart + pos, head);
                Ebml.TryReadId(head, out uint actual, out _);
                Assert.Equal(id, actual);
            }
        }
    }

    internal static byte[] NewTags(string title)
    {
        byte[] name = EbmlWriter.Element(0x45A3, "TITLE"u8);
        byte[] value = EbmlWriter.Element(0x4487, System.Text.Encoding.UTF8.GetBytes(title));
        byte[] simple = EbmlWriter.Element(0x67C8, [.. name, .. value]);
        byte[] tag = EbmlWriter.Element(0x7373, simple);
        return EbmlWriter.Element(MatroskaIds.Tags, tag);
    }
}

public class RiffWriterTests
{
    [Theory]
    [InlineData("avi", "tagged.avi")]
    [InlineData("avi", "mjpeg-pcm.avi")]
    public void UntouchedFileRoundTripsThroughBothPaths(string dir, string name)
    {
        byte[] file = File.ReadAllBytes(WriterTestSupport.Fixture(dir, name));
        using MemoryMediaSource src = new(file);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
        EditPlan plain = new ChunkEditor(cf, src).Build();
        Assert.True(plain.IsSameLayout);
        Assert.Equal(file, WriterTestSupport.Apply(plain, file));
        EditPlan strict = new ChunkEditor(cf, src) { RewriteHeaders = true }.Build();
        Assert.Equal(file, WriterTestSupport.Apply(strict, file));
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] c = new byte[8 + payload.Length + (payload.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(id, c);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(c, 8);
        return c;
    }

    // A small WAVE file; rootSizeDelta moves the stated RIFF size away from the true one (writers that state the file length get +8).
    private static byte[] Wave(int rootSizeDelta, int dataBytes, byte[]? junk = null)
    {
        byte[] fmt = new byte[16];
        fmt[0] = 1;
        fmt[2] = 1;
        fmt[4] = 0x40;
        fmt[5] = 0x1F;
        fmt[8] = 0x80;
        fmt[9] = 0x3E;
        fmt[12] = 2;
        fmt[14] = 16;
        List<byte> body = [.. "WAVE"u8.ToArray(), .. Chunk("fmt ", fmt)];
        if (junk is not null)
        {
            body.AddRange(Chunk("JUNK", junk));
        }

        body.AddRange(Chunk("data", new byte[dataBytes]));
        byte[] file = new byte[8 + body.Count];
        "RIFF"u8.CopyTo(file);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(body.Count + rootSizeDelta));
        body.CopyTo(file, 8);
        return file;
    }

    [Fact]
    public void AppendingToAFileWhoseOuterSizeOvershootsStatesTheRealSize()
    {
        byte[] file = Wave(rootSizeDelta: 8, dataBytes: 100);
        using MemoryMediaSource src = new(file);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
        Assert.True(cf.Root.StatedEndPastFile);
        ChunkEditor e = new(cf, src);
        e.AppendChild(null, ChunkBuilder.CreatePadding(cf.Layout, 64));
        byte[] output = WriterTestSupport.Apply(e.Build(), file);
        Assert.Equal(file.Length + 64, output.Length);
        Assert.Equal(output.Length - 8, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4)));
        using MemoryMediaSource back = new(output);
        ChunkFile reopened = ChunkFile.TryOpen(back, MediaLimits.Default, new DefectLog())!;
        Assert.False(reopened.Root.StatedEndPastFile);
        Assert.Equal("JUNK", reopened.Root.Chunks[^1].Id);
    }

    [Fact]
    public void AppendingAfterACutOffChunkIsRefused()
    {
        byte[] file = Wave(rootSizeDelta: 0, dataBytes: 100);
        byte[] cut = file[..^10];
        using MemoryMediaSource src = new(cut);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
        ChunkEditor e = new(cf, src);
        e.AppendChild(null, ChunkBuilder.CreatePadding(cf.Layout, 64));
        Assert.Throws<UnsupportedMediaException>(() => e.Build());
    }

    [Fact]
    public void RiffEditFitsJunkInPlaceAndRefusesWhenTooLarge()
    {
        byte[] file = Wave(rootSizeDelta: 0, dataBytes: 100, junk: new byte[200]);
        string path = Path.Combine(Path.GetTempPath(), "onemedia-riff-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, file);
        try
        {
            byte[] list = ChunkBuilder.CreateList(ChunkLayout.Riff, "INFO", Chunk("INAM", "hello\0"u8.ToArray()));
            using (MemoryMediaSource src = new(file))
            {
                ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
                ChunkEditor e = new(cf, src);
                e.Replace(cf.Chunks.First(c => c.Id == "JUNK"), list);
                EditPlan plan = e.Build();
                Assert.True(plan.IsSameLayout);
                WriteReport r = SafeWriter.SaveInPlace(plan, path);
                Assert.Equal(file.Length, new FileInfo(path).Length);
                Assert.NotEmpty(r.Changes);
            }

            byte[] after = File.ReadAllBytes(path);
            Assert.NotEqual(file, after);
            Assert.Equal(file.AsSpan(0, 36).ToArray(), after.AsSpan(0, 36).ToArray());
            Assert.Equal(file.AsSpan(file.Length - 108).ToArray(), after.AsSpan(after.Length - 108).ToArray());

            using (MemoryMediaSource src = new(after))
            {
                ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
                ChunkEditor e = new(cf, src);
                e.Replace(cf.Chunks.First(c => c.Id == "LIST"), ChunkBuilder.CreateList(ChunkLayout.Riff, "INFO", Chunk("INAM", new byte[5000])));
                EditPlan plan = e.Build();
                Assert.False(plan.IsSameLayout);
                Assert.Throws<PaddingRequiredException>(() => SafeWriter.SaveInPlace(plan, path));
            }

            Assert.Equal(after, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class WriterFuzzRegressionTests
{
    [Fact]
    public void SavedWriterInputsReplayWithoutAFinding()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "writer-ebml");
        string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            byte[] input = File.ReadAllBytes(f);
            // Every path byte (strict headers, appended padding) over the saved file.
            for (byte path = 0; path < 4; path++)
            {
                input[0] = path;
                RecursiveAI.OneMedia.Fuzz.WriterTarget.Run(input, RecursiveAI.OneMedia.Fuzz.WriterTarget.Family.Ebml);
            }
        }
    }
}

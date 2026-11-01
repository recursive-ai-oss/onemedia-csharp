using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;
using Xunit;

namespace RecursiveAI.OneMedia.Tests;

public sealed class TagWriteTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static TheoryData<string> Files =>
    [
        "audio-es/plain.mp3", "audio-es/tagged/id3v23-basic.mp3", "audio-es/tagged/id3v22-basic.mp3", "audio-es/tagged/id3v1-only.mp3", "audio-es/tagged/apev2-only.mp3",
        "audio/aac.m4a", "audio/aac.mka", "audio/opus.mka", "audio/flac.flac", "audio/opus.ogg", "pcm/aiff-id3.aiff", "pcm/bwf-v0.wav",
        "asf/wmav2-tagged.wma", "asf/mp3.wma", "asf/wmv2-wmav2.wmv",
    ];

    private static byte[] Apply(string fixture, TagEdits edits, TagWriteOptions? options = null) => Apply(File.ReadAllBytes(WriterTestSupport.Fixture(fixture)), edits, options);

    private static byte[] Apply(byte[] input, TagEdits edits, TagWriteOptions? options = null)
    {
        using MemoryMediaSource src = new(input);
        TagWritePlan plan = TagWriter.Plan(src, edits, options);
        using MemoryStream output = new();
        SafeWriter.Write(plan.Plan, src, output);
        byte[] result = output.ToArray();
        Assert.Equal(plan.Plan.Length, result.Length);
        // Everything the plan copies is byte for byte what the source held.
        long pos = 0;
        foreach (OutputPiece p in plan.Plan.Pieces)
        {
            if (p.Kind == OutputPieceKind.Copy)
            {
                Assert.True(input.AsSpan((int)p.SourceRange.Offset, (int)p.Length).SequenceEqual(result.AsSpan((int)pos, (int)p.Length)));
            }

            pos += p.Length;
        }

        Assert.Equal(WriterTestSupport.PayloadHash(input), WriterTestSupport.PayloadHash(result));
        return result;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EditsReadBackAndThePayloadIsUnchanged(string fixture)
    {
        bool asf = fixture.StartsWith("asf/", StringComparison.Ordinal);
        TagEdits e = new();
        e.Set(CommonField.Title, "New title \u00e9\u4e2d");
        e.Set(CommonField.Artist, "New artist");
        e.Set(asf ? CommonField.Description : CommonField.Comment, "A comment");
        e.SetPicture(Png, "image/png");
        byte[] result = Apply(fixture, e);
        using MemoryMediaSource src = new(result);
        using MediaFile m = MediaFile.Open(src);
        Assert.Equal("New title \u00e9\u4e2d", m.Tags.Title);
        Assert.Equal("New artist", m.Tags.Artist);
        Assert.Equal("A comment", asf ? m.Tags.Description : m.Tags.Comment);
        Assert.Contains(m.Tags.Pictures, p => p.Length == Png.Length && p.ReadData().AsSpan().SequenceEqual(Png));
    }

    [Theory]
    [InlineData("audio-es/tagged/id3v23-basic.mp3")]
    [InlineData("audio/aac.m4a")]
    [InlineData("audio/aac.mka")]
    [InlineData("audio/flac.flac")]
    [InlineData("audio/opus.ogg")]
    [InlineData("pcm/bwf-v0.wav")]
    public void ARemovedFieldIsGoneAndTheOthersStay(string fixture)
    {
        TagEdits add = new();
        add.Set(CommonField.Title, "T");
        add.Set(CommonField.Artist, "A");
        add.Set(CommonField.Genre, "Rock");
        add.Set(CommonField.Comment, "C");
        byte[] tagged = Apply(File.ReadAllBytes(WriterTestSupport.Fixture(fixture)), add);
        TagEdits remove = new();
        remove.Remove(CommonField.Genre);
        byte[] result = Apply(tagged, remove);
        using MemoryMediaSource src = new(result);
        using MediaFile after = MediaFile.Open(src);
        Assert.Null(after.Tags.Genre);
        Assert.Equal("T", after.Tags.Title);
        Assert.Equal("A", after.Tags.Artist);
        Assert.Equal("C", after.Tags.Comment);
    }

    [Fact]
    public void TrackAndTotalAreWrittenAsAPair()
    {
        TagEdits e = new();
        e.Set(CommonField.TrackNumber, 4);
        e.Set(CommonField.TrackTotal, 11);
        foreach (string fixture in new[] { "audio-es/tagged/id3v23-basic.mp3", "audio/aac.m4a", "audio/flac.flac", "audio/aac.mka" })
        {
            using MemoryMediaSource src = new(Apply(fixture, e));
            using MediaFile m = MediaFile.Open(src);
            Assert.Equal(4, m.Tags.TrackNumber);
            Assert.Equal(11, m.Tags.TrackTotal);
        }
    }

    [Fact]
    public void TheReportNamesWhatASystemCannotHold()
    {
        TagEdits e = new();
        e.Set(CommonField.Rating, "5");
        TagWritePlan plan = TagWriter.Plan(WriterTestSupport.Fixture("audio-es/tagged/id3v23-basic.mp3"), e);
        SystemWriteReport r = Assert.Single(plan.Report.Systems);
        Assert.Contains(r.NotRepresentable, n => n.Field == CommonField.Rating);
        Assert.Empty(r.Written);
    }

    [Fact]
    public void Id3SaveInPlaceFitsThePaddingOrRefuses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemedia-tagwrite-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "t.mp3");
            File.Copy(WriterTestSupport.Fixture("audio-es/plain.mp3"), path);
            TagEdits first = new();
            first.Set(CommonField.Title, "first");
            TagWriter.Save(path, first, new TagWriteOptions { PaddingBytes = 4096 });
            long length = new FileInfo(path).Length;
            TagEdits small = new();
            small.Set(CommonField.Title, "a longer title than before");
            small.Set(CommonField.Artist, "someone");
            TagWriteReport r = TagWriter.SaveInPlace(path, small);
            Assert.Equal(WriteMode.InPlace, r.Write!.Mode);
            Assert.Equal(length, new FileInfo(path).Length);
            using (MediaFile m = Media.Open(path))
            {
                Assert.Equal("a longer title than before", m.Tags.Title);
            }

            TagEdits big = new();
            big.Set(CommonField.Comment, new string('x', 10000));
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<PaddingRequiredException>(() => TagWriter.SaveInPlace(path, big));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheCommonViewStagesEditsAndTheMediaFileSavesThem()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemedia-tagwrite-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "t.m4a");
            File.Copy(WriterTestSupport.Fixture("audio/aac.m4a"), path);
            using (MediaFile m = Media.Open(path))
            {
                m.Tags.Title = "Staged";
                Assert.Equal("Staged", m.Tags.Title);
                m.Tags.TrackNumber = 7;
                TagWriteReport report = m.SaveAs(path + ".new");
                Assert.NotNull(report.Write);
            }

            using MediaFile m2 = Media.Open(path + ".new");
            Assert.Equal("Staged", m2.Tags.Title);
            Assert.Equal(7, m2.Tags.TrackNumber);
            using MediaFile original = Media.Open(path);
            Assert.NotEqual("Staged", original.Tags.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AVersion22TagIsUpgradedAndReported()
    {
        TagEdits e = new();
        e.Set(CommonField.Title, "Upgraded");
        TagWritePlan plan = TagWriter.Plan(WriterTestSupport.Fixture("audio-es/tagged/id3v22-basic.mp3"), e);
        Assert.Contains(plan.Report.Notes, n => n.Contains("upgraded", StringComparison.Ordinal));
        byte[] result = Apply("audio-es/tagged/id3v22-basic.mp3", e);
        Assert.Equal(4, result[3]);
    }

    [Fact]
    public void AStreamTheWriterCannotEditIsRefusedWithATypedException()
    {
        TagEdits e = new();
        e.Set(CommonField.Title, "x");
        using MemoryMediaSource src = new(new byte[64]);
        Assert.Throws<UnsupportedMediaException>(() => TagWriter.Plan(src, e));
    }

    [Theory]
    [InlineData("asf/wmav2-tagged.wma")]
    [InlineData("asf/wmv2-wmav2.wmv")]
    public void AsfDescriptionsKeepOtherAttributesAndTheFileSizeStaysTrue(string fixture)
    {
        byte[] input = File.ReadAllBytes(WriterTestSupport.Fixture(fixture));
        TagEdits e = new();
        e.Set(CommonField.Album, "An album");
        e.Set(CommonField.Rating, "50");
        e.Set(CommonField.TrackNumber, 3);
        e.Set(CommonField.TrackTotal, 9);
        byte[] result = Apply(input, e);
        using MemoryMediaSource src = new(result);
        using MediaFile m = MediaFile.Open(src);
        Assert.Equal("An album", m.Tags.Album);
        Assert.Equal(3, m.Tags.TrackNumber);
        Assert.Equal(9, m.Tags.TrackTotal);
        // The File Properties object states the file size; it follows the header.
        Assert.Equal(result.Length, BitConverter.ToInt64(result, 30 + 24 + 16));
    }

    [Fact]
    public void AsfSaveInPlaceUsesThePaddingOrRefuses()
    {
        byte[] input = File.ReadAllBytes(WriterTestSupport.Fixture("asf/wmav2-tagged.wma"));
        string path = Path.Combine(Path.GetTempPath(), "asf-inplace-" + Guid.NewGuid().ToString("N") + ".wma");
        File.WriteAllBytes(path, input);
        try
        {
            TagEdits grow = new();
            grow.Set(CommonField.Description, new string('c', 5000));
            try
            {
                TagWriter.SaveInPlace(path, grow);
                Assert.Equal(input.Length, new FileInfo(path).Length);
            }
            catch (PaddingRequiredException ex)
            {
                Assert.True(ex.Required > 0);
                Assert.Equal(input, File.ReadAllBytes(path));
            }

            TagWriter.Save(path, grow);
            // The growing save left padding in the Header Extension: a smaller edit now fits without moving a byte.
            long size = new FileInfo(path).Length;
            TagEdits small = new();
            small.Set(CommonField.Description, "short");
            TagWriter.SaveInPlace(path, small);
            Assert.Equal(size, new FileInfo(path).Length);
            using FileMediaSource fs = FileMediaSource.Open(path);
            using MediaFile m = MediaFile.Open(fs);
            Assert.Equal("short", m.Tags.Description);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void XmpPacketsAreCreatedEditedAndKeepTheirOtherProperties()
    {
        TagEdits create = new();
        create.Set(CommonField.Title, "First <&> title");
        create.Set(CommonField.Artist, "An artist");
        create.Set(CommonField.Keywords, ["a", "b"]);
        byte[] once = Apply("audio/aac.m4a", create, new TagWriteOptions { Systems = [TagSystems.Xmp] });
        using (MemoryMediaSource src = new(once))
        using (MediaFile m = MediaFile.Open(src))
        {
            Assert.Contains(m.Tags.Values(CommonField.Title), v => v.Source.System == TagSystems.Xmp && v.Text == "First <&> title");
        }

        TagEdits edit = new();
        edit.Set(CommonField.Title, "Second");
        edit.Set(CommonField.Date, "2024-05-06T07:08:09Z");
        byte[] twice = Apply(once, edit, new TagWriteOptions { Systems = [TagSystems.Xmp] });
        using (MemoryMediaSource src = new(twice))
        using (MediaFile m = MediaFile.Open(src))
        {
            Assert.Contains(m.Tags.Values(CommonField.Title), v => v.Source.System == TagSystems.Xmp && v.Text == "Second");
            Assert.Contains(m.Tags.Values(CommonField.Artist), v => v.Source.System == TagSystems.Xmp && v.Text == "An artist");
            Assert.Contains(m.Tags.Values(CommonField.Date), v => v.Source.System == TagSystems.Xmp);
        }

        // The second packet fits the first one's padding: the file does not change size.
        Assert.Equal(once.Length, twice.Length);
    }

    [Fact]
    public void IXmlElementsAreCreatedAndEditedByPath()
    {
        TagEdits create = new();
        create.SetNative(TagSystems.IXml, "PROJECT", "Feature <1>");
        create.SetNative(TagSystems.IXml, "BEXT/BWF_ORIGINATOR", "Recorder");
        byte[] once = Apply("pcm/bwf-v0.wav", create, new TagWriteOptions { Systems = [TagSystems.IXml] });
        string Read(byte[] file, string key)
        {
            using MemoryMediaSource src = new(file);
            using MediaFile m = MediaFile.Open(src);
            return m.Metadata.NativeTags.First(t => t.System == TagSystems.IXml && t.Key == key).Text!;
        }

        Assert.Equal("Feature <1>", Read(once, "PROJECT"));
        Assert.Equal("Recorder", Read(once, "BEXT/BWF_ORIGINATOR"));
        TagEdits edit = new();
        edit.SetNative(TagSystems.IXml, "PROJECT", "P");
        edit.RemoveNative(TagSystems.IXml, "BEXT/BWF_ORIGINATOR");
        byte[] twice = Apply(once, edit, new TagWriteOptions { Systems = [TagSystems.IXml] });
        Assert.Equal("P", Read(twice, "PROJECT"));
        Assert.Equal(once.Length, twice.Length);
    }

    [Fact]
    public void AnAppendInsideAReplacedOrRemovedElementIsRefusedInsteadOfLost()
    {
        // ISO: a udta box with a child; replacing udta and appending into it cannot both happen.
        byte[] udta = BoxBuilder.Create(BoxTypes.Udta, BoxBuilder.Create(BoxTypes.Free, new byte[8]));
        byte[] file = [.. BoxBuilder.Create(BoxTypes.Make("ftyp"), new byte[8]), .. BoxBuilder.Create(BoxTypes.Moov, udta)];
        MemoryMediaSource src = new(file);
        BoxTree tree = new(src);
        BoxNode moov = tree.TopLevel.First(b => b.Type == BoxTypes.Moov);
        BoxNode u = moov.Children[0];
        BoxEditor e1 = new(tree, src);
        e1.Replace(u, udta);
        Assert.Throws<ArgumentException>(() => e1.AppendChild(u, BoxBuilder.Create(BoxTypes.Free, new byte[8])));
        BoxEditor e2 = new(tree, src);
        e2.AppendChild(u, BoxBuilder.Create(BoxTypes.Free, new byte[8]));
        Assert.Throws<ArgumentException>(() => e2.Remove(moov));

        // RIFF: the same for a LIST chunk.
        byte[] list = ChunkBuilder.CreateList(ChunkLayout.Riff, "INFO", ChunkBuilder.Create(ChunkLayout.Riff, "INAM", "x\0"u8));
        byte[] wave = [.. "RIFF"u8, .. BitConverter.GetBytes(4 + list.Length), .. "WAVE"u8, .. list];
        MemoryMediaSource wsrc = new(wave);
        ChunkFile cf = ChunkFile.TryOpen(wsrc, MediaLimits.Default, new DefectLog(MediaLimits.Default))!;
        ChunkNode first = cf.Chunks.First(c => c.Id == "LIST");
        ChunkEditor c1 = new(cf, wsrc, MediaLimits.Default);
        c1.AppendChild(first, ChunkBuilder.Create(cf.Layout, "junk", new byte[4]));
        Assert.Throws<ArgumentException>(() => c1.Remove(first));
    }

    [Fact]
    public void ChunksAppendedAfterAnOddUnpaddedDataChunkAllStartOnEvenOffsets()
    {
        // A data chunk of odd length whose pad byte the file lacks: one pad byte is added, not one per new chunk.
        byte[] fmt = ChunkBuilder.Create(ChunkLayout.Riff, "fmt ", [1, 0, 1, 0, 0x40, 0x1f, 0, 0, 0x40, 0x1f, 0, 0, 1, 0, 8, 0]);
        byte[] data = [.. "data"u8, 3, 0, 0, 0, 1, 2, 3];
        byte[] wave = [.. "RIFF"u8, .. BitConverter.GetBytes(4 + fmt.Length + data.Length), .. "WAVE"u8, .. fmt, .. data];
        TagEdits e = new();
        e.Set(CommonField.Title, "T");
        e.SetPicture(Png, "image/png");
        byte[] result = Apply(wave, e);
        using MemoryMediaSource src = new(result);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog(MediaLimits.Default))!;
        Assert.Equal(["fmt ", "data", "LIST", "id3 "], cf.Chunks.Select(c => c.Id).ToArray());
        Assert.All(cf.Chunks, c => Assert.Equal(0, c.Offset % 2));
        using MediaFile m = MediaFile.Open(src);
        Assert.Single(m.Tags.Pictures);
    }
}

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
}

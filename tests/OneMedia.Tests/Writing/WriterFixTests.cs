using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;
using Xunit;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Tests for the writer fixes: padding that is generated and never held, in-place writes that touch only the bytes that change, padding left behind a
/// growing edit, date warnings, recognisable temporary files and their cleanup, and the real shortfall of a refused in-place edit.
/// </summary>
public sealed class WriterFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onemedia-writerfix-" + Guid.NewGuid().ToString("N")[..8]);

    public WriterFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Fx(string rel) => File.ReadAllBytes(WriterTestSupport.Fixture(rel.Split('/')));

    private string Put(string name, byte[] bytes)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static TagEdits Title(string title)
    {
        TagEdits e = new();
        e.Set(CommonField.Title, title);
        return e;
    }

    private static string TitleOf(string path)
    {
        using MediaFile m = Media.Open(path);
        return m.Tags.Title ?? string.Empty;
    }

    // Not zeros: a run of zeros would equal the padding it replaces and write nothing.
    private static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)((i * seed) + (i >> 8) + 1);
        }

        return b;
    }

    private static bool IsFree(ReadOnlySpan<byte> b, int at, out int size)
    {
        size = (int)BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
        return b.Slice(at + 4, 4).SequenceEqual("free"u8);
    }

    // ---- finding 14: padding is generated, in-place writes touch only what changes ----

    [Fact]
    public void AHugeId3PaddingCostsNoMemoryAndAnInPlaceEditWritesOnlyTheChange()
    {
        const int Pad = 48 * 1024 * 1024;
        string path = Put("big.mp3", Fx("audio-es/plain.mp3"));
        TagWriteOptions big = new() { PaddingBytes = Pad };

        long before = GC.GetAllocatedBytesForCurrentThread();
        TagWriteReport made = TagWriter.Save(path, Title("abcdef"), big);
        long madeAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(madeAllocated < 8 * 1024 * 1024, $"creating the tag with {Pad} bytes of padding allocated {madeAllocated} bytes");
        Assert.True(new FileInfo(path).Length > Pad);
        byte[] copy = File.ReadAllBytes(path)[..4096];

        before = GC.GetAllocatedBytesForCurrentThread();
        TagWriteReport edited = TagWriter.SaveInPlace(path, Title("abcxyz"), big);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"an in-place edit allocated {allocated} bytes");
        Assert.NotNull(edited.Write);
        Assert.True(edited.Write!.BytesWritten <= 64, $"an in-place edit of 3 bytes wrote {edited.Write.BytesWritten} bytes");
        Assert.Equal("abcxyz", TitleOf(path));
        Assert.NotEqual(copy, File.ReadAllBytes(path)[..4096]);
        _ = made;
    }

    [Fact]
    public void AnInPlaceWriteSkipsBytesThatAreAlreadyThere()
    {
        // A Save of the same value first, so the second plan has the identical metadata: nothing may be written.
        string path = Put("same.mp3", Fx("audio-es/plain.mp3"));
        TagWriter.Save(path, Title("same title"));
        byte[] before = File.ReadAllBytes(path);
        TagWriteReport r = TagWriter.SaveInPlace(path, Title("same title"));
        Assert.Equal(0, r.Write!.BytesWritten);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ACoverChangedInPlaceIntoALargeFreeBoxWritesTheCoverAndNotTheBox()
    {
        const int Free = 16 * 1024 * 1024;
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        string path = Put("cover.mp4", Fx("metadata/tags_ilst.mp4"));
        TagWriteOptions big = new() { PaddingBytes = Free };
        TagEdits first = new();
        first.SetPicture(png, "image/png");
        TagWriter.Save(path, first, big);

        // A different, larger picture: it grows the ilst, which has to fit the free box behind it.
        byte[] other = [.. png, .. Pattern(40_000, 5)];
        TagEdits second = new();
        second.SetPicture(other, "image/png");
        long before = GC.GetAllocatedBytesForCurrentThread();
        TagWriteReport r = TagWriter.SaveInPlace(path, second, big);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"allocated {allocated}");
        Assert.True(r.Write!.BytesWritten is > 39_000 and < 100_000, $"wrote {r.Write.BytesWritten} bytes for a 40 KB larger cover");
        using MediaFile m = Media.Open(path);
        Assert.Equal(other.Length, m.Tags.Pictures[0].Length);
    }

    /// <summary>
    /// The numbers behind the padding claims, printed for the record. Run with ONEMEDIA_WRITERFIX_BIG=1 (needs about 300 MB of disk): a 3-byte title edit
    /// in a 200 MiB ID3 padding and a cover change into a 64 MiB free box, each with allocated bytes, bytes written and the process's peak working set.
    /// </summary>
    [Fact]
    public void PaddingNumbers()
    {
        if (Environment.GetEnvironmentVariable("ONEMEDIA_WRITERFIX_BIG") != "1")
        {
            return;
        }

        string mp3 = Put("numbers.mp3", Fx("audio-es/plain.mp3"));
        TagWriteOptions pad200 = new() { PaddingBytes = 200 * 1024 * 1024 };
        TagWriter.Save(mp3, Title("abcdef"), pad200);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TagWriteReport r = TagWriter.SaveInPlace(mp3, Title("abcxyz"), pad200);
        Console.WriteLine($"NUMBERS id3 200 MiB padding, 3-byte title edit in place: allocated {GC.GetAllocatedBytesForCurrentThread() - before}, written {r.Write!.BytesWritten}, peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}");
        File.Delete(mp3);

        string mp4 = Put("numbers.mp4", Fx("metadata/tags_ilst.mp4"));
        TagWriteOptions pad64 = new() { PaddingBytes = 64 * 1024 * 1024 };
        TagEdits small = new();
        small.SetPicture(Pattern(20_000_000, 31), "image/png");
        TagWriter.Save(mp4, small, pad64);
        TagEdits bigger = new();
        bigger.SetPicture(Pattern(24_000_000, 37), "image/png");
        before = GC.GetAllocatedBytesForCurrentThread();
        r = TagWriter.SaveInPlace(mp4, bigger, pad64);
        Console.WriteLine($"NUMBERS mp4 cover 20 MB to 24 MB into a 64 MiB free box in place: allocated {GC.GetAllocatedBytesForCurrentThread() - before}, written {r.Write!.BytesWritten}, peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}");
    }

    /// <summary>
    /// A fuzz find: a nested BW64 chunk stating 0xFFFFFFFF (its size lives in ds64) lost that marker when headers were regenerated for an
    /// untouched file.
    /// </summary>
    [Fact]
    public void RegeneratingHeadersOfAnUntouchedRf64FileKeepsANestedAllOnesSize()
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "writer-riff", "writer-riff-nested-bw64-size-ffffffff-regenerated.bin"));
        byte[] file = raw[1..];
        using MemoryMediaSource src = new(file);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
        ChunkEditor strict = new(cf, src) { RewriteHeaders = true };
        Assert.Equal(file, WriterTestSupport.Apply(strict.Build(), file));
    }

    /// <summary>A fuzz find from the first fix: a nested BW64 whose ds64 size runs far past the end of the file is refused, not planned as a copy of bytes that do not exist.</summary>
    [Fact]
    public void RegeneratingHeadersWithADs64SizePastTheEndOfTheFileIsATypedRefusalOrAValidPlan()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "writer-riff", "writer-riff-nested-bw64-ds64-size-past-end.bin"))[1..];
        using MemoryMediaSource src = new(file);
        ChunkFile cf = ChunkFile.TryOpen(src, MediaLimits.Default, new DefectLog())!;
        try
        {
            EditPlan plan = new ChunkEditor(cf, src) { RewriteHeaders = true }.Build();
            Assert.True(plan.Pieces.All(p => p.Kind != OutputPieceKind.Copy || p.SourceRange.End <= file.Length));
        }
        catch (MediaException)
        {
        }
    }

    // ---- finding 17: padding on growth ----

    private static (byte[] Plain, byte[] Padded) Grow(string fixture, TagEdits edits, TagWriteOptions? padded = null)
    {
        byte[] input = Fx(fixture);
        MemoryMediaSource src = new(input);
        TagWritePlan minimal = TagWriter.Plan(src, edits, TagWriteOptions.Minimal);
        TagWritePlan withPad = TagWriter.Plan(src, edits, padded);
        return (WriterTestSupport.Apply(minimal.Plan, input), WriterTestSupport.Apply(withPad.Plan, input));
    }

    public static TheoryData<string> GrowingFiles =>
    [
        "metadata/tags_ilst.mp4", "metadata/tags_keys.mov", "metadata/mkv_ffmpeg_tags.mkv", "metadata/mkvmerge_global.mkv", "pcm/bwf-v0.wav", "pcm/aiff-id3.aiff", "xiph-flv/tags.opus", "audio-es/plain.mp3", "xiph-flv/tags.flac",
    ];

    [Theory]
    [MemberData(nameof(GrowingFiles))]
    public void AGrowingEditLeavesPaddingSoALaterEditFitsInPlace(string fixture)
    {
        string longTitle = new('T', 700);
        (byte[] plain, byte[] padded) = Grow(fixture, Title(longTitle));
        Assert.True(padded.Length >= plain.Length, "padding cannot make the output shorter");
        Assert.True(padded.Length > plain.Length || fixture.EndsWith(".flac", StringComparison.Ordinal) || fixture.EndsWith(".mp3", StringComparison.Ordinal), $"{fixture}: no padding was added");

        // The next edit, to a title 300 bytes longer, has to fit the padding of the first.
        string path = Put("grown" + Path.GetExtension(fixture), padded);
        string longer = new('U', 1000);
        TagWriter.SaveInPlace(path, Title(longer));
        Assert.Equal(longer, TitleOf(path));
    }

    [Theory]
    [MemberData(nameof(GrowingFiles))]
    public void MinimalOutputLeavesNoPaddingAndTheNextGrowingEditIsRefused(string fixture)
    {
        if (fixture.EndsWith(".flac", StringComparison.Ordinal) || fixture.EndsWith(".mp3", StringComparison.Ordinal))
        {
            return; // ID3 and FLAC always had a padding policy; the minimal option is checked on the others
        }

        (byte[] plain, _) = Grow(fixture, Title(new string('T', 700)));
        string path = Put("minimal" + Path.GetExtension(fixture), plain);
        Assert.Throws<PaddingRequiredException>(() => TagWriter.SaveInPlace(path, Title(new string('U', 1000)), TagWriteOptions.Minimal));
    }

    [Theory]
    [InlineData("metadata/tags_ilst.mp4")]
    [InlineData("metadata/mkv_ffmpeg_tags.mkv")]
    [InlineData("pcm/bwf-v0.wav")]
    public void AnEditThatFitsAddsNoPadding(string fixture)
    {
        // Same-size title twice: the second edit changes bytes in place and the file keeps its length.
        string path = Put("fit" + Path.GetExtension(fixture), Fx(fixture));
        TagWriter.Save(path, Title("first title!"));
        long length = new FileInfo(path).Length;
        TagWriter.Save(path, Title("other title!"));
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("metadata/tags_ilst.mp4")]
    [InlineData("metadata/mkv_ffmpeg_tags.mkv")]
    [InlineData("pcm/bwf-v0.wav")]
    [InlineData("pcm/aiff-id3.aiff")]
    public void AnEmptyEditStaysByteIdentical(string fixture)
    {
        byte[] input = Fx(fixture);
        TagWritePlan plan = TagWriter.Plan(new MemoryMediaSource(input), new TagEdits());
        Assert.Equal(input, WriterTestSupport.Apply(plan.Plan, input));
    }

    [Fact]
    public void GrowthPaddingInAnMp4IsAFreeBoxNextToTheGrownBox()
    {
        (_, byte[] padded) = Grow("metadata/tags_ilst.mp4", Title(new string('T', 700)));
        int largest = 0;
        for (int from = 0; from < padded.Length - 8;)
        {
            int found = padded.AsSpan(from).IndexOf("free"u8);
            if (found < 0)
            {
                break;
            }

            int at = from + found;
            if (at >= 4 && IsFree(padded, at - 4, out int size))
            {
                largest = Math.Max(largest, size);
            }

            from = at + 4;
        }

        Assert.Equal(1024, largest);
    }

    // ---- finding 16: date warning ----

    [Fact]
    public void ADateWriteNamesTheDateTagsItLeavesInPlace()
    {
        TagEdits e = new();
        e.Set(CommonField.Date, "2020-01-02");
        TagWritePlan plan = TagWriter.Plan(new MemoryMediaSource(Fx("metadata/tags_keys.mov")), e);
        Assert.Contains(plan.Report.Warnings, w => w.Contains("creationdate", StringComparison.Ordinal) || w.Contains("mvhd", StringComparison.Ordinal));
        TagWritePlan title = TagWriter.Plan(new MemoryMediaSource(Fx("metadata/tags_keys.mov")), Title("no date here"));
        Assert.Empty(title.Report.Warnings);
    }

    [Fact]
    public void AMatroskaDateWriteNamesTheOtherDateTags()
    {
        // Set DATE_RECORDED natively, then write the common Date, which goes to DATE_RELEASED.
        string path = Put("dates.mkv", Fx("metadata/mkv_ffmpeg_tags.mkv"));
        TagEdits native = new();
        native.SetNative(TagSystems.Matroska, "DATE_RECORDED", "2019-05-06");
        TagWriter.Save(path, native);
        TagEdits e = new();
        e.Set(CommonField.Date, "2020-01-02");
        TagWritePlan plan = TagWriter.Plan(new MemoryMediaSource(File.ReadAllBytes(path)), e);
        Assert.Contains(plan.Report.Warnings, w => w.Contains("DATE_RECORDED", StringComparison.Ordinal) && w.Contains("2019-05-06", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Report.Warnings, w => w.Contains("DATE_RELEASED", StringComparison.Ordinal));
    }

    [Fact]
    public void AnId3DateWriteNamesTheOriginalReleaseDate()
    {
        string path = Put("dates.mp3", Fx("audio-es/plain.mp3"));
        TagEdits native = new();
        native.SetNative(TagSystems.Id3v2, "TDOR", "1999");
        TagWriter.Save(path, native);
        TagEdits e = new();
        e.Set(CommonField.Date, "2020-01-02");
        TagWritePlan plan = TagWriter.Plan(new MemoryMediaSource(File.ReadAllBytes(path)), e);
        Assert.Contains(plan.Report.Warnings, w => w.Contains("TDOR", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Report.Warnings, w => w.Contains("TDRC", StringComparison.Ordinal));
    }

    // ---- finding 11: the real shortfall ----

    [Fact]
    public void ARefusedShrinkWithATinyGapReportsHowMuchIsMissing()
    {
        // A box that shrinks by 3 bytes leaves a gap too small for a free box header (8): it needs 5 more bytes, or the exact old size.
        string path = Put("gap.mp4", Fx("metadata/tags_ilst.mp4"));
        TagWriter.Save(path, Title("abcdefghij"));
        PaddingRequiredException ex = Assert.Throws<PaddingRequiredException>(() => TagWriter.SaveInPlace(path, Title("abcdefg"), TagWriteOptions.Minimal));
        Assert.Equal(5, ex.Required);
    }

    // ---- finding 10: the message ----

    [Fact]
    public void TrailingZerosAfterTheSegmentAreNamedAsSuch()
    {
        byte[] file = [.. Fx("audio/aac.mka"), .. new byte[5]];
        UnsupportedMediaException ex = Assert.Throws<UnsupportedMediaException>(() => TagWriter.Plan(new MemoryMediaSource(file), Title("x")));
        Assert.Contains("after the Segment", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("in front of its first Cluster", ex.Message, StringComparison.Ordinal);
    }

    // ---- stray temporary files ----

    [Fact]
    public void TemporaryFilesAreNamedAfterTheirTargetAndTheProcess()
    {
        string path = Put("target.mp3", Fx("audio-es/plain.mp3"));
        string? seen = null;
        using FileSystemWatcher watcher = new(_dir) { EnableRaisingEvents = true };
        watcher.Created += (_, e) => seen ??= e.Name;
        TagWriter.Save(path, Title("name check"));
        Thread.Sleep(200);
        // The watcher can miss a short-lived file; the name format is also covered by the cleanup test below.
        if (seen is not null)
        {
            Assert.StartsWith(".target.mp3.onemedia-", seen, StringComparison.Ordinal);
            Assert.EndsWith(".tmp", seen, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASaveRemovesTemporaryFilesOfAKilledSaveOfTheSameTarget()
    {
        string path = Put("t.wav", Fx("pcm/bwf-v0.wav"));
        int host = HostTag();
        string deadPid = Path.Combine(_dir, $".t.wav.onemedia-{host:x4}-7ffffffe-0123abcd.tmp");
        string otherHost = Path.Combine(_dir, $".t.wav.onemedia-{(host + 1) & 0xFFFF:x4}-7ffffffe-0123abcd.tmp");
        string otherHostOld = Path.Combine(_dir, $".t.wav.onemedia-{(host + 1) & 0xFFFF:x4}-7ffffffd-0123abcd.tmp");
        string otherTarget = Path.Combine(_dir, $".u.wav.onemedia-{host:x4}-7ffffffe-0123abcd.tmp");
        string notOurs = Path.Combine(_dir, ".t.wav.onemedia-nothex.tmp");
        string plain = Path.Combine(_dir, "t.wav.tmp");
        foreach (string f in new[] { deadPid, otherHost, otherHostOld, otherTarget, notOurs, plain })
        {
            File.WriteAllText(f, "debris");
        }

        File.SetLastWriteTimeUtc(otherHostOld, DateTime.UtcNow.AddDays(-3));
        TagWriter.Save(path, Title("cleanup"));
        Assert.False(File.Exists(deadPid), "the temporary file of a dead process of this host should be removed");
        Assert.False(File.Exists(otherHostOld), "a day-old temporary file should be removed");
        Assert.True(File.Exists(otherHost), "the temporary file of another host may be live and stays");
        Assert.True(File.Exists(otherTarget), "another target's temporary file stays");
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(plain));
    }

    [Fact]
    public void ATemporaryFileOfThisProcessIsNotRemoved()
    {
        // Another save in this process may be writing it right now.
        string path = Put("live.wav", Fx("pcm/bwf-v0.wav"));
        string live = Path.Combine(_dir, $".live.wav.onemedia-{HostTag():x4}-{Environment.ProcessId:x}-0123abcd.tmp");
        File.WriteAllText(live, "debris");
        TagWriter.Save(path, Title("cleanup"));
        Assert.True(File.Exists(live));
    }

    private static int HostTag()
    {
        int h = 17;
        foreach (char c in Environment.MachineName)
        {
            h = unchecked((h * 31) + c);
        }

        return h & 0xFFFF;
    }

    // ---- in-place writing ----

    [Fact]
    public void AnInPlaceSaveWritesOnlyTheRunsThatDiffer()
    {
        byte[] original = new byte[200_000];
        string path = Put("range.bin", original);
        PieceList list = new();
        list.AddCopy(0, 10);
        list.AddData(new byte[] { 1, 2 });
        list.AddCopy(12, 149_988);
        list.AddData(new byte[] { 9 });
        list.AddZeros(1000); // equal to what is there: nothing to write
        list.AddCopy(151_001, 200_000 - 151_001);
        EditPlan plan = list.ToPlan(200_000, 0, 0, 0);
        Assert.True(plan.IsSameLayout);
        WriteReport report = SafeWriter.SaveInPlace(plan, path);
        Assert.Equal(3, report.BytesWritten);
        byte[] expected = (byte[])original.Clone();
        expected[10] = 1;
        expected[11] = 2;
        expected[150_000] = 9;
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnInPlaceSaveWithManyScatteredChangesStaysWithinTheRunBound()
    {
        // More separate runs than the list keeps: neighbouring runs are joined (unchanged bytes between them are rewritten), and the result is still right.
        byte[] original = new byte[4_000_000];
        string path = Put("scatter.bin", original);
        byte[] changed = new byte[4_000_000];
        for (int i = 0; i < changed.Length; i += 40)
        {
            changed[i] = 7;
        }

        PieceList list = new();
        list.AddData(changed);
        EditPlan plan = list.ToPlan(4_000_000, 0, 0, 0);
        SafeWriter.SaveInPlace(plan, path);
        Assert.Equal(changed, File.ReadAllBytes(path));
    }
}

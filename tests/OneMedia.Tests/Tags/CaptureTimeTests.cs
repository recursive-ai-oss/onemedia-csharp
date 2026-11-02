using System.Globalization;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using Xunit;

namespace RecursiveAI.OneMedia.Tests;

public sealed class CaptureTimeTests
{
    private static readonly DateTimeOffset When = new(2024, 5, 1, 12, 30, 0, TimeSpan.FromHours(2));

    private static byte[] Fx(string name) => File.ReadAllBytes(WriterTestSupport.Fixture("datewrite", name));

    private static (byte[] Output, CaptureTimeReport Report) Apply(byte[] input, DateTimeOffset when, CaptureTimeOptions? options = null)
    {
        using MemoryMediaSource src = new(input);
        CaptureTimePlan plan = CaptureTimeWriter.Plan(src, when, options);
        using MemoryStream output = new();
        SafeWriter.Write(plan.Plan, src, output);
        byte[] result = output.ToArray();
        Assert.Equal(plan.Plan.Length, result.Length);
        Assert.Equal(WriterTestSupport.PayloadHash(input), WriterTestSupport.PayloadHash(result));
        return (result, plan.Report);
    }

    private static MediaFile Open(byte[] bytes) => MediaFile.Open(new MemoryMediaSource(bytes));

    private static bool Reads(MediaFile m, DateTimeOffset when) =>
        m.CaptureTime.Best is { } b && (b.Value.ToDateTimeOffset() is { } i ? i == when : b.Value.Value == when.DateTime);

    [Theory]
    [InlineData("plain.mp4", "mvhd")]
    [InlineData("keys.mov", "mdta:com.apple.quicktime.creationdate")]
    [InlineData("tagged.mkv", "mkv:DATE_RECORDED")]
    [InlineData("undated.mkv", "mkv-info:DateUTC")]
    [InlineData("bwf.wav", "riff-info:ICRD")]
    [InlineData("undated.wav", "riff-info:ICRD")]
    [InlineData("dated.mp3", "id3v2:TDRC")]
    [InlineData("undated.mp3", "id3v2:TDRC")]
    [InlineData("dated.flac", "vorbis:DATE")]
    [InlineData("dated.ogg", "vorbis:DATE")]
    [InlineData("plain.wmv", "asf:FileProperties.CreationDate")]
    public void TheWrittenTimeReadsBackAndThePayloadIsUnchanged(string fixture, string source)
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx(fixture), When);
        using MediaFile m = Open(result);
        Assert.True(Reads(m, When), $"{fixture}: resolved {m.CaptureTime.Best}");
        Assert.Equal(source, m.CaptureTime.Best!.Source);
        Assert.NotEmpty(report.Written);
        Assert.All(report.Written, w => Assert.False(string.IsNullOrEmpty(w.NewValue)));
    }

    [Theory]
    [InlineData("tags_udta.mov")]
    [InlineData("et_xmp_gps.mp4")]
    [InlineData("tags_ilst.mp4")]
    [InlineData("tags_keys_android.mp4")]
    [InlineData("mkvmerge_global.mkv")]
    public void EveryDateCandidateOfTheResultNamesTheWrittenInstant(string fixture)
    {
        (byte[] result, CaptureTimeReport report) = Apply(File.ReadAllBytes(WriterTestSupport.Fixture("metadata", fixture)), When);
        using MediaFile m = Open(result);
        Assert.True(Reads(m, When), $"{fixture}: resolved {m.CaptureTime.Best}");
        Assert.Empty(report.Disagreements);
        foreach (FactCandidate<MediaDateTime> c in m.CaptureTime.Candidates)
        {
            Assert.True(c.Value.ToDateTimeOffset() is { } i ? i == When : c.Value.Value == When.DateTime, $"{fixture}: {c.Source} reads {c.RawValue}");
        }
    }

    [Fact]
    public void AMovieHeaderOfZeroesIsLeftUnset()
    {
        (_, CaptureTimeReport report) = Apply(File.ReadAllBytes(WriterTestSupport.Fixture("metadata", "tags_udta.mov")), When);
        Assert.Contains(report.Left, l => l.System == "tkhd" && l.Reason.StartsWith("unset", StringComparison.Ordinal));
    }

    [Fact]
    public void Id3v23FilesGetTheYearDayMonthAndTimeFrames()
    {
        (byte[] result, _) = Apply(File.ReadAllBytes(WriterTestSupport.Fixture("audio-es", "tagged", "id3v23-basic.mp3")), When);
        using MediaFile m = Open(result);
        Assert.Equal("2024", m.Native["id3v2", "TYER"]!.Text);
        Assert.Equal("0105", m.Native["id3v2", "TDAT"]!.Text);
        Assert.Equal("1230", m.Native["id3v2", "TIME"]!.Text);
        Assert.True(Reads(m, When), $"resolved {m.CaptureTime.Best}");
    }

    // Inputs the fuzz target found: the first six bytes are its time and option prefix. Each must be written or refused with a typed exception, twice in a row.
    [Theory]
    [InlineData("asf-datewrite.bin")]
    [InlineData("asf-extension-size.bin")]
    [InlineData("iso-datewrite.bin")]
    [InlineData("riff-last-chunk-unpadded.bin")]
    public void FuzzFindingsAreWrittenOrRefusedWithATypedException(string name)
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "datewrite", name));
        try
        {
            (byte[] once, _) = Apply(input[6..], When);
            Apply(once, When.AddDays(1));
        }
        catch (MediaException)
        {
            // A typed refusal is fine.
        }
    }

    [Fact]
    public void EveryHeaderOfAnMp4HoldsTheInstantInUtc()
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx("plain.mp4"), When);
        Assert.False(report.HeaderTimesLocal);
        Assert.Contains(report.Written, w => w.System == "mvhd" && w.Field == "creation_time" && w.OldValue == "2020-01-02T03:04:05" && w.NewValue == "2024-05-01T10:30:00");
        Assert.Contains(report.Written, w => w.System == "tkhd");
        Assert.Contains(report.Written, w => w.System == "mdhd");
        using MediaFile m = Open(result);
        Assert.Equal(new DateTime(2024, 5, 1, 10, 30, 0, DateTimeKind.Utc), m.CaptureTime.Best!.Value.Value);
    }

    [Fact]
    public void LocalHeaderTimesStoreTheWallClockWhenAskedFor()
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx("plain.mp4"), When, new CaptureTimeOptions { HeaderTimesAreLocal = true });
        Assert.True(report.HeaderTimesLocal);
        Assert.Contains("local", report.HeaderTimesBasis, StringComparison.Ordinal);
        using MediaFile m = Open(result);
        // Without a device profile the reader takes the header as UTC, so it reads the wall clock back as it was stored.
        Assert.Equal(new DateTime(2024, 5, 1, 12, 30, 0), m.CaptureTime.Best!.Value.Value);
    }

    [Fact]
    public void AppleKeysAndTheHeaderAgreeAndTheOffsetIsKept()
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx("keys.mov"), When);
        using MediaFile m = Open(result);
        Assert.Equal("2024-05-01T12:30:00+0200", m.Native["mdta", "com.apple.quicktime.creationdate"]!.Text);
        Assert.Contains(report.Written, w => w.System == "mdta" && w.Field == "creation_time" && w.NewValue == "2024-05-01T10:30:00Z");
        Assert.Contains(report.Written, w => w.System == "udta" || w.System == "ilst" || w.System == "mdta");
        Assert.Empty(report.Disagreements);
    }

    [Fact]
    public void AMatroskaYearOnlyDateIsLeftWithItsReason()
    {
        (_, CaptureTimeReport report) = Apply(Fx("tagged.mkv"), When);
        Assert.Contains(report.Written, w => w.System == "mkv" && w.Field == "DATE_RECORDED");
        Assert.Contains(report.Written, w => w.System == "mkv-info");
    }

    [Fact]
    public void BwfOriginationDateAndTimeAreBothWritten()
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx("bwf.wav"), When);
        Assert.Contains(report.Written, w => w.System == "bext" && w.Field == "OriginationDate" && w.NewValue == "2024-05-01");
        Assert.Contains(report.Written, w => w.System == "bext" && w.Field == "OriginationTime" && w.NewValue == "12:30:00");
        using MediaFile m = Open(result);
        Assert.Equal("2024-05-01", m.Native["bext", "OriginationDate"]!.Text);
    }

    [Fact]
    public void AddMissingFalseLeavesAFileWithoutADateAlone()
    {
        (byte[] result, CaptureTimeReport report) = Apply(Fx("undated.mp3"), When, new CaptureTimeOptions { AddMissing = false });
        Assert.Empty(report.Written);
        Assert.Equal(Fx("undated.mp3"), result);
    }

    [Fact]
    public void AnExifBlockInAUserDataAtomIsRewrittenInPlaceWithItsOffsetTag()
    {
        byte[] file = TagTestHelpers.IsoWith(Mp4Box("udta", Mp4Box("QVMI", Facts.ExifBlockTests.Tiff(false))));
        (byte[] result, CaptureTimeReport report) = Apply(file, When);
        Assert.Contains(report.Written, w => w.System == "exif:QVMI" && w.Field == "ExifIFD.DateTimeOriginal" && w.NewValue == "2024:05:01 12:30:00+02:00");
        Assert.Equal(file.Length, result.Length);
        using MediaFile m = Open(result);
        FactCandidate<MediaDateTime> exif = Assert.Single(m.CaptureTime.Candidates, c => c.Source == "exif:QVMI:ExifIFD.DateTimeOriginal");
        Assert.Equal(When, exif.Value.ToDateTimeOffset());
    }

    [Fact]
    public void DatesOutsideTheFieldRangeAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Apply(Fx("plain.mp4"), new DateTimeOffset(1850, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Apply(Fx("plain.mp4"), new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AFormatWithoutAWriterIsRefused()
    {
        string avi = WriterTestSupport.Fixture("avi", Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "testdata", "avi"), "*.avi").Select(Path.GetFileName).First()!);
        Assert.Throws<UnsupportedMediaException>(() => Apply(File.ReadAllBytes(avi), When));
    }

    [Fact]
    public void EveryOffsetFormatsTheSameInstant()
    {
        Assert.Equal("2024-05-01T12:30:00+02:00", DateText.Offset(When, colon: true, zulu: true));
        Assert.Equal("2024-05-01T12:30:00+0200", DateText.Offset(When, colon: false, zulu: false));
        Assert.Equal("2024-05-01T10:30:00Z", DateText.Offset(When.ToUniversalTime(), colon: true, zulu: true));
        Assert.Equal("Wed May  1 12:30:00 2024", DateText.Ctime(When));
        Assert.Equal("2024-05-01T12:30:00-0330", DateText.Offset(new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.FromMinutes(-210)), colon: false, zulu: false));
        _ = CultureInfo.InvariantCulture;
    }

    private static byte[] Mp4Box(string type, params byte[][] payload) => IsoBmffSupport.Mp4Builder.Box(type, payload);
}

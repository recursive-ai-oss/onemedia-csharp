using System.Text;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Privacy;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests.Privacy;

// The DJI, AVCHD camera data and text-track parts of the strip, on the synthetic files in testdata/telemetry (invented coordinates and serial) and
// on files this test writes.
public class StripTelemetryTextTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "strip2-" + Guid.NewGuid().ToString("N") + "-" + name);

    private static (string Path, byte[] Bytes) Copy(string folder, string name)
    {
        string p = Temp(name);
        File.Copy(WriterTestSupport.Fixture(folder, name), p);
        return (p, File.ReadAllBytes(p));
    }

    private static byte[] Run(string path, byte[] original, StripOptions? options, out StripReport report, out StripPlan plan)
    {
        plan = PrivacyStrip.Plan(path, options);
        report = plan.Report;
        return WriterTestSupport.Apply(plan.Plan, original);
    }

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    // The hash of the video and audio tracks only; a telemetry track's samples change by design.
    private static SortedDictionary<int, string> MediaHash(byte[] file)
    {
        using MediaFile mf = MediaFile.Open(new MemoryMediaSource(file));
        HashSet<int> keep = [.. mf.Tracks.Where(t => t.Kind is TrackKind.Video or TrackKind.Audio).Select(t => t.Index)];
        return new SortedDictionary<int, string>(WriterTestSupport.PayloadHash(file).Where(k => keep.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value));
    }

    [Theory]
    [InlineData("djmd-real.mp4")]
    [InlineData("djmd-osmo.mp4")]
    public void DjmdPositionAltitudeAndSerialAreGoneAndNothingMoves(string name)
    {
        (string path, byte[] original) = Copy("telemetry", name);
        try
        {
            using (MediaFile before = MediaFile.Open(new MemoryMediaSource(original)))
            {
                Assert.Contains(before.Telemetry, c => c.Name == "djmd gps");
            }

            byte[] output = Run(path, original, null, out StripReport report, out StripPlan plan);
            Assert.True(plan.Plan.IsSameLayout);
            Assert.Equal(original.Length, output.Length);
            Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.Name + " " + l.Reason)));
            Assert.Contains(report.Entries, e => e.System == "djmd" && e.Name == "djmd gps" && e.Action == StripAction.Overwritten);
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.DoesNotContain(after.Telemetry, c => c.Name is "djmd gps" or "djmd altitude" or "serial number" && c.Samples.Count > 0);
            Assert.False(Contains(output, "SYNTHETIC-SERIAL"u8));
            Assert.Equal(MediaHash(original), MediaHash(output));
            // The other fields (attitude, camera settings) are still there.
            Assert.Contains(after.Telemetry, c => c.Name == "djmd aircraft" || c.Name == "djmd camera");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DjmdSerialGoesOnlyWhenDeviceIdsAreAsked()
    {
        (string path, byte[] original) = Copy("telemetry", "djmd-real.mp4");
        try
        {
            byte[] output = Run(path, original, StripOptions.LocationOnly, out _, out _);
            Assert.True(Contains(output, "SYNTHETIC-SERIAL"u8));
            output = Run(path, original, StripOptions.DeviceIdsOnly, out StripReport report, out _);
            Assert.False(Contains(output, "SYNTHETIC-SERIAL"u8));
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.Contains(after.Telemetry, c => c.Name == "djmd gps" && c.Samples.Count > 0);
            Assert.DoesNotContain(report.Entries, e => e.Category == StripCategories.Location);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DjiTextTrackLosesItsCoordinatesAndKeepsItsOtherFields()
    {
        (string path, byte[] original) = Copy("telemetry", "dji-text.mp4");
        try
        {
            byte[] output = Run(path, original, null, out StripReport report, out StripPlan plan);
            Assert.True(plan.Plan.IsSameLayout);
            Assert.True(report.IsComplete);
            Assert.Contains(report.Entries, e => e.System == "text");
            Assert.False(Contains(output, "52.5"u8), "the latitude text is gone");
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.DoesNotContain(after.Telemetry, c => c.Name == "dji gps" && c.Samples.Count > 0);
            Assert.Contains(after.Telemetry, c => c.Name == "dji camera" && c.Samples.Count > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("mdpm-gps.mts")]
    [InlineData("mdpm-south-west.mts")]
    public void MdpmPositionEntriesAreOverwrittenInPlaceInTheTransportStream(string name)
    {
        (string path, byte[] original) = Copy("telemetry", name);
        try
        {
            using (MediaFile before = MediaFile.Open(new MemoryMediaSource(original)))
            {
                Assert.Contains(before.Telemetry, c => c.Name == "mdpm gps");
            }

            byte[] output = Run(path, original, null, out StripReport report, out StripPlan plan);
            Assert.True(plan.Plan.IsSameLayout);
            Assert.Equal(original.Length, output.Length);
            Assert.True(report.IsComplete);
            Assert.Contains(report.Entries, e => e.System == "mdpm" && e.Action == StripAction.Overwritten);
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.DoesNotContain(after.Telemetry, c => c.Name == "mdpm gps" && c.Samples.Count > 0);
            Assert.DoesNotContain(after.Defects, d => d.Severity >= DefectSeverity.Error);

            // Only bytes the report covers changed; a TS header, the other NAL units and every other byte are as they were.
            long changed = 0;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != output[i])
                {
                    changed++;
                    Assert.Equal(0xFF, output[i]);
                }
            }

            Assert.InRange(changed, 1, report.Entries.Sum(e => e.Bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MdpmDeviceIdsOnlyChangesNothing()
    {
        (string path, byte[] original) = Copy("telemetry", "mdpm-gps.mts");
        try
        {
            byte[] output = Run(path, original, StripOptions.DeviceIdsOnly, out StripReport report, out _);
            Assert.Equal(original, output);
            Assert.Empty(report.Entries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[latitude: 59.302335] [longitude: 18.203059] [rel_alt: 1.300 abs_alt: 132.860] [iso : 2700]", "[iso : 2700]")]
    [InlineData("GPS(39.906217,116.391305,69.800) BAROMETER(91.2)\nHOME(39.906206,116.391400) D=5.2m", "BAROMETER(91.2)")]
    [InlineData("F/5.6, GPS (-58.851745, -34.237922, 15), HOME (-58.847509, -34.232707, -57.98m), D 698.70m", "F/5.6")]
    [InlineData("seen at +47.6205-122.3493+012.5/ today", "seen at")]
    [InlineData("48.858370, 2.294481 is a place", "is a place")]
    [InlineData("Serial Number: ABCD123456 ok", "ok")]
    public void TextRulesBlankPositionsAndKeepTheRest(string text, string kept)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (TextScrub.Span s in TextScrub.Find(bytes, StripCategories.All))
        {
            bytes.AsSpan(s.Start, s.Length).Fill((byte)' ');
        }

        string result = Encoding.UTF8.GetString(bytes);
        Assert.Equal(text.Length, result.Length);
        Assert.Contains(kept, result);
        Assert.DoesNotMatch(@"\d{2,3}\.\d{3}", result.Replace("698.70", "").Replace("57.98", ""));
        Assert.DoesNotContain("ABCD123456", result);
    }

    [Theory]
    [InlineData("Version 1.2.345 build 2.000")]
    [InlineData("00:00:01.500 --> 00:00:03.250")]
    [InlineData("Chapter 3, page 12.5 of 20")]
    [InlineData("Latitude is a word, and so is alt")]
    [InlineData("ISO 100 f/1.8 1/60 24.000 fps")]
    public void TextRulesLeaveOrdinarySubtitlesAlone(string text)
    {
        Assert.Empty(TextScrub.Find(Encoding.UTF8.GetBytes(text), StripCategories.All));
    }

    [Fact]
    public void TextRulesNeverThrowOnShortOrHostileBytes()
    {
        Random rng = new(44);
        byte[] buf = new byte[64];
        for (int round = 0; round < 2000; round++)
        {
            rng.NextBytes(buf);
            for (int i = 0; i < buf.Length; i += 3)
            {
                buf[i] = (byte)"0123456789.,-+/GPSgpshomeLATlat: ()"[rng.Next(35)];
            }

            _ = TextScrub.Find(buf.AsSpan(0, rng.Next(buf.Length + 1)), StripCategories.All);
        }
    }

    [Fact]
    public void MatroskaTagRemovalKeepsTheLayoutSoSaveInPlaceWorks()
    {
        string src = Temp("src.mka");
        string tagged = Temp("tagged.mka");
        try
        {
            File.Copy(WriterTestSupport.Fixture("audio", "aac.mka"), src);
            TagEdits edits = new();
            edits.SetNative(TagSystems.Matroska, "RECORDING_LOCATION", "Atlantis");
            edits.SetNative(TagSystems.Matroska, "SERIAL_NUMBER", "MKVSERIAL42");
            edits.Set(CommonField.Title, "Kept");
            TagWriter.SaveAs(src, tagged, edits);
            byte[] original = File.ReadAllBytes(tagged);
            StripPlan plan = PrivacyStrip.Plan(tagged);
            Assert.True(plan.Plan.IsSameLayout, "Matroska strip should keep the layout");
            byte[] output = WriterTestSupport.Apply(plan.Plan, original);
            Assert.Equal(original.Length, output.Length);
            Assert.False(Contains(output, "Atlantis"u8));
            Assert.False(Contains(output, "MKVSERIAL42"u8));
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.Equal("Kept", after.Tags.Title);
            Assert.DoesNotContain(after.Defects, d => d.Severity >= DefectSeverity.Error);
            PrivacyStrip.SaveInPlace(tagged);
            Assert.Equal(output, File.ReadAllBytes(tagged));
        }
        finally
        {
            File.Delete(src);
            File.Delete(tagged);
        }
    }

    [Fact]
    public void Id3FrameRemovalKeepsTheLayoutSoSaveInPlaceWorks()
    {
        string src = Temp("src.mp3");
        string tagged = Temp("tagged.mp3");
        try
        {
            File.Copy(WriterTestSupport.Fixture("audio-es", "plain.mp3"), src);
            TagEdits edits = new();
            edits.Set(CommonField.Title, "Kept");
            edits.SetNative(TagSystems.Id3v2, "TXXX:SERIAL_NUMBER", "ID3SERIAL42");
            edits.SetNative(TagSystems.Id3v2, "TXXX:LOCATION", "Atlantis");
            TagWriter.SaveAs(src, tagged, edits);
            byte[] original = File.ReadAllBytes(tagged);
            Assert.True(Contains(original, "Atlantis"u8));
            StripPlan plan = PrivacyStrip.Plan(tagged);
            Assert.True(plan.Plan.IsSameLayout, "ID3 strip should keep the layout");
            byte[] output = WriterTestSupport.Apply(plan.Plan, original);
            Assert.False(Contains(output, "Atlantis"u8));
            Assert.Equal(original.Length, output.Length);
            Assert.False(Contains(output, "ID3SERIAL42"u8));
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.Equal("Kept", after.Tags.Title);
            Assert.Equal(WriterTestSupport.PayloadHash(original), WriterTestSupport.PayloadHash(output));
        }
        finally
        {
            File.Delete(src);
            File.Delete(tagged);
        }
    }
}

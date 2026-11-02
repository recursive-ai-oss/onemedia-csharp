using System.Text;
using RecursiveAI.OneMedia.Privacy;

namespace RecursiveAI.OneMedia.Tests.Privacy;

// Regression tests for the findings of the privacy-strip review. The files in testdata/strip/review are written by tools/strip/review/gen-adversarial.py
// (invented position 47.620512,-122.349312 and serial ZQ9X7R5T3M1K).
public class StripReviewTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "strip3-" + Guid.NewGuid().ToString("N") + "-" + name);

    private static byte[] Strip(string name, out StripReport report, StripOptions? options = null)
    {
        string path = Temp(name);
        try
        {
            File.Copy(WriterTestSupport.Fixture("strip/review", name), path);
            byte[] original = File.ReadAllBytes(path);
            StripPlan plan = PrivacyStrip.Plan(path, options);
            report = plan.Report;
            return WriterTestSupport.Apply(plan.Plan, original);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool Has(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static bool HasUtf16(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0 || haystack.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(text)) >= 0;

    [Theory]
    [InlineData("47\u00b037'14.8\"N 122\u00b020'57.5\"W is here", "is here")]
    [InlineData("seen at 47\u00b0 37' 14.8\" N, 122\u00b0 20' 57.5\" W today", "seen at")]
    [InlineData("47.620512\u00b0 N, 122.349312\u00b0 W ok", "ok")]
    [InlineData("47.620512\n-122.349312 ok", "ok")]
    [InlineData("47.620512\t-122.349312 ok", "ok")]
    [InlineData("latitude 47.620512 longitude 122.349312 ok", "ok")]
    public void TextRulesTakeDegreesMinutesSecondsLineBreaksAndSpacedKeywords(string text, string kept)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (TextScrub.Span s in TextScrub.Find(bytes, StripCategories.All))
        {
            bytes.AsSpan(s.Start, s.Length).Fill((byte)' ');
        }

        string result = Encoding.UTF8.GetString(bytes);
        Assert.Contains(kept, result);
        Assert.DoesNotMatch(@"\d{2,3}\.\d{3}", result);
        Assert.DoesNotContain("37'", result);
    }

    [Theory]
    [InlineData("12:30 on 3 May at 5 degrees")]
    [InlineData("Room 47 section 12 row 3")]
    [InlineData("lat 5 lon 3")]
    public void TextRulesStillLeaveOrdinaryTextAlone(string text) => Assert.Empty(TextScrub.Find(Encoding.UTF8.GetBytes(text), StripCategories.All));

    [Fact]
    public void WebVttCuesAreScannedPastTheirBoxHeaders()
    {
        // payl's four type bytes sit directly in front of the cue text; they used to glue themselves to the first word or number.
        byte[] original = File.ReadAllBytes(WriterTestSupport.Fixture("strip/review", "text-wvtt.mp4"));
        byte[] output = Strip("text-wvtt.mp4", out StripReport report);
        Assert.Equal(original.Length, output.Length);
        Assert.False(Has(output, "47.620512"));
        Assert.False(Has(output, "122.349312"));
        Assert.False(Has(output, "ZQ9X7R5T3M1K"));
        Assert.True(Has(output, "payl") && Has(output, "vttc"), "the cue boxes themselves stay");
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void Utf16TimedTextIsScannedAndBlankedAsUtf16()
    {
        byte[] original = File.ReadAllBytes(WriterTestSupport.Fixture("strip/review", "text-tx3g-utf16.mp4"));
        Assert.True(HasUtf16(original, "ZQ9X7R5T3M1K"));
        byte[] output = Strip("text-tx3g-utf16.mp4", out StripReport report);
        Assert.Equal(original.Length, output.Length);
        Assert.False(HasUtf16(output, "ZQ9X7R5T3M1K"));
        Assert.False(HasUtf16(output, "47.620512"));
        Assert.False(HasUtf16(output, "122.349312"));
        Assert.Contains(report.Entries, e => e.System == "text");
    }

    [Fact]
    public void XmpPropertiesNamedGeoAreRemovedAsTheDocumentationSays()
    {
        byte[] output = Strip("xmp-geo-words.mp4", out _);
        Assert.False(Has(output, "xmp:Geo="));
        Assert.False(Has(output, "47.620512,-122.349312"));
    }

    [Fact]
    public void XmpPacketsInUtf16AreReportedNotSilentlyKept()
    {
        Strip("xmp-utf16.mp4", out StripReport report);
        Assert.False(report.IsComplete);
        Assert.Contains(report.NotRemoved, l => l.System == "xmp" && l.Reason.Contains("UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDataTracksAreNamedInTheNotes()
    {
        Strip("tele-unknown-track.mp4", out StripReport report);
        Assert.Contains(report.Notes, n => n.Contains("Data track 2", StringComparison.Ordinal) && n.Contains("xxxx", StringComparison.Ordinal));
    }

    [Fact]
    public void MatroskaTagsAimedAtATrackAndNestedSimpleTagsAreRemovedToo()
    {
        byte[] original = File.ReadAllBytes(WriterTestSupport.Fixture("strip/review", "mkv-tags.mkv"));
        Assert.Equal(2, CountOf(original, "47.620512-122.349312+184.5/"));
        byte[] output = Strip("mkv-tags.mkv", out StripReport report);
        Assert.Equal(original.Length, output.Length);
        Assert.Equal(0, CountOf(output, "47.620512-122.349312+184.5/"));
        Assert.False(Has(output, "nested"), "the SimpleTag under GPS goes with its parent");
        // Every entry the report lists as removed really changed.
        foreach (StripEntry e in report.Entries.Where(e => e.System == "mkv"))
        {
            Assert.False(original.AsSpan((int)e.Range.Offset, (int)e.Range.Length).SequenceEqual(output.AsSpan((int)e.Range.Offset, (int)e.Range.Length)), e.Name + " is reported but unchanged");
        }
    }

    [Fact]
    public void ATagsElementAfterTheLastClusterWithoutSeekHeadEntryIsReported()
    {
        Strip("mkv-tags-at-end.mkv", out StripReport report);
        Assert.False(report.IsComplete);
        Assert.Contains(report.NotRemoved, l => l.Name == "Tags");
    }

    [Theory]
    [InlineData("com.example.lat", StripCategories.Location)]
    [InlineData("com.example.LON", StripCategories.Location)]
    [InlineData("DEVICE_SN", StripCategories.DeviceIds)]
    [InlineData("translate", StripCategories.None)]
    [InlineData("snow", StripCategories.None)]
    [InlineData("com.apple.quicktime.model", StripCategories.None)]
    public void ShortNamesCountOnlyAsWholeTokens(string name, StripCategories expected) => Assert.Equal(expected, StripRules.ClassifyWords(name));

    [Fact]
    public void Id3FileOwnerIsADeviceId() => Assert.Equal(StripCategories.DeviceIds, StripRules.Classify("id3v2", "TOWN"));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(37)]
    public void MdpmPositionsAfterDroppedOrInsertedBytesAreStillFound(int junk)
    {
        // Bytes inserted in the middle of a recording move every later packet off the 188-byte grid; the scan finds the next run of sync bytes and goes on.
        byte[] clean = File.ReadAllBytes(WriterTestSupport.Fixture("telemetry", "mdpm-gps.mts"));
        int cut = (clean.Length / 188 / 2 * 188) + (junk == 37 ? 50 : 0);
        byte[] damaged = [.. clean.AsSpan(0, cut), .. Enumerable.Repeat((byte)0x11, junk), .. clean.AsSpan(cut)];
        string path = Temp("junk.mts");
        try
        {
            File.WriteAllBytes(path, damaged);
            StripPlan plan = PrivacyStrip.Plan(path);
            byte[] output = WriterTestSupport.Apply(plan.Plan, damaged);
            Assert.Equal(damaged.Length, output.Length);
            Assert.Equal(21, MdpmPositionEntries(damaged));
            Assert.Equal(0, MdpmPositionEntries(output));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Position entries (tags B0-C8, CA-CC) of every MDPM message, read straight from the bytes.
    private static int MdpmPositionEntries(byte[] d)
    {
        byte[] marker = [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66, (byte)'M', (byte)'D', (byte)'P', (byte)'M'];
        int count = 0, at = -1;
        while ((at = d.AsSpan(at + 1).IndexOf(marker) is var i and >= 0 ? at + 1 + i : -1) >= 0)
        {
            int entries = d[at + 20];
            for (int e = 0; e < entries && at + 21 + (5 * e) < d.Length; e++)
            {
                byte tag = d[at + 21 + (5 * e)];
                if (tag is (>= 0xB0 and <= 0xC8) or (>= 0xCA and <= 0xCC))
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void RiffInfoArchivalLocationIsReallyRemovedWhenTheReportSaysSo()
    {
        static byte[] Chunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
        byte[] fmt = [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0];
        byte[] info = [.. "INFO"u8, .. Chunk("IARL", [.. "+47.620512-122.349312+184.5/"u8, 0]), .. Chunk("ICMT", [.. "kept"u8, 0])];
        // A first INFO list of its own (many writers add one with their name) and a second one holding the location.
        byte[] firstList = [.. "INFO"u8, .. Chunk("ISFT", [.. "writer"u8, 0])];
        byte[] body = [.. "WAVE"u8, .. Chunk("fmt ", fmt), .. Chunk("LIST", firstList), .. Chunk("data", new byte[64]), .. Chunk("LIST", info)];
        byte[] wav = [.. "RIFF"u8, .. BitConverter.GetBytes(body.Length), .. body];
        string path = Temp("archival.wav");
        try
        {
            File.WriteAllBytes(path, wav);
            StripPlan plan = PrivacyStrip.Plan(path);
            byte[] output = WriterTestSupport.Apply(plan.Plan, wav);
            Assert.Contains(plan.Report.Entries, e => e.Name == "IARL");
            Assert.False(Has(output, "47.620512"), "the report lists IARL as removed, so it must be gone");
            Assert.True(Has(output, "kept"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static int CountOf(byte[] data, string text)
    {
        byte[] needle = Encoding.UTF8.GetBytes(text);
        int count = 0, at = 0;
        while ((at = data.AsSpan(at).IndexOf(needle) is var i and >= 0 ? at + i + 1 : -1) > 0)
        {
            count++;
        }

        return count;
    }
}

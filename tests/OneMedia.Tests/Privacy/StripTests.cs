using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Metadata.Telemetry;
using RecursiveAI.OneMedia.Privacy;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests.Privacy;

// testdata/strip/strip-mixed.mp4 is written by tools/gen-strip-fixtures.py: invented coordinates and serial numbers in every place the ISO strip handles.
public class StripTests
{
    private static readonly string[] Secrets =
    [
        "+47.6205-122.3493", "+47.6206-122.3494", "LXX0000000000001", "C0000000000042", "XMPBODY0000042", "XMPAUX0000042", "EXIFSERIAL000042",
        "AAAA-BBBB-CCCC-0042", "C9999999999999", "47,37.230N", "122,29.358W", "Somewhere", "240101120000",
    ];

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "strip-" + Guid.NewGuid().ToString("N") + "-" + name);

    private static (string Path, byte[] Bytes) Copy(string name, string folder = "strip")
    {
        string p = Temp(name);
        File.Copy(WriterTestSupport.Fixture(folder, name), p);
        return (p, File.ReadAllBytes(p));
    }

    private static byte[] Run(string path, byte[] original, StripOptions? options, out StripReport report)
    {
        StripPlan plan = PrivacyStrip.Plan(path, options);
        report = plan.Report;
        return WriterTestSupport.Apply(plan.Plan, original);
    }

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static IEnumerable<byte[]> SecretBytes()
    {
        foreach (string s in Secrets)
        {
            yield return Encoding.UTF8.GetBytes(s);
        }

        // The 16-byte camera hash and the 32-byte media id, and the GPS5 latitude as stored (476205000, big-endian) and the CAMM latitude (double, little-endian).
        yield return Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray();
        yield return Enumerable.Range(0x10, 32).Select(i => (byte)i).ToArray();
        byte[] be = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(be, 476205000);
        yield return be;
        byte[] d = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(d, 47.6205);
        yield return d;
    }

    [Fact]
    public void StripAllRemovesEverythingTheFixtureHolds()
    {
        (string path, byte[] original) = Copy("strip-mixed.mp4");
        try
        {
            foreach (byte[] s in SecretBytes())
            {
                Assert.True(Contains(original, s), "the fixture should hold " + Convert.ToHexString(s));
            }

            byte[] output = Run(path, original, null, out StripReport report);
            Assert.Equal(original.Length, output.Length);
            Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.System + ":" + l.Name + " " + l.Reason)));
            foreach (byte[] s in SecretBytes())
            {
                Assert.False(Contains(output, s), "still present: " + Convert.ToHexString(s));
            }

            // Everything outside the changed ranges is the same byte for byte, and the tracks' packets keep their sizes.
            Assert.Equal(WriterTestSupport.PayloadHash(original).Keys, WriterTestSupport.PayloadHash(output).Keys);
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.DoesNotContain(after.Defects, d => d.Severity >= DefectSeverity.Error);
            string[] left = [.. after.Metadata.NativeTags.Select(t => t.System + ":" + t.Key)];
            Assert.DoesNotContain("udta:\u00A9xyz", left);
            Assert.DoesNotContain("udta:LENS", left);
            Assert.DoesNotContain("udta:CAME", left);
            Assert.DoesNotContain("udta:MUID", left);
            Assert.DoesNotContain("udta:GUMI", left);
            Assert.DoesNotContain("3gpp:loci", left);
            Assert.DoesNotContain("mdta:com.apple.quicktime.location.ISO6709", left);
            Assert.DoesNotContain("mdta:com.apple.quicktime.content.identifier", left);
            // Make and model stay.
            Assert.Contains("udta:\u00A9mak", left);
            Assert.Contains("udta:\u00A9mod", left);
            Assert.Contains("mdta:com.apple.quicktime.model", left);
            Assert.Contains(after.Metadata.XmpPackets, x => x.Find("http://purl.org/dc/elements/1.1/", "title") is not null);
            Assert.DoesNotContain(after.Metadata.XmpPackets, x => x.Properties.Any(p => p.Name.StartsWith("GPS", StringComparison.Ordinal) || p.Name.Contains("Serial", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StripKeepsTheStructureOfTelemetryAndZeroesTheValues()
    {
        (string path, byte[] original) = Copy("strip-mixed.mp4");
        try
        {
            byte[] output = Run(path, original, null, out _);
            using MediaFile before = MediaFile.Open(new MemoryMediaSource(original));
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            MediaTrack gpmd = after.Tracks.Single(t => t.CodecTag == "gpmd");
            using (TelemetryTrackReader reader = after.OpenTelemetry(gpmd))
            {
                int gps = 0;
                while (reader.TryRead(out TelemetryItem item))
                {
                    if (item.Kind == TelemetryKind.Gps)
                    {
                        gps++;
                        foreach (double v in item.Values)
                        {
                            Assert.True(v == 0 || double.IsNaN(v), "GPS value " + v);
                        }
                    }
                }

                Assert.Equal(0, gps); // the GPS streams are renamed to EMPT, so no GPS sample is reported any more
                Assert.Contains(reader.Channels, c => c.Name == "ACCL");
            }

            MediaTrack camm = after.Tracks.Single(t => t.CodecTag == "camm");
            using (TelemetryTrackReader reader = after.OpenTelemetry(camm))
            {
                List<TelemetryItem> items = [];
                while (reader.TryRead(out TelemetryItem item))
                {
                    items.Add(item);
                }

                Assert.All(items.Where(i => i.Kind == TelemetryKind.Gps), i => Assert.All(i.Values.ToArray(), v => Assert.True(v == 0 || double.IsNaN(v))));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LocationOnlyKeepsSerialsAndDeviceIdsOnlyKeepsLocation()
    {
        (string path, byte[] original) = Copy("strip-mixed.mp4");
        try
        {
            byte[] noLocation = Run(path, original, StripOptions.LocationOnly, out StripReport r1);
            Assert.All(r1.Entries, e => Assert.Equal(StripCategories.Location, e.Category));
            Assert.False(Contains(noLocation, "+47.6205-122.3493"u8));
            Assert.False(Contains(noLocation, "47,37.230N"u8));
            Assert.True(Contains(noLocation, "LXX0000000000001"u8));
            Assert.True(Contains(noLocation, "XMPBODY0000042"u8));
            Assert.True(Contains(noLocation, "EXIFSERIAL000042"u8));

            byte[] noIds = Run(path, original, StripOptions.DeviceIdsOnly, out StripReport r2);
            Assert.All(r2.Entries, e => Assert.Equal(StripCategories.DeviceIds, e.Category));
            Assert.True(Contains(noIds, "+47.6205-122.3493"u8));
            Assert.True(Contains(noIds, "47,37.230N"u8));
            Assert.False(Contains(noIds, "LXX0000000000001"u8));
            Assert.False(Contains(noIds, "C0000000000042"u8));
            Assert.False(Contains(noIds, "XMPBODY0000042"u8));
            Assert.False(Contains(noIds, "EXIFSERIAL000042"u8));
            Assert.False(Contains(noIds, "AAAA-BBBB-CCCC-0042"u8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheReportNamesEveryFieldWithItsRange()
    {
        (string path, byte[] original) = Copy("strip-mixed.mp4");
        try
        {
            StripPlan plan = PrivacyStrip.Plan(path);
            Assert.True(plan.Plan.IsSameLayout);
            string[] names = [.. plan.Report.Entries.Select(e => e.System + ":" + e.Name)];
            foreach (string expected in new[] { "udta:\u00A9xyz", "udta:LENS", "udta:CAME", "udta:MUID", "udta:GUMI", "3gpp:loci", "mdta:com.apple.quicktime.location.ISO6709", "mdta:com.apple.quicktime.content.identifier",
                         "gpmd:GPS5", "gpmd:GPSU", "gpmd:CASN", "udta:CASN", "udta:LINF", "camm:camm type 6", "camm:camm type 5", "mebx:com.apple.quicktime.location.ISO6709", "exif:EXIF BodySerialNumber", "exif:EXIF GPS IFD" })
            {
                Assert.Contains(expected, names);
            }

            Assert.Contains(plan.Report.Entries, e => e.System == "xmp" && e.Name.EndsWith("#GPSLatitude", StringComparison.Ordinal));
            Assert.Contains(plan.Report.Entries, e => e.System == "gpmd" && e.Name == "GPS5" && e.Count == 2 && e.Bytes > 0 && e.Range.Length > 0);
            foreach (StripEntry e in plan.Report.Entries)
            {
                Assert.InRange(e.Range.Offset, 0, original.Length);
                Assert.True(e.Range.End <= original.Length, e.System + ":" + e.Name);
            }

            // The fdsc blob repeated the lens serial, camera serial and position without labels.
            Assert.Contains(plan.Report.Entries, e => e.System == "fdsc" && e.Category == StripCategories.DeviceIds);
            Assert.Contains(plan.Report.Entries, e => e.System == "fdsc" && e.Category == StripCategories.Location);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveInPlaceWorksBecauseNothingMoves()
    {
        (string path, byte[] original) = Copy("strip-mixed.mp4");
        try
        {
            StripReport report = PrivacyStrip.SaveInPlace(path);
            Assert.True(report.Write!.LayoutPreserved);
            byte[] now = File.ReadAllBytes(path);
            Assert.Equal(original.Length, now.Length);
            Assert.False(Contains(now, "LXX0000000000001"u8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AudioPayloadIsUnchangedAndTheTagIsGone()
    {
        string src = Temp("src.m4a");
        string tagged = Temp("tagged.m4a");
        try
        {
            File.Copy(WriterTestSupport.Fixture("audio", "aac.m4a"), src);
            TagEdits edits = new();
            edits.SetNative(TagSystems.ITunes, "\u00A9xyz", "+10.1234+020.5678/");
            edits.Set(CommonField.Title, "Kept");
            TagWriter.SaveAs(src, tagged, edits);
            byte[] original = File.ReadAllBytes(tagged);
            Assert.True(Contains(original, "+10.1234+020.5678/"u8));
            byte[] output = Run(tagged, original, StripOptions.LocationOnly, out StripReport report);
            Assert.False(Contains(output, "+10.1234+020.5678/"u8));
            Assert.Equal(WriterTestSupport.PayloadHash(original), WriterTestSupport.PayloadHash(output));
            using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
            Assert.Equal("Kept", after.Tags.Title);
            Assert.Single(report.Entries);
        }
        finally
        {
            File.Delete(src);
            File.Delete(tagged);
        }
    }

    [Fact]
    public void TiffScrubEmptiesTheGpsIfdAndRenamesSerialEntries()
    {
        // A little-endian TIFF built by hand: IFD0 with a GPSInfo pointer; GPS IFD with one entry (latitude, three rationals out of line); Exif IFD with a body serial.
        byte[] t = new byte[200];
        "II*\0"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), 2); // IFD0: 2 entries
        WriteEntry(t, 10, 0x8825, 4, 1, 60); // GPS IFD at 60
        WriteEntry(t, 22, 0x8769, 4, 1, 100); // Exif IFD at 100
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(60), 1);
        WriteEntry(t, 62, 2, 5, 3, 150); // latitude at 150, 24 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(100), 1);
        WriteEntry(t, 102, 0xA431, 2, 8, 140); // serial at 140
        "SERIAL1\0"u8.CopyTo(t.AsSpan(140));
        Array.Fill(t, (byte)0x77, 150, 24);
        PatchSet patches = new();
        StripSink sink = new(patches);
        Assert.True(TiffScrub.Scrub(t.AsMemory(), 1000, "exif", 0, StripCategories.All, sink));
        EditPlan plan = patches.ToPlan(2000);
        byte[] file = new byte[2000];
        t.CopyTo(file, 1000);
        byte[] after = WriterTestSupport.Apply(plan, file);
        Assert.False(Contains(after, "SERIAL1"u8));
        Assert.True(after.AsSpan(1150, 24).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1060)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1102)));
        Assert.Equal(0x8825, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1010)));
        Assert.Equal(2000, after.Length);
    }

    private static void WriteEntry(byte[] t, int at, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 8), value);
    }

    [Fact]
    public void TiffScrubIgnoresBytesThatAreNotTiffAndNeverThrowsOnDamage()
    {
        PatchSet p = new();
        StripSink sink = new(p);
        Assert.False(TiffScrub.Scrub("not a tiff at all"u8.ToArray(), 0, "exif", 0, StripCategories.All, sink));
        Random rnd = new(7);
        byte[] t = new byte[256];
        for (int round = 0; round < 500; round++)
        {
            rnd.NextBytes(t);
            "II*\0"u8.CopyTo(t);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), (uint)rnd.Next(0, 300));
            TiffScrub.Scrub(t.AsMemory(), 0, "exif", 0, StripCategories.All, new StripSink(new PatchSet()));
        }
    }

    [Fact]
    public void NonIsoFormatsGoThroughTheTagWriters()
    {
        // Matroska: a recording-location tag and a serial tag are removed, the title stays.
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
            Assert.True(Contains(original, "Atlantis"u8));
            byte[] output = Run(tagged, original, null, out StripReport report);
            Assert.False(Contains(output, "Atlantis"u8));
            Assert.False(Contains(output, "MKVSERIAL42"u8));
            Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.System + ":" + l.Name + " " + l.Range.Offset + " " + l.Reason)));
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

    [Fact]
    public void AFormatThatNeedsNoStripIsCopiedAndSaysSo()
    {
        (string path, byte[] original) = Copy("flac.flac", "audio");
        try
        {
            byte[] output = Run(path, original, null, out StripReport report);
            Assert.Equal(original, output);
            Assert.Empty(report.Entries);
            Assert.True(report.IsComplete);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Exif;
using RecursiveAI.OneMedia.Metadata;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests.Facts;

public class FactResolverTests
{
    private static FactCandidate<MediaDateTime> Time(string source, string text, TimeZoneCertainty? force = null)
    {
        Assert.True(MediaDateTime.TryParse(text, out MediaDateTime dt, out _));
        if (force is { } c)
        {
            dt = dt with { Certainty = c };
        }

        return new FactCandidate<MediaDateTime>(dt, source, text, "test", dt.Certainty);
    }

    private static MediaFacts Resolve(FactEvidence e, DeviceProfileTable? profiles = null) => new DefaultFactResolver(profiles).Resolve(e);

    [Fact]
    public void OffsetBearingVendorTimeBeatsHeaderAndLocal()
    {
        FactEvidence e = new()
        {
            CaptureTime = [Time("mvhd", "2023-04-05T10:00:00Z"), Time("udta:©day", "2023-04-05T12:00:00"), Time("mdta:com.apple.quicktime.creationdate", "2023-04-05T12:00:01+0200")],
        };
        MediaFacts f = Resolve(e);
        Assert.Equal("mdta:com.apple.quicktime.creationdate", f.CaptureTime.Best!.Source);
        Assert.Equal(TimeZoneCertainty.Offset, f.CaptureTime.Best.Certainty);
        Assert.Equal("udta:©day", f.CaptureTime.Candidates[1].Source);
        Assert.Equal("mvhd", f.CaptureTime.Candidates[2].Source);
        Assert.Equal("capture-time.tier-1", f.CaptureTime.Resolution!.Rule);
        Assert.Equal(3, f.CaptureTime.Candidates.Count);
    }

    [Fact]
    public void TelemetryGpsTimeSitsBetweenVendorOffsetAndLocalAndIsMovedBackToTheStart()
    {
        GpsSample fix = new(new MediaTime(2002, 1000), MediaTime.Unknown, ByteRange.None, 47, 8, Fix: GpsFixKind.Fix3D, GpsTime: new DateTime(2018, 1, 24, 19, 27, 58, 674, DateTimeKind.Utc));
        TelemetryChannel gps = new(TelemetryKind.Gps, "gpmf", "GPS5", [fix]);
        FactEvidence e = new() { CaptureTime = [Time("udta:©day", "2018-01-24T11:27:32")], Telemetry = [gps] };
        MediaFacts f = Resolve(e);
        Assert.Equal("telemetry:gpmf:GPS5", f.CaptureTime.Best!.Source);
        Assert.Equal(new DateTime(2018, 1, 24, 19, 27, 56, 672, DateTimeKind.Utc), f.CaptureTime.Best.Value.Value);
        Assert.Equal(TimeZoneCertainty.Utc, f.CaptureTime.Best.Certainty);

        // A vendor time with an offset still wins over telemetry.
        e = e with { CaptureTime = [.. e.CaptureTime, Time("xmp:x", "2018-01-24T11:27:00-08:00")] };
        Assert.Equal("xmp:x", Resolve(e).CaptureTime.Best!.Source);
    }

    [Fact]
    public void GpsSamplesWithoutFixOrWithRolloverYearAreNotEvidence()
    {
        GpsSample noFix = new(MediaTime.Zero, MediaTime.Unknown, ByteRange.None, 1, 2, Fix: GpsFixKind.None, GpsTime: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        GpsSample old = new(MediaTime.Zero, MediaTime.Unknown, ByteRange.None, 1, 2, Fix: GpsFixKind.Fix2D, GpsTime: new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc));
        MediaFacts f = Resolve(new FactEvidence { Telemetry = [new TelemetryChannel(TelemetryKind.Gps, "camm", "type 6", [noFix, old])] });
        Assert.False(f.CaptureTime.HasValue);
        Assert.True(f.Location.HasValue); // the second sample is a valid fix, only its clock is junk
        Assert.Equal("location.telemetry", f.Location.Resolution!.Rule);
    }

    [Fact]
    public void HeaderTimeIsUtcByDefaultAndLocalWhenTheProfileSaysSo()
    {
        FactCandidate<MediaDateTime> mvhd = new(new MediaDateTime(new DateTime(2018, 1, 24, 11, 27, 32, DateTimeKind.Utc), null, TimeZoneCertainty.Utc), "mvhd", "3", "seconds since 1904", TimeZoneCertainty.Utc);
        FactCandidate<MediaDateTime> day = Time("udta:©day", "2018-01-24T12:00:00");
        FactEvidence plain = new() { CaptureTime = [mvhd, day], Model = [new FactCandidate<string>("HERO6 Black", "udta:©mod", "HERO6 Black", "text")] };

        MediaFacts withProfile = Resolve(plain);
        Assert.Equal("gopro", withProfile.Profile!.Id);
        Assert.Equal(TimeZoneCertainty.Local, withProfile.CaptureTime.Candidates.Single(c => c.Source == "mvhd").Certainty);
        Assert.Equal("capture-time.tier-3", withProfile.CaptureTime.Resolution!.Rule); // the profiled header joined the local readings, in reader order
        Assert.Equal("mvhd", withProfile.CaptureTime.Best!.Source);

        MediaFacts without = Resolve(plain, DeviceProfileTable.Empty);
        Assert.Null(without.Profile);
        Assert.Equal(TimeZoneCertainty.Utc, without.CaptureTime.Candidates.Single(c => c.Source == "mvhd").Certainty);
        Assert.Equal("udta:©day", without.CaptureTime.Best!.Source);
        Assert.Equal("mvhd", without.CaptureTime.Candidates[^1].Source);
    }

    [Theory]
    [InlineData("HD6.01.01.60.00", true)]
    [InlineData("H19.03.01.30.00", true)]
    [InlineData("FS1.04.01.70.00", true)]
    [InlineData("H264 encoder", false)]
    [InlineData("hd6.01.01.60.00", false)]
    [InlineData("HD6.01.01.60", false)]
    public void GoProFirmwareStringIdentifiesTheDevice(string firmware, bool gopro)
    {
        FactCandidate<MediaDateTime> mvhd = new(new MediaDateTime(new DateTime(2018, 1, 24, 11, 27, 32, DateTimeKind.Utc), null, TimeZoneCertainty.Utc), "mvhd", "3", "seconds since 1904", TimeZoneCertainty.Utc);
        MediaFacts f = Resolve(new FactEvidence { CaptureTime = [mvhd], Software = [new FactCandidate<string>(firmware, "udta:FIRM", firmware, "text")] });
        Assert.Equal(gopro, f.Profile?.Id == "gopro-firmware");
        Assert.Equal(gopro ? TimeZoneCertainty.Local : TimeZoneCertainty.Utc, f.CaptureTime.Best!.Certainty);
    }

    [Fact]
    public void GpsEvidenceThatTheHeaderIsUtcOverridesALocalTimeProfile()
    {
        FactCandidate<MediaDateTime> mvhd = new(new MediaDateTime(new DateTime(2017, 4, 17, 19, 27, 57, DateTimeKind.Utc), null, TimeZoneCertainty.Utc), "mvhd", "3", "seconds since 1904", TimeZoneCertainty.Utc);
        GpsSample fix = new(MediaTime.Zero, MediaTime.Unknown, ByteRange.None, 33, -117, Fix: GpsFixKind.Fix3D, GpsTime: new DateTime(2017, 4, 17, 19, 27, 57, 526, DateTimeKind.Utc));
        FactCandidate<string> fw = new("HD5.02.02.00.00", "udta:FIRM", "HD5.02.02.00.00", "text");
        MediaFacts f = Resolve(new FactEvidence { CaptureTime = [mvhd], Software = [fw], Telemetry = [new TelemetryChannel(TelemetryKind.Gps, "gpmf", "GPS5", [fix])] });
        Assert.Equal(TimeZoneCertainty.Utc, f.CaptureTime.Candidates.Single(c => c.Source == "mvhd").Certainty);
        Assert.Contains("not applied", f.CaptureTime.Resolution!.Reason, StringComparison.Ordinal);

        // A GPS time eight hours away confirms the profile.
        GpsSample far = fix with { GpsTime = new DateTime(2017, 4, 18, 3, 28, 0, DateTimeKind.Utc) };
        f = Resolve(new FactEvidence { CaptureTime = [mvhd], Software = [fw], Telemetry = [new TelemetryChannel(TelemetryKind.Gps, "gpmf", "GPS5", [far])] });
        Assert.Equal(TimeZoneCertainty.Local, f.CaptureTime.Candidates.Single(c => c.Source == "mvhd").Certainty);
    }

    [Fact]
    public void FileNameAndFileSystemAreLastAndOnlyWhenGiven()
    {
        FactEvidence e = new() { CaptureTime = [Time("mvhd", "2023-04-05T10:00:00Z")] };
        Assert.Single(Resolve(e).CaptureTime.Candidates);
        MediaFacts f = Resolve(e with { FileName = "VID_20230401_090807.mp4", FileSystemTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        Assert.Equal(["mvhd", "filename", "filesystem"], f.CaptureTime.Candidates.Select(c => c.Source));
        Assert.Equal(new DateTime(2023, 4, 1, 9, 8, 7), f.CaptureTime.Candidates[1].Value.Value);
    }

    [Fact]
    public void NothingIsDroppedAndAnEmptyFactHasNoValue()
    {
        MediaFacts f = Resolve(new FactEvidence());
        Assert.False(f.CaptureTime.HasValue);
        Assert.False(f.Location.HasValue);
        Assert.False(f.Device.Serial.HasValue);
    }

    [Theory]
    [InlineData("VID_20230405_101112.mp4", true, 2023, 4, 5, 10, 11, 12)]
    [InlineData("PXL_20230405_101112345.mp4", true, 2023, 4, 5, 10, 11, 12)]
    [InlineData("2023-04-05 10.11.12.mov", true, 2023, 4, 5, 10, 11, 12)]
    [InlineData("clip 2023-04-05_10-11-12.mp4", true, 2023, 4, 5, 10, 11, 12)]
    [InlineData("20230405101112.avi", true, 2023, 4, 5, 10, 11, 12)]
    [InlineData("IMG_0412.MOV", false, 0, 0, 0, 0, 0, 0)]
    [InlineData("20231345_101112.mp4", false, 0, 0, 0, 0, 0, 0)]
    [InlineData("20230405.mp4", false, 0, 0, 0, 0, 0, 0)]
    public void FileNameDatesParse(string name, bool ok, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.Equal(ok, FileNameDates.TryParse(name, out MediaDateTime v, out _));
        if (ok)
        {
            Assert.Equal(new DateTime(y, mo, d, h, mi, s), v.Value);
            Assert.Equal(TimeZoneCertainty.Unknown, v.Certainty);
        }
    }

    [Fact]
    public void CustomResolverCanReplaceTheDefault()
    {
        FactEvidence e = new() { CaptureTime = [Time("mvhd", "2023-04-05T10:00:00Z"), Time("udta:©day", "2023-04-05T12:00:00")] };
        MediaFacts f = new ReverseResolver().Resolve(e);
        Assert.Equal("udta:©day", f.CaptureTime.Best!.Source);
        Assert.Equal("reverse", f.CaptureTime.Resolution!.Rule);
    }

    private sealed class ReverseResolver : IFactResolver
    {
        public MediaFacts Resolve(FactEvidence evidence) => new(Fact.Create<MediaTime>([]), Fact.Create(evidence.CaptureTime.Reverse(), new FactResolution("reverse", "test", "reverse")), Fact.Create<GeoLocation>([]),
            Fact.Create<string>([]), Fact.Create<string>([]), Fact.Create<string>([]), Fact.Create<VideoOrientation>([]), Fact.Create<FrameSize>([]));
    }

    [Fact]
    public void DeviceProfileMatchingNeedsMakeOrModel()
    {
        DeviceProfile p = new("t", null, null, true, ProfileBasis.Documented, "x");
        Assert.False(p.Matches("Any", "Thing"));
        DeviceProfile make = new("m", "Apple", null, false, ProfileBasis.Documented, "x");
        Assert.True(make.Matches(" apple ", null));
        Assert.False(make.Matches("Google", "Pixel"));
        DeviceProfile model = new("g", null, "HERO", true, ProfileBasis.ObservedInFiles, "x");
        Assert.True(model.Matches(null, "HERO6 Black"));
        Assert.False(model.Matches("GoPro", "Fusion"));
    }
}

public class ExifBlockTests
{
    internal static byte[] Tiff(bool big)
    {
        // IFD0: Make, Model, ExifIFD ptr, GPS ptr; ExifIFD: DateTimeOriginal, OffsetTimeOriginal, BodySerialNumber; GPS: refs, lat, lon, alt, time, date.
        List<byte> b = [];
        void U16(int v) { Span<byte> s = stackalloc byte[2]; if (big) { BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)v); } else { BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)v); } b.AddRange(s.ToArray()); }
        void U32(int v) { Span<byte> s = stackalloc byte[4]; if (big) { BinaryPrimitives.WriteUInt32BigEndian(s, (uint)v); } else { BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)v); } b.AddRange(s.ToArray()); }
        void Entry(int tag, int type, int count, int value) { U16(tag); U16(type); U32(count); U32(value); }
        b.AddRange(big ? "MM\0*"u8.ToArray() : "II*\0"u8.ToArray());
        U32(8);
        // IFD0 at 8: 4 entries -> 2 + 48 + 4 = 54 bytes, data area from 62
        List<byte> pool = [];
        int poolBase = 62;
        int Put(byte[] bytes) { int o = poolBase + pool.Count; pool.AddRange(bytes); if (pool.Count % 2 == 1) { pool.Add(0); } return o; }
        int make = Put("Acme\0"u8.ToArray());
        int model = Put("Cam 9\0"u8.ToArray());
        U16(4);
        Entry(0x010F, 2, 5, make);
        Entry(0x0110, 2, 6, model);
        int exifIfd = 0;
        int gpsIfd = 0;
        int exifPos = b.Count;
        Entry(0x8769, 4, 1, 0);
        int gpsPos = b.Count;
        Entry(0x8825, 4, 1, 0);
        U32(0);

        byte[] Rat(params (int n, int d)[] r)
        {
            List<byte> x = [];
            foreach ((int n, int d) in r)
            {
                byte[] s = new byte[8];
                if (big) { BinaryPrimitives.WriteUInt32BigEndian(s, (uint)n); BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(4), (uint)d); }
                else { BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)n); BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(4), (uint)d); }
                x.AddRange(s);
            }

            return [.. x];
        }

        int dto = Put("2023:04:05 10:11:12\0"u8.ToArray());
        int serial = Put("SN12345\0"u8.ToArray());
        int lat = Put(Rat((47, 1), (22, 1), (3000, 100)));
        int lon = Put(Rat((8, 1), (30, 1), (0, 1)));
        int alt = Put(Rat((4500, 10)));
        int ts = Put(Rat((9, 1), (11, 1), (12, 1)));
        int ds = Put("2023:04:05\0"u8.ToArray());
        int off = Put("+02:00\0"u8.ToArray());

        // The directories go after the pool.
        int dirBase = poolBase + pool.Count;
        exifIfd = dirBase;
        List<byte> head = b;
        b = [];
        U16(3);
        Entry(0x9003, 2, 20, dto);
        Entry(0x9011, 2, 7, off);
        Entry(0xA431, 2, 8, serial);
        U32(0);
        List<byte> exifBytes = b;
        b = [];
        // The offset tag shifted the GPS directory; recompute.
        gpsIfd = dirBase + exifBytes.Count;
        U16(9);
        Entry(0x0001, 2, 2, ReadInline("N\0"u8, big));
        Entry(0x0002, 5, 3, lat);
        Entry(0x0003, 2, 2, ReadInline("E\0"u8, big));
        Entry(0x0004, 5, 3, lon);
        Entry(0x0005, 1, 1, 0);
        Entry(0x0006, 5, 1, alt);
        Entry(0x0007, 5, 3, ts);
        Entry(0x001D, 2, 11, ds);
        Entry(0x0009, 2, 2, ReadInline("A\0"u8, big));
        U32(0);
        List<byte> gpsBytes = b;

        byte[] result = [.. head, .. pool, .. exifBytes, .. gpsBytes];
        // patch the sub-IFD pointers
        void Patch(int pos, int v) { if (big) { BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 8), (uint)v); } else { BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 8), (uint)v); } }
        Patch(exifPos, exifIfd);
        Patch(gpsPos, gpsIfd);
        return result;
    }

    // An inline ASCII value occupies the first bytes of the 4-byte field; build the int whose in-memory bytes (in the block's order) read back as the text.
    private static int ReadInline(ReadOnlySpan<byte> text, bool big)
    {
        Span<byte> four = stackalloc byte[4];
        text.CopyTo(four);
        return big ? BinaryPrimitives.ReadInt32BigEndian(four) : BinaryPrimitives.ReadInt32LittleEndian(four);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesDirectoriesAndMapsToFacts(bool big)
    {
        byte[] tiff = Tiff(big);
        byte[] wrapped = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        DefectLog defects = new();
        Assert.True(ExifBlock.TryParse(wrapped, 1000, MediaLimits.Default, defects, out ExifBlock block));
        Assert.Empty(defects);
        Assert.Equal(big, block.BigEndian);
        Assert.Equal("Acme", block.Find("IFD0", 0x010F)!.AsText());
        Assert.Equal(1000 + 6 + 62, block.Find("IFD0", 0x010F)!.ValueRange.Offset);

        EvidenceSet e = block.ToEvidence();
        Assert.Equal("Acme", e.Make.Single().Value);
        Assert.Equal("Cam 9", e.Model.Single().Value);
        Assert.Equal("SN12345", e.Serial.Single().Value);
        FactCandidate<MediaDateTime> local = e.CaptureTime.Single(c => c.Source == "exif:ExifIFD.DateTimeOriginal");
        Assert.Equal(TimeZoneCertainty.Offset, local.Certainty);
        Assert.Equal(120, local.Value.OffsetMinutes);
        Assert.Equal(new DateTime(2023, 4, 5, 10, 11, 12), local.Value.Value);
        FactCandidate<MediaDateTime> gps = e.CaptureTime.Single(c => c.Source == "exif:GPS.DateTime");
        Assert.Equal(new DateTime(2023, 4, 5, 9, 11, 12, DateTimeKind.Utc), gps.Value.Value);
        GeoLocation g = e.Location.Single().Value;
        Assert.Equal(47 + (22 / 60.0) + (30 / 3600.0), g.Latitude, 6);
        Assert.Equal(8.5, g.Longitude, 6);
        Assert.Equal(450, g.Altitude);
    }

    [Fact]
    public void LoopsTruncationAndGarbageAreDefectsNotExceptions()
    {
        // IFD0 whose next-IFD offset points at itself.
        byte[] loop = [(byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 0, 0, 8, 0, 0, 0];
        DefectLog d = new();
        Assert.True(ExifBlock.TryParse(loop, -1, MediaLimits.Default, d, out ExifBlock b));
        Assert.Empty(b.Entries);
        Assert.Contains(d, x => x.Code == "exif.loop");

        // Entry count says 1000 but the block ends.
        byte[] cut = [(byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8, 0x03, 0xE8, 1, 2, 3];
        d = new DefectLog();
        Assert.True(ExifBlock.TryParse(cut, 0, MediaLimits.Default, d, out _));
        Assert.Contains(d, x => x.Code == "exif.truncated");

        Assert.False(ExifBlock.TryParse(new byte[64], 0, MediaLimits.Default, new DefectLog(), out _));

        // A value offset far outside the block.
        byte[] far = [(byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 0xFF, 0xFF, 0, 0, 0xFE, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0];
        d = new DefectLog();
        Assert.True(ExifBlock.TryParse(far, 0, MediaLimits.Default, d, out ExifBlock fb));
        Assert.Single(fb.Entries);
        Assert.True(fb.Entries[0].Value.IsEmpty);
        Assert.Contains(d, x => x.Code == "exif.truncated");
    }
}

public class SidecarTests
{
    private const string SonyXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <NonRealTimeMeta xmlns="urn:schemas-professionalDisc:nonRealTimeMeta:ver.2.00" lastUpdate="2016-01-03T06:12:00-04:00">
         <Duration value="120"/>
         <CreationDate value="2016-01-03T06:12:00-04:00"/>
         <Device manufacturer="Sony" modelName="FDR-AX100" serialNo="1234567890"/>
        </NonRealTimeMeta>
        """;

    [Fact]
    public void SonyXmlGivesOffsetTimeAndDevice()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(SonyXml);
        Assert.Equal(SidecarKind.SonyXml, Sidecars.Sniff(bytes));
        EvidenceSet e = Sidecars.Read(bytes)!;
        FactCandidate<MediaDateTime> t = e.CaptureTime.Single();
        Assert.Equal(TimeZoneCertainty.Offset, t.Certainty);
        Assert.Equal(-240, t.Value.OffsetMinutes);
        Assert.Equal("Sony", e.Make.Single().Value);
        Assert.Equal("FDR-AX100", e.Model.Single().Value);
        Assert.Equal("1234567890", e.Serial.Single().Value);
    }

    [Fact]
    public void SonyXmlWithAllOnesSerialAndZuluTime()
    {
        EvidenceSet e = Sidecars.ReadSonyXml(Encoding.UTF8.GetBytes(SonyXml.Replace("1234567890", "4294967295").Replace("-04:00\"/>\n <Device", "Z\"/>\n <Device")));
        Assert.Empty(e.Serial);
    }

    [Fact]
    public void SonyXmlMalformedKeepsWhatCameBeforeAndReportsADefect()
    {
        EvidenceSet e = Sidecars.ReadSonyXml(Encoding.UTF8.GetBytes(SonyXml[..(SonyXml.IndexOf("<Device", StringComparison.Ordinal) - 1)] + "<Device manufacturer=\"Sony"));
        Assert.Single(e.CaptureTime);
        Assert.Contains(e.Defects, d => d.Code == "sidecar.xml-malformed");
    }

    [Fact]
    public void XmlBombsAreRefusedWithoutExpansion()
    {
        string bomb = "<?xml version=\"1.0\"?><!DOCTYPE NonRealTimeMeta [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><NonRealTimeMeta><CreationDate value=\"&b;\"/></NonRealTimeMeta>";
        EvidenceSet e = Sidecars.ReadSonyXml(Encoding.UTF8.GetBytes(bomb));
        Assert.Empty(e.CaptureTime);
        Assert.NotEmpty(e.Defects);
    }

    private const string Srt = "1\r\n00:00:00,000 --> 00:00:00,033\r\n<font size=\"36\">SrtCnt : 1, DiffTime : 33ms\r\n2023-02-24 08:47:37.832\r\n[iso : 100] [shutter : 1/250.0] [fnum : 170] [ev : 0] [ct : 4730] [latitude: 12.345678] [longitude: -56.789012] [rel_alt: 0.500 abs_alt: 101.250] </font>\r\n\r\n2\r\n00:00:00,033 --> 00:00:00,066\r\n<font size=\"36\">SrtCnt : 2\r\n2023-02-24 08:47:37.862\r\n</font>\r\n";

    [Fact]
    public void DjiSrtGivesLocalTimeAndPosition()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Srt);
        Assert.Equal(SidecarKind.DjiSrt, Sidecars.Sniff(bytes));
        EvidenceSet e = Sidecars.Read(bytes)!;
        FactCandidate<MediaDateTime> t = e.CaptureTime.Single();
        Assert.Equal(new DateTime(2023, 2, 24, 8, 47, 37, 832), t.Value.Value);
        Assert.Equal(TimeZoneCertainty.Unknown, t.Certainty);
        GeoLocation g = e.Location.Single().Value;
        Assert.Equal(12.345678, g.Latitude);
        Assert.Equal(-56.789012, g.Longitude);
        Assert.Equal(101.250, g.Altitude);
    }

    [Fact]
    public void DjiSrtZeroPositionIsSkippedAndCueStartIsSubtracted()
    {
        string srt = "5\n00:00:01,000 --> 00:00:01,033\nFrameCnt: 5\n2026-05-27 13:14:22.911 [latitude: 0.000000] [longitude: 0.000000]\n";
        EvidenceSet e = Sidecars.ReadDjiSrt(Encoding.UTF8.GetBytes(srt));
        Assert.Empty(e.Location);
        Assert.Equal(new DateTime(2026, 5, 27, 13, 14, 21, 911), e.CaptureTime.Single().Value.Value);
    }

    [Fact]
    public void XmpSidecarUsesTheSameMappingAsEmbeddedXmp()
    {
        string xmp = """
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/" xmlns:aux="http://ns.adobe.com/exif/1.0/aux/"
              exif:DateTimeOriginal="2020-06-01T12:00:00+09:00" tiff:Make="Canon" tiff:Model="EOS R5" aux:SerialNumber="0123"/>
            </rdf:RDF></x:xmpmeta><?xpacket end="w"?>
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(xmp);
        Assert.Equal(SidecarKind.Xmp, Sidecars.Sniff(bytes));
        EvidenceSet e = Sidecars.Read(bytes)!;
        Assert.Equal(TimeZoneCertainty.Offset, e.CaptureTime.Single().Certainty);
        Assert.StartsWith("sidecar:xmp:", e.CaptureTime.Single().Source, StringComparison.Ordinal);
        Assert.Equal("EOS R5", e.Model.Single().Value);
        Assert.Equal("0123", e.Serial.Single().Value);
    }

    [Fact]
    public void UnknownContentIsNotASidecar() => Assert.Null(Sidecars.Read("hello world"u8.ToArray()));

    [Fact]
    public void SidecarEvidenceJoinsTheCandidatesAndWinsByCertainty()
    {
        FactEvidence e = new() { CaptureTime = [new FactCandidate<MediaDateTime>(new MediaDateTime(new DateTime(2016, 1, 3, 10, 12, 0, DateTimeKind.Utc), null, TimeZoneCertainty.Utc), "mvhd", "1", "x", TimeZoneCertainty.Utc)] };
        e = e.With(Sidecars.Read(Encoding.UTF8.GetBytes(SonyXml))!);
        MediaFacts f = new DefaultFactResolver().Resolve(e);
        Assert.Equal("sidecar:sony-xml:CreationDate", f.CaptureTime.Best!.Source);
        Assert.Equal("Sony", f.Make.Best!.Value);
        Assert.Equal("1234567890", f.Device.Serial.Best!.Value);
        Assert.Equal(2, f.CaptureTime.Candidates.Count);
    }
}

public class FactsHappyPathTests
{
    [Fact]
    public void ExifInAUserDataAtomBecomesCandidates()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", Box("QVMI", ExifBlockTests.Tiff(false))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.Equal("Acme", m.Facts.Make.Best!.Value);
            Assert.Equal("SN12345", m.Facts.Serial.Best!.Value);
            Assert.Equal("exif:QVMI:ExifIFD.DateTimeOriginal", m.Facts.CaptureTime.Candidates[0].Source);
            Assert.True(m.Facts.Location.HasValue);
        });
    }

    [Fact]
    public void GoProFirmwareAtomBecomesTheSoftwareCandidateAndSelectsTheProfile()
    {
        byte[] file = TagTestHelpers.IsoWith(Box("udta", Box("FIRM", System.Text.Encoding.Latin1.GetBytes("HD6.01.01.60.00"))));
        TagTestHelpers.WithIso(file, m =>
        {
            Assert.Equal("udta:FIRM", m.Facts.Software.Best!.Source);
            Assert.Equal("HD6.01.01.60.00", m.Facts.Software.Best.Value);
            MediaFacts f = new DefaultFactResolver().Resolve(FactEvidence.FromContainer(m.Facts));
            Assert.Equal("gopro-firmware", f.Profile?.Id);
        });
    }

    [Fact]
    public void SidecarsBesideTheFileAreFoundAndResolved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemedia-facts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string media = Path.Combine(dir, "C0001.mp4");
            File.Copy(TagTestHelpers.Path("tags_ilst.mp4"), media);
            File.WriteAllText(Path.Combine(dir, "C0001M01.XML"), "<NonRealTimeMeta><CreationDate value=\"2016-01-03T06:12:00-04:00\"/><Device manufacturer=\"Sony\" modelName=\"FDR-AX100\" serialNo=\"77\"/></NonRealTimeMeta>");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
            using MediaFile f = Media.Open(media);
            Assert.DoesNotContain(f.CaptureTime.Candidates, c => c.Source.StartsWith("sidecar:", StringComparison.Ordinal));
            IReadOnlyList<string> found = f.AddSidecarsBesideFile();
            Assert.Equal([Path.Combine(dir, "C0001M01.XML")], found);
            Assert.Equal("sidecar:sony-xml:CreationDate", f.CaptureTime.Best!.Source);
            Assert.Equal("FDR-AX100", f.Device.Model.Best!.Value);
            Assert.Equal("77", f.Device.Serial.Best!.Value);
            Assert.Contains(f.CaptureTime.Candidates, c => c.Source == "filename" || c.Source == "filesystem");

            f.Resolver = new DefaultFactResolver(DeviceProfileTable.Empty);
            Assert.Null(f.Facts.Profile);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class FactsFixtureTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "facts", name));

    [Theory]
    [InlineData("exif-le.tiff", false)]
    [InlineData("exif-be.tiff", true)]
    public void ExifFixturesParseInBothByteOrders(string name, bool big)
    {
        DefectLog defects = new();
        Assert.True(ExifBlock.TryParse(Fixture(name), 0, MediaLimits.Default, defects, out ExifBlock b));
        Assert.Empty(defects);
        Assert.Equal(big, b.BigEndian);
        EvidenceSet e = b.ToEvidence();
        Assert.Equal("Acme", e.Make.Single().Value);
        Assert.Equal(120, e.CaptureTime[0].Value.OffsetMinutes);
        Assert.Equal(8.5, e.Location.Single().Value.Longitude, 6);
    }

    [Theory]
    [InlineData("sidecar.xmp", SidecarKind.Xmp, "EOS R5")]
    [InlineData("sony-clip.xml", SidecarKind.SonyXml, "FDR-AX100")]
    public void SidecarFixturesAreDetectedAndRead(string name, SidecarKind kind, string model)
    {
        byte[] bytes = Fixture(name);
        Assert.Equal(kind, Sidecars.Sniff(bytes));
        EvidenceSet e = Sidecars.Read(bytes)!;
        Assert.Equal(model, e.Model.Single().Value);
        Assert.Empty(e.Defects);
    }

    [Fact]
    public void DjiFixtureGivesPosition()
    {
        EvidenceSet e = Sidecars.Read(Fixture("dji.srt"))!;
        Assert.Equal(12.345678, e.Location.Single().Value.Latitude);
        Assert.Equal(new DateTime(2023, 2, 24, 8, 47, 37, 832), e.CaptureTime.Single().Value.Value);
    }
}

public class FactsFuzzRegressionTests
{
    private static string Dir(string target) => Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", target);

    [Fact]
    public void ExifRepros()
    {
        foreach (string f in Directory.GetFiles(Dir("exif")))
        {
            byte[] data = File.ReadAllBytes(f);
            if (ExifBlock.TryParse(data, 0, MediaLimits.Default, new DefectLog(), out ExifBlock b))
            {
                EvidenceSet set = b.ToEvidence();
                _ = DefaultFactResolver.Instance.Resolve(new FactEvidence().With(set));
            }
        }
    }

    [Fact]
    public void SidecarRepros()
    {
        foreach (string f in Directory.GetFiles(Dir("sidecars")))
        {
            ReadOnlyMemory<byte> data = File.ReadAllBytes(f).AsMemory(1);
            _ = Sidecars.ReadXmp(data);
            _ = Sidecars.ReadSonyXml(data);
            _ = Sidecars.ReadDjiSrt(data);
            _ = Sidecars.Read(data);
        }
    }

    [Fact]
    public void OffsetBeyondFourteenHoursHasNoInstant()
    {
        Assert.True(MediaDateTime.TryParse("2023-04-05T10:00:00+22:00", out MediaDateTime dt, out _));
        Assert.Null(dt.ToDateTimeOffset());
        Assert.Equal(TimeSpan.FromHours(2), MediaDateTime.TryParse("2023-04-05T10:00:00+02:00", out MediaDateTime ok, out _) ? ok.ToDateTimeOffset()!.Value.Offset : default);
    }
}

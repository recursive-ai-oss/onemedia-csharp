using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Privacy;
using RecursiveAI.OneMedia.Tests.Avi;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests.Privacy;

// What the strip has no rule for must show up in StripReport.NotRemoved, named from what the file holds. The files are built here.
public class StripInventoryTests
{
    private static StripReport Report(byte[] file, out byte[] output)
    {
        StripPlan plan = PrivacyStrip.Plan(new MemoryMediaSource(file));
        output = WriterTestSupport.Apply(plan.Plan, file);
        return plan.Report;
    }

    private static StripReport Report(byte[] file) => Report(file, out _);

    private static byte[] Atom(string type, byte[] payload)
    {
        byte[] b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    private static byte[] Filled(int n, byte v)
    {
        byte[] b = new byte[n];
        Array.Fill(b, v);
        return b;
    }

    [Fact]
    public void ACleanIsoFileReportsNothing() => Assert.True(Report(TagTestHelpers.IsoWith()).IsComplete);

    [Fact]
    public void IsoVendorBoxesAtomsAndTrailersAreNamed()
    {
        byte[] file = TagTestHelpers.IsoWith(
            Atom("udta", Atom("SNUM", Filled(24, 0x41))),
            Atom("vndr", Filled(16, 0x42)),
            Atom("free", new byte[40]));
        file = [.. file, .. Atom("uuid", [.. Filled(16, 0x11), .. Filled(8, 0x43)]), .. Atom("zzzz", Filled(8, 0x44)), 1, 2, 3, 4, 5];
        StripReport report = Report(file, out byte[] output);
        Assert.Equal(file, output);
        string[] names = [.. report.NotRemoved.Select(l => l.Name)];
        Assert.Contains("SNUM", names);
        Assert.Contains("vndr", names);
        Assert.Contains("zzzz", names);
        Assert.Contains(names, n => n.StartsWith("uuid ", StringComparison.Ordinal));
        Assert.Contains("trailer", names);
        Assert.DoesNotContain("free", names);
        Assert.False(report.IsComplete);
        StripLeftover snum = report.NotRemoved.First(l => l.Name == "SNUM");
        Assert.Equal(file.AsSpan().IndexOf("SNUM"u8) - 4, snum.Range.Offset);
    }

    [Fact]
    public void ACoverPictureIsReportedOnlyWhenItHoldsMetadata()
    {
        byte[] jpegWithExif = [0xFF, 0xD8, 0xFF, 0xE1, 0, 14, .. "Exif\0\0"u8.ToArray(), 1, 2, 3, 4, 5, 6, 0xFF, 0xDA, 0, 2, 0xFF, 0xD9];
        byte[] jpegPlain = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xDA, 0, 2, 0xFF, 0xD9];
        byte[] Cover(byte[] image) => TagTestHelpers.IsoWith(Atom("udta", TagTestHelpers.Ilst(TagTestHelpers.Item("covr", TagTestHelpers.Data(13, image)))));
        StripReport withExif = Report(Cover(jpegWithExif));
        Assert.Contains(withExif.NotRemoved, l => l.Name == "covr" && l.Reason.Contains("Exif", StringComparison.Ordinal));
        Assert.DoesNotContain(Report(Cover(jpegPlain)).NotRemoved, l => l.Name == "covr");
    }

    private static byte[] WrappedExif(string serial)
    {
        // JPEG SOI + APP1, then 'Exif', two zero bytes and a little-endian TIFF block: IFD0 -> Exif IFD -> MakerNote holding the serial.
        byte[] note = new byte[20];
        Encoding.ASCII.GetBytes(serial).CopyTo(note, 0);
        byte[] tiff = new byte[64];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x8769);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18), 26);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(28), 0x927C);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(30), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(32), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(36), 44);
        note.CopyTo(tiff, 44);
        byte[] result = new byte[2 + 4 + 6 + tiff.Length];
        result[0] = 0xFF; result[1] = 0xD8; result[2] = 0xFF; result[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)(2 + 6 + tiff.Length));
        "Exif\0\0"u8.CopyTo(result.AsSpan(6));
        tiff.CopyTo(result, 12);
        return result;
    }

    [Fact]
    public void ExifInsideAVendorAtomOrUuidBoxIsScrubbedNotJustReported()
    {
        const string Serial = "FAKESERIAL-0042-QQ";
        byte[] atomFile = TagTestHelpers.IsoWith(Atom("udta", Atom("VNDX", [.. Filled(32, 0x41), .. WrappedExif(Serial)])));
        StripReport atom = Report(atomFile, out byte[] atomOut);
        Assert.True(atomFile.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Serial)) >= 0);
        Assert.True(atomOut.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Serial)) < 0, string.Join("; ", atom.Entries.Select(e => e.Name)) + " | " + string.Join("; ", atom.NotRemoved.Select(l => l.Name + ":" + l.Reason)) + " | " + string.Join("; ", atom.Notes));
        Assert.Equal(atomFile.Length, atomOut.Length);
        Assert.Contains(atom.Entries, e => e.Name == "EXIF MakerNote");
        Assert.DoesNotContain(atom.NotRemoved, l => l.Name == "VNDX");
        Assert.Contains(atom.Notes, n => n.Contains("VNDX", StringComparison.Ordinal));

        byte[] uuid = Atom("uuid", [.. Filled(16, 0x11), .. Atom("CNTX", WrappedExif(Serial))]);
        byte[] boxFile = TagTestHelpers.IsoWith(uuid);
        StripReport box = Report(boxFile, out byte[] boxOut);
        Assert.True(boxOut.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Serial)) < 0);
        Assert.DoesNotContain(box.NotRemoved, l => l.System == "iso");
    }

    [Fact]
    public void PaddingBoxesThatHoldBytesAreZeroed()
    {
        byte[] file = TagTestHelpers.IsoWith(Atom("free", Filled(24, 0x55)), Atom("udta", Atom("free", Filled(24, 0x66))));
        file = [.. file, .. Atom("free", Filled(24, 0x77))];
        StripReport report = Report(file, out byte[] output);
        Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.System + ":" + l.Name)));
        Assert.Equal(file.Length, output.Length);
        foreach (byte v in new byte[] { 0x55, 0x66, 0x77 })
        {
            Assert.True(file.AsSpan().IndexOf(Filled(24, v)) >= 0);
            Assert.True(output.AsSpan().IndexOf(Filled(24, v)) < 0);
        }
    }

    [Fact]
    public void EntriesSayWhatTheirCountCounts()
    {
        StripReport tag = Report(File.ReadAllBytes(WriterTestSupport.Fixture("strip", "strip-mixed.mp4")));
        StripEntry single = tag.Entries.First(e => e.SamplesExamined == 0);
        Assert.Equal("1 occurrence", single.CountText);
        StripEntry sampled = tag.Entries.First(e => e.SamplesExamined > 0);
        Assert.Equal($"{sampled.Count} occurrence{(sampled.Count == 1 ? string.Empty : "s")} in {sampled.SamplesExamined} samples", sampled.CountText);
        Assert.True(sampled.SamplesExamined >= sampled.Count || sampled.Count > 0);
    }

    private static byte[] Chunk(string id, byte[] data, bool bigEndian = false)
    {
        byte[] b = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.Latin1.GetBytes(id).CopyTo(b, 0);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)data.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
        }

        data.CopyTo(b, 8);
        return b;
    }

    private static byte[] Wav(params byte[][] extra)
    {
        byte[] fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        byte[] body = [.. "WAVE"u8.ToArray(), .. Chunk("fmt ", fmt), .. Chunk("data", new byte[160]), .. extra.SelectMany(e => e)];
        byte[] file = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)body.Length);
        body.CopyTo(file, 8);
        return file;
    }

    [Fact]
    public void WaveVendorChunksListsAndTrailersAreNamed()
    {
        Assert.True(Report(Wav()).IsComplete);
        byte[] list = Chunk("LIST", [.. "ncdt"u8.ToArray(), .. Chunk("nctg", new byte[10])]);
        byte[] file = Wav(Chunk("SNUM", Filled(12, 0x41)), list, Chunk("JUNK", Filled(20, 0x42)), Chunk("PEAK", new byte[8]));
        StripReport report = Report(file, out byte[] output);
        Assert.Equal(file.Length, output.Length);
        Assert.True(file.AsSpan().IndexOf(Filled(20, 0x42)) >= 0);
        Assert.True(output.AsSpan().IndexOf(Filled(20, 0x42)) < 0);
        Assert.Contains(report.Entries, e => e.System == "chunk" && e.Name == "JUNK");
        string[] names = [.. report.NotRemoved.Select(l => l.Name)];
        Assert.Contains("SNUM", names);
        Assert.Contains("LIST ncdt", names);
        Assert.DoesNotContain("JUNK", names);
        Assert.DoesNotContain("PEAK", names);
        Assert.Contains(Report([.. Wav(), 1, 2, 3, 4, 5]).NotRemoved, l => l.Name == "trailer");
    }

    [Fact]
    public void AiffApplicationChunksAreNamed()
    {
        byte[] comm = new byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(comm, 1);
        BinaryPrimitives.WriteUInt16BigEndian(comm.AsSpan(6), 16);
        comm[8] = 0x40; comm[9] = 0x0B; comm[10] = 0xFA; // 8000 Hz as an 80-bit float
        byte[] ssnd = Chunk("SSND", new byte[8 + 160], bigEndian: true);
        byte[] appl = Chunk("APPL", [.. "SNUM"u8.ToArray(), .. Filled(10, 0x41)], bigEndian: true);
        byte[] body = [.. "AIFF"u8.ToArray(), .. Chunk("COMM", comm, bigEndian: true), .. ssnd, .. appl];
        byte[] file = new byte[8 + body.Length];
        "FORM"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)body.Length);
        body.CopyTo(file, 8);
        Assert.Contains(Report(file).NotRemoved, l => l.Name == "APPL");
    }

    [Fact]
    public void MatroskaTrailersAndUnknownSegmentElementsAreNamed()
    {
        byte[] original = File.ReadAllBytes(WriterTestSupport.Fixture("strip/review", "mkv-tags.mkv"));
        Assert.DoesNotContain(Report(original).NotRemoved, l => l.Name is "trailer" || l.Name.StartsWith("element", StringComparison.Ordinal));

        Assert.Contains(Report([.. original, .. Encoding.ASCII.GetBytes("trailing bytes")]).NotRemoved, l => l.Name == "trailer");

        // An element the Segment does not define, appended inside it: the Segment's size field grows by the element's length.
        byte[] element = [0x1A, 0x1A, 0x1A, 0x1A, 0x84, 1, 2, 3, 4];
        byte[] grown = [.. original, .. element];
        int segment = grown.AsSpan().IndexOf(new byte[] { 0x18, 0x53, 0x80, 0x67 });
        Assert.True(segment >= 0);
        int sizeAt = segment + 4;
        int length = 1;
        while ((grown[sizeAt] & (0x80 >> (length - 1))) == 0)
        {
            length++;
        }

        int marker = 0x80 >> (length - 1);
        ulong size = (ulong)(grown[sizeAt] & (marker - 1));
        for (int i = 1; i < length; i++)
        {
            size = (size << 8) | grown[sizeAt + i];
        }

        Assert.NotEqual((1UL << (7 * length)) - 1, size); // not an unknown-size Segment
        size += (ulong)element.Length;
        for (int i = length - 1; i >= 1; i--)
        {
            grown[sizeAt + i] = (byte)size;
            size >>= 8;
        }

        grown[sizeAt] = (byte)(marker | (int)size);

        Assert.Contains(Report(grown).NotRemoved, l => l.Name == "element 0x1A1A1A1A");
    }

    [Fact]
    public void AsfObjectsOutsideTheKnownSetAreNamed()
    {
        AsfBuilder clean = new();
        clean.AddFileProperties().AddAudioStream(1).AddContentDescription("t", "a");
        clean.AddPacket(true, new AsfBuilder.Payload(1, true, 1, 0, 8, 0, new byte[8]));
        Assert.True(Report(clean.Build()).IsComplete);

        AsfBuilder b = new();
        b.AddFileProperties().AddAudioStream(1).AddContentDescription("t", "a");
        Guid vendor = new("11111111-2222-3333-4444-555555555555");
        b.HeaderObjects.Add(AsfBuilder.Obj(vendor, Filled(20, 0x41)));
        b.AddPacket(true, new AsfBuilder.Payload(1, true, 1, 0, 8, 0, new byte[8]));
        StripReport report = Report(b.Build());
        Assert.Contains(report.NotRemoved, l => l.System == "asf" && l.Name.Contains("11111111-2222-3333-4444-555555555555", StringComparison.OrdinalIgnoreCase));
    }
}

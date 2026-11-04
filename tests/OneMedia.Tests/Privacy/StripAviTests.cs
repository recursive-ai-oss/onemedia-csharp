using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Exif;
using RecursiveAI.OneMedia.Privacy;
using RecursiveAI.OneMedia.Tests.Avi;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Tests.Privacy;

// AVI files built here carry an invented serial number and coordinates in a Fujifilm-style 'strd' chunk: the text AVIF, four bytes, then an Exif directory
// without a TIFF header (offsets count from the directory), whose maker note holds the serial.
public class StripAviTests
{
    private const string Serial = "FAKESERIAL0042-ZZ";

    private static byte[] Strd(bool withMakerNote = true, bool withGps = true)
    {
        byte[] make = Encoding.ASCII.GetBytes("EXAMPLEMAKER\0"), model = Encoding.ASCII.GetBytes("Model X1\0");
        byte[] note = new byte[40];
        "FUJIFILM"u8.CopyTo(note);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(8), 12);
        Encoding.ASCII.GetBytes(Serial).CopyTo(note, 16);
        int ifd0Entries = 2 + (withMakerNote ? 1 : 0) + (withGps ? 1 : 0);
        int ifd0 = 0, makeAt = ifd0 + 2 + (ifd0Entries * 12) + 4, modelAt = makeAt + make.Length, exifAt = modelAt + model.Length;
        int exifLength = withMakerNote ? 2 + 12 + 4 : 0, noteAt = exifAt + exifLength, gpsAt = noteAt + (withMakerNote ? note.Length : 0);
        int gpsLength = withGps ? 2 + 24 + 4 : 0, latAt = gpsAt + gpsLength, total = latAt + (withGps ? 24 : 0);
        byte[] d = new byte[8 + total];
        "AVIF"u8.CopyTo(d);
        void Entry(int at, ushort tag, ushort type, uint count, uint value)
        {
            Span<byte> span = d.AsSpan(8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], tag);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], value);
        }

        Span<byte> dir = d.AsSpan(8);

        BinaryPrimitives.WriteUInt16LittleEndian(dir, (ushort)ifd0Entries);
        int e = 2;
        Entry(e, 0x010F, 2, (uint)make.Length, (uint)makeAt);
        Entry(e += 12, 0x0110, 2, (uint)model.Length, (uint)modelAt);
        if (withMakerNote)
        {
            Entry(e += 12, 0x8769, 4, 1, (uint)exifAt);
        }

        if (withGps)
        {
            Entry(e += 12, 0x8825, 4, 1, (uint)gpsAt);
        }

        make.CopyTo(dir[makeAt..]);
        model.CopyTo(dir[modelAt..]);
        if (withMakerNote)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dir[exifAt..], 1);
            Entry(exifAt + 2, 0x927C, 7, (uint)note.Length, (uint)noteAt);
            note.CopyTo(dir[noteAt..]);
        }

        if (withGps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dir[gpsAt..], 2);
            Entry(gpsAt + 2, 0x0001, 2, 2, 0x4E); // "N"
            Entry(gpsAt + 14, 0x0002, 5, 3, (uint)latAt);
            for (int i = 0; i < 3; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(dir[(latAt + (i * 8))..], (uint)(47 + (i * 7)));
                BinaryPrimitives.WriteUInt32LittleEndian(dir[(latAt + (i * 8) + 4)..], 1);
            }
        }

        return d;
    }

    private static byte[] Avi(byte[]? strd, byte[]? info = null, byte[]? extraTop = null)
    {
        AviBuilder b = new() { InfoList = info };
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "FMP4", 1, 25, 0, AviBuilder.Bitmap(320, 240, "FMP4")));
        for (int i = 0; i < 6; i++)
        {
            byte[] frame = new byte[100 + i];
            new Random(i).NextBytes(frame);
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, frame, i % 3 == 0));
        }

        byte[] file = b.Build();
        if (strd is not null)
        {
            // Insert the chunk at the end of the first 'strl' list and grow the lists around it.
            byte[] chunk = AviBuilder.Chunk("strd", strd);
            int strl = file.AsSpan().IndexOf("strl"u8) - 8;
            int strlEnd = strl + 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(strl + 4));
            file = [.. file.AsSpan(0, strlEnd), .. chunk, .. file.AsSpan(strlEnd)];
            Grow(file, strl, chunk.Length);
            Grow(file, file.AsSpan().IndexOf("hdrl"u8) - 8, chunk.Length);
            Grow(file, 0, chunk.Length);
        }

        if (extraTop is not null)
        {
            file = [.. file, .. extraTop];
            Grow(file, 0, extraTop.Length);
        }

        return file;
    }

    private static void Grow(byte[] file, int at, int by) =>
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)) + (uint)by);

    private static byte[] Strip(byte[] file, out StripReport report, StripOptions? options = null)
    {
        StripPlan plan = PrivacyStrip.Plan(new MemoryMediaSource(file), options);
        report = plan.Report;
        return WriterTestSupport.Apply(plan.Plan, file);
    }

    private static List<byte[]> Frames(byte[] file)
    {
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        List<byte[]> frames = [];
        while (d.TryReadPacket(out PacketView p))
        {
            frames.Add(p.Data.ToArray());
        }

        return frames;
    }

    private static bool Has(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    [Fact]
    public void FujifilmStrdSerialAndPositionAreRemovedWithoutMovingAnyByte()
    {
        byte[] file = Avi(Strd());
        Assert.True(Has(file, Serial));
        byte[] output = Strip(file, out StripReport report);
        Assert.Equal(file.Length, output.Length);
        Assert.False(Has(output, Serial));
        Assert.Contains(report.Entries, e => e.System == "riff:strd" && e.Name == "EXIF MakerNote" && e.Category == StripCategories.DeviceIds);
        Assert.Contains(report.Entries, e => e.System == "riff:strd" && e.Name == "EXIF GPS IFD" && e.Category == StripCategories.Location);
        Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.Name)));

        // Only bytes inside the strd chunk differ; the frames are equal and the file still opens.
        int strd = file.AsSpan().IndexOf("strd"u8);
        int strdEnd = strd + 8 + Strd().Length;
        for (int i = 0; i < file.Length; i++)
        {
            if (file[i] != output[i])
            {
                Assert.InRange(i, strd + 8, strdEnd);
            }
        }

        Assert.Equal(Frames(file).Select(Convert.ToHexString), Frames(output).Select(Convert.ToHexString));

        // The maker and model stay readable; the position and serial no longer parse as entries.
        using MediaFile after = MediaFile.Open(new MemoryMediaSource(output));
        Assert.Equal("EXAMPLEMAKER", after.Metadata.Facts.Make.Value);
        Assert.Equal("Model X1", after.Metadata.Facts.Model.Value);
        byte[] chunk = output.AsSpan(strd + 8, Strd().Length).ToArray();
        ExifBlock block = ExifBlock.ParseHeaderless(chunk.AsMemory(8), -1, new DefectLog(MediaLimits.Default));
        Assert.DoesNotContain(block.Entries, e => e.Tag == 0x927C || e.Ifd == "GPS");
    }

    [Fact]
    public void LocationOnlyLeavesTheSerialInPlaceButTheDeviceCategoryTakesJustThat()
    {
        byte[] file = Avi(Strd());
        byte[] location = Strip(file, out _, StripOptions.LocationOnly);
        Assert.True(Has(location, Serial));
        byte[] ids = Strip(file, out StripReport report, StripOptions.DeviceIdsOnly);
        Assert.False(Has(ids, Serial));
        Assert.DoesNotContain(report.Entries, e => e.Category == StripCategories.Location);
    }

    [Fact]
    public void AStrdOfUnknownLayoutIsReportedNotRemoved()
    {
        byte[] vendor = new byte[64];
        "VNDR"u8.CopyTo(vendor);
        Encoding.ASCII.GetBytes(Serial).CopyTo(vendor, 8);
        byte[] file = Avi(vendor);
        byte[] output = Strip(file, out StripReport report);
        Assert.Equal(file, output);
        StripLeftover left = Assert.Single(report.NotRemoved, l => l.System == "riff:strd");
        Assert.Contains("'strd'", left.Reason);
        Assert.False(report.IsComplete);
        Assert.Equal(file.AsSpan().IndexOf("strd"u8), left.Range.Offset);
    }

    [Fact]
    public void AllZeroStrdAndPaddingAreNotReported()
    {
        byte[] file = Avi(new byte[32], extraTop: AviBuilder.Chunk("JUNK", new byte[40]));
        Strip(file, out StripReport report);
        Assert.True(report.IsComplete, string.Join("; ", report.NotRemoved.Select(l => l.Name)));
    }

    [Fact]
    public void VendorChunksAndATrailerAreNamedAndPaddingWithBytesIsZeroed()
    {
        byte[] top = AviBuilder.Concat(
            AviBuilder.Chunk("JUNK", Encoding.ASCII.GetBytes(Serial)),
            AviBuilder.List("LIST", "ncdt", AviBuilder.Chunk("nctg", new byte[12])),
            AviBuilder.Chunk("Xtra", new byte[6]));
        byte[] file = Avi(null, extraTop: top);
        byte[] output = Strip(file, out StripReport report);
        Assert.Equal(file.Length, output.Length);
        Assert.True(Has(file, Serial));
        Assert.False(Has(output, Serial));
        Assert.Contains(report.Entries, e => e.System == "riff" && e.Name == "JUNK" && e.Action == StripAction.Zeroed);
        string[] names = [.. report.NotRemoved.Select(l => l.Name)];
        Assert.DoesNotContain("JUNK", names);
        Assert.Contains("LIST ncdt", names);
        Assert.Contains("Xtra", names);

        byte[] withTrailer = [.. Avi(null), .. Encoding.ASCII.GetBytes("TRAILER-" + Serial)];
        Strip(withTrailer, out StripReport trailer);
        Assert.Contains(trailer.NotRemoved, l => l.Name == "trailer");
    }

    [Fact]
    public void ATagRewriteAheadOfThePatchedChunkDoesNotMisplaceThePatch()
    {
        // The base plan inserts 10 bytes up front, so source offset 20 lands at output offset 25 and the later bytes shift by 5.
        PieceList pieces = new();
        pieces.AddData(new byte[10]);
        pieces.AddCopy(5, 95);
        EditPlan basePlan = pieces.ToPlan(100, 0, 0, 0);
        PatchSet patches = new();
        patches.Zero(20, 4);
        patches.Write(40, [9, 9]);
        EditPlan plan = patches.Overlay(basePlan, out List<ByteRange> uncovered);
        Assert.Empty(uncovered);
        byte[] source = new byte[100];
        Array.Fill(source, (byte)7);
        byte[] output = WriterTestSupport.Apply(plan, source);
        Assert.Equal(105, output.Length);
        for (int i = 0; i < output.Length; i++)
        {
            byte expected = i < 10 ? (byte)0 : (byte)7;
            if (i is >= 25 and < 29)
            {
                expected = 0;
            }

            if (i is 45 or 46)
            {
                expected = 9;
            }

            Assert.Equal(expected, output[i]);
        }

        // A patch inside a range the base plan dropped is reported as not applied.
        PatchSet dropped = new();
        dropped.Zero(2, 2);
        dropped.Overlay(basePlan, out List<ByteRange> missed);
        Assert.Single(missed);
    }
}

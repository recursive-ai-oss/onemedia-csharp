using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tests.Avi;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests.Facts;

/// <summary>Maker, model and software from the atoms and chunks that compact cameras and DSLRs write themselves. The layouts are built here from the field positions the readers document.</summary>
public class VendorCameraTests
{
    private static byte[] Text(string s, int size)
    {
        byte[] b = new byte[size];
        Encoding.Latin1.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    // One Nikon tag record: tag, TIFF-style type 2 (text), count, the text; values under four bytes still take four.
    private static byte[] NikonText(uint tag, string s, int count)
    {
        byte[] r = new byte[8 + Math.Max(4, count)];
        BinaryPrimitives.WriteUInt32BigEndian(r, tag);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)count);
        Encoding.Latin1.GetBytes(s).CopyTo(r, 8);
        return r;
    }

    private static byte[] NikonShort(uint tag, ushort value)
    {
        byte[] r = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(r, tag);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), value);
        return r;
    }

    private static byte[] NcdtAtom()
    {
        byte[] nctg = Box("NCTG", NikonText(1, "NIKON", 6), NikonText(2, "Z9000", 6), NikonText(3, "Z9000 Ver.1.0", 14), NikonShort(0x21, 2));
        return Box("NCDT", Box("NCHD", new byte[10]), nctg, Box("NCTH", new byte[5000]));
    }

    [Fact]
    public void NikonNcdtGivesMakeModelAndSoftware()
    {
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", NcdtAtom())), m =>
        {
            Assert.Equal("NIKON", m.Facts.Make.Value);
            Assert.Equal("Z9000", m.Facts.Model.Value);
            Assert.Equal("Z9000 Ver.1.0", m.Facts.Software.Value);
            Assert.Contains("NCDT", m.Facts.Model.Best!.Source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NikonTagRecordsStopAtUnknownTypesAndShortData()
    {
        byte[] bad = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(4), 99);
        Assert.True(VendorCameraRecords.ReadNikonTagRecords(bad).IsEmpty);
        byte[] cut = NikonText(2, "Z9000", 6)[..10];
        Assert.True(VendorCameraRecords.ReadNikonTagRecords(cut).IsEmpty);
        Assert.True(VendorCameraRecords.ReadNikonNcdt([0, 0, 0, 12, 0x4E, 0x43, 0x54, 0x47]).IsEmpty);
    }

    [Fact]
    public void NikonCoolpixTagsAtomGivesIdentity()
    {
        byte[] tags = new byte[233];
        Text("NIKON DIGITAL CAMERA", 24).CopyTo(tags, 0);
        Text("L99    ", 14).CopyTo(tags, 24);
        Text("COOLPIX L99 V1.0", 32).CopyTo(tags, 175);
        tags[175 + 16] = 1; // a binary field follows the software text directly, with no NUL
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("TAGS", tags))), m =>
        {
            Assert.Equal("NIKON DIGITAL CAMERA", m.Facts.Make.Value);
            Assert.Equal("L99", m.Facts.Model.Value);
            Assert.Equal("COOLPIX L99 V1.0", m.Facts.Software.Value);
        });
    }

    [Fact]
    public void OlympusTagsAtomGivesMakeAndModelFromTheTypeCode()
    {
        byte[] prms = new byte[8 + 300];
        Text("OLYM", 4).CopyTo(prms, 8);
        Text("Digital Camera", 14).CopyTo(prms, 12);
        Text("OLYMPUS DIGITAL CAMERA", 26).CopyTo(prms, 8 + 18);
        Text("D4586", 8).CopyTo(prms, 8 + 44);
        byte[] header = new byte[12];
        byte[] tags = Cat(header, Box("OLYM", Box("prms", prms[8..])));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("TAGS", tags))), m =>
        {
            Assert.Equal("OLYMPUS DIGITAL CAMERA", m.Facts.Make.Value);
            Assert.Equal("TG-4", m.Facts.Model.Value);
        });
        Assert.Equal("E-M5", OlympusCameraTypes.Find("S0036"));
        Assert.Null(OlympusCameraTypes.Find("Q9999"));
    }

    [Fact]
    public void OlympusUnlistedCodeIsKeptAsItStands()
    {
        byte[] prms = new byte[300];
        Text("OLYMPUS DIGITAL CAMERA", 26).CopyTo(prms, 18);
        Text("Q9999", 8).CopyTo(prms, 44);
        VendorCameraRecords.Identity id = VendorCameraRecords.ReadOlympusTags(Cat(new byte[12], Box("OLYM", Box("prms", prms))));
        Assert.Equal("Q9999", id.Model);
    }

    [Fact]
    public void PanasonicPanaAtomGivesMakeAndModel()
    {
        byte[] pana = new byte[200];
        Text("DC-X1", 16).CopyTo(pana, 12);
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("PANA", pana))), m =>
        {
            Assert.Equal("Panasonic", m.Facts.Make.Value);
            Assert.Equal("DC-X1", m.Facts.Model.Value);
        });
        Assert.True(VendorCameraRecords.ReadPanasonicPana(new byte[200]).IsEmpty);
    }

    [Fact]
    public void CanonCnmnAndDjiModelAtomsGiveTheModel()
    {
        byte[] djiModel = Cat(Text("FC0000", 24)); // DJI writes bare text without the QuickTime record header
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("CNMN", Text("Canon EOS Q1", 64)))), m => Assert.Equal("Canon EOS Q1", m.Facts.Model.Value));
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("\u00A9mdl", djiModel))), m => Assert.Equal("FC0000", m.Facts.Model.Value));
    }

    [Fact]
    public void BinaryGarbageInVendorAtomsGivesNoFacts()
    {
        byte[] noise = new byte[400];
        new Random(5).NextBytes(noise);
        TagTestHelpers.WithIso(TagTestHelpers.IsoWith(Box("udta", Box("NCDT", noise), Box("TAGS", noise), Box("PANA", noise), Box("CNMN", noise))), m =>
        {
            Assert.False(m.Facts.Make.HasValue && m.Facts.Make.Value.Any(char.IsControl));
            Assert.False(m.Facts.Model.HasValue && m.Facts.Model.Value.Any(char.IsControl));
        });
    }

    [Fact]
    public void NikonAviChunkGivesMakeModelAndSoftware()
    {
        static byte[] Rec(ushort tag, string s)
        {
            byte[] r = new byte[4 + s.Length + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), (ushort)(s.Length + 1));
            Encoding.Latin1.GetBytes(s).CopyTo(r, 4);
            return r;
        }

        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 30, 0, AviBuilder.Bitmap(160, 120, "MJPG")));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, new byte[16]));
        byte[] nctg = AviBuilder.Chunk("nctg", Cat(Rec(3, "NIKON CORPORATION"), Rec(4, "NIKON D0"), Rec(5, "A1.00\0 B1.00")));
        b.InfoList = AviBuilder.List("LIST", "ncdt", AviBuilder.Chunk("ncvr", new byte[18]), nctg);
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal("NIKON CORPORATION", m.Facts.Make.Value);
        Assert.Equal("NIKON D0", m.Facts.Model.Value);
        Assert.Equal("A1.00 B1.00", m.Facts.Software.Value);
    }
}

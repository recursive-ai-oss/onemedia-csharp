using System.Buffers.Binary;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tests.AudioStreams;
using static RecursiveAI.OneMedia.Tests.AsfBuilder;

namespace RecursiveAI.OneMedia.Tests;

// What spans formats: a second read of an opened file starts over in every reader, and an ID3v2 tag embedded in another container is read.
public class CrossFormatTests
{
    private static string DataPath(string dir, string name) => Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    private static List<(int Track, long Pts, byte[] Data)> ReadEverything(MediaFile media)
    {
        List<(int, long, byte[])> all = [];
        foreach (MediaTrack t in media.Tracks)
        {
            foreach (OwnedPacket p in media.Packets(t))
            {
                using (p)
                {
                    all.Add((t.Index, p.Info.Pts.Ticks, p.Data.ToArray()));
                }
            }
        }

        return all;
    }

    [Theory]
    [InlineData("asf", "wmav2-tagged.wma")]
    [InlineData("asf", "wmv2-wmav2.wmv")]
    [InlineData("mpeg", "h264-aac.ts")]
    [InlineData("mpeg", "h264-aac.m2ts")]
    [InlineData("mpeg", "mpeg2-mp2.vob")]
    [InlineData("mpeg", "mpeg1-mp2.mpg")]
    [InlineData("audio-es", "plain.mp3")]
    [InlineData("audio-es", "lc.aac")]
    [InlineData("audio-es", "ac3.ac3")]
    [InlineData("audio-es", "nb.amr")]
    public void ASecondReadOfAFileStartsOver(string dir, string file)
    {
        using MediaFile media = Media.Open(DataPath(dir, file));
        List<(int Track, long Pts, byte[] Data)> first = ReadEverything(media);
        Assert.NotEmpty(first);
        List<(int Track, long Pts, byte[] Data)> second = ReadEverything(media);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Track, second[i].Track);
            Assert.Equal(first[i].Pts, second[i].Pts);
            Assert.True(first[i].Data.AsSpan().SequenceEqual(second[i].Data), $"packet {i} differs");
        }
    }

    private static byte[] Id3WithTitleAndArtist() => AudioStreamTestData.Id3Bytes(4, AudioStreamTestData.Concat(
        AudioStreamTestData.TextFrame(4, "TIT2", "Deposition, day 2"),
        AudioStreamTestData.TextFrame(4, "TPE1", "A. Speaker")));

    [Fact]
    public void AnId3AttributeInAsfIsReadAsId3Frames()
    {
        AsfBuilder b = new();
        b.AddFileProperties().AddAudioStream(1).AddExtendedContentDescription(new Attr("ID3", 1, Id3WithTitleAndArtist()));
        b.AddPacket(true, new Payload(1, true, 1, 0, 8, 0, new byte[8]));
        using MediaFile media = Media.Open(b.Build());

        Assert.Equal("Deposition, day 2", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
        Assert.Equal("A. Speaker", media.Native[TagSystems.Id3v2, "TPE1"]?.Text);
        Assert.NotNull(media.Native[TagSystems.Asf, "ID3"]); // the attribute itself stays
        Assert.Equal("Deposition, day 2", media.Tags.Title);
        Assert.Equal("A. Speaker", media.Tags.Artist);
    }

    [Fact]
    public void AnAsfAttributeNamedId3ThatIsNotATagIsLeftAlone()
    {
        AsfBuilder b = new();
        b.AddFileProperties().AddAudioStream(1).AddExtendedContentDescription(new Attr("ID3", 1, new byte[40]));
        b.AddPacket(true, new Payload(1, true, 1, 0, 8, 0, new byte[8]));
        using MediaFile media = Media.Open(b.Build());
        Assert.NotNull(media.Native[TagSystems.Asf, "ID3"]);
        Assert.Null(media.Native[TagSystems.Id3v2, "TIT2"]);
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] body = AudioStreamTestData.Concat(parts);
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(body.Length + 8));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        return AudioStreamTestData.Concat(header, body);
    }

    [Fact]
    public void AnId32BoxInAnMp4MetaBoxIsReadAsId3Frames()
    {
        byte[] tag = Id3WithTitleAndArtist();
        // ISO meta box: full box, handler 'ID32', then ID32 boxes: full box, packed language ('und'), the ID3v2 tag.
        byte[] hdlr = Box("hdlr", new byte[8], "ID32"u8.ToArray(), new byte[12], [0]);
        byte[] id32 = Box("ID32", new byte[4], [0x55, 0xC4], tag);
        byte[] meta = Box("meta", new byte[4], hdlr, id32);
        byte[] file = AudioStreamTestData.Concat(File.ReadAllBytes(DataPath("audio", "aac.m4a")), meta);
        using MediaFile media = Media.Open(file);

        Assert.Equal("Deposition, day 2", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
        Assert.Equal("Deposition, day 2", media.Tags.Title);
        Assert.Equal("A. Speaker", media.Tags.Artist);
    }
}

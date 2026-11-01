using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tests.Avi;
using RecursiveAI.OneMedia.Tests.AudioStreams;

namespace RecursiveAI.OneMedia.Tests;

// An ID3v2 tag that rides in another container: WAVE and AIFF chunks, and the bytes in front of FLAC and Ogg. The container keeps the raw block;
// the frames are read as id3v2 native tags and feed the common view. These tests also check that files led by an ID3 tag open as their own format.
public class EmbeddedId3Tests
{
    private static byte[] Tag(string title, string artist, string? composer = null) => AudioStreamTestData.Id3Bytes(4, AudioStreamTestData.Concat(
        AudioStreamTestData.TextFrame(4, "TIT2", title),
        AudioStreamTestData.TextFrame(4, "TPE1", artist),
        composer is null ? [] : AudioStreamTestData.TextFrame(4, "TCOM", composer)));

    private static byte[] Le32(int v) { byte[] t = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); return t; }

    private static byte[] Be32(int v) { byte[] t = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(t, v); return t; }

    private static byte[] RiffChunk(string id, byte[] payload) => AudioStreamTestData.Concat(Encoding.ASCII.GetBytes(id), Le32(payload.Length), payload, payload.Length % 2 == 1 ? new byte[1] : []);

    private static byte[] IffChunk(string id, byte[] payload) => AudioStreamTestData.Concat(Encoding.ASCII.GetBytes(id), Be32(payload.Length), payload, payload.Length % 2 == 1 ? new byte[1] : []);

    private static byte[] Wav(params byte[][] extraChunks)
    {
        byte[] fmt = [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0]; // PCM, mono, 8000 Hz, 16 bits
        byte[] body = AudioStreamTestData.Concat("WAVE"u8.ToArray(), RiffChunk("fmt ", fmt), RiffChunk("data", new byte[160]), AudioStreamTestData.Concat(extraChunks));
        return AudioStreamTestData.Concat("RIFF"u8.ToArray(), Le32(body.Length), body);
    }

    private static byte[] Aiff(params byte[][] extraChunks)
    {
        byte[] rate = [0x40, 0x0B, 0xFA, 0, 0, 0, 0, 0, 0, 0]; // 8000 Hz as an 80-bit extended number
        byte[] comm = AudioStreamTestData.Concat([0, 1], Be32(80), [0, 16], rate);
        byte[] ssnd = AudioStreamTestData.Concat(Be32(0), Be32(0), new byte[160]);
        byte[] body = AudioStreamTestData.Concat("AIFF"u8.ToArray(), IffChunk("COMM", comm), IffChunk("SSND", ssnd), AudioStreamTestData.Concat(extraChunks));
        return AudioStreamTestData.Concat("FORM"u8.ToArray(), Be32(body.Length), body);
    }

    private static MediaFile Open(byte[] data) => MediaFile.Open(new MemoryMediaSource(data));

    private static byte[] Fixture(string dir, string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", dir, name));

    // AVI and WAVE share one INFO reader and one tag system, so the same INFO list reads the same in both and feeds the same common view.
    [Fact]
    public void AviInfoAndIditShareTheRiffInfoSystemWithWave()
    {
        byte[] info = AviBuilder.List("LIST", "INFO", AviBuilder.Chunk("INAM", Encoding.ASCII.GetBytes("Deposition\0")), AviBuilder.Chunk("IART", Encoding.ASCII.GetBytes("A. Speaker\0")));
        AviBuilder b = new()
        {
            InfoList = info,
            Idit = AviBuilder.Chunk("IDIT", Encoding.ASCII.GetBytes("Tue Mar 08 21:11:35 2005\n\0")),
        };
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 25, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, new byte[16]));
        using MediaFile avi = Open(b.Build());
        using MediaFile wav = Open(Wav(info));
        Assert.Equal(MediaFormatId.Avi, avi.Format);
        Assert.Equal(MediaFormatId.Wav, wav.Format);
        foreach (MediaFile f in new[] { avi, wav })
        {
            Assert.Equal("Deposition", f.Native[TagSystems.RiffInfo, "INAM"]?.Text);
            Assert.Equal("Deposition", f.Tags.Title);
            Assert.Equal("A. Speaker", f.Tags.Artist);
        }

        Assert.Equal("Tue Mar 08 21:11:35 2005", avi.Native[TagSystems.RiffInfo, "IDIT"]?.Text?.Trim());
        Assert.Equal(new DateTime(2005, 3, 8, 21, 11, 35), avi.CaptureTime.Candidates[0].Value.Value);
    }

    [Theory]
    [InlineData("id3 ")]
    [InlineData("ID3 ")]
    public void AWaveId3ChunkIsReadAsId3Frames(string chunkId)
    {
        using MediaFile media = Open(Wav(RiffChunk(chunkId, Tag("Deposition, day 2", "A. Speaker"))));
        Assert.Equal(MediaFormatId.Wav, media.Format);
        Assert.Equal("Deposition, day 2", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
        Assert.Equal("Deposition, day 2", media.Tags.Title);
        Assert.Equal("A. Speaker", media.Tags.Artist);
        Assert.Contains(media.Metadata.NativeTags, t => t.System == TagSystems.Id3Chunk); // the raw chunk stays
    }

    [Fact]
    public void AnAiffId3ChunkIsReadAsId3Frames()
    {
        using MediaFile media = Open(Aiff(IffChunk("ID3 ", Tag("Interview", "B. Witness"))));
        Assert.Equal(MediaFormatId.Aiff, media.Format);
        Assert.Equal("Interview", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
        Assert.Equal("Interview", media.Tags.Title);
        Assert.Equal("B. Witness", media.Tags.Artist);
        Assert.Contains(media.Metadata.NativeTags, t => t.System == TagSystems.Id3Chunk);
    }

    [Fact]
    public void FramesInAWaveId3ChunkPointIntoTheFile()
    {
        byte[] file = Wav(RiffChunk("id3 ", Tag("Ranged", "C")));
        using MediaFile media = Open(file);
        NativeTag title = media.Metadata.NativeTags.Single(t => t.System == TagSystems.Id3v2 && t.Key == "TIT2");
        Assert.Equal("Ranged", title.Text);
        Assert.True(title.Range.Offset > file.AsSpan().IndexOf("id3 "u8));
        Assert.True(title.Range.End <= file.Length);
    }

    [Fact]
    public void WaveInfoAndId3AgreeOnWhoWins()
    {
        // LIST INFO text is in a code page; the ID3 frames are Unicode and carry more fields, so ID3 is tried first.
        byte[] info = RiffChunk("LIST", AudioStreamTestData.Concat("INFO"u8.ToArray(), RiffChunk("INAM", Encoding.ASCII.GetBytes("From INFO\0"))));
        using MediaFile media = Open(Wav(info, RiffChunk("id3 ", Tag("From ID3", "A"))));
        Assert.Equal("From ID3", media.Tags.Title);
        Assert.Equal("From INFO", media.Native[TagSystems.RiffInfo, "INAM"]?.Text);
    }

    [Fact]
    public void AChunkThatIsNotAnId3TagIsReportedAndLeftRaw()
    {
        using MediaFile media = Open(Wav(RiffChunk("id3 ", new byte[40])));
        Assert.Null(media.Native[TagSystems.Id3v2, "TIT2"]);
        Assert.Contains(media.Metadata.NativeTags, t => t.System == TagSystems.Id3Chunk);
        Assert.Contains(media.Defects, d => d.Code == "tags.id3-chunk-not-id3");
    }

    [Fact]
    public void ATagLongerThanItsChunkIsNotReadIntoTheNextChunk()
    {
        byte[] tag = Tag("Too long", "A");
        byte[] cut = tag[..24]; // the header says 'tag.Length - 10' body bytes; the chunk holds fewer
        using MediaFile media = Open(Wav(RiffChunk("id3 ", cut), RiffChunk("LIST", AudioStreamTestData.Concat("INFO"u8.ToArray(), RiffChunk("INAM", Encoding.ASCII.GetBytes("Next\0"))))));
        Assert.Null(media.Native[TagSystems.Id3v2, "TIT2"]);
        Assert.Contains(media.Defects, d => d.Code == "tags.id3-chunk-overrun");
        Assert.Equal("Next", media.Tags.Title);
    }

    [Theory]
    [InlineData("tags.flac", "flac")]
    [InlineData("tags.ogg", "ogg")]
    [InlineData("tags.opus", "ogg")]
    public void AnId3PrefixInFrontOfFlacOrOggIsReadAndTheStreamsOwnCommentsWin(string name, string formatName)
    {
        byte[] stream = Fixture("xiph-flv", name);
        byte[] data = AudioStreamTestData.Concat(Tag("From ID3", "ID3 Artist", "ID3 Composer"), stream);
        using MediaFile media = Open(data);
        Assert.Equal(formatName, media.Format.Name);
        Assert.Equal("From ID3", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
        Assert.Equal("Tagged Title", media.Tags.Title); // the Vorbis comment, not the ID3 frame
        Assert.Equal("ID3 Composer", media.Tags.Composer); // a field only the ID3 tag has
        Assert.Contains(media.Metadata.NativeTags, t => t.System == RawId3Prefix.SystemName);
    }

    [Theory]
    [InlineData("tags.flac", "flac")]
    [InlineData("tags.ogg", "ogg")]
    public void AnId3TagLongerThanTheSniffedHeadStillOpensAsItsOwnFormat(string name, string formatName)
    {
        // 6000 bytes of padding keep the audio signature far past anything a sniffer is given.
        byte[] data = AudioStreamTestData.Concat(AudioStreamTestData.Id3Bytes(4, AudioStreamTestData.TextFrame(4, "TIT2", "Padded"), padding: 6000), Fixture("xiph-flv", name));
        using MediaFile media = Open(data);
        Assert.Equal(formatName, media.Format.Name);
        Assert.NotEmpty(media.Tracks);
        Assert.Equal("Padded", media.Native[TagSystems.Id3v2, "TIT2"]?.Text);
    }

    [Fact]
    public void AnMp3WithAnId3TagStillOpensAsMpegAudio()
    {
        using MediaFile media = Media.Open(Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", "tagged", "id3v24-basic.mp3"));
        Assert.Equal("mpegaudio", media.Format.Name);
        Assert.NotNull(media.Native[TagSystems.Id3v2, "TIT2"]);
    }
}

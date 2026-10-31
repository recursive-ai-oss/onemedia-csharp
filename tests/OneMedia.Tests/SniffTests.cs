using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Sniffing;

namespace RecursiveAI.OneMedia.Tests;

public class SniffTests
{
    private static MediaFormatRegistry AllFormats()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffSniffer());
        registry.Add(new EbmlSniffer());
        registry.Add(new WavSniffer());
        registry.Add(new AviSniffer());
        registry.Add(new AsfSniffer());
        registry.Add(new MpegTsSniffer());
        registry.Add(new MpegPsSniffer());
        registry.Add(new FlvSniffer());
        registry.Add(new OggSniffer());
        registry.Add(new FlacSniffer());
        registry.Add(new AiffSniffer());
        registry.Add(new CafSniffer());
        registry.Add(new Id3Sniffer());
        registry.Add(new MpegAudioSniffer());
        registry.Add(new AdtsSniffer());
        registry.Add(new AmrSniffer());
        registry.Add(new IvfSniffer());
        return registry;
    }

    private static byte[] Box(string type, params byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ebml(string docType)
    {
        byte[] doc = Encoding.ASCII.GetBytes(docType);
        // EBMLVersion 1, then DocType, inside an EBML header element.
        byte[] body = Concat([0x42, 0x86, 0x81, 0x01], [0x42, 0x82, (byte)(0x80 | doc.Length)], doc);
        return Concat([0x1A, 0x45, 0xDF, 0xA3, (byte)(0x80 | body.Length)], body);
    }

    private static byte[] TransportStream(int packets, int spacing, int offset)
    {
        byte[] data = new byte[offset + (packets * spacing)];
        for (int i = 0; i < packets; i++)
        {
            data[offset + (i * spacing)] = 0x47;
        }

        return data;
    }

    [Theory]
    [InlineData("isom")]
    [InlineData("qt  ")]
    [InlineData("3gp4")]
    public void RecognisesIsoBmffByFtypAndReportsTheBrand(string brand)
    {
        byte[] file = Concat(Box("ftyp", [.. Encoding.ASCII.GetBytes(brand), 0, 0, 2, 0]), Box("free"), Box("mdat", 1, 2, 3));
        Assert.True(AllFormats().TrySniff(file, out FormatMatch match));
        Assert.Equal(MediaFormatId.IsoBmff, match.Format.Id);
        Assert.Equal(SniffConfidence.Certain, match.Result.Confidence);
        Assert.Equal(brand, match.Result.Detail);
    }

    [Fact]
    public void RecognisesIsoBmffWithoutFtypAsLikely()
    {
        byte[] file = Concat(Box("wide"), Box("mdat", 1, 2, 3, 4));
        Assert.True(AllFormats().TrySniff(file, out FormatMatch match));
        Assert.Equal(MediaFormatId.IsoBmff, match.Format.Id);
        Assert.Equal(SniffConfidence.Likely, match.Result.Confidence);
    }

    [Fact]
    public void IsoBmffRejectsImpossibleSizesAndShortHeads()
    {
        IsoBmffSniffer sniffer = new();
        Assert.Equal(SniffConfidence.None, sniffer.Sniff("\0\0\0\x04ftypisom"u8).Confidence);
        Assert.Equal(SniffConfidence.None, sniffer.Sniff("\0\0\0\x0Aftypisom"u8).Confidence); // too small to hold a brand and version
        Assert.Equal(SniffConfidence.None, sniffer.Sniff("\0\0\0"u8).Confidence);
        Assert.Equal(SniffConfidence.None, sniffer.Sniff("\0\0\0\x20abcd"u8).Confidence);
    }

    [Fact]
    public void IsoBmffAcceptsLargeSizeAndUntilEndSize()
    {
        byte[] large = [0, 0, 0, 1, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2'];
        Assert.Equal(SniffConfidence.Certain, new IsoBmffSniffer().Sniff(large).Confidence);
        byte[] untilEnd = [0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 1, 2, 3, 4];
        Assert.Equal(SniffConfidence.Likely, new IsoBmffSniffer().Sniff(untilEnd).Confidence);
    }

    [Theory]
    [InlineData("webm")]
    [InlineData("matroska")]
    public void RecognisesEbmlAndReadsTheDocType(string docType)
    {
        byte[] file = Concat(Ebml(docType), [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.True(AllFormats().TrySniff(file, out FormatMatch match));
        Assert.Equal(MediaFormatId.Matroska, match.Format.Id);
        Assert.Equal(SniffConfidence.Certain, match.Result.Confidence);
        Assert.Equal(docType, match.Result.Detail);
    }

    [Fact]
    public void EbmlSniffingSurvivesTruncationAtEveryLength()
    {
        byte[] file = Ebml("webm");
        EbmlSniffer sniffer = new();
        for (int length = 0; length <= file.Length; length++)
        {
            SniffResult result = sniffer.Sniff(file.AsSpan(0, length));
            if (length < 4)
            {
                Assert.Equal(SniffConfidence.None, result.Confidence);
            }
            else
            {
                Assert.NotEqual(SniffConfidence.None, result.Confidence);
            }
        }

        Assert.Equal("webm", sniffer.Sniff(file).Detail);
    }

    [Fact]
    public void EbmlWithOversizedChildDoesNotOverrun()
    {
        byte[] bad = [0x1A, 0x45, 0xDF, 0xA3, 0x8F, 0x42, 0x82, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        SniffResult result = new EbmlSniffer().Sniff(bad);
        Assert.Equal(SniffConfidence.Certain, result.Confidence);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(188, 0, "188")]
    [InlineData(204, 0, "204")]
    [InlineData(192, 4, "192")]
    public void RecognisesTransportStreamsBySyncSpacing(int spacing, int offset, string detail)
    {
        byte[] data = TransportStream(8, spacing, offset);
        Assert.True(AllFormats().TrySniff(data, out FormatMatch match));
        Assert.Equal(MediaFormatId.MpegTs, match.Format.Id);
        Assert.Equal(detail, match.Result.Detail);
        Assert.Equal(SniffConfidence.Certain, match.Result.Confidence);
    }

    [Fact]
    public void TransportStreamNeedsMoreThanOneSyncByte()
    {
        MpegTsSniffer sniffer = new();
        Assert.Equal(SniffConfidence.None, sniffer.Sniff([0x47]).Confidence);
        Assert.Equal(SniffConfidence.None, sniffer.Sniff(new byte[300]).Confidence);
        byte[] broken = TransportStream(8, 188, 0);
        broken[188 * 3] = 0;
        Assert.Equal(SniffConfidence.None, sniffer.Sniff(broken).Confidence);
        Assert.Equal(SniffConfidence.Likely, sniffer.Sniff(TransportStream(3, 188, 0)).Confidence);
    }

    [Fact]
    public void RecognisesProgramStreams()
    {
        Assert.Equal("mpeg2", new MpegPsSniffer().Sniff([0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1]).Detail);
        Assert.Equal("mpeg1", new MpegPsSniffer().Sniff([0, 0, 1, 0xBA, 0x21, 0, 1, 0, 1, 0x80]).Detail);
        Assert.Equal(SniffConfidence.None, new MpegPsSniffer().Sniff([0, 0, 1, 0xBA, 0xFF]).Confidence);
        Assert.Equal(SniffConfidence.None, new MpegPsSniffer().Sniff([0, 0, 1, 0xB3, 0x44]).Confidence);
    }

    [Fact]
    public void RecognisesRiffForms()
    {
        MediaFormatRegistry registry = AllFormats();
        byte[] wav = Concat("RIFF"u8.ToArray(), [0x24, 0, 0, 0], "WAVEfmt "u8.ToArray());
        Assert.True(registry.TrySniff(wav, out FormatMatch w));
        Assert.Equal((MediaFormatId.Wav, "riff"), (w.Format.Id, w.Result.Detail));
        byte[] rf64 = Concat("RF64"u8.ToArray(), [0xFF, 0xFF, 0xFF, 0xFF], "WAVEds64"u8.ToArray());
        Assert.True(registry.TrySniff(rf64, out FormatMatch r));
        Assert.Equal((MediaFormatId.Wav, "rf64"), (r.Format.Id, r.Result.Detail));
        byte[] avi = Concat("RIFF"u8.ToArray(), [0x24, 0, 0, 0], "AVI LIST"u8.ToArray());
        Assert.True(registry.TrySniff(avi, out FormatMatch a));
        Assert.Equal(MediaFormatId.Avi, a.Format.Id);
        byte[] other = Concat("RIFF"u8.ToArray(), [0x24, 0, 0, 0], "WEBPVP8 "u8.ToArray());
        Assert.False(registry.TrySniff(other, out _));
    }

    [Fact]
    public void RecognisesWave64()
    {
        byte[] riff = [0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
        byte[] wave = [0x77, 0x61, 0x76, 0x65, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];
        byte[] file = Concat(riff, new byte[8], wave);
        SniffResult result = new WavSniffer().Sniff(file);
        Assert.Equal((SniffConfidence.Certain, "w64"), (result.Confidence, result.Detail));
    }

    [Theory]
    [InlineData("OggS\0", "ogg")]
    [InlineData("fLaC", "flac")]
    [InlineData("FLV\x01\x05", "flv")]
    [InlineData("caff\0\x01\0\0", "caf")]
    [InlineData("DKIF", "ivf")]
    [InlineData("#!AMR\n", "amr")]
    [InlineData("#!AMR-WB\n", "amr")]
    [InlineData("FORM\0\0\0\x10" + "AIFF", "aiff")]
    [InlineData("FORM\0\0\0\x10" + "AIFC", "aiff")]
    public void RecognisesSignatureFormats(string head, string expected)
    {
        Assert.True(AllFormats().TrySniff(Encoding.Latin1.GetBytes(head), out FormatMatch match));
        Assert.Equal(expected, match.Format.Id.Name);
        Assert.Equal(SniffConfidence.Certain, match.Result.Confidence);
    }

    [Fact]
    public void RecognisesAsfByHeaderGuid()
    {
        byte[] guid = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, 0x10, 0, 0, 0];
        Assert.True(AllFormats().TrySniff(guid, out FormatMatch match));
        Assert.Equal(MediaFormatId.Asf, match.Format.Id);
    }

    [Fact]
    public void RecognisesId3AndFrameSync()
    {
        MediaFormatRegistry registry = AllFormats();
        byte[] id3 = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0x02, 0x01, 0xFF, 0xFB];
        Assert.True(registry.TrySniff(id3, out FormatMatch tag));
        Assert.Equal((MediaFormatId.Id3, "2.4"), (tag.Format.Id, tag.Result.Detail));
        byte[] badSize = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0x80, 0, 0, 0];
        Assert.False(registry.TrySniff(badSize, out _));

        // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz.
        Assert.True(registry.TrySniff([0xFF, 0xFB, 0x90, 0x00], out FormatMatch mp3));
        Assert.Equal((MediaFormatId.MpegAudio, SniffConfidence.Possible, "layer3"), (mp3.Format.Id, mp3.Result.Confidence, mp3.Result.Detail));
        // Reserved bit-rate and sampling-rate indexes are rejected.
        Assert.False(registry.TrySniff([0xFF, 0xFB, 0xF0, 0x00], out _));
        Assert.False(registry.TrySniff([0xFF, 0xFB, 0x9C, 0x00], out _));
    }

    [Fact]
    public void AdtsNeedsConsistentFrames()
    {
        // Frame length 10; a second header follows at +10.
        byte[] header = [0xFF, 0xF1, 0x50, 0x80, 0x01, 0x40, 0xFC];
        byte[] frames = new byte[20];
        header.CopyTo(frames, 0);
        header.CopyTo(frames, 10);
        // aac_frame_length = 10 -> bits: byte3 low 2 bits = 0, byte4 = 1, byte5 high 3 bits = 2.
        frames[3] = 0x80;
        frames[4] = 0x01;
        frames[5] = 0x40;
        frames[13] = 0x80;
        frames[14] = 0x01;
        frames[15] = 0x40;
        Assert.Equal(SniffConfidence.Likely, new AdtsSniffer().Sniff(frames).Confidence);
        frames[10] = 0;
        Assert.Equal(SniffConfidence.None, new AdtsSniffer().Sniff(frames).Confidence);
        Assert.Equal(SniffConfidence.Possible, new AdtsSniffer().Sniff(frames.AsSpan(0, 8)).Confidence);
    }

    [Fact]
    public void ReadsOnlyBoundedHeadFromASource()
    {
        byte[] file = Concat(Box("ftyp", "isom"u8.ToArray().Concat(new byte[4]).ToArray()), new byte[100_000]);
        Counter counter = new();
        MemoryMediaSource inner = new(file);
        using ObservedMediaSource source = new(inner, inner, counter);
        Assert.True(AllFormats().TrySniff(source, out FormatMatch match));
        Assert.Equal(MediaFormatId.IsoBmff, match.Format.Id);
        Assert.Equal(1, counter.Calls);
        Assert.Equal(MediaFormatRegistry.SniffBytes, counter.Bytes);
    }

    [Fact]
    public async Task SniffsAsyncSourcesWithOneRangeRequest()
    {
        byte[] file = Concat(Box("ftyp", "isom"u8.ToArray().Concat(new byte[4]).ToArray()), new byte[10_000]);
        Counter counter = new();
        MemoryMediaSource inner = new(file);
        await using ObservedMediaSource source = new(null, inner, counter);
        FormatMatch? match = await AllFormats().SniffAsync(source);
        Assert.Equal(MediaFormatId.IsoBmff, match?.Format.Id);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void SniffersNeverThrowOnArbitraryBytes()
    {
        MediaFormatRegistry registry = AllFormats();
        Random random = new(12345);
        byte[] data = new byte[300];
        for (int round = 0; round < 2000; round++)
        {
            random.NextBytes(data);
            if (round % 3 == 0)
            {
                // Bias toward the interesting prefixes.
                data[0] = (byte)(round % 2 == 0 ? 0x47 : 0x1A);
            }

            for (int length = 0; length <= 40; length++)
            {
                registry.TrySniff(data.AsSpan(0, length), out _);
            }

            registry.TrySniff(data, out _);
        }
    }

    [Fact]
    public void OpenNeedsAFormatThatCanOpen()
    {
        MediaFormatRegistry registry = AllFormats();
        using MemoryMediaSource none = new(new byte[64]);
        Assert.Throws<UnsupportedMediaException>(() => registry.Open(none));
        using MemoryMediaSource mp4 = new(Box("ftyp", [.. "isom"u8, 0, 0, 0, 0]));
        UnsupportedMediaException ex = Assert.Throws<UnsupportedMediaException>(() => registry.Open(mp4));
        Assert.Contains("no reader", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TiesGoToTheFirstRegisteredAndConfidenceBeatsOrder()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new Fixed("a", SniffConfidence.Likely));
        registry.Add(new Fixed("b", SniffConfidence.Likely));
        registry.Add(new Fixed("c", SniffConfidence.Certain));
        registry.Add(new Fixed("d", SniffConfidence.Certain));
        Assert.True(registry.TrySniff(new byte[4], out FormatMatch match));
        Assert.Equal("c", match.Format.Id.Name);
        Assert.Throws<ArgumentNullException>(() => registry.Add(null!));
    }

    private sealed class Fixed(string name, SniffConfidence confidence) : IMediaFormatSniffer
    {
        public MediaFormatId Id => new(name);

        public string DisplayName => name;

        public SniffResult Sniff(ReadOnlySpan<byte> head) => new(confidence);
    }

    private sealed class Counter : IMediaReadObserver
    {
        public int Calls { get; private set; }

        public int Bytes { get; private set; }

        public void OnRead(long offset, int requested, int returned)
        {
            Calls++;
            Bytes += requested;
        }
    }
}

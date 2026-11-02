using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Teletext;
using Xunit;

namespace RecursiveAI.OneMedia.Tests.Subtitles;

/// <summary>
/// One subtitle view over everything a file carries: <see cref="MediaFile.Subtitles"/> lists container text and bitmap tracks, teletext pages and
/// embedded captions, and the bitmap sidecars read through <c>Subtitles.Read</c>.
/// </summary>
public sealed class SubtitleWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onemedia-subwire-" + Guid.NewGuid().ToString("N")[..8]);

    public SubtitleWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Data(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", .. parts]);

    [Fact]
    public void TextTracksAreListedWithKindAndCodec()
    {
        using MediaFile m = Media.Open(Data("text", "oracle", "ff_tx3g.mp4"));
        SubtitleTrack t = Assert.Single(m.Subtitles);
        Assert.Equal(SubtitleKind.Text, t.Kind);
        Assert.Equal(TextTrackFormat.Tx3g, t.Format);
        Assert.Equal("tx3g", t.Codec);
        Assert.NotEmpty(t.Cues);
        Assert.Equal(m.ContainerSubtitles.Count, m.Subtitles.Count);
    }

    [Fact]
    public void AClosedCaptionTrackListsItsChannelAndReadsTheSameCuesAsTheDecoder()
    {
        using MediaFile m = Media.Open(Data("captions", "c608.mov"));
        Assert.Empty(m.ContainerSubtitles);
        SubtitleTrack t = Assert.Single(m.Subtitles);
        Assert.Equal(SubtitleKind.Cea608, t.Kind);
        Assert.Equal("CC1", t.Label);
        Assert.Equal("cea608", t.Codec);
        Assert.Equal(["HELLO WORLD\nsecond line here", "Caf\u00e9 \u266a ni\u00f1o", "Italic and underline"], t.Cues.Select(c => c.Text));
    }

    [Fact]
    public void TeletextPagesAreListedPerPage()
    {
        byte[] ts = TeletextBuilder.TransportStream([(1 * TeletextBuilder.Clock, Page("Ciao")), (3 * TeletextBuilder.Clock, Page(null)), (4 * TeletextBuilder.Clock, Page("Dopo"))]);
        string path = Path.Combine(_dir, "ttx.ts");
        File.WriteAllBytes(path, ts);
        using MediaFile m = Media.Open(path);
        Assert.Empty(m.ContainerSubtitles);
        SubtitleTrack t = Assert.Single(m.Subtitles);
        Assert.Equal(SubtitleKind.Teletext, t.Kind);
        Assert.Equal("777", t.Label);
        Assert.Equal("ita", t.Language);
        Assert.Equal(["Ciao", "Dopo"], t.Cues.Select(c => c.Text));
        // The listing rewinds the file: packets still read after it.
        Assert.NotEmpty(m.Packets(m.Tracks[0]).Take(1).ToList());
    }

    private static byte[][] Page(string? text) => text is null
        ? TeletextBuilder.Page(7, 0x77, subtitle: true, erase: true, 0)
        : TeletextBuilder.Page(7, 0x77, subtitle: true, erase: true, 0, (20, TeletextBuilder.BoxedRow(text, 7)));

    [Fact]
    public void ABitmapTrackInMatroskaIsListedWithoutAScanAndDecodesToImages()
    {
        byte[] sup = File.ReadAllBytes(Data("bitmapsubs", "pgs.sup"));
        string path = Path.Combine(_dir, "pgs.mkv");
        File.WriteAllBytes(path, MatroskaWithPgs(sup));
        using MediaFile m = Media.Open(path);
        SubtitleTrack t = Assert.Single(m.ContainerSubtitles);
        Assert.Equal(SubtitleKind.Bitmap, t.Kind);
        Assert.Equal("S_HDMV/PGS", t.Codec);
        Assert.Equal("eng", t.Language);
        SubtitleDocument direct = BitmapSubtitles.ReadSup(sup);
        Assert.NotEmpty(direct.Cues);
        Assert.Equal(direct.Cues.Count, t.Cues.Count);
        Assert.Equal(direct.Cues[0].Images[0].Pixels, t.Cues[0].Images[0].Pixels);
        Assert.Equal(direct.Cues[0].Images[0].Width, t.Cues[0].Images[0].Width);
        Assert.Single(m.Subtitles);
    }

    [Fact]
    public void BitmapSidecarsReadThroughTheHappyPathAndTheRegistry()
    {
        SubtitleDocument sup = global::RecursiveAI.OneMedia.Subtitles.Read(Data("bitmapsubs", "pgs.sup"));
        Assert.Equal("pgs", sup.Format);
        Assert.NotEmpty(sup.Cues);
        Assert.NotEmpty(sup.Cues[0].Images);

        SubtitleDocument idx = global::RecursiveAI.OneMedia.Subtitles.Read(Data("bitmapsubs", "vobsub.idx"));
        Assert.Equal("vobsub", idx.Format);
        Assert.NotEmpty(idx.Cues);

        SubtitleDocument sub = global::RecursiveAI.OneMedia.Subtitles.Read(Data("bitmapsubs", "vobsub.sub"));
        Assert.Equal("vobsub", sub.Format);

        SubtitleFormat pgs = Assert.IsAssignableFrom<SubtitleFormat>(SubtitleFormats.Find(".sup"));
        Assert.Equal("pgs", pgs.Name);
        Assert.False(pgs.CanWrite);
        Assert.Equal("vobsub", SubtitleFormats.Find("idx")?.Name);
        Assert.Same(PgsSubtitleFormat.Instance, SubtitleFormats.Sniff(File.ReadAllBytes(Data("bitmapsubs", "pgs.sup"))));
        Assert.Throws<UnsupportedMediaException>(() => global::RecursiveAI.OneMedia.Subtitles.Write(sup, Path.Combine(_dir, "out.sup")));
    }

    [Fact]
    public void TextSidecarsStillSniffAsText()
    {
        Assert.Equal("srt", SubtitleFormats.Sniff(File.ReadAllBytes(Data("subtitles", "sample.srt")))?.Name);
        Assert.Equal("webvtt", global::RecursiveAI.OneMedia.Subtitles.Read(Data("subtitles", "sample.vtt")).Format);
    }

    // A Matroska file with one S_HDMV/PGS track whose blocks are the display sets of a .sup (segments without the PG, PTS and DTS header).
    private static byte[] MatroskaWithPgs(byte[] sup)
    {
        var blocks = new List<(long Ms, byte[] Data)>();
        var current = new List<byte>();
        long pts = 0;
        for (int pos = 0; pos + 13 <= sup.Length;)
        {
            int size = (sup[pos + 11] << 8) | sup[pos + 12];
            if (current.Count == 0)
            {
                pts = (long)(uint)((sup[pos + 2] << 24) | (sup[pos + 3] << 16) | (sup[pos + 4] << 8) | sup[pos + 5]) / 90;
            }

            current.AddRange(sup.AsSpan(pos + 10, 3 + size).ToArray());
            pos += 13 + size;
            if (sup[pos - 13 - size + 10] == 0x80)
            {
                blocks.Add((pts, [.. current]));
                current.Clear();
            }
        }

        byte[] header = El(0x1A45DFA3, [.. El(0x4282, "matroska"u8.ToArray()), .. El(0x4287, [4]), .. El(0x4285, [2])]);
        byte[] info = El(0x1549A966, El(0x2AD7B1, [0x0F, 0x42, 0x40]));
        byte[] track = El(0xAE, [.. El(0xD7, [1]), .. El(0x73C5, [1]), .. El(0x83, [0x11]), .. El(0x86, "S_HDMV/PGS"u8.ToArray()), .. El(0x22B59C, "eng"u8.ToArray())]);
        var cluster = new List<byte>(El(0xE7, [0]));
        foreach ((long ms, byte[] data) in blocks)
        {
            cluster.AddRange(El(0xA3, [0x81, (byte)(ms >> 8), (byte)ms, 0x80, .. data]));
        }

        return [.. header, .. El(0x18538067, [.. info, .. El(0x1654AE6B, track), .. El(0x1F43B675, [.. cluster])])];
    }

    private static byte[] El(uint id, byte[] payload)
    {
        var bytes = new List<byte>();
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            if ((id >> shift) != 0)
            {
                bytes.Add((byte)(id >> shift));
            }
        }

        // An eight-byte size (0x01 marker, 56 bits).
        bytes.Add(0x01);
        for (int shift = 48; shift >= 0; shift -= 8)
        {
            bytes.Add((byte)((long)payload.Length >> shift));
        }

        bytes.AddRange(payload);
        return [.. bytes];
    }

    private static byte[] El(uint id, string text) => El(id, System.Text.Encoding.ASCII.GetBytes(text));
}

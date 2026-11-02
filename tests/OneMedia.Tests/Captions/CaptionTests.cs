using RecursiveAI.OneMedia.Codecs.Captions;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Cea608;
using RecursiveAI.OneMedia.Captions.Cea708;
using RecursiveAI.OneMedia.Captions.Scc;

namespace RecursiveAI.OneMedia.Tests.Captions;

public sealed class CaptionTests
{
    private static MediaTime T(long frame) => new(frame * 1001, 30000);

    private static void Feed(Cea608Decoder d, long startFrame, IEnumerable<(byte, byte)> pairs)
    {
        long f = startFrame;
        foreach ((byte a, byte b) in pairs)
        {
            d.Push(T(f++), 0, a, b);
        }
    }

    [Fact]
    public void PopOnCaptionAppearsAtEndOfCaptionAndEndsAtErase()
    {
        var d = new Cea608Decoder();
        Cea608Row[] rows = [new Cea608Row(14, 1, "HELLO", []), new Cea608Row(15, 5, "world", [])];
        Feed(d, 0, Cea608Encoder.PopOn(rows));
        var pairs = Cea608Encoder.PopOn(rows).Count; // loading takes this many frames; the last two are EOC
        Feed(d, 100, Cea608Encoder.Erase());
        d.Finish(T(200));
        Cea608Caption c = Assert.Single(d.Captions);
        Assert.Equal(T(pairs - 2), c.Start);
        Assert.Equal(T(100), c.End);
        Assert.Equal(2, c.Rows.Count);
        Assert.Equal(14, c.Rows[0].Row);
        Assert.Equal("HELLO", c.Rows[0].Text);
        Assert.Equal((15, 5, "world"), (c.Rows[1].Row, c.Rows[1].Column, c.Rows[1].Text));
    }

    [Fact]
    public void EncoderAndDecoderAgreeOnAccentsStylesAndColumns()
    {
        Cea608Row[] rows =
        [
            new Cea608Row(3, 1, "Caf\u00e9 \u266a ni\u00f1o \u00dcber ~x", []),
            new Cea608Row(15, 10, "Yellow plain", [new Cea608Span(0, 6, Cea608Color.Yellow, false, false, false)]),
            new Cea608Row(8, 1, "ab cd ef", [new Cea608Span(3, 2, Cea608Color.White, true, false, false), new Cea608Span(6, 2, Cea608Color.White, false, true, false)]),
        ];
        var d = new Cea608Decoder();
        Feed(d, 0, Cea608Encoder.PopOn(rows, Cea608Channel.CC2));
        d.Finish(T(500));
        Cea608Caption c = Assert.Single(d.Captions);
        Assert.Equal(Cea608Channel.CC2, c.Channel);
        Assert.Equal(["Caf\u00e9 \u266a ni\u00f1o \u00dcber ~x", "ab cd ef", "Yellow plain"], c.Rows.Select(r => r.Text));
        Assert.Equal(10, c.Rows[2].Column);
        Assert.Equal(new Cea608Span(0, 6, Cea608Color.Yellow, false, false, false), Assert.Single(c.Rows[2].Spans));
        // The mid-row code occupies the space before the word and carries its style.
        Assert.Equal(new Cea608Span(2, 3, Cea608Color.White, true, false, false), c.Rows[1].Spans[0]);
    }

    [Fact]
    public void ControlCodesSentTwiceActOnce()
    {
        var d = new Cea608Decoder();
        // RDC (paint-on) twice, PAC row 15 twice, "A", then EDM twice.
        Feed(d, 0, [(Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x29)), (Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x29)),
            (Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x70)), (Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x70)),
            (Cea608Encoder.WithParity('A'), Cea608Encoder.WithParity(0)), (Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x2c)),
            (Cea608Encoder.WithParity(0x14), Cea608Encoder.WithParity(0x2c))]);
        d.Finish(T(20));
        Cea608Caption c = Assert.Single(d.Captions);
        Assert.Equal("A", c.Text);
        Assert.Equal(T(4), c.Start);
        Assert.Equal(T(5), c.End);
    }

    [Fact]
    public void BadParityPairsAreDroppedUnlessAllowed()
    {
        var strict = new Cea608Decoder();
        var lenient = new Cea608Decoder { RequireParity = false };
        foreach (Cea608Decoder d in new[] { strict, lenient })
        {
            d.Push(T(0), 0, 0x94, 0x29); // RDC (valid parity)
            d.Push(T(1), 0, 0x14, 0x70); // PAC without parity: 0x14 has even parity
            d.Push(T(2), 0, 0x41, 0x00); // 'A' with bad parity (0x41 has two bits set)
            d.Push(T(3), 0, 0x94, 0x2c);
            d.Finish(T(4));
        }

        Assert.Empty(strict.Captions);
        Assert.Single(lenient.Captions);
    }

    [Fact]
    public void RollUpScrollsAndKeepsTheLastRows()
    {
        var d = new Cea608Decoder();
        byte P(int b) => Cea608Encoder.WithParity(b);
        long f = 0;
        void Pair(int a, int b) { d.Push(T(f++), 0, P(a), P(b)); }
        void Text(string s) { for (int i = 0; i < s.Length; i += 2) { Pair(s[i], i + 1 < s.Length ? s[i + 1] : 0); } }
        Pair(0x14, 0x25); Pair(0x14, 0x25); // RU2
        Pair(0x14, 0x70); Pair(0x14, 0x70); // PAC row 15
        Text("ONE ");
        Pair(0x14, 0x2d); Pair(0x14, 0x2d); // CR
        Text("TWO ");
        Pair(0x14, 0x2d); Pair(0x14, 0x2d); // CR
        Text("THREE ");
        d.Finish(T(f));
        Assert.Equal(["ONE", "ONE\nTWO", "TWO\nTHREE"], d.Captions.Select(c => c.Text));
    }

    [Fact]
    public void XdsProgramNameIsDecodedWithItsChecksum()
    {
        var d = new Cea608Decoder();
        byte P(int b) => Cea608Encoder.WithParity(b);
        // Class 1 (current), type 3 (program name) "HI", end, checksum so the sum of all bytes is 0 mod 128.
        int sum = 0x01 + 0x03 + 'H' + 'I' + 0x0f;
        int check = (128 - (sum & 0x7f)) & 0x7f;
        d.Push(T(0), 1, P(0x01), P(0x03));
        d.Push(T(1), 1, P('H'), P('I'));
        d.Push(T(2), 1, P(0x0f), P(check));
        XdsPacket x = Assert.Single(d.Xds);
        Assert.True(x.ChecksumValid);
        Assert.Equal("HI", x.ProgramName);
    }

    [Fact]
    public void SccCanonicalFormRoundTripsAndDropFrameTimecodesAreExact()
    {
        var caps = new List<Cea608Caption>
        {
            new(Cea608Channel.CC1, new MediaTime(61_061, 1000), new MediaTime(63_063, 1000), [new Cea608Row(14, 1, "ONE MINUTE IN", [])]),
            new(Cea608Channel.CC1, new MediaTime(600_600, 1000), new MediaTime(603_000, 1000), [new Cea608Row(15, 1, "TEN MINUTES", [])]),
        };
        SccDocument doc = SccDocument.FromCaptions(caps);
        string text = doc.Write();
        Assert.StartsWith("Scenarist_SCC V1.0\n\n", text, StringComparison.Ordinal);
        Assert.Equal(text, SccDocument.Parse(text).Write());
        IReadOnlyList<Cea608Caption> back = doc.Decode();
        Assert.Equal(2, back.Count);
        Assert.Equal(["ONE MINUTE IN", "TEN MINUTES"], back.Select(c => c.Text));
        // 00:01:01;00 in drop-frame is frame 1,800 + 30 - 2 = 1,828 (frames 0 and 1 are skipped at minute 1).
        Assert.Equal(1828, SccDocument.Parse("Scenarist_SCC V1.0\n\n00:01:01;00\t9420\n").Lines[0].Frame);
        Assert.Equal(1830, SccDocument.Parse("Scenarist_SCC V1.0\n\n00:01:01:00\t9420\n").Lines[0].Frame);
        Assert.Equal("00:10:00;00", SccDocument.Parse("00:10:00;00\t9420\n").Write().Split('\n')[2].Split('\t')[0]);
        Assert.False(SccDocument.TryParse("00:00:00:00\tzzzz", out _));
    }

    [Fact]
    public void CcDataBuildAndParseRoundTripThroughASeiMessage()
    {
        CcTriplet[] t = [new(true, 0, 0x94, 0x2c), new(true, 3, 0x42, 0x01), new(false, 2, 0x80, 0x80)];
        byte[] payload = CaptionData.BuildItuT35Payload(t);
        Assert.True(CaptionData.TryGetFromItuT35(payload, out ReadOnlySpan<byte> cc));
        Span<CcTriplet> back = stackalloc CcTriplet[31];
        int n = CaptionData.ParseCcData(cc, back);
        Assert.Equal(3, n);
        Assert.Equal(t, back[..n].ToArray());

        // Inside a length-prefixed H.264 sample: [SEI NAL][slice NAL].
        var nal = new List<byte> { 0x06, 4, (byte)payload.Length };
        nal.AddRange(payload);
        nal.Add(0x80);
        var sample = new List<byte> { 0, 0, 0, (byte)nal.Count };
        sample.AddRange(nal);
        sample.AddRange([0, 0, 0, 3, 0x65, 0x88, 0x84]);
        var found = new List<CcTriplet>();
        Assert.Equal(3, new CaptionExtractor(CaptionCarriage.H264Sei, 4).Extract(sample.ToArray(), found));
        Assert.Equal(t, found);
    }

    [Fact]
    public void Cea708WindowTextBecomesACaptionAtServiceOne()
    {
        var d = new Cea708Decoder();
        // Service block 1: DF0 (visible, anchor row 70 col 20, 2 rows x 20 cols), "Hi" CR "there".
        List<byte> block = [0x98, 0x20, 70, 20, 0x01, 19, 0x01, (byte)'H', (byte)'i', 0x0d, (byte)'t', (byte)'h', (byte)'e', (byte)'r', (byte)'e'];
        List<byte> packet = [(byte)((1 << 5) | block.Count), .. block];
        // packet_size_code: size in bytes including the header byte, in 2-byte words.
        int words = (packet.Count + 1 + 1) / 2;
        List<byte> all = [(byte)words, .. packet];
        if (all.Count % 2 == 1)
        {
            all.Add(0);
        }

        d.Push(T(0), 3, all[0], all[1]);
        for (int i = 2; i + 1 < all.Count; i += 2)
        {
            d.Push(T(i / 2), 2, all[i], all[i + 1]);
        }

        d.Finish(T(30));
        Cea708Caption c = Assert.Single(d.Captions);
        Assert.Equal(1, c.Service);
        Assert.Equal("Hi\nthere", c.Text);
        Cea708Window w = Assert.Single(c.Windows);
        Assert.Equal((0, 70, 20), (w.Id, w.AnchorVertical, w.AnchorHorizontal));
    }

    [Fact]
    public void SccReadsAndWritesThroughTheCueModel()
    {
        var doc = new SubtitleDocument
        {
            Cues = new SubtitleList<SubtitleCue>(
            [
                new SubtitleCue
                {
                    Start = new MediaTime(5005, 1000),
                    End = new MediaTime(7007, 1000),
                    Text = "Hello there\nthis is a very long second line that must be wrapped at the grid width",
                    Spans = new SubtitleList<SubtitleSpan>([new SubtitleSpan(0, 5) { Style = new TextStyle { Italic = true } }, new SubtitleSpan(6, 5) { Style = new TextStyle { Color = new SubtitleColor(255, 255, 0) } }]),
                },
            ]),
        };
        SubtitleFormats.Register(SccSubtitleFormat.Instance);
        Assert.Same(SccSubtitleFormat.Instance, SubtitleFormats.Find(".scc"));
        SubtitleOutput written = SccSubtitleFormat.Instance.Write(doc);
        Assert.Contains(written.Report.Losses, l => l.Message.Contains("wrapped", StringComparison.Ordinal));
        Assert.Same(SccSubtitleFormat.Instance, SubtitleFormats.Sniff(written.Text.AsSpan()));
        SubtitleDocument back = SccSubtitleFormat.Instance.ReadText(written.Text);
        SubtitleCue cue = Assert.Single(back.Cues);
        Assert.Equal(CaptionMode.PopOn, cue.Mode);
        Assert.Equal(new MediaTime(5005, 1000), cue.Start);
        Assert.StartsWith("Hello there\nthis is a very long second line", cue.Text, StringComparison.Ordinal);
        Assert.Contains(cue.Spans, sp => sp.Style?.Italic == true);
        Assert.Contains(cue.Spans, sp => sp.Style?.Color == new SubtitleColor(255, 255, 0));
        Assert.Equal(15 - 4 + 1, cue.Placement!.Row); // four rows (the long line wraps twice) sit at the bottom of the grid
    }
}

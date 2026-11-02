using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Teletext;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Tests;

public class TeletextTests
{
    private static MediaTime T(long seconds) => new(seconds * TeletextBuilder.Clock, TeletextBuilder.Clock);

    private static byte[][] Say(string text, int nationalBits = 0, int colour = 7, int magazine = 7, int page = 0x77)
        => TeletextBuilder.Page(magazine, page, subtitle: true, erase: true, nationalBits, (20, TeletextBuilder.BoxedRow(text, colour)));

    private static byte[][] Clear(int magazine = 7, int page = 0x77)
        => TeletextBuilder.Page(magazine, page, subtitle: true, erase: true, 0);

    private static IReadOnlyList<TeletextSubtitleTrack> Run(IEnumerable<(long Seconds, byte[][] Units)> pes, MediaTime end, TeletextOptions? options = null, IReadOnlyList<TeletextService>? services = null, DefectLog? defects = null)
    {
        TeletextSubtitleReader reader = new(options, services, defects);
        foreach ((long s, byte[][] units) in pes)
        {
            reader.Push(TeletextBuilder.PesPayload(units), T(s));
        }

        return reader.Finish(end);
    }

    [Fact]
    public void Hamming84RoundTripsCorrectsOneBitAndRejectsTwo()
    {
        for (int d = 0; d < 16; d++)
        {
            byte code = TeletextBuilder.Hamming84(d);
            Assert.Equal(d, TeletextCodes.DecodeHamming84(code));
            for (int bit = 0; bit < 8; bit++)
            {
                Assert.Equal(d, TeletextCodes.DecodeHamming84((byte)(code ^ (1 << bit))));
            }

            Assert.Equal(-1, TeletextCodes.DecodeHamming84((byte)(code ^ 0x03)));
        }
    }

    [Fact]
    public void Hamming2418RoundTripsAndCorrectsOneBit()
    {
        foreach (int data in new[] { 0, 1, 0x3FFFF, 0x2A5A5, 0x15555, 0x12345, 0x00F0F })
        {
            byte[] t = TeletextBuilder.Hamming2418(data);
            Assert.Equal(data, TeletextCodes.DecodeHamming2418(t[0], t[1], t[2]));
            for (int bit = 0; bit < 24; bit++)
            {
                int w = (t[0] | (t[1] << 8) | (t[2] << 16)) ^ (1 << bit);
                Assert.Equal(data, TeletextCodes.DecodeHamming2418((byte)w, (byte)(w >> 8), (byte)(w >> 16)));
            }

            int two = (t[0] | (t[1] << 8) | (t[2] << 16)) ^ 0x000005;
            Assert.Equal(-1, TeletextCodes.DecodeHamming2418((byte)two, (byte)(two >> 8), (byte)(two >> 16)));
        }
    }

    [Fact]
    public void ShowsAPageUntilTheNextOneAndTheLastUntilStreamEnd()
    {
        IReadOnlyList<TeletextSubtitleTrack> tracks = Run([(1, Say("Hello there")), (3, Clear()), (5, Say("Second line")), (6, Say("Third"))], T(9));
        TeletextSubtitleTrack track = Assert.Single(tracks);
        Assert.Equal(new TeletextPageAddress(7, 0x77), track.Page);
        SubtitleCue[] cues = [.. track.Document.Cues];
        Assert.Equal(3, cues.Length);
        Assert.Equal("Hello there", cues[0].Text);
        Assert.Equal(T(1), cues[0].Start);
        Assert.Equal(T(3), cues[0].End);
        Assert.Equal("Second line", cues[1].Text);
        Assert.Equal(T(6), cues[1].End);
        Assert.Equal("Third", cues[2].Text);
        Assert.Equal(T(9), cues[2].End);
        Assert.Equal(20, cues[0].Placement?.Row);
    }

    [Fact]
    public void LastCueWithoutAStreamEndGetsTheDefaultDuration()
    {
        IReadOnlyList<TeletextSubtitleTrack> tracks = Run([(2, Say("Only"))], MediaTime.Unknown);
        SubtitleCue cue = Assert.Single(tracks[0].Document.Cues);
        Assert.Equal(T(5), cue.End);
    }

    [Fact]
    public void ColourBecomesASpan()
    {
        IReadOnlyList<TeletextSubtitleTrack> tracks = Run([(1, Say("Yellow", colour: 3))], T(4));
        SubtitleCue cue = Assert.Single(tracks[0].Document.Cues);
        Assert.Equal("Yellow", cue.Text);
        SubtitleSpan span = Assert.Single(cue.Spans);
        Assert.Equal(new SubtitleColor(255, 255, 0), span.Style?.Color);
    }

    [Fact]
    public void LiveWordByWordTransmissionsMergeUnlessProgressive()
    {
        (long, byte[][])[] input = [(1, Say("One")), (2, Say("One two")), (3, Say("One two three")), (5, Clear())];
        SubtitleCue merged = Assert.Single(Run(input, T(8))[0].Document.Cues);
        Assert.Equal("One two three", merged.Text);
        Assert.Equal(T(1), merged.Start);
        Assert.Equal(T(5), merged.End);

        SubtitleCue[] states = [.. Run(input, T(8), new TeletextOptions { Progressive = true })[0].Document.Cues];
        Assert.Equal(3, states.Length);
        Assert.Equal("One two", states[1].Text);
        Assert.Equal(T(3), states[1].End);
    }

    [Fact]
    public void NationalOptionBitsChooseTheSubset()
    {
        // ETS 300 706 Table 36: the Italian subset puts "à" at 0x7B; the header's C12 to C14 carry the option (C12 the most significant).
        int italian = 0b110; // C12 is the most significant of the three bits: C13 and C14 set -> option 3
        IReadOnlyList<TeletextSubtitleTrack> tracks = Run([(1, Say("citt{", nationalBits: italian))], T(3));
        Assert.Equal("città", Assert.Single(tracks[0].Document.Cues).Text);
    }

    [Fact]
    public void EnhancementPacketAddsAnAccent()
    {
        // X/26 mode 0x12 (acute) at column 5 of row 20 over the letter 'e' (ETS 300 706 §12.3).
        byte[][] unitsBase = Say("cafe");
        List<byte[]> units = [.. unitsBase];
        units.Add(TeletextBuilder.DataUnit(7, 26, TeletextBuilder.Enhancement(0, (40 + 20, 0x04, 0), (0 + 6, 0x12, 'e'))));
        TeletextSubtitleTrack track = Assert.Single(Run([(1, [.. units])], T(3)));
        Assert.Equal("café", Assert.Single(track.Document.Cues).Text);
    }

    [Fact]
    public void Designation36ReadsCyrillic()
    {
        // The designation packet follows the header it belongs to (ETS 300 706 §9.4.2); Table 39: 0x40 is Yu, 0x41 A, 0x42 Be.
        List<byte[]> units = [.. Say("@AB")];
        units.Insert(1, TeletextBuilder.DataUnit(7, 28, TeletextBuilder.Designation(0b0100100)));
        TeletextSubtitleTrack track = Assert.Single(Run([(1, [.. units])], T(3)));
        Assert.Equal("ЮАБ", Assert.Single(track.Document.Cues).Text);
    }

    [Fact]
    public void LanguageFallbackChoosesPolish()
    {
        TeletextService[] services = [new TeletextService("pol", TeletextPageType.Subtitle, new TeletextPageAddress(7, 0x77))];
        // Without a designation and with C12 to C14 zero the PMT language decides; Table 36 puts the Polish "ś" at 0x7C.
        TeletextSubtitleTrack track = Assert.Single(Run([(1, Say("|wiat")), (2, Clear())], T(3), services: services));
        Assert.Equal("pol", track.Language);
        Assert.Equal("świat", Assert.Single(track.Document.Cues).Text);
    }

    [Fact]
    public void UnannouncedNonSubtitlePagesAreIgnoredUnlessAllPages()
    {
        byte[][] page = TeletextBuilder.Page(1, 0x00, subtitle: false, erase: true, 0, (1, TeletextBuilder.Row("ABC")));
        Assert.Empty(Run([(1, page), (2, TeletextBuilder.Page(1, 0x00, false, true, 0))], T(3)));
        IReadOnlyList<TeletextSubtitleTrack> all = Run([(1, page), (2, TeletextBuilder.Page(1, 0x00, false, true, 0))], T(3), new TeletextOptions { AllPages = true });
        TeletextSubtitleTrack track = Assert.Single(all);
        Assert.Equal(2, track.Pages.Count);
        Assert.Equal("ABC", track.Pages[0].RowText(1).Trim());
    }

    [Fact]
    public void MosaicCellsAreDroppedAndReported()
    {
        // 0x11 selects red mosaics; a boxed mosaic run shows as spaces.
        byte[] row = TeletextBuilder.Row("\u0007\u000B\u000Bok\u0011###\u0007ok\u000A\u000A");
        byte[][] page = TeletextBuilder.Page(7, 0x77, true, true, 0, (20, row));
        TeletextSubtitleTrack track = Assert.Single(Run([(1, page)], T(3)));
        Assert.StartsWith("ok", Assert.Single(track.Document.Cues).Text);
        Assert.True(track.Report.MosaicCells > 0);
        Assert.False(track.Report.IsLossless);
    }

    [Fact]
    public void HostileInputNeverThrows()
    {
        DefectLog defects = new();
        TeletextSubtitleReader reader = new(new TeletextOptions { AllPages = true }, null, defects);
        Random random = new(1234);
        byte[] junk = new byte[4096];
        for (int round = 0; round < 200; round++)
        {
            random.NextBytes(junk);
            reader.Push(junk.AsSpan(0, random.Next(junk.Length)), T(round));
        }

        // Rows 25 to 31 and a unit that claims more than it has.
        byte[] unit25 = TeletextBuilder.DataUnit(7, 25, TeletextBuilder.Row("x"));
        byte[] unit31 = TeletextBuilder.DataUnit(7, 31, TeletextBuilder.Row("x"));
        byte[] cut = [0x10, 0x03, 0xFF, 1, 2, 3];
        reader.Push([0x10, .. unit25, .. unit31], T(500));
        reader.Push(cut, T(501));
        reader.Push([], T(502));
        _ = reader.Finish(T(600));
        Assert.True(defects.Count > 0);
    }

    [Fact]
    public void ServiceDescriptorIsParsed()
    {
        byte[] d = [0x56, 10, (byte)'e', (byte)'n', (byte)'g', (2 << 3) | 1, 0x88, (byte)'f', (byte)'r', (byte)'a', (5 << 3) | 0, 0x01];
        IReadOnlyList<TeletextService> s = TeletextService.Parse(d);
        Assert.Equal(2, s.Count);
        Assert.Equal("eng", s[0].Language);
        Assert.True(s[0].IsSubtitle);
        Assert.Equal(new TeletextPageAddress(1, 0x88), s[0].Page);
        Assert.Equal(TeletextPageType.SubtitleHearingImpaired, s[1].Type);
        Assert.Equal(0x01, s[1].Page.Number);
    }

    [Fact]
    public void TransportStreamEndToEnd()
    {
        byte[] ts = TeletextBuilder.TransportStream([(1 * TeletextBuilder.Clock, Say("Ciao")), (3 * TeletextBuilder.Clock, Clear()), (4 * TeletextBuilder.Clock, Say("Dopo"))]);
        using MpegTsDemuxer demuxer = MpegTsDemuxer.Open(new MemoryMediaSource(ts));
        Assert.Contains(demuxer.Tracks, TeletextExtractor.IsTeletext);
        TeletextTrackSubtitles found = Assert.Single(TeletextExtractor.ReadAll(demuxer));
        Assert.Equal(0x101, found.Pid);
        TeletextSubtitleTrack track = Assert.Single(found.Subtitles);
        Assert.Equal("ita", track.Language);
        Assert.True(track.Declared);
        SubtitleCue[] cues = [.. track.Document.Cues];
        Assert.Equal(["Ciao", "Dopo"], cues.Select(c => c.Text).ToArray());
        Assert.Equal(T(1), cues[0].Start);
        Assert.Equal(T(3), cues[0].End);
    }

    [Fact]
    public void ProgramStreamCarriesTeletextInPrivateStream1()
    {
        byte[] ps = TeletextBuilder.ProgramStream([(1 * TeletextBuilder.Clock, Say("Hallo")), (3 * TeletextBuilder.Clock, Clear())]);
        using MpegPsDemuxer demuxer = MpegPsDemuxer.Open(new MemoryMediaSource(ps));
        Assert.Contains(demuxer.Tracks, TeletextExtractor.IsTeletext);
        TeletextTrackSubtitles found = Assert.Single(TeletextExtractor.ReadAll(demuxer));
        Assert.Equal(-1, found.Pid);
        Assert.Equal("Hallo", Assert.Single(found.Subtitles[0].Document.Cues).Text);
    }
}

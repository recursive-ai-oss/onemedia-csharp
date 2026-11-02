using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>The wire layer of container text tracks: sample entries, samples and blocks read and written without change.</summary>
public class TextTrackWireTests
{
    // Real bytes from a file MP4Box wrote from a SubRip input: the tx3g entry after its 8-byte header, and a sample with a style box.
    private static readonly byte[] MP4BoxEntry = Convert.FromHexString("0000000001ff0000000000000000003000400000000000010012ffffffff000000126674616200010001055365726966");

    private static byte[] StyledSample()
    {
        List<byte> b = [0x00, 0x1b];
        b.AddRange(Encoding.ASCII.GetBytes("Bold, italic and underlined"));
        b.AddRange(Convert.FromHexString("0000002e7374796c0003" + "0000000400010112ffffffff" + "0006000c00010212ffffffff" + "0011001b00010412ffffffff"));
        return [.. b];
    }

    [Fact]
    public void Tx3gEntryReadsItsFieldsAndWritesTheSameBytes()
    {
        Assert.True(TimedTextSampleEntry.TryParse(MP4BoxEntry, out TimedTextSampleEntry? e));
        Assert.NotNull(e);
        Assert.Equal(0u, e.DisplayFlags);
        Assert.Equal(1, e.HorizontalJustification);
        Assert.Equal(-1, e.VerticalJustification);
        Assert.Equal(new TextBoxRecord(0, 0, 0x30, 0x40), e.DefaultTextBox);
        Assert.Equal(new TextStyleRecord(0, 0, 1, TextFaceStyle.None, 0x12, 0xFFFFFFFF), e.DefaultStyle);
        Assert.Equal([new TimedTextFont(1, "Serif")], e.Fonts);
        Assert.Equal(MP4BoxEntry, e.ToConfiguration());
    }

    [Fact]
    public void Tx3gEntryKeepsBoxesItDoesNotKnow()
    {
        byte[] withBitrate = [.. MP4BoxEntry, .. Convert.FromHexString("0000001462747274000000000000010100000101")];
        Assert.True(TimedTextSampleEntry.TryParse(withBitrate, out TimedTextSampleEntry? e));
        Assert.Equal(20, e!.OtherBoxes.Length);
        Assert.Equal(withBitrate, e.ToConfiguration());
        Assert.False(TimedTextSampleEntry.TryParse(MP4BoxEntry.AsSpan(0, 20), out _));
    }

    [Fact]
    public void StyledSampleReadsBoxesAndWritesTheSameBytes()
    {
        byte[] bytes = StyledSample();
        Assert.True(TimedTextSample.TryParse(bytes, out TimedTextSample? s));
        Assert.Equal("Bold, italic and underlined", s!.Text);
        TextModifierBox styl = Assert.Single(s.Modifiers);
        Assert.True(styl.TryGetStyles(out IReadOnlyList<TextStyleRecord> styles));
        Assert.Equal(3, styles.Count);
        Assert.Equal(new TextStyleRecord(0, 4, 1, TextFaceStyle.Bold, 18, 0xFFFFFFFF), styles[0]);
        Assert.Equal(TextFaceStyle.Italic, styles[1].Face);
        Assert.Equal(TextFaceStyle.Underline, styles[2].Face);
        Assert.Equal(bytes, s.ToArray());
    }

    [Fact]
    public void EmptyAndTruncatedSamples()
    {
        Assert.True(TimedTextSample.TryParse([0, 0], out TimedTextSample? gap));
        Assert.True(gap!.IsEmpty);
        Assert.Equal([0, 0], gap.ToArray());
        Assert.True(TimedTextSample.TryParse([], out TimedTextSample? none));
        Assert.True(none!.IsEmpty);
        Assert.False(TimedTextSample.TryParse([0, 5, (byte)'a'], out _));
        Assert.False(TimedTextSample.TryParse([7], out _));
    }

    [Fact]
    public void BoxThatDoesNotFitIsKeptAsTrailingBytes()
    {
        byte[] bytes = [0, 1, (byte)'x', 0, 0, 0, 99, (byte)'s', (byte)'t', (byte)'y', (byte)'l'];
        Assert.True(TimedTextSample.TryParse(bytes, out TimedTextSample? s));
        Assert.Empty(s!.Modifiers);
        Assert.Equal(8, s.Trailing.Length);
        Assert.Equal(bytes, s.ToArray());
    }

    [Fact]
    public void Utf16TextNeedsAByteOrderMarkAndRoundTrips()
    {
        byte[] bytes = [0, 6, 0xFE, 0xFF, 0x00, 0x48, 0x00, 0x69];
        Assert.True(TimedTextSample.TryParse(bytes, out TimedTextSample? s));
        Assert.Equal("Hi", s!.Text);
        Assert.True(s.IsUtf16);
        Assert.Equal(bytes, s.ToArray());
    }

    [Fact]
    public void CharacterOffsetsCountCodePointsInUtf8AndUnitsInUtf16()
    {
        TimedTextSample utf8 = new("a\U0001F600b");
        Assert.Equal(3, utf8.ToTextIndex(2));
        Assert.Equal(2, utf8.ToCharOffset(3));
        TimedTextSample utf16 = new("a\U0001F600b", null, utf16: true);
        Assert.Equal(2, utf16.ToTextIndex(2));
        Assert.Equal(3, utf16.ToCharOffset(3));
    }

    [Fact]
    public void ModifierBoxesRoundTripThroughTheirCreators()
    {
        TextModifierBox k = TextModifierBox.CreateKaraoke(100, [new KaraokeStep(400, 0, 3), new KaraokeStep(900, 3, 6)]);
        Assert.True(k.TryGetKaraoke(out uint start, out IReadOnlyList<KaraokeStep> steps));
        Assert.Equal(100u, start);
        Assert.Equal(new KaraokeStep(900, 3, 6), steps[1]);
        TextModifierBox link = TextModifierBox.CreateLink(new TimedTextLink(1, 4, "https://example.org/", "alt"));
        Assert.True(link.TryGetLink(out TimedTextLink l));
        Assert.Equal(new TimedTextLink(1, 4, "https://example.org/", "alt"), l);
        TextModifierBox box = TextModifierBox.CreateTextBox(new TextBoxRecord(1, 2, 3, 4));
        Assert.True(box.TryGetTextBox(out TextBoxRecord r));
        Assert.Equal(new TextBoxRecord(1, 2, 3, 4), r);
        Assert.False(box.TryGetKaraoke(out _, out _));
        // A count that does not fit its box is refused, not read past the end.
        Assert.False(new TextModifierBox("styl", new byte[] { 0xFF, 0xFF, 1 }).TryGetStyles(out _));
        Assert.False(new TextModifierBox("krok", new byte[] { 0, 0, 0, 0, 0xFF, 0xFF }).TryGetKaraoke(out _, out _));
    }

    [Fact]
    public void QuickTimeEntryRoundTrips()
    {
        QuickTimeTextSampleEntry e = new() { DisplayFlags = 0x2000, Justification = 1, FontFace = 1, Foreground = (0xFFFF, 0xFFFF, 0xFFFF), FontName = "Helvetica" };
        byte[] bytes = e.ToConfiguration();
        Assert.True(QuickTimeTextSampleEntry.TryParse(bytes, out QuickTimeTextSampleEntry? back));
        Assert.Equal(e.FontName, back!.FontName);
        Assert.Equal(e.Foreground, back.Foreground);
        Assert.Equal(bytes, back.ToConfiguration());
    }

    // MP4Box 26.07 wrote this sample for a cue with an identifier, settings and a payload.
    private static readonly byte[] WebVttSampleBytes = Convert.FromHexString("00000056" + "76747463" + "0000000d6964656e696e74726f" + "0000002a737474676c696e653a31302520706f736974696f6e3a3530252061" + "6c69676e3a63656e746572" + "000000177061796c506c61696e20666972737420637565");

    [Fact]
    public void WebVttCueBoxReadsItsPartsAndWritesTheSameBytes()
    {
        Assert.True(WebVttSample.TryParse(WebVttSampleBytes, out WebVttSample? s));
        WebVttCueBox cue = Assert.IsType<WebVttCueBox>(Assert.Single(s!.Items));
        Assert.Equal("Plain first cue", cue.Payload);
        Assert.Equal("intro", cue.Identifier);
        Assert.Equal("line:10% position:50% align:center", cue.Settings);
        Assert.Null(cue.CueTime);
        Assert.Equal(WebVttSampleBytes, s.ToArray());
    }

    [Fact]
    public void WebVttSampleHoldsGapsCommentsAndUnknownBoxes()
    {
        byte[] bytes = [.. Convert.FromHexString("0000000876747465"), .. Convert.FromHexString("00000010767474614e4f544520686572"), .. Convert.FromHexString("0000000a7a7a7a7a0102")];
        Assert.True(WebVttSample.TryParse(bytes, out WebVttSample? s));
        Assert.IsType<WebVttEmptyCue>(s!.Items[0]);
        Assert.True(s.HasNoCues);
        Assert.Equal("NOTE her", Assert.IsType<WebVttAdditionalText>(s.Items[1]).Text);
        Assert.Equal("zzzz", Assert.IsType<WebVttUnknownBox>(s.Items[2]).Type);
        Assert.Equal(bytes, s.ToArray());
    }

    [Fact]
    public void WebVttEntryReadsHeaderAndLabel()
    {
        byte[] bytes = [.. Convert.FromHexString("00000024" + "76747443" + "574542565454202d2067656e65726174656420746573742063756573"), .. Convert.FromHexString("0000000b766c6162656e67")];
        // "vlab" payload "eng".
        Assert.True(WebVttSampleEntry.TryParse(bytes, out WebVttSampleEntry? e));
        Assert.Equal("WEBVTT - generated test cues", e!.HeaderText);
        Assert.Equal("eng", e.SourceLabel);
        Assert.Equal(bytes, e.ToConfiguration());
    }

    [Fact]
    public void XmlSubtitleEntryHoldsThreeStrings()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("http://www.w3.org/ns/ttml"), 0, 0, 0, .. Convert.FromHexString("0000001462747274000000000000010100000101")];
        Assert.True(XmlSubtitleSampleEntry.TryParse(bytes, out XmlSubtitleSampleEntry? e));
        Assert.Equal("http://www.w3.org/ns/ttml", e!.Namespace);
        Assert.Equal(string.Empty, e.SchemaLocation);
        Assert.Equal(20, e.OtherBoxes.Length);
        Assert.Equal(bytes, e.ToConfiguration());
        Assert.False(XmlSubtitleSampleEntry.TryParse(Encoding.UTF8.GetBytes("no terminator"), out _));
    }

    [Fact]
    public void AssEventSplitsIntoNineFieldsAndTheTextKeepsItsCommas()
    {
        byte[] block = Encoding.UTF8.GetBytes("12,1,Top,Narrator,0000,0000,0000,,Hello, world, again");
        Assert.True(MatroskaAssEvent.TryParse(block, out MatroskaAssEvent? e));
        Assert.Equal("12", e!.ReadOrder);
        Assert.Equal("1", e.Layer);
        Assert.Equal("Top", e.Style);
        Assert.Equal("Narrator", e.Name);
        Assert.Equal("Hello, world, again", e.Text);
        Assert.Equal(block, e.ToBytes());
        Assert.False(MatroskaAssEvent.TryParse("1,2,3"u8, out _));
    }

    [Fact]
    public void WebVttAdditionsFollowTheMatroskaExamples()
    {
        // Cue 1 of the Matroska mapping's example: no settings, identifier "hello".
        MatroskaWebVttAdditions a = MatroskaWebVttAdditions.Parse("\nhello"u8);
        Assert.Equal(new MatroskaWebVttAdditions(string.Empty, "hello", string.Empty), a);
        Assert.Equal("\nhello"u8.ToArray(), a.ToBytes());
        // Cue 2: neither settings nor identifier, then a comment block.
        MatroskaWebVttAdditions b = MatroskaWebVttAdditions.Parse("\n\nNOTE style blocks can't appear after the first cue."u8);
        Assert.Equal("NOTE style blocks can't appear after the first cue.", b.Comments);
        Assert.Equal("\n\nNOTE style blocks can't appear after the first cue."u8.ToArray(), b.ToBytes());
        // Cue 3: settings, and an empty identifier line.
        MatroskaWebVttAdditions c = MatroskaWebVttAdditions.Parse("position:90% align:right size:35%\n"u8);
        Assert.Equal("position:90% align:right size:35%", c.Settings);
        Assert.Equal(string.Empty, c.Identifier);
        Assert.Equal("position:90% align:right size:35%\n"u8.ToArray(), c.ToBytes());
        Assert.True(MatroskaWebVttAdditions.Parse([]).IsEmpty);
        Assert.Empty(MatroskaWebVttAdditions.Parse([]).ToBytes());
    }

    [Fact]
    public void WebmWebVttBlockHasIdentifierSettingsAndPayload()
    {
        WebmWebVttBlock b = WebmWebVttBlock.Parse("intro\nline:10%\nHello\nworld"u8);
        Assert.Equal(new WebmWebVttBlock("intro", "line:10%", "Hello\nworld"), b);
        Assert.Equal("intro\nline:10%\nHello\nworld"u8.ToArray(), b.ToBytes());
        Assert.Equal("\n\nPlain"u8.ToArray(), new WebmWebVttBlock(string.Empty, string.Empty, "Plain").ToBytes());
        Assert.Equal("just text", WebmWebVttBlock.Parse("just text"u8).Payload);
    }
}

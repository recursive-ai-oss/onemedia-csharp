using System.Text;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Tests.Captions;

public class SubtitleFormatTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "subtitles", name);

    private static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    public static TheoryData<string, string> Canonical => new()
    {
        { "sample.srt", "srt" },
        { "sample.vtt", "webvtt" },
        { "sample.ass", "ass" },
        { "sample.sbv", "sbv" },
        { "sample.lrc", "lrc" },
    };

    [Theory]
    [MemberData(nameof(Canonical))]
    public void CanonicalFileRoundTripsByteIdentical(string file, string format)
    {
        SubtitleFormat f = SubtitleFormats.Find(format)!;
        byte[] input = Bytes(file);
        var defects = new DefectLog();
        SubtitleDocument doc = f.Read(input, defects);
        SubtitleOutput output = f.Write(doc);
        Assert.Equal(Encoding.UTF8.GetString(input), Encoding.UTF8.GetString(output.Data));
        Assert.Equal(input, output.Data);
        SubtitleDocument again = f.Read(output.Data);
        Assert.Equal(doc, again);
        Assert.DoesNotContain(defects, d => d.Severity != DefectSeverity.Info);
    }

    [Theory]
    [MemberData(nameof(Canonical))]
    public void SniffingIgnoresTheExtension(string file, string format)
    {
        byte[] input = Bytes(file);
        Assert.Equal(format, SubtitleFormats.Sniff(input)?.Name);
        Assert.Equal(format, SubtitleFormats.Read(input).Format);
    }

    [Fact]
    public void TtmlIsSniffedAndRead()
    {
        byte[] input = Bytes("sample.ttml");
        Assert.Equal("ttml", SubtitleFormats.Sniff(input)?.Name);
        SubtitleDocument doc = SubtitleFormats.Ttml.Read(input);
        Assert.Equal(4, doc.Cues.Count);
        Assert.Equal("en", doc.Language);
        Assert.Equal("Sample", doc.Title);
        Assert.Equal(new MediaTime(1000, 1000), doc.Cues[0].Start);
        Assert.Equal(new MediaTime(3500, 1000), doc.Cues[0].End);
        Assert.Equal("Alice", doc.Cues[0].Speaker);
        Assert.Equal("bottom", doc.Cues[0].Placement?.Region);
        Assert.Equal("Hello, world!", doc.Cues[0].Text);
        Assert.Contains(doc.Cues[0].Spans, s => s.Style?.Bold == true && doc.Cues[0].TextOf(s) == "world");
        // begin="4s" dur="2.25s"
        Assert.Equal(new MediaTime(6250, 1000), doc.Cues[1].End);
        Assert.Equal("Second\nline red", doc.Cues[1].Text);
        Assert.Equal("s2", doc.Cues[1].StyleName);
        Assert.Contains(doc.Cues[1].Spans, s => s.Style?.Color == new SubtitleColor(255, 0, 0) && s.Style.Underline == true);
        // Word timing is relative to the paragraph.
        Assert.Equal(new MediaTime(7000, 1000), doc.Cues[2].Spans[0].WordStart);
        Assert.Equal(new MediaTime(8000, 1000), doc.Cues[2].Spans[2].WordStart);
        // 00:00:10:15 at the default 30 frames per second is 10.5 s.
        Assert.Equal(new MediaTime(21, 2), doc.Cues[3].Start);
        Assert.Equal(2, doc.Styles.Count);
        Assert.Equal(2, doc.Regions.Count);
        Assert.Equal(80, doc.Regions[0].Width);
    }

    [Fact]
    public void TtmlWriteIsStableAndWellFormed()
    {
        SubtitleDocument doc = SubtitleFormats.Ttml.Read(Bytes("sample.ttml"));
        SubtitleOutput first = SubtitleFormats.Ttml.Write(doc);
        SubtitleDocument again = SubtitleFormats.Ttml.Read(first.Data);
        SubtitleOutput second = SubtitleFormats.Ttml.Write(again);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(doc.Cues.Count, again.Cues.Count);
        for (int i = 0; i < doc.Cues.Count; i++)
        {
            Assert.Equal(doc.Cues[i].Text, again.Cues[i].Text);
            Assert.Equal(doc.Cues[i].Start, again.Cues[i].Start);
            Assert.Equal(doc.Cues[i].End, again.Cues[i].End);
            Assert.Equal(doc.Cues[i].Spans, again.Cues[i].Spans);
        }

        var xml = new System.Xml.XmlDocument { XmlResolver = null };
        xml.LoadXml(first.Text);
        Assert.Equal("tt", xml.DocumentElement!.LocalName);
        Assert.Equal("http://www.w3.org/ns/ttml", xml.DocumentElement.NamespaceURI);
        Assert.Contains("ttp:profile=\"http://www.w3.org/ns/ttml/profile/imsc1/text\"", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VttModelHasWhatTheFileSays()
    {
        SubtitleDocument doc = SubtitleFormats.WebVtt.Read(Bytes("sample.vtt"));
        Assert.Equal(3, doc.Cues.Count);
        Assert.Equal("intro", doc.Cues[0].Id);
        Assert.Equal("Alice", doc.Cues[0].Speaker);
        Assert.Equal("Hello, world!", doc.Cues[0].Text);
        Assert.Equal("fred", doc.Cues[0].Placement?.Region);
        Assert.Equal(SubtitleAlign.Start, doc.Cues[0].Placement?.Align);
        Assert.Single(doc.Regions);
        Assert.Equal(40, doc.Regions[0].Width);
        Assert.Equal(3, doc.Regions[0].Lines);
        Assert.True(doc.Regions[0].ScrollUp);
        Assert.Contains(doc.Cues[0].Extras, e => e.Key == "note" && e.Value == "NOTE a comment");
        Assert.Contains(doc.Extras, e => e.Key == "style");
        SubtitleCue second = doc.Cues[1];
        Assert.Equal("Second & red <tag>\n\u6f22\u5b57", second.Text);
        Assert.Equal(0, second.Placement!.Line!.Value.Value);
        Assert.Equal(10, second.Placement.Position);
        Assert.Equal(SubtitleAlign.Left, second.Placement.PositionAnchor);
        Assert.Equal(80, second.Placement.Size);
        Assert.Contains(second.Spans, s => s.Style?.Color == new SubtitleColor(255, 0, 0));
        Assert.Contains(second.Spans, s => s.Ruby == "kan" && second.TextOf(s) == "\u6f22");
        SubtitleCue third = doc.Cues[2];
        Assert.Equal(3, third.Spans.Count(s => s.WordStart is not null));
        Assert.Equal(new MediaTime(7500, 1000), third.Spans.First(s => third.TextOf(s) == "two ").WordStart);
    }

    [Fact]
    public void AssModelHasWhatTheFileSays()
    {
        SubtitleDocument doc = SubtitleFormats.Ass.Read(Bytes("sample.ass"));
        Assert.Equal("Sample", doc.Title);
        Assert.Equal(384, doc.ReferenceWidth);
        Assert.Equal(2, doc.Styles.Count);
        Assert.Equal(true, doc.Styles[1].Text.Bold);
        Assert.Equal(new SubtitleColor(255, 255, 0), doc.Styles[1].Text.Color);
        Assert.Equal(SubtitleVerticalAlign.Top, doc.Styles[1].Placement!.VerticalAlign);
        Assert.Equal(4, doc.Cues.Count);
        Assert.Equal("Top line\nsecond line", doc.Cues[1].Text);
        Assert.Equal("Bob", doc.Cues[1].Speaker);
        Assert.Equal("Top", doc.Cues[1].StyleName);
        Assert.Equal(50, doc.Cues[1].Placement!.Position!.Value, 6);
        Assert.Equal(SubtitleVerticalAlign.Top, doc.Cues[1].Placement!.VerticalAlign);
        // {\k50}Ka{\k50}ra{\k100}oke : 0.5 s, 0.5 s, 1 s from the cue start.
        SubtitleSpan[] words = doc.Cues[2].Spans.Where(s => s.WordStart is not null).ToArray();
        Assert.Equal(3, words.Length);
        Assert.Equal(new MediaTime(7500, 1000), words[1].WordStart);
        Assert.Equal(new MediaTime(9000, 1000), words[2].WordEnd);
        Assert.Contains(doc.Cues[3].Spans, s => s.Raw is { Key: "override", Value: "\\fad(200,200)" });
    }

    [Fact]
    public void SrtDefectsCarryByteOffsets()
    {
        // The accented letters take two bytes each in UTF-8, so a character index and a byte offset differ.
        string text = "1\n00:00:01,000 --> 00:00:02,000\n\u00e9\u00e9\u00e9\n\nstray line\n\n2\n00:00:03,000 --> 00:00:04,000\nb\n";
        var defects = new DefectLog();
        SubtitleDocument doc = SubtitleFormats.Srt.Read(Encoding.UTF8.GetBytes(text), defects);
        Assert.Equal(2, doc.Cues.Count);
        MediaDefect d = Assert.Single(defects, x => x.Code == "subtitles.srt.blank-in-text");
        Assert.Equal(Encoding.UTF8.GetByteCount(text[..text.IndexOf("\nstray", StringComparison.Ordinal)]), d.Offset);
        Assert.Contains("line 4", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TolerantReaderKeepsGoing()
    {
        string text = "1\r\n00:00:01.5 --> 00:00:02,000\r\nfirst <b>bold\r\n\r\n5\r\n00:00:03,000 --> 00:00:02,000\r\nsecond</i>\r\n";
        var defects = new DefectLog();
        SubtitleDocument doc = SubtitleFormats.Srt.Read(Encoding.UTF8.GetBytes(text), defects);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("\r\n", doc.NewLine);
        Assert.Equal(new MediaTime(1500, 1000), doc.Cues[0].Start);
        Assert.Contains(defects, d => d.Code == "subtitles.srt.tag-unclosed");
        Assert.Contains(defects, d => d.Code == "subtitles.srt.tag-unmatched");
        Assert.Contains(defects, d => d.Code == "subtitles.srt.end-before-start");
        Assert.Contains(defects, d => d.Code == "subtitles.srt.index");
        Assert.Contains(doc.Cues[1].Extras, e => e is { Key: "index", Value: "5" });
        // The odd numbering survives a same-format write.
        Assert.Contains("\n5\r\n", SubtitleFormats.Srt.Write(doc).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodingsAreDetected()
    {
        string srt = "1\n00:00:01,000 --> 00:00:02,000\ncaf\u00e9 \u00fcber\n\n";
        SubtitleDocument latin = SubtitleFormats.Srt.Read(Encoding.Latin1.GetBytes(srt), out DefectLog log1);
        Assert.Equal("caf\u00e9 \u00fcber", latin.Cues[0].Text);
        Assert.Equal("windows-1252", latin.TextEncoding);
        Assert.Contains(log1, d => d.Code == "subtitles.encoding.legacy");
        Assert.Equal(Encoding.Latin1.GetBytes(srt), SubtitleFormats.Srt.Write(latin).Data);

        byte[] utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(srt)).ToArray();
        SubtitleDocument wide = SubtitleFormats.Srt.Read(utf16);
        Assert.Equal("utf-16", wide.TextEncoding);
        Assert.True(wide.ByteOrderMark);
        Assert.Equal(utf16, SubtitleFormats.Srt.Write(wide).Data);

        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(srt)).ToArray();
        SubtitleDocument withBom = SubtitleFormats.Srt.Read(bom);
        Assert.True(withBom.ByteOrderMark);
        Assert.Equal(bom, SubtitleFormats.Srt.Write(withBom).Data);

        // WebVTT is always UTF-8 without a mark, whatever the source used.
        SubtitleOutput vtt = SubtitleFormats.WebVtt.Write(latin);
        Assert.Equal(Encoding.UTF8.GetBytes(vtt.Text), vtt.Data);
    }

    [Fact]
    public void ConversionReportsWhatTheTargetCannotCarry()
    {
        SubtitleDocument ass = SubtitleFormats.Ass.Read(Bytes("sample.ass"));
        SubtitleOutput srt = SubtitleFormats.Srt.Write(ass);
        ConversionReport report = srt.Report;
        Assert.False(report.IsLossless);
        Assert.True(report.Count(SubtitleFeatures.Styles, LossKind.Dropped) > 0);
        Assert.True(report.Count(SubtitleFeatures.Voice, LossKind.Dropped) == 1);
        Assert.True(report.Count(SubtitleFeatures.WordTiming, LossKind.Dropped) == 1);
        Assert.True(report.Count(SubtitleFeatures.Extras, LossKind.Dropped) > 0);
        Assert.Contains(report.Losses, l => l.CueIndex == 1 && l.Feature == SubtitleFeatures.Voice);
        // Bold survives as markup.
        Assert.Contains("<b>world</b>", srt.Text, StringComparison.Ordinal);
        // Top alignment survives as the {\an8} extension.
        Assert.Contains("{\\an8}", srt.Text, StringComparison.Ordinal);

        SubtitleOutput lrc = SubtitleFormats.Lrc.Write(SubtitleFormats.Srt.Read(Bytes("sample.srt")));
        Assert.True(lrc.Report.Count(SubtitleFeatures.Timing, LossKind.Approximated) > 0);
        Assert.True(lrc.Report.Count(SubtitleFeatures.Bold, LossKind.Dropped) > 0);
    }

    [Fact]
    public void SrtToAssRoundsToCentiseconds()
    {
        SubtitleDocument srt = SubtitleFormats.Srt.Read(Bytes("sample.srt"));
        SubtitleOutput ass = SubtitleFormats.Ass.Write(srt);
        // 62.003 s does not fit in centiseconds.
        Assert.Contains("0:01:02.00,0:01:06.00", ass.Text, StringComparison.Ordinal);
        Assert.True(ass.Report.Count(SubtitleFeatures.Timing, LossKind.Approximated) >= 2);
        SubtitleDocument back = SubtitleFormats.Ass.Read(ass.Data);
        Assert.Equal(srt.Cues.Count, back.Cues.Count);
        Assert.Equal(srt.Cues[0].Text, back.Cues[0].Text);
        Assert.Contains(back.Cues[0].Spans, s => s.Style?.Bold == true && back.Cues[0].TextOf(s) == "world");
        Assert.Equal(SubtitleVerticalAlign.Top, back.Cues[3].Placement?.VerticalAlign);
    }

    [Theory]
    [InlineData("sample.srt")]
    [InlineData("sample.vtt")]
    [InlineData("sample.ass")]
    [InlineData("sample.ttml")]
    [InlineData("sample.sbv")]
    [InlineData("sample.lrc")]
    public void EveryPairConvertsAndKeepsTextAndTimes(string file)
    {
        SubtitleDocument source = SubtitleFormats.Read(Bytes(file));
        foreach (SubtitleFormat target in SubtitleFormats.All)
        {
            SubtitleOutput output = target.Write(source);
            SubtitleDocument back = target.Read(output.Data);
            if (target.Name == "lrc")
            {
                // One line per cue: multi-line cues come back as several.
                Assert.True(back.Cues.Count >= source.Cues.Count);
                continue;
            }

            Assert.Equal(source.Cues.Count, back.Cues.Count);
            for (int i = 0; i < source.Cues.Count; i++)
            {
                if (target.Name != "lrc")
                {
                    Assert.True(Math.Abs((back.Cues[i].End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks) - source.Cues[i].End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks) <= 5, $"{file}->{target.Name} cue {i} end");
                }

                Assert.True(Math.Abs(back.Cues[i].Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks - source.Cues[i].Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks) <= 5, $"{file}->{target.Name} cue {i} start");
                if (target.Name != "lrc")
                {
                    Assert.Equal(source.Cues[i].Text, back.Cues[i].Text);
                }
            }
        }
    }

    [Fact]
    public void ShiftMovesCuesAndDropsWhatFallsBeforeZero()
    {
        SubtitleDocument doc = SubtitleFormats.Srt.Read(Bytes("sample.srt"));
        AlignedSubtitles later = SubtitleAlignment.Shift(doc, new MediaTime(1500, 1000));
        Assert.Equal(new MediaTime(2500, 1000), later.Document.Cues[0].Start);
        Assert.True(later.Report.IsLossless);
        AlignedSubtitles earlier = SubtitleAlignment.Shift(doc, new MediaTime(-4200, 1000));
        Assert.Equal(doc.Cues.Count - 1, earlier.Document.Cues.Count);
        Assert.Contains(earlier.Report.Losses, l => l.Kind == LossKind.Dropped && l.CueIndex == 0);
        Assert.Contains(earlier.Report.Losses, l => l.Kind == LossKind.Approximated && l.CueIndex == 1);
        Assert.Equal(MediaTime.Zero, earlier.Document.Cues[0].Start);
        Assert.Equal(new MediaTime(2050, 1000), earlier.Document.Cues[0].End);
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        string text = string.Concat(Enumerable.Range(1, 50).Select(i => $"{i}\n00:00:{i % 60:00},000 --> 00:00:{i % 60:00},500\nx\n\n"));
        Assert.Throws<MediaLimitExceededException>(() => SubtitleFormats.Srt.ReadText(text, null, new MediaLimits { MaxCues = 10 }));
        Assert.Throws<MediaLimitExceededException>(() => SubtitleFormats.Srt.ReadText(text, null, new MediaLimits { MaxAllocationBytes = 100 }));
        string deep = "<tt xmlns=\"http://www.w3.org/ns/ttml\">" + string.Concat(Enumerable.Repeat("<div>", 200)) + string.Concat(Enumerable.Repeat("</div>", 200)) + "</tt>";
        Assert.Throws<MediaLimitExceededException>(() => SubtitleFormats.Ttml.ReadText(deep));
    }

    [Fact]
    public void HostileTtmlIsRejectedWithTypedErrors()
    {
        string bomb = "<?xml version=\"1.0\"?><!DOCTYPE tt [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"0s\" end=\"1s\">&b;</p></div></body></tt>";
        var defects = new DefectLog();
        Assert.Throws<MediaFormatException>(() => SubtitleFormats.Ttml.ReadText(bomb, defects));
        Assert.True(defects.HasErrors);
        Assert.False(SubtitleFormats.Ttml.TryRead(Encoding.UTF8.GetBytes("<tt><p></tt>"), out _));
        Assert.Throws<UnsupportedMediaException>(() => SubtitleFormats.Read("just some text"u8));
    }

    [Fact]
    public void WordTimingsFromRecognitionRoundTripThroughVttAndTtml()
    {
        // A consumer building karaoke spans from recognised words.
        var cue = new SubtitleCue
        {
            Start = new MediaTime(1000, 1000),
            End = new MediaTime(3000, 1000),
            Text = "hello brave world",
            Spans = SubtitleList.Of(
                new SubtitleSpan(0, 6) { WordStart = new MediaTime(1000, 1000), WordEnd = new MediaTime(1800, 1000) },
                new SubtitleSpan(6, 6) { WordStart = new MediaTime(1800, 1000), WordEnd = new MediaTime(2200, 1000) },
                new SubtitleSpan(12, 5) { WordStart = new MediaTime(2200, 1000), WordEnd = new MediaTime(3000, 1000) }),
        };
        var doc = new SubtitleDocument { Cues = SubtitleList.Of(cue) };
        foreach (SubtitleFormat f in new[] { SubtitleFormats.WebVtt, SubtitleFormats.Ttml, SubtitleFormats.Ass })
        {
            SubtitleDocument back = f.Read(f.Write(doc).Data);
            Assert.Equal(cue.Text, back.Cues[0].Text);
            Assert.Equal(cue.Spans.Select(s => s.WordStart), back.Cues[0].Spans.Select(s => s.WordStart));
        }
    }

    [Fact]
    public void CaptionGridCuesCarryRowAndColumn()
    {
        var doc = new SubtitleDocument
        {
            Cues = SubtitleList.Of(new SubtitleCue
            {
                Start = new MediaTime(0, 1),
                End = new MediaTime(2, 1),
                Text = "ROLL UP",
                Mode = CaptionMode.RollUp,
                Placement = new CuePlacement { Row = 15, Column = 4 },
                Spans = SubtitleList.Of(new SubtitleSpan(0, 4) { Style = new TextStyle { Italic = true, Color = new SubtitleColor(255, 255, 0) } }),
            }),
        };
        SubtitleOutput vtt = SubtitleFormats.WebVtt.Write(doc);
        Assert.True(vtt.Report.Count(SubtitleFeatures.CaptionMode, LossKind.Dropped) == 1);
        Assert.True(vtt.Report.Count(SubtitleFeatures.Position, LossKind.Approximated) >= 1);
        Assert.Contains("<i><c.yellow>ROLL</c></i>", vtt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PositionAtTheRightEdgeWritesAsTtml()
    {
        var doc = new SubtitleDocument
        {
            Cues = SubtitleList.Of(new SubtitleCue
            {
                Start = new MediaTime(0, 1),
                End = new MediaTime(2, 1),
                Text = "edge",
                Placement = new CuePlacement { Position = 100, Size = 50 },
            }),
        };
        SubtitleOutput ttml = SubtitleFormats.Ttml.Write(doc);
        Assert.Single(SubtitleFormats.Ttml.Read(ttml.Data).Cues);
    }

    [Theory]
    [InlineData("fuzz/subs-webvtt/regions-position-100.vtt")]
    [InlineData("fuzz/subs-ass/position-100.ass")]
    public void FuzzReprosWriteThroughEveryFormat(string file)
    {
        byte[] input = File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", file));
        SubtitleDocument doc = SubtitleFormats.Read(input);
        foreach (SubtitleFormat target in SubtitleFormats.All)
        {
            Assert.NotEmpty(target.Write(doc).Data);
        }
    }

    [Fact]
    public void ImageCueToTextFormatReportsTheImageDropped()
    {
        var image = new SubtitleImage(10, 20, 2, 2, 8, new byte[16]) { DisplayWidth = 1920, DisplayHeight = 1080 };
        var doc = new SubtitleDocument
        {
            Cues = SubtitleList.Of(new SubtitleCue { Start = new MediaTime(0, 1), End = new MediaTime(1, 1), Images = SubtitleList.Of(image) }),
        };
        Assert.Equal(image, doc.Cues[0].Images[0]);
        SubtitleOutput srt = SubtitleFormats.Srt.Write(doc);
        Assert.True(srt.Report.Count(SubtitleFeatures.Images, LossKind.Dropped) == 1);
    }

    [Fact]
    public void RegisteredFormatsAreFoundByName()
    {
        Assert.Same(SubtitleFormats.WebVtt, SubtitleFormats.Find("vtt"));
        Assert.Same(SubtitleFormats.Ass, SubtitleFormats.Find(".SSA"));
        Assert.Same(SubtitleFormats.Ttml, SubtitleFormats.Find("dfxp"));
        Assert.Null(SubtitleFormats.Find("scc-not-yet"));
    }
}

internal static class SubtitleFormatTestExtensions
{
    internal static SubtitleDocument Read(this SubtitleFormat format, byte[] data, out DefectLog log)
    {
        log = new DefectLog();
        return format.Read(data, log);
    }
}

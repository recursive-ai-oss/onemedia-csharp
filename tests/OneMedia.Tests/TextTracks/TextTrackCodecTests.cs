using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Text tracks to and from cues: every encoder against its decoder, and files that carry text tracks through remux.</summary>
public class TextTrackCodecTests
{
    private static string Data(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", "text", .. parts]);

    private static string Plain(string s) => string.Join(' ', s.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static (long Start, long End, string Text)[] Summary(IEnumerable<SubtitleCue> cues) =>
        [.. cues.Select(c => (c.Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks, c.End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks, Plain(c.Text)))];

    public static TheoryData<string, TextTrackFormat> Combinations()
    {
        TheoryData<string, TextTrackFormat> data = new();
        foreach (string file in new[] { "cues.srt", "cues.vtt", "cues.ass" })
        {
            foreach (TextTrackFormat f in new[] { TextTrackFormat.Tx3g, TextTrackFormat.WebVttIso, TextTrackFormat.XmlSubtitleIso, TextTrackFormat.SubRip, TextTrackFormat.Ass, TextTrackFormat.WebVtt, TextTrackFormat.WebmWebVtt })
            {
                data.Add(file, f);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EncodedTrackDecodesToTheSameCues(string file, TextTrackFormat format)
    {
        SubtitleDocument doc = SubtitleFormats.Sniff(File.ReadAllBytes(Data(file)))!.Read(File.ReadAllBytes(Data(file)));
        TextTrackEncoding enc = TextTrackEncoder.Encode(doc, format);
        Assert.Equal(format, TextTracks.FormatOf(enc.Track));
        TextTrackDecoder dec = new(enc.Track);
        foreach (TextTrackPacket p in enc.Packets)
        {
            dec.Add(p.Info, p.Data, p.Additional);
        }

        SubtitleDocument back = dec.Finish();
        Assert.NotEmpty(back.Cues);
        // A tx3g sample shows one block of text, so a cue that overlaps another is merged into it (and reported); everything before the overlap must match.
        long limit = format == TextTrackFormat.Tx3g ? 5000 : long.MaxValue;
        Assert.Equal(Summary(doc.Cues).Where(c => c.Start < limit), Summary(back.Cues).Where(c => c.Start < limit).Take(Summary(doc.Cues).Count(c => c.Start < limit)));
        if (format != TextTrackFormat.Tx3g)
        {
            Assert.Equal(Summary(doc.Cues), Summary(back.Cues));
        }
    }

    public static TheoryData<string, RemuxFormat> Remuxes() => new()
    {
        { "mb_tx3g.mp4", RemuxFormat.Matroska },
        { "ff_tx3g.mp4", RemuxFormat.Matroska },
        { "mb_wvtt.mp4", RemuxFormat.Matroska },
        { "mb_stpp.mp4", RemuxFormat.Matroska },
        { "ff_srt.mkv", RemuxFormat.Mp4 },
        { "mm_srt.mkv", RemuxFormat.Mp4 },
        { "ff_ass.mkv", RemuxFormat.Mp4 },
        { "mm_vtt.mkv", RemuxFormat.Mp4 },
        { "ff_vtt.webm", RemuxFormat.Mp4 },
        { "ff_vtt.mkv", RemuxFormat.Matroska },
        { "mb_tx3g.mp4", RemuxFormat.Mp4 },
        { "mb_wvtt.mp4", RemuxFormat.Mp4 },
        { "mb_stpp.mp4", RemuxFormat.Mp4 },
    };

    [Theory]
    [MemberData(nameof(Remuxes))]
    public void RemuxKeepsTheCues(string name, RemuxFormat target)
    {
        string input = Data("oracle", name);
        string output = Path.Combine(Path.GetTempPath(), $"u49-{Guid.NewGuid():N}." + (target == RemuxFormat.Matroska ? "mkv" : "mp4"));
        try
        {
            RemuxReport report = Remuxer.Remux(input, output, target);
            using MediaFile before = Media.Open(input);
            using MediaFile after = Media.Open(output);
            Assert.Equal(before.Subtitles.Count, after.Subtitles.Count);
            Assert.NotEmpty(after.Subtitles);
            for (int i = 0; i < before.Subtitles.Count; i++)
            {
                var want = Summary(before.Subtitles[i].Cues);
                var got = Summary(after.Subtitles[i].Cues);
                if (before.Subtitles[i].Format != TextTrackFormat.Tx3g && after.Subtitles[i].Format == TextTrackFormat.Tx3g)
                {
                    // Converted into timed text, which cannot show two cues at once: the overlap is merged and reported.
                    bool overlaps = want.Where((c, k) => k > 0 && c.Start < want[k - 1].End).Any();
                    if (overlaps)
                    {
                        want = [.. want.Where(c => c.Start < 5000)];
                    }

                    got = [.. got.Take(want.Length)];
                    Assert.True(!overlaps || report.Losses.Any(l => l.Detail.Contains("overlap", StringComparison.OrdinalIgnoreCase) || l.Subject.Contains("overlap", StringComparison.OrdinalIgnoreCase)));
                }

                Assert.Equal(want, got);
            }

            _ = report;
        }
        finally
        {
            File.Delete(output);
        }
    }
}

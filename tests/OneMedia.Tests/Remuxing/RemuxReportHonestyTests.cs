using System.Globalization;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests.Remuxing;

// A remux report must describe the output that was written: every input track is either in Tracks (and exists in the output with the codec the
// report names) or is listed as lost, and no Changes entry claims a conversion that did not happen.
// testdata/text/oracle/ff_many.* carry several text tracks of one kind (written by tools/gen-texttrack-fixtures.sh).
public class RemuxReportHonestyTests
{
    private static string Oracle(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "text", "oracle", name);

    public static TheoryData<string, RemuxFormat> Cases() => new()
    {
        { "ff_many.mkv", RemuxFormat.Mp4 },
        { "ff_many.mkv", RemuxFormat.Mov },
        { "ff_many.mp4", RemuxFormat.Matroska },
        { "ff_many.mp4", RemuxFormat.Mov },
        { "ff_two.mkv", RemuxFormat.Mp4 },
        { "ff_srt.mkv", RemuxFormat.Mp4 },
        { "ff_tx3g.mp4", RemuxFormat.Matroska },
        { "mb_wvtt.mp4", RemuxFormat.Matroska },
        { "mb_stpp.mp4", RemuxFormat.Matroska },
        { "mm_vtt.mkv", RemuxFormat.Mp4 },
        { "ff_vtt.mkv", RemuxFormat.Matroska },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReportMatchesTheOutputTracks(string name, RemuxFormat target)
    {
        string input = Oracle(name);
        string output = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}" + (target == RemuxFormat.Matroska ? ".mkv" : target == RemuxFormat.Mov ? ".mov" : ".mp4"));
        try
        {
            RemuxReport report = Remuxer.Remux(input, output, target);
            using MediaFile before = Media.Open(input);
            using MediaFile after = Media.Open(output);

            Assert.Equal(report.Tracks.Count, after.Tracks.Count);
            HashSet<int> carried = [.. report.Tracks.Select(t => t.InputTrack)];
            HashSet<int> lost = [.. report.Losses.Where(l => l.Kind == RecursiveAI.OneMedia.Remuxing.LossKind.Track).Select(l => l.InputTrack)];
            lost.UnionWith(report.LostMediaTracks);
            foreach (MediaTrack t in before.Tracks)
            {
                Assert.True(carried.Contains(t.Index) ^ lost.Contains(t.Index), $"{name}: input track {t.Index} ({t.Codec.Name}) must be carried or reported lost, not both or neither");
            }

            // Each conversion the report names happened to a track that is in the output, in that format.
            foreach (string change in report.Changes)
            {
                const string prefix = "Input track ";
                if (!change.StartsWith(prefix, StringComparison.Ordinal) || !change.Contains(" was converted to ", StringComparison.Ordinal))
                {
                    continue;
                }

                int end = change.IndexOf(':', StringComparison.Ordinal);
                int index = int.Parse(change[prefix.Length..end], CultureInfo.InvariantCulture);
                RemuxTrackReport mapped = Assert.Single(report.Tracks, t => t.InputTrack == index);
                MediaTrack outTrack = after.Tracks[mapped.OutputTrack];
                TextTrackFormat format = TextTracks.FormatOf(outTrack);
                string wanted = format switch
                {
                    TextTrackFormat.Tx3g => "tx3g",
                    TextTrackFormat.WebVttIso => "wvtt",
                    TextTrackFormat.XmlSubtitleIso => "stpp",
                    TextTrackFormat.SubRip => "S_TEXT/UTF8",
                    TextTrackFormat.Ass => "S_TEXT/ASS",
                    TextTrackFormat.WebVtt => "S_TEXT/WEBVTT",
                    TextTrackFormat.QuickTimeText => "QuickTime text",
                    _ => "?",
                };
                Assert.Contains(" converted to " + wanted, change.Replace(" was converted to", " converted to", StringComparison.Ordinal), StringComparison.Ordinal);
            }

            // The subtitle tracks that were converted are all there with their cues.
            Assert.Equal(before.Subtitles.Count - SubtitleLosses(report, before), after.Subtitles.Count);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static int SubtitleLosses(RemuxReport report, MediaFile before)
    {
        HashSet<int> text = [.. before.Tracks.Where(t => TextTracks.FormatOf(t) != TextTrackFormat.Unknown).Select(t => t.Index)];
        return report.Losses.Where(l => l.Kind == RecursiveAI.OneMedia.Remuxing.LossKind.Track && text.Contains(l.InputTrack)).Select(l => l.InputTrack).Distinct().Count();
    }

    [Theory]
    [InlineData("ff_many.mkv", RemuxFormat.Mp4)]
    [InlineData("ff_many.mkv", RemuxFormat.Mov)]
    [InlineData("ff_many.mp4", RemuxFormat.Matroska)]
    public void SeveralTextTracksAreAllConverted(string name, RemuxFormat target)
    {
        string input = Oracle(name);
        string output = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}" + (target == RemuxFormat.Matroska ? ".mkv" : target == RemuxFormat.Mov ? ".mov" : ".mp4"));
        try
        {
            RemuxReport report = Remuxer.Remux(input, output, target);
            using MediaFile before = Media.Open(input);
            using MediaFile after = Media.Open(output);
            int textIn = before.Tracks.Count(t => t.Kind == TrackKind.Subtitle);
            Assert.True(textIn >= 3);
            Assert.Equal(textIn, after.Tracks.Count(t => t.Kind == TrackKind.Subtitle));
            Assert.Empty(report.LostMediaTracks);
            Assert.DoesNotContain(report.Losses, l => l.Kind == RecursiveAI.OneMedia.Remuxing.LossKind.Track);
            Assert.Equal(before.Subtitles.Count, after.Subtitles.Count);
            for (int i = 0; i < before.Subtitles.Count; i++)
            {
                Assert.Equal(before.Subtitles[i].Language, after.Subtitles[i].Language);
            }
        }
        finally
        {
            File.Delete(output);
        }
    }
}

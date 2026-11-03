using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tests.Remuxing;

public sealed class SiteFixTests
{
    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", .. parts]);

    [Fact]
    public void MediaRemuxTakesTheLayersOptionsInOneLine()
    {
        string output = Path.Combine(Path.GetTempPath(), "onemedia-remux-options-" + Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            RemuxReport report = Media.Remux(Fixture("repair", "tiny.mp4"), output, new RemuxOptions { Select = t => t.Kind == TrackKind.Audio });
            using MediaFile written = Media.Open(output);
            Assert.NotNull(written.Audio);
            Assert.Null(written.Video);
            Assert.Single(written.Tracks);
            Assert.Empty(report.LostMediaTracks); // a track the caller chose not to select is not a loss of media
        }
        finally
        {
            File.Delete(output);
        }
    }

    // ffprobe 8.0.1 -show_entries stream=bit_rate on the same files: the audio average the sample entry declares.
    [Theory]
    [InlineData("audio/aac.m4a", 26931L)]
    [InlineData("isobmff/bframes_aac.mp4", 25857L)]
    public void AnIsoAudioTrackReportsTheAverageBitRateItsSampleEntryDeclares(string file, long expected)
    {
        using MediaFile media = Media.Open(Fixture(file.Split('/')));
        Assert.Equal(expected, media.Audio!.BitRate);
    }

    [Fact]
    public void AMovieHeaderTimeIsAUtcCandidate()
    {
        // docs/migration/metadataextractor.md says so: the specification defines mvhd times as UTC, so the candidate is Utc, not Unknown.
        using MediaFile media = Media.Open(Fixture("datewrite", "plain.mp4"));
        FactCandidate<MediaDateTime> header = Assert.Single(media.CaptureTime.Candidates, c => c.Source == "mvhd");
        Assert.Equal(TimeZoneCertainty.Utc, header.Certainty);
    }

    [Fact]
    public void AnAudioOnlyFileIsOneRemuxLineWithASelect()
    {
        // The ffmpeg migration page shows this line for "extract the audio track".
        string output = Path.Combine(Path.GetTempPath(), "onemedia-audio-only-" + Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            Media.Remux(Fixture("repair", "tiny.mp4"), output, new RemuxOptions { Select = t => t.Kind == TrackKind.Audio });
            using MediaFile written = Media.Open(output);
            Assert.Equal(TrackKind.Audio, Assert.Single(written.Tracks).Kind);
        }
        finally
        {
            File.Delete(output);
        }
    }
}

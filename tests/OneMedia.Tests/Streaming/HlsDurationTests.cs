using RecursiveAI.OneMedia.Streaming;
using RecursiveAI.OneMedia.Streaming.Sources;

namespace RecursiveAI.OneMedia.Tests.Streaming;

public sealed class HlsDurationTests
{
    private static readonly Uri Location = new("file:///stream/index.m3u8");

    private static PresentationPlan Read(string text) => HlsReader.Read(text, Location, MediaLimits.Default, new DefectLog());

    [Fact]
    public void FractionalSegmentDurationsAreSummedWithoutRounding()
    {
        // RFC 8216 §4.3.2.1: EXTINF is a decimal number. Each of these used to be rounded to a whole second as it was added, so this stream read 51 s.
        string text = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:11\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXTINF:10.4,\na0.ts\n#EXTINF:10.4,\na1.ts\n#EXTINF:10.4,\na2.ts\n#EXTINF:10.4,\na3.ts\n#EXTINF:10.4,\na4.ts\n#EXTINF:0.008,\na5.ts\n#EXT-X-ENDLIST\n";
        PresentationPlan plan = Read(text);
        Assert.True(plan.Duration.IsKnown);
        Assert.Equal(52_008, plan.Duration.Rescale(1000, MediaRounding.Floor).Ticks);
        Assert.Equal(plan.Duration, new MediaTime(52_008, 1000));
    }

    [Fact]
    public void DurationsOfDifferentPrecisionAddUpExactly()
    {
        // 6 + 6.006 + 0.0416667 seconds: three timescales (1, 1000, 10^7), one exact total of 12.0476667 s.
        string text = "#EXTM3U\n#EXT-X-TARGETDURATION:7\n#EXTINF:6,\na.ts\n#EXTINF:6.006,\nb.ts\n#EXTINF:0.0416667,\nc.ts\n#EXT-X-ENDLIST\n";
        PresentationPlan plan = Read(text);
        Assert.Equal(new MediaTime(120_476_667, 10_000_000), plan.Duration);
    }

    [Fact]
    public void ThousandsOfFractionalSegmentsDoNotDrift()
    {
        // A double would accumulate error over this many additions; decimal ticks do not.
        System.Text.StringBuilder sb = new("#EXTM3U\n#EXT-X-TARGETDURATION:3\n");
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("#EXTINF:2.002,\ns").Append(i).Append(".ts\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        Assert.Equal(new MediaTime(10_010_000, 1000), Read(sb.ToString()).Duration);
    }

    [Fact]
    public void ASegmentWithoutADurationLeavesTheTotalUnknownAndALivePlaylistHasNone()
    {
        Assert.False(Read("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:2.5,\na.ts\n").Duration.IsKnown);
    }

    [Fact]
    public void TheVariantBandwidthIsTheAverageWhenThePlaylistGivesOneAndThePeakOtherwise()
    {
        string text = "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,AVERAGE-BANDWIDTH=1500000,RESOLUTION=1280x720\nhd.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nsd.m3u8\n";
        PresentationPlan plan = Read(text);
        Assert.Equal(1_500_000, plan.Variants[0].Bandwidth);
        Assert.Equal(800_000, plan.Variants[1].Bandwidth);
    }
}

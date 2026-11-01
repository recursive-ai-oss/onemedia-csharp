using RecursiveAI.OneMedia.AudioStreams;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests.AudioStreams;

/// <summary>The committed fixtures (written by ffmpeg and tools/audio-es/make_tags.py) through the registry, the way a caller meets them.</summary>
public class AudioStreamFixtureTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", name);

    [Theory]
    [InlineData("lame-info.mp3", "mp3", "mpegaudio")]
    [InlineData("plain.mp3", "mp3", "mpegaudio")]
    [InlineData("mpeg2-mono.mp3", "mp3", "mpegaudio")]
    [InlineData("plain.mp2", "mp2", "mpegaudio")]
    [InlineData("lc.aac", "aac", "adts")]
    [InlineData("ac3.ac3", "ac3", "ac3")]
    [InlineData("eac3.eac3", "eac3", "ac3")]
    [InlineData("nb.amr", "amr-nb", "amr")]
    [InlineData("wb.awb", "amr-wb", "amr")]
    public void EveryFixtureOpensWithItsCodecAndPackets(string name, string codec, string format)
    {
        using MediaFile media = Media.Open(Path(name));
        Assert.Equal(format, media.Demuxer!.Format.Name);
        Assert.Equal(codec, media.Tracks[0].Codec.Name);
        Assert.NotNull(media.Duration);
        int packets = 0;
        long end = 0;
        foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
        {
            using (p)
            {
                packets++;
                end = p.Info.SourceRange.End;
            }
        }

        Assert.True(packets > 5);
        Assert.Empty(media.Demuxer.Defects);
    }

    [Fact]
    public void LameFixtureCarriesGaplessValues()
    {
        using MediaFile media = Media.Open(Path("lame-info.mp3"));
        MediaTrack t = media.Tracks[0];
        Assert.True(t.Edit.Priming.Ticks > 0);
        Assert.False(string.IsNullOrEmpty(((AudioStreamDemuxer)media.Demuxer!).Encoder)); // the encoder named in the LAME extension (libavcodec writes its own version string there)
    }

    [Fact]
    public void EveryTaggedVariantHasTheAudioOfThePlainFile()
    {
        int expected = CountPackets(Path("plain.mp3"));
        string[] files = Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", "tagged"), "*.mp3");
        Assert.True(files.Length >= 30);
        foreach (string file in files)
        {
            Assert.True(CountPackets(file) == expected, $"{System.IO.Path.GetFileName(file)}: packet count");
            using MediaFile media = Media.Open(file);
            Assert.NotEmpty(media.Metadata.NativeTags);
        }
    }

    [Fact]
    public void ReadingTagsNeverNeedsTheMediaBytes()
    {
        foreach (string file in Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "audio-es", "tagged"), "*.mp3"))
        {
            using MediaFile media = Media.Open(file);
            foreach (NativeTag t in media.Metadata.NativeTags)
            {
                _ = t.Text;
            }

            foreach (CommonField f in Enum.GetValues<CommonField>())
            {
                _ = media.Tags.Values(f);
            }
        }
    }

    private static int CountPackets(string file)
    {
        using MediaFile media = Media.Open(file);
        int n = 0;
        foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
        {
            p.Dispose();
            n++;
        }

        return n;
    }
}

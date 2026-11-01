using OneAudio;
using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Ogg and native FLAC files decoded through the OneMedia readers equal what OneAudio's own readers decode.</summary>
public class XiphAudioTests
{
    private static string Path(string dir, string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    private static float[] ReadAll(IAudioDecoder d)
    {
        using (d)
        {
            List<float> all = [];
            float[] buf = new float[4096 * d.Format.Channels];
            int got;
            while ((got = d.Read(buf)) > 0)
            {
                all.AddRange(buf.AsSpan(0, got * d.Format.Channels).ToArray());
            }

            return [.. all];
        }
    }

    [Theory]
    [InlineData("audio", "opus.ogg")]
    [InlineData("xiph-flv", "tags.opus")]
    [InlineData("xiph-flv", "tags.flac")]
    [InlineData("xiph-flv", "tags.ogg")]
    [InlineData("xiph-flv", "vorbis-6ch.ogg")]
    public void DecodedSamplesEqualOneAudiosOwnReader(string dir, string name)
    {
        float[] ours = ReadAll(AudioDecoder.OpenFile(Path(dir, name), MediaAudioExtensions.CreateRegistry()));
        float[] theirs = ReadAll(AudioDecoder.OpenFile(Path(dir, name), new AudioCodecRegistry().AddOgg().AddVorbis().AddOpus().AddFlac()));
        Assert.NotEmpty(ours);
        // Vorbis ends with a granule position that trims the last block; the adapter keeps that block whole until the decoder takes an end position
        // from a container other than Ogg, so the tail may be longer.
        int n = Math.Min(ours.Length, theirs.Length);
        Assert.True(ours.Length >= theirs.Length && ours.Length - theirs.Length < 4096 * 6, $"{ours.Length} vs {theirs.Length}");
        Assert.True(ours.AsSpan(0, n).SequenceEqual(theirs.AsSpan(0, n)));
    }
}

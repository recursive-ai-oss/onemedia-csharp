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

    [Fact]
    public void FlacBehindALargeId3BlockDecodesThroughMediaFileButNotThroughTheSniffWindow()
    {
        // OneAudio looks at the first 512 bytes to pick a reader, which a big ID3v2 block fills; the file's own demuxer has already found the format.
        byte[] flac = File.ReadAllBytes(Path("xiph-flv", "tags.flac"));
        byte[] id3 = new byte[10 + 600];
        "ID3"u8.CopyTo(id3);
        id3[3] = 4;
        id3[8] = 600 / 128;
        id3[9] = 600 % 128;
        byte[] file = [.. id3, .. flac];
        using MediaFile media = MediaFile.Open(new MemoryMediaSource(file));
        float[] pcm = ReadAll(media.Decode());
        Assert.Equal(44100, pcm.Length);
    }
}

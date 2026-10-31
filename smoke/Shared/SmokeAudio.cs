using OneAudio;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Tests.IsoBmffSupport;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>The happy path on in-memory files and the audio adapter decoding two tiny files, under NativeAOT and in the browser runtime.</summary>
internal static class SmokeAudio
{
    public static void Run(Action<string, bool> check)
    {
        Mp4Builder.TrackSpec video = new() { SampleCount = 12, IsSync = i => i % 6 == 0, SamplesPerChunk = 4 };
        Mp4Builder.TrackSpec sound = new() { Id = 2, Handler = "soun", Timescale = 44100, SampleCount = 8, DurationOf = _ => 1024, SizeOf = _ => 30 };
        Mp4Builder.Layout layout = Mp4Builder.Build([video, sound], moovFirst: true);
        using (MediaFile mp4 = Media.Open(layout.ToArray([video, sound])))
        {
            check("Media.Open mp4", mp4.Format == MediaFormatId.IsoBmff && mp4.Tracks.Count == 2 && mp4.Video is not null && mp4.Audio is not null && mp4.Duration > TimeSpan.Zero);
            int n = 0;
            foreach (OwnedPacket p in mp4.Packets(mp4.Audio!))
            {
                using (p)
                {
                    n++;
                }
            }

            check("Media.Open mp4 packets", n == 8);
        }

        byte[] webm = Resource("opus.webm");
        using (MediaFile media = Media.Open(webm))
        {
            check("Media.Open webm", media.Format == MediaFormatId.Matroska && media.Audio?.Codec == CodecId.Opus);
        }

        check("decode opus in webm", DecodedFrames(webm) > 19000);
        check("decode aac in m4a", DecodedFrames(Resource("aac.m4a")) == 6400);
    }

    private static long DecodedFrames(byte[] file)
    {
        using MemoryStream stream = new(file);
        using IAudioDecoder decoder = AudioDecoder.Open(stream, MediaAudioExtensions.CreateRegistry(), leaveOpen: true);
        float[] buf = new float[4096 * decoder.Format.Channels];
        long frames = 0;
        int got;
        while ((got = decoder.Read(buf)) > 0)
        {
            frames += got;
        }

        return frames;
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(SmokeAudio).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

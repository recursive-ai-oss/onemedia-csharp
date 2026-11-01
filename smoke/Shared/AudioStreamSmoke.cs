using System.Text;
using OneAudio;
using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>The audio elementary-stream readers and their tag layers under NativeAOT and in the browser runtime: sync, forward-only, ranged, tags, and decoding through the adapter.</summary>
internal static class AudioStreamSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        foreach ((string name, string codec) in new[] { ("lame-info.mp3", "mp3"), ("lc.aac", "aac"), ("ac3.ac3", "ac3"), ("nb.amr", "amr-nb"), ("id3v24-basic.mp3", "mp3") })
        {
            byte[] file = Resource(name);
            int sync = 0;
            using (MediaFile media = Media.Open(file))
            {
                foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
                {
                    using (p)
                    {
                        sync++;
                    }
                }

                check($"audio es: {name} codec", media.Tracks[0].Codec.Name == codec && sync > 5);
            }

            int async = 0;
            await using (MediaFile ranged = await Media.OpenAsync(file.Length, (offset, buffer, _) =>
            {
                int n = (int)Math.Min(buffer.Length, file.Length - offset);
                file.AsMemory((int)offset, n).CopyTo(buffer);
                return new ValueTask<int>(n);
            }))
            {
                await foreach (OwnedPacket p in ranged.PacketsAsync(ranged.Tracks[0]))
                {
                    using (p)
                    {
                        async++;
                    }
                }
            }

            check($"audio es: {name} ranged packets equal", async == sync);
            MediaFormats.Default.TrySniff(file, out FormatMatch match);
            using IDemuxer forward = ((IMediaFormat)match.Format).Open(new StreamForwardSource(new MemoryStream(file)), MediaOpenOptions.Default);
            int fwd = 0;
            while (forward.TryReadPacket(out _))
            {
                fwd++;
            }

            check($"audio es: {name} forward-only packets equal", fwd == sync);
        }

        using (MediaFile tagged = Media.Open(Resource("id3v24-basic.mp3")))
        {
            check("audio es: id3v2 tags", tagged.Tags.Title is { Length: > 0 } && tagged.Metadata.NativeTags.Count > 3);
        }

        check("audio es: decode mp3 with LAME gapless", Decoded("lame-info.mp3") == 13230);
        check("audio es: decode adts aac", Decoded("lc.aac") == 14336);
        check("audio es: decode amr", Decoded("nb.amr") == 6400);
    }

    private static long Decoded(string name)
    {
        using MediaFile media = Media.Open(Resource(name));
        using IAudioDecoder decoder = media.Decode();
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
        using Stream s = typeof(AudioStreamSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

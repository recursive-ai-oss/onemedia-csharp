using RecursiveAI.OneMedia.Flac;
using RecursiveAI.OneMedia.Flv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Ogg;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads one small Ogg Opus, Ogg Vorbis, FLAC and FLV file (embedded resources) through the readers, their tags, and the ranged and forward-only paths.
/// Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class XiphFlvSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        MediaFormatRegistry registry = new();
        registry.Add(new OggFormat());
        registry.Add(new FlacFormat());
        registry.Add(new FlvFormat());
        foreach ((string name, MediaFormatId format, string codec, IMediaFormat opener) in new (string, MediaFormatId, string, IMediaFormat)[]
        {
            ("opus.ogg", MediaFormatId.Ogg, "opus", new OggFormat()),
            ("vorbis.ogg", MediaFormatId.Ogg, "vorbis", new OggFormat()),
            ("tags.flac", MediaFormatId.Flac, "flac", new FlacFormat()),
            ("tiny.flv", MediaFormatId.Flv, "flv1", new FlvFormat()),
        })
        {
            byte[] file = Resource(name);
            int expected = Count(registry.Open(new MemoryMediaSource(file)));
            using (IDemuxer d = registry.Open(new MemoryMediaSource(file)))
            {
                bool hasCodec = false;
                foreach (MediaTrack t in d.Tracks)
                {
                    hasCodec |= t.Codec.Name == codec;
                }

                check($"xiph/flv: {name} sniffs and opens", d.Format == format && hasCodec && expected > 0);
                check($"xiph/flv: {name} tags", ((IMetadataProvider)d).ReadMetadata().Facts.Duration.HasValue || d.Duration.IsKnown);
            }

            using (IDemuxer f = opener.Open(new StreamForwardSource(new MemoryStream(file)), MediaOpenOptions.Default))
            {
                check($"xiph/flv: {name} forward-only", Count(f) == expected);
            }

            await using RangedMediaSource ranged = new(file.Length, (offset, buffer, _) =>
            {
                int n = (int)Math.Min(buffer.Length, file.Length - offset);
                file.AsMemory((int)offset, n).CopyTo(buffer);
                return new ValueTask<int>(n);
            });
            await using IAsyncDemuxer a = await opener.OpenAsync(ranged, MediaOpenOptions.Default);
            int packets = 0;
            while (await a.ReadPacketAsync())
            {
                packets++;
            }

            check($"xiph/flv: {name} ranged", packets == expected);
        }

        using (IDemuxer d = registry.Open(new MemoryMediaSource(Resource("tags.flac"))))
        {
            check("xiph/flv: flac comments", ((IMetadataProvider)d).ReadMetadata().Tags.Title == "Tagged Title");
        }
    }

    private static int Count(IDemuxer d)
    {
        using (d)
        {
            int n = 0;
            while (d.TryReadPacket(out _))
            {
                n++;
            }

            return n;
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(XiphFlvSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

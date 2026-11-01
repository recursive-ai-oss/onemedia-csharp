namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Opens a tiny ffmpeg-written WMA through the happy path and the ASF demuxers (seekable, forward-only, ranged) under NativeAOT and in the browser runtime.</summary>
internal static class AsfSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        byte[] file = Resource("wmav2-tagged.wma");
        using (MediaFile media = Media.Open(file))
        {
            check("asf: Media.Open", media.Format == MediaFormatId.Asf && media.Tracks.Count == 1 && media.Tracks[0].Codec == CodecId.WmaV2);
            check("asf: tags", !string.IsNullOrEmpty(media.Tags.Title));
            int n = 0;
            foreach (OwnedPacket p in media.Packets(media.Tracks[0]))
            {
                using (p)
                {
                    n++;
                }
            }

            check("asf: packets", n > 10);
        }

        using (RecursiveAI.OneMedia.Asf.AsfDemuxer f = RecursiveAI.OneMedia.Asf.AsfDemuxer.Open(new StreamForwardSource(new MemoryStream(file))))
        {
            int n = 0;
            while (f.TryReadPacket(out _))
            {
                n++;
            }

            check("asf: forward-only", n > 10);
        }

        await using RangedMediaSource ranged = new(file.Length, (offset, buffer, _) =>
        {
            int count = (int)Math.Min(buffer.Length, file.Length - offset);
            file.AsMemory((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        });
        await using RecursiveAI.OneMedia.Asf.AsfAsyncDemuxer a = await RecursiveAI.OneMedia.Asf.AsfAsyncDemuxer.OpenAsync(ranged);
        int m = 0;
        while (await a.ReadPacketAsync())
        {
            m++;
        }

        check("asf: ranged", m > 10);
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(AsfSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

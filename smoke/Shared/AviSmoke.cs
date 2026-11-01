using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Dv;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads a small AVI (motion JPEG and PCM), a DV-in-AVI type 1 file and a raw DV frame, synchronously, forward-only and through a ranged
/// source. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class AviSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        byte[] avi = Resource("mjpeg-pcm.avi");
        using (AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(avi)))
        {
            check("avi tracks", d.Tracks.Count == 2 && d.Tracks[0].Codec == CodecId.Mjpeg && d.Tracks[1].Kind == TrackKind.Audio);
            int video = 0;
            int audio = 0;
            while (d.TryReadPacket(out PacketView p))
            {
                if (p.Info.TrackIndex == 0)
                {
                    video++;
                }
                else
                {
                    audio++;
                }
            }

            check("avi packets", video == 5 && audio > 0 && d.Defects.Count == 0);
            check("avi seek", d.CanSeek && d.Seek(MediaTime.Zero).Ticks == 0);
            check("avi metadata", d.ReadMetadata().Facts.Duration.Best is not null);
        }

        using (AviDemuxer f = AviDemuxer.Open(new StreamForwardSource(new MemoryStream(avi))))
        {
            int n = 0;
            while (f.TryReadPacket(out _))
            {
                n++;
            }

            check("avi forward-only", n > 5);
        }

        await using (RangedMediaSource ranged = new(avi.Length, (offset, buffer, _) =>
        {
            int count = (int)Math.Min(buffer.Length, avi.Length - offset);
            avi.AsMemory((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        }))
        {
            await using IAsyncDemuxer a = await AviDemuxer.OpenAsync(ranged);
            check("avi ranged open", a.Tracks.Count == 2);
        }

        using (AviDemuxer t1 = AviDemuxer.Open(new MemoryMediaSource(Resource("one-frame-pal-type1.avi"))))
        {
            check("dv type 1 in avi", t1.Tracks.Count == 1 && t1.Tracks[0].Codec.Name == "dvvideo" && t1.TryReadPacket(out PacketView p) && p.Data.Length == 144000);
        }

        using (DvDemuxer dv = DvDemuxer.Open(new MemoryMediaSource(Resource("one-frame-pal.dv"))))
        {
            check("raw dv", dv.Tracks.Count == 1 && dv.TryReadPacket(out PacketView p) && p.Data.Length == 144000 && dv.ReadMetadata().Facts.CaptureTime.Best is not null);
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(AviSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

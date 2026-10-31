using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads a tiny hand-built WebM through the Matroska demuxer (seekable, forward-only and ranged) and its element tree. Part of the
/// NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class MatroskaSmoke
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        byte[] file = Build();

        using (MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file)))
        {
            check("matroska tracks", d.Tracks.Count == 1 && d.Tracks[0].Codec == CodecId.Vp8);
            int n = 0;
            long lastPts = -1;
            bool keys = true;
            while (d.TryReadPacket(out PacketView p))
            {
                keys &= (n == 0) == p.Info.IsKeyframe;
                lastPts = p.Info.Pts.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks;
                n++;
            }

            check("matroska packets", n == 2 && lastPts == 33 && keys);
            check("matroska seek", d.CanSeek && d.Seek(MediaTime.Zero).Ticks == 0);
        }

        using (MatroskaDemuxer f = MatroskaDemuxer.Open(new StreamForwardSource(new MemoryStream(file))))
        {
            int n = 0;
            while (f.TryReadPacket(out _))
            {
                n++;
            }

            check("matroska forward-only", n == 2);
        }

        await using (RangedMediaSource ranged = new(file.Length, (offset, buffer, _) =>
        {
            int count = (int)Math.Min(buffer.Length, file.Length - offset);
            file.AsMemory((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        }))
        {
            await using MatroskaAsyncDemuxer a = await MatroskaAsyncDemuxer.OpenAsync(ranged);
            int n = 0;
            while (await a.ReadPacketAsync())
            {
                n++;
            }

            check("matroska ranged", n == 2);
        }

        EbmlDocument doc = EbmlDocument.Open(new MemoryMediaSource(file));
        check("matroska tree", doc.Header is not null && doc.Segment is { } s && s.ChildElements.Count == 3 && s.ChildElements[2].Name == "Cluster");
    }

    private static byte[] El(uint id, params byte[][] parts)
    {
        List<byte> bytes = [];
        int length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        for (int i = length - 1; i >= 0; i--)
        {
            bytes.Add((byte)(id >> (8 * i)));
        }

        int size = 0;
        foreach (byte[] part in parts)
        {
            size += part.Length;
        }

        bytes.Add((byte)(0x80 | size)); // payloads here are all under 127 bytes
        foreach (byte[] part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    private static byte[] Build()
    {
        byte[] header = El(0x1A45DFA3, El(0x4282, "webm"u8.ToArray()));
        byte[] info = El(0x1549A966, El(0x2AD7B1, [0x0F, 0x42, 0x40]));
        byte[] tracks = El(0x1654AE6B, El(0xAE, El(0xD7, [1]), El(0x83, [1]), El(0x86, "V_VP8"u8.ToArray())));
        byte[] cluster = El(0x1F43B675, El(0xE7, [0]), El(0xA3, [0x81, 0, 0, 0x80, 0x11, 0x22]), El(0xA3, [0x81, 0, 0x21, 0, 0xAA, 0xBB, 0xCC]));
        return [.. header, .. El(0x18538067, info, tracks, cluster)];
    }
}

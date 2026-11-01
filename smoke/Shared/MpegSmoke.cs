namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Opens a small transport stream, M2TS stream, program stream, raw video stream and IVF file through the default registry and pulls
/// every packet, so the MPEG readers and their static tables run under NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class MpegSmoke
{
    public static void Run(Action<string, bool> check)
    {
        foreach ((string name, string format) in new[] { ("h264-aac.ts", "mpegts"), ("h264-aac.m2ts", "mpegts"), ("mpeg2-mp2.vob", "mpegps"), ("h264.264", "h264"), ("vp8.ivf", "ivf") })
        {
            byte[] file = Resource(name);
            using IDemuxer d = MediaFormats.Default.Open(new MemoryMediaSource(file), new MediaOpenOptions());
            int packets = 0;
            while (d.TryReadPacket(out _))
            {
                packets++;
            }

            check("mpeg " + name, string.Equals(d.Format.ToString(), format, StringComparison.OrdinalIgnoreCase) && d.Tracks.Count > 0 && packets > 20);
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(MpegSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

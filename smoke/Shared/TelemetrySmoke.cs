using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Metadata.Telemetry;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Decodes a small GoPro GPMF, Google CAMM and Apple mebx file through the telemetry reader, so the key tables and decoders run under
/// NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class TelemetrySmoke
{
    public static void Run(Action<string, bool> check)
    {
        foreach ((string name, TelemetryFormat format) in new[] { ("gpmf-synthetic.mp4", TelemetryFormat.Gpmf), ("camm-all-types.mp4", TelemetryFormat.Camm), ("mebx-synthetic.mp4", TelemetryFormat.Mebx) })
        {
            using MediaFile media = MediaFile.Open(new MemoryMediaSource(Resource(name)));
            int tracks = 0;
            int items = 0;
            foreach (MediaTrack t in media.Tracks)
            {
                if (TelemetryTrackReader.FormatOf(t) != format)
                {
                    continue;
                }

                tracks++;
                using TelemetryTrackReader reader = media.OpenTelemetry(t);
                while (reader.TryRead(out _))
                {
                    items++;
                }
            }

            bool gps = false;
            foreach (TelemetryChannel c in media.Telemetry)
            {
                gps |= c.Kind == TelemetryKind.Gps && c.Samples.Count > 0;
            }

            check("telemetry " + name, tracks == 1 && items > 5 && gps);
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(TelemetrySmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream m = new();
        s.CopyTo(m);
        return m.ToArray();
    }
}

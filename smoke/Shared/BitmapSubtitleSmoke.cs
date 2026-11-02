using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Decodes one PGS stream, one VobSub pair and DVB subtitle payloads of each pixel depth, and writes a picture as PNG, so the bitmap
/// subtitle decoders and their tables run under NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class BitmapSubtitleSmoke
{
    public static void Run(Action<string, bool> check)
    {
        SubtitleDocument pgs = BitmapSubtitles.ReadSup(Resource("bitmap-pgs.sup"));
        check("bitmap pgs cues", pgs.Cues.Count == 1 && pgs.Cues[0].Images.Count == 2 && pgs.Cues[0].End.IsKnown && BitmapSubtitleCues.IsForced(pgs.Cues[0]));

        SubtitleDocument vob = BitmapSubtitles.ReadVobSub(Resource("bitmap-vobsub.idx"), Resource("bitmap-vobsub.sub"));
        check("bitmap vobsub cues", vob.Cues.Count == 2 && vob.Cues[0].Images[0].Width == 48 && vob.Cues[0].Language == "en" && vob.ReferenceWidth == 720);

        foreach (int depth in new[] { 2, 4, 8 })
        {
            var dvb = new DvbSubtitleDecoder();
            dvb.Feed(Resource($"bitmap-dvb-{depth}bit.pes"), new MediaTime(1000, 1000));
            IReadOnlyList<SubtitleCue> cues = dvb.Flush();
            check($"bitmap dvb {depth}-bit", cues.Count == 1 && cues[0].Images[0].Width == 70 && cues[0].End == new MediaTime(5000, 1000));
        }

        byte[] png = SubtitleImagePng.Encode(pgs.Cues[0].Images[0]);
        check("bitmap png", png.Length > 40 && png[1] == (byte)'P');
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(BitmapSubtitleSmoke).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}

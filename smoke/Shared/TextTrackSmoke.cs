using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Reads the text tracks of small tx3g, wvtt, stpp, SubRip, ASS and WebVTT files into cues and writes them back in every text-track format,
/// so the text-track mappings and the sidecar readers they use run under NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class TextTrackSmoke
{
    public static void Run(Action<string, bool> check)
    {
        foreach (string name in new[] { "tt-mb_tx3g.mp4", "tt-mb_wvtt.mp4", "tt-mb_stpp.mp4", "tt-mm_srt.mkv", "tt-mm_ass.mkv", "tt-mm_vtt.mkv" })
        {
            using Stream s = typeof(TextTrackSmoke).Assembly.GetManifestResourceStream(name)!;
            using MemoryStream copy = new();
            s.CopyTo(copy);
            using MediaFile file = Media.Open(copy.ToArray());
            bool ok = file.Subtitles.Count == 1;
            if (ok)
            {
                SubtitleDocument doc = file.Subtitles[0].Document;
                ok = doc.Cues.Count >= 4;
                foreach (TextTrackFormat f in new[] { TextTrackFormat.Tx3g, TextTrackFormat.WebVttIso, TextTrackFormat.XmlSubtitleIso, TextTrackFormat.SubRip, TextTrackFormat.Ass, TextTrackFormat.WebVtt, TextTrackFormat.WebmWebVtt })
                {
                    TextTrackEncoding enc = TextTrackEncoder.Encode(doc, f);
                    TextTrackDecoder back = new(enc.Track);
                    foreach (TextTrackPacket p in enc.Packets)
                    {
                        back.Add(p.Info, p.Data, p.Additional);
                    }

                    ok &= back.Finish().Cues.Count >= 4;
                }
            }

            check("texttrack " + name, ok);
        }
    }
}

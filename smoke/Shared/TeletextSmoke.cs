using RecursiveAI.OneMedia.Captions.Teletext;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;
using RecursiveAI.OneMedia.Tests;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Builds a small transport stream and a program stream that carry teletext subtitle pages and reads the cues back, so the Hamming
/// tables, the national character tables and the page decoder run under NativeAOT and in the browser WebAssembly runtime.
/// </summary>
internal static class TeletextSmoke
{
    public static void Run(Action<string, bool> check)
    {
        long clock = TeletextBuilder.Clock;
        (long, byte[][])[] pes =
        [
            (clock, TeletextBuilder.Page(7, 0x77, true, true, 0b110, (20, TeletextBuilder.BoxedRow("citt{")))),
            (3 * clock, TeletextBuilder.Page(7, 0x77, true, true, 0)),
        ];

        using MpegTsDemuxer ts = MpegTsDemuxer.Open(new MemoryMediaSource(TeletextBuilder.TransportStream(pes)));
        IReadOnlyList<TeletextTrackSubtitles> fromTs = TeletextExtractor.ReadAll(ts);
        check("teletext ts", fromTs.Count == 1 && fromTs[0].Subtitles[0].Language == "ita" && fromTs[0].Subtitles[0].Document.Cues.Count == 1
            && fromTs[0].Subtitles[0].Document.Cues[0].Text == "citt\u00e0");

        using MpegPsDemuxer ps = MpegPsDemuxer.Open(new MemoryMediaSource(TeletextBuilder.ProgramStream(pes)));
        IReadOnlyList<TeletextTrackSubtitles> fromPs = TeletextExtractor.ReadAll(ps);
        check("teletext ps", fromPs.Count == 1 && fromPs[0].Subtitles[0].Document.Cues.Count == 1);
    }
}

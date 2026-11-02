using System.Text;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Reads SRT, converts it to every subtitle format and reads each result back, under NativeAOT and in the browser WebAssembly runtime.</summary>
internal static class SubtitleSmoke
{
    public static void Run(Action<string, bool> check)
    {
        byte[] srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,500\nHello, <b>world</b>!\n\n2\n00:00:04,000 --> 00:00:06,250\n<i>Second</i>\ncue\n\n");
        SubtitleDocument doc = SubtitleFormats.Read(srt);
        check("subtitles sniff and read", doc.Format == "srt" && doc.Cues.Count == 2);
        bool all = true;
        foreach (SubtitleFormat format in SubtitleFormats.All)
        {
            SubtitleOutput output = format.Write(doc);
            SubtitleDocument back = format.Read(output.Data);
            all &= back.Cues.Count >= 2 && back.Cues[0].Text.StartsWith("Hello, world!", StringComparison.Ordinal);
            all &= SubtitleFormats.Sniff(output.Data)?.Name == format.Name;
        }

        check("subtitles convert to every format and back", all);
        check("subtitles loss report", SubtitleFormats.Sbv.Write(doc).Report.Count(SubtitleFeatures.Bold, LossKind.Dropped) == 1);
    }
}

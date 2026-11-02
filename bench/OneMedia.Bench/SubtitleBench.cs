using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using RecursiveAI.OneMedia.Captions;
using SubtitlesParser.Classes.Parsers;
using SeFormat = Nikse.SubtitleEdit.Core.SubtitleFormats.SubtitleFormat;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>
/// Indicative parse and convert timings of the subtitle layer next to SubtitlesParser (MIT) and the Subtitle Edit core library
/// (GPL, public API only, bench project only). One process, every implementation warmed with at least 30 runs before sampling.
/// Usage: <c>subsbench [cues]</c>.
/// </summary>
internal static class SubtitleBench
{
    public static int Run(string[] args)
    {
        int cues = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 20_000;
        byte[] srt = MakeSrt(cues);
        SubtitleDocument doc = SubtitleFormats.Srt.Read(srt);
        byte[] vtt = SubtitleFormats.WebVtt.Write(doc).Data;
        byte[] ass = SubtitleFormats.Ass.Write(doc).Data;
        Console.WriteLine($"cues={cues} srt={srt.Length} B vtt={vtt.Length} B ass={ass.Length} B");
        Console.WriteLine("row\timpl\tmedian ms\tmin ms\tKiB allocated per run\tcues parsed");

        Row("parse srt", "onemedia", () => SubtitleFormats.Srt.Read(srt).Cues.Count);
        Row("parse srt", "SubtitlesParser", () => new SrtParser().ParseStream(new MemoryStream(srt), Encoding.UTF8).Count);
        Row("parse srt", "SubtitleEdit core", () => Load(new SubRip(), srt));
        Row("parse vtt", "onemedia", () => SubtitleFormats.WebVtt.Read(vtt).Cues.Count);
        Row("parse vtt", "SubtitlesParser", () => new VttParser().ParseStream(new MemoryStream(vtt), Encoding.UTF8).Count);
        Row("parse vtt", "SubtitleEdit core", () => Load(new WebVTT(), vtt));
        Row("parse ass", "onemedia", () => SubtitleFormats.Ass.Read(ass).Cues.Count);
        Row("parse ass", "SubtitlesParser", () => new SsaParser().ParseStream(new MemoryStream(ass), Encoding.UTF8).Count);
        Row("parse ass", "SubtitleEdit core", () => Load(new AdvancedSubStationAlpha(), ass));
        Row("convert srt->vtt", "onemedia", () => SubtitleFormats.WebVtt.Write(SubtitleFormats.Srt.Read(srt)).Data.Length);
        Row("convert srt->vtt", "SubtitleEdit core", () => Convert(new SubRip(), new WebVTT(), srt));
        Row("convert srt->ass", "onemedia", () => SubtitleFormats.Ass.Write(SubtitleFormats.Srt.Read(srt)).Data.Length);
        Row("convert srt->ass", "SubtitleEdit core", () => Convert(new SubRip(), new AdvancedSubStationAlpha(), srt));
        return 0;
    }

    private static int Load(SeFormat format, byte[] data)
    {
        var sub = new Subtitle();
        format.LoadSubtitle(sub, [.. Encoding.UTF8.GetString(data).Split('\n')], null);
        return sub.Paragraphs.Count;
    }

    private static int Convert(SeFormat from, SeFormat to, byte[] data)
    {
        var sub = new Subtitle();
        from.LoadSubtitle(sub, [.. Encoding.UTF8.GetString(data).Split('\n')], null);
        return to.ToText(sub, "bench").Length;
    }

    private static void Row(string row, string impl, Func<int> work)
    {
        int result = 0;
        for (int i = 0; i < 30; i++)
        {
            result = work();
        }

        var times = new List<double>();
        long allocated = 0;
        for (int i = 0; i < 30; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            result = work();
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        times.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{row}\t{impl}\t{times[times.Count / 2]:0.00}\t{times[0]:0.00}\t{allocated / 30 / 1024}\t{result}"));
    }

    private static byte[] MakeSrt(int cues)
    {
        var rng = new Random(11);
        string[] words = ["alpha", "beta", "gamma", "caf\u00e9", "hello,", "world!", "it's", "x", "delta"];
        var sb = new StringBuilder();
        long t = 500;
        for (int i = 1; i <= cues; i++)
        {
            t += rng.Next(100, 3000);
            long d = rng.Next(300, 4000);
            sb.Append(i).Append('\n').Append(Clock(t)).Append(" --> ").Append(Clock(t + d)).Append('\n');
            int lines = rng.Next(1, 3);
            for (int l = 0; l < lines; l++)
            {
                string text = string.Join(' ', Enumerable.Range(0, rng.Next(2, 7)).Select(_ => words[rng.Next(words.Length)]));
                sb.Append(rng.Next(7) == 0 ? "<i>" + text + "</i>" : text).Append('\n');
            }

            sb.Append('\n');
            t += d;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Clock(long ms) => string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
}

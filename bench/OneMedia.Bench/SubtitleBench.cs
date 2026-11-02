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
/// Usage: <c>subsbench [-r ROWFILTER] [-n SAMPLES] [cues...]</c>; several cue counts run one after the other (default 20000 100000).
/// ROWFILTER is a substring of the row name ("parse", "convert", "ttml", "srt->vtt", ...).
/// </summary>
internal static class SubtitleBench
{
    public static int Run(string[] args)
    {
        string filter = "";
        int samples = 30;
        var sizes = new List<int>();
        for (int a = 0; a < args.Length; a++)
        {
            if (args[a] == "-r")
            {
                filter = args[++a];
            }
            else if (args[a] == "-n")
            {
                samples = int.Parse(args[++a], CultureInfo.InvariantCulture);
            }
            else
            {
                sizes.Add(int.Parse(args[a], CultureInfo.InvariantCulture));
            }
        }

        if (sizes.Count == 0)
        {
            sizes.Add(20_000);
            sizes.Add(100_000);
        }

        Console.WriteLine("cues\trow\timpl\tmedian ms\tmin ms\tKiB allocated per run\tcues parsed");
        foreach (int cues in sizes)
        {
            RunSize(cues, filter, samples);
        }

        return 0;
    }

    private static void RunSize(int cues, string filter, int samples)
    {
        byte[] srt = MakeSrt(cues);
        SubtitleDocument doc = SubtitleFormats.Srt.Read(srt);
        byte[] vtt = SubtitleFormats.WebVtt.Write(doc).Data;
        byte[] ass = SubtitleFormats.Ass.Write(doc).Data;
        byte[] ttml = SubtitleFormats.Ttml.Write(doc).Data;
        Console.Error.WriteLine($"cues={cues} srt={srt.Length} B vtt={vtt.Length} B ass={ass.Length} B ttml={ttml.Length} B");

        // Implementations of one row are sampled in turn (one run of each, round after round) so that load from other
        // processes on a shared machine lands on all of them alike.
        var groups = new List<(string Row, List<(string Impl, Func<int> Work)> Impls)>();
        void R(string row, string impl, Func<int> work)
        {
            if (!row.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (groups.Count == 0 || groups[^1].Row != row)
            {
                groups.Add((row, []));
            }

            groups[^1].Impls.Add((impl, work));
        }

        // Same input and output on both sides (docs/bench.md): file bytes in, decoded, cues with their text out; convert = that parse plus
        // the target format written and encoded to file bytes. onemedia does more on the way (encoding detection, spans, extras, defects
        // with offsets, a loss report and the text of the output next to its bytes); the comparators do not.
        R("parse srt", "onemedia", () => SubtitleFormats.Srt.Read(srt).Cues.Count);
        R("parse srt", "SubtitlesParser", () => new SrtParser().ParseStream(new MemoryStream(srt), Encoding.UTF8).Count);
        R("parse srt", "SubtitleEdit core", () => Load(new SubRip(), srt));
        R("parse vtt", "onemedia", () => SubtitleFormats.WebVtt.Read(vtt).Cues.Count);
        R("parse vtt", "SubtitlesParser", () => new VttParser().ParseStream(new MemoryStream(vtt), Encoding.UTF8).Count);
        R("parse vtt", "SubtitleEdit core", () => Load(new WebVTT(), vtt));
        R("parse ass", "onemedia", () => SubtitleFormats.Ass.Read(ass).Cues.Count);
        R("parse ass", "SubtitlesParser", () => new SsaParser().ParseStream(new MemoryStream(ass), Encoding.UTF8).Count);
        R("parse ass", "SubtitleEdit core", () => Load(new AdvancedSubStationAlpha(), ass));
        R("parse ttml", "onemedia", () => SubtitleFormats.Ttml.Read(ttml).Cues.Count);
        R("parse ttml", "SubtitleEdit core", () => Load(new TimedText10(), ttml));
        R("convert srt->vtt", "onemedia", () => SubtitleFormats.WebVtt.Write(SubtitleFormats.Srt.Read(srt)).Data.Length);
        R("convert srt->vtt", "SubtitleEdit core", () => Convert(new SubRip(), new WebVTT(), srt));
        R("convert srt->ass", "onemedia", () => SubtitleFormats.Ass.Write(SubtitleFormats.Srt.Read(srt)).Data.Length);
        R("convert srt->ass", "SubtitleEdit core", () => Convert(new SubRip(), new AdvancedSubStationAlpha(), srt));
        R("convert srt->ttml", "onemedia", () => SubtitleFormats.Ttml.Write(SubtitleFormats.Srt.Read(srt)).Data.Length);
        R("convert srt->ttml", "SubtitleEdit core", () => Convert(new SubRip(), new TimedText10(), srt));
        foreach ((string row, List<(string Impl, Func<int> Work)> impls) in groups)
        {
            Row(cues, samples, row, impls);
        }
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
        return Encoding.UTF8.GetBytes(to.ToText(sub, "bench")).Length;
    }

    private static void Row(int cues, int samples, string row, List<(string Impl, Func<int> Work)> impls)
    {
        var result = new int[impls.Count];
        for (int k = 0; k < impls.Count; k++)
        {
            for (int i = 0; i < 30; i++)
            {
                result[k] = impls[k].Work();
            }
        }

        var times = new List<double>[impls.Count];
        var allocated = new long[impls.Count];
        for (int k = 0; k < impls.Count; k++)
        {
            times[k] = [];
        }

        for (int i = 0; i < samples; i++)
        {
            for (int k = 0; k < impls.Count; k++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                result[k] = impls[k].Work();
                times[k].Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                allocated[k] += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        for (int k = 0; k < impls.Count; k++)
        {
            times[k].Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{cues}\t{row}\t{impls[k].Impl}\t{times[k][times[k].Count / 2]:0.00}\t{times[k][0]:0.00}\t{allocated[k] / samples / 1024}\t{result[k]}"));
        }
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

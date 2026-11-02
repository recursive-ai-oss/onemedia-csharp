using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>subs</c> command: thin wrapper over <see cref="SubtitleFormats"/> and <see cref="SubtitleAlignment"/>.</summary>
internal static class SubsCommand
{
    public const string Usage =
        "       onemedia subs info <file> [--encoding name] [--cues]   format, encoding, cues, styles, features used and defects (--cues: one line per cue, times in ms)\n"
        + "       onemedia subs <in> --to <out> [--format name] [--offset seconds] [--align-to media] [--encoding name] [--overwrite] [--report]\n"
        + "                      convert a subtitle file (srt, webvtt, ass/ssa, ttml/dfxp, sbv, lrc; format by content) into the format <out> names\n"
        + "                      (or --format), optionally shifting times; prints what the target could not carry (--report lists every cue)";

    public static int Run(string[] args)
    {
        bool info = args.Length > 0 && args[0] == "info";
        string? input = null, output = null, format = null, alignTo = null, encoding = null;
        string? offset = null;
        bool overwrite = false, report = false, cues = false;
        for (int i = info ? 1 : 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--to" when i + 1 < args.Length: output = args[++i]; break;
                case "--format" when i + 1 < args.Length: format = args[++i]; break;
                case "--offset" when i + 1 < args.Length: offset = args[++i]; break;
                case "--align-to" when i + 1 < args.Length: alignTo = args[++i]; break;
                case "--encoding" when i + 1 < args.Length: encoding = args[++i]; break;
                case "--overwrite": overwrite = true; break;
                case "--report": report = true; break;
                case "--cues": cues = true; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal) || input is not null)
                    {
                        Console.Error.WriteLine("unexpected argument " + a);
                        return 2;
                    }

                    input = a;
                    break;
            }
        }

        if (input is null || (!info && output is null))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            var defects = new DefectLog();
            var options = new SubtitleReadOptions { Encoding = encoding };
            byte[] data = File.ReadAllBytes(input);
            SubtitleFormat source = SubtitleFormats.Sniff(data) ?? throw new UnsupportedMediaException("The content is not a recognised subtitle format.");
            SubtitleDocument doc = source.Read(data, defects, null, options);
            if (info)
            {
                return Info(doc, source, defects, cues);
            }

            if (offset is not null)
            {
                doc = SubtitleAlignment.Shift(doc, ParseSeconds(offset)).Document;
            }

            if (alignTo is not null)
            {
                using MediaFile media = Media.Open(alignTo);
                AlignedSubtitles aligned = SubtitleAlignment.ToMediaTimeline(doc, media);
                doc = aligned.Document;
                Print(aligned.Report, report);
            }

            SubtitleFormat target = (format is not null ? SubtitleFormats.Find(format) : SubtitleFormats.Find(Path.GetExtension(output!)))
                ?? throw new UnsupportedMediaException("Cannot tell which format to write; use --format.");
            SubtitleOutput result = target.Write(doc);
            if (File.Exists(output!) && !overwrite)
            {
                Console.Error.WriteLine("onemedia: " + output + " exists; use --overwrite");
                return 1;
            }

            string temp = output + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllBytes(temp, result.Data);
            File.Move(temp, output!, true);
            Console.WriteLine($"{source.Name} -> {target.Name}: {doc.Cues.Count} cue(s) written to {output}");
            Print(result.Report, report);
            return 0;
        }
        catch (Exception e) when (e is MediaException or IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("onemedia: " + e.Message);
            return 1;
        }
    }

    private static int Info(SubtitleDocument doc, SubtitleFormat format, DefectLog defects, bool cues)
    {
        Console.WriteLine($"format:   {format.DisplayName} ({format.Name})");
        Console.WriteLine($"encoding: {doc.TextEncoding}{(doc.ByteOrderMark ? " with byte order mark" : "")}, line ending {(doc.NewLine == "\r\n" ? "CRLF" : doc.NewLine == "\r" ? "CR" : "LF")}");
        if (doc.Title is not null)
        {
            Console.WriteLine($"title:    {doc.Title}");
        }

        if (doc.Language is not null)
        {
            Console.WriteLine($"language: {doc.Language}");
        }

        Console.WriteLine($"cues:     {doc.Cues.Count}");
        if (doc.Cues.Count > 0)
        {
            MediaTime first = doc.Cues[0].Start;
            MediaTime last = doc.Cues[0].End;
            foreach (SubtitleCue c in doc.Cues)
            {
                first = MediaTime.Min(first, c.Start);
                last = MediaTime.Max(last, c.End);
            }

            Console.WriteLine($"span:     {Seconds(first)} s to {Seconds(last)} s");
        }

        Console.WriteLine($"styles:   {doc.Styles.Count}, regions: {doc.Regions.Count}");
        Console.WriteLine($"uses:     {SubtitleFormats.FeaturesUsed(doc)}");
        foreach (MediaDefect d in defects)
        {
            Console.WriteLine($"defect:   {d}");
        }

        if (cues)
        {
            for (int i = 0; i < doc.Cues.Count; i++)
            {
                SubtitleCue c = doc.Cues[i];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cue\t{i}\t{c.Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks}\t{c.End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks}\t{c.Text.Replace("\n", "\\n", StringComparison.Ordinal)}"));
            }
        }

        return 0;
    }

    private static void Print(ConversionReport r, bool all)
    {
        if (r.IsLossless)
        {
            Console.WriteLine("lossless");
            return;
        }

        foreach (LossKind kind in new[] { LossKind.Dropped, LossKind.Approximated })
        {
            SubtitleFeatures affected = r.Affected(kind);
            if (affected == SubtitleFeatures.None)
            {
                continue;
            }

            var parts = new List<string>();
            for (long bit = 1; bit <= (long)SubtitleFeatures.Images; bit <<= 1)
            {
                var f = (SubtitleFeatures)bit;
                if ((affected & f) != 0)
                {
                    parts.Add($"{f} x{r.Count(f, kind)}");
                }
            }

            Console.WriteLine($"{kind.ToString().ToLowerInvariant()}: {string.Join(", ", parts)}");
        }

        if (all)
        {
            foreach (ConversionLoss l in r.Losses)
            {
                Console.WriteLine("  " + l);
            }

            if (r.Omitted > 0)
            {
                Console.WriteLine($"  ... and {r.Omitted} more");
            }
        }
    }

    private static string Seconds(MediaTime t) => (t.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    // "1.5", "-0.25": decimal seconds, exactly.
    private static MediaTime ParseSeconds(string s)
    {
        bool negative = s.StartsWith('-');
        string digits = s.TrimStart('-', '+');
        int dot = digits.IndexOf('.');
        int frac = dot < 0 ? 0 : digits.Length - dot - 1;
        if (frac > 9 || !long.TryParse(digits.Replace(".", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
        {
            throw new FormatException("--offset takes decimal seconds such as 1.5 or -0.25");
        }

        long scale = 1;
        for (int i = 0; i < frac; i++)
        {
            scale *= 10;
        }

        return new MediaTime(negative ? -ticks : ticks, scale);
    }
}

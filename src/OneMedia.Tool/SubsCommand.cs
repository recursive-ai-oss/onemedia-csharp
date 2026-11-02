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
        + "       onemedia subs info <media>                             every subtitle source of a video or audio file: text and bitmap tracks, teletext pages, CEA-608 channels, CEA-708 services\n"
        + "                      (reads the video once to find embedded captions)\n"
        + "       onemedia subs <media> --to <out> [--track n] [--format name]   extract entry n (default 0) of the list above as a text subtitle file\n"
        + "       onemedia subs <media> --all --to <dir> [--format name]         extract every entry into dir; bitmap entries become PNG files plus an index.tsv\n"
        + "       onemedia subs <in> --to <out> [--format name] [--offset seconds] [--align-to media] [--encoding name] [--overwrite] [--report]\n"
        + "                      convert a subtitle file (srt, webvtt, ass/ssa, ttml/dfxp, sbv, lrc; format by content) into the format <out> names\n"
        + "                      (or --format), optionally shifting times; prints what the target could not carry (--report lists every cue)";

    public static int Run(string[] args)
    {
        bool info = args.Length > 0 && args[0] == "info";
        string? input = null, output = null, format = null, alignTo = null, encoding = null;
        string? offset = null;
        bool overwrite = false, report = false, cues = false, all = false;
        int? track = null;
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
                case "--all": all = true; break;
                case "--track" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int n): track = n; break;
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
            SubtitleFormat? sniffed = SubtitleFormats.Sniff(ReadHead(input));
            if (!IsSidecar(sniffed, input))
            {
                return MediaSubs(input, info, output, track, all, format, overwrite, cues);
            }

            SubtitleFormat source = sniffed ?? throw new UnsupportedMediaException("The content is not a recognised subtitle format.");
            SubtitleDocument doc = source is PgsSubtitleFormat or VobSubSubtitleFormat ? BitmapSubtitles.ReadFile(input) : source.Read(File.ReadAllBytes(input), defects, null, options);
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

    private static byte[] ReadHead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] head = new byte[(int)Math.Min(stream.Length, 8192)];
        stream.ReadExactly(head);
        return head;
    }

    // A program stream also starts a .vob or .mpg video, so only the extensions of the DVD subtitle pair make it a sidecar.
    private static bool IsSidecar(SubtitleFormat? sniffed, string path) =>
        sniffed is not null && (sniffed is not VobSubSubtitleFormat || Path.GetExtension(path).ToLowerInvariant() is ".sub" or ".idx");

    private static int MediaSubs(string input, bool info, string? output, int? track, bool all, string? format, bool overwrite, bool cues)
    {
        using MediaFile media = Media.Open(input);
        IReadOnlyList<SubtitleTrack> subs = media.Subtitles;
        if (info)
        {
            Console.WriteLine($"subtitle sources: {subs.Count}");
            for (int i = 0; i < subs.Count; i++)
            {
                SubtitleTrack s = subs[i];
                string count;
                try
                {
                    count = s.Cues.Count.ToString(CultureInfo.InvariantCulture) + " cue(s)";
                }
                catch (Exception e) when (e is MediaException or InvalidOperationException)
                {
                    count = "unreadable: " + e.Message;
                }

                Console.WriteLine($"{i}\t{s.Kind}\t{s.Codec}\t{s.Label ?? "-"}\t{s.Language ?? "-"}\ttrack {s.Track.Index}\t{count}");
                if (cues && s.IsRead)
                {
                    for (int c = 0; c < s.Cues.Count; c++)
                    {
                        SubtitleCue cue = s.Cues[c];
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cue\t{i}\t{c}\t{cue.Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks}\t{cue.End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks}\t{cue.Text.Replace("\n", "\\n", StringComparison.Ordinal)}"));
                    }
                }
            }

            return 0;
        }

        if (subs.Count == 0)
        {
            Console.Error.WriteLine("onemedia: the file has no subtitle source");
            return 1;
        }

        List<int> chosen = [];
        if (all)
        {
            for (int i = 0; i < subs.Count; i++)
            {
                chosen.Add(i);
            }
        }
        else
        {
            int pick = track ?? 0;
            if (pick >= subs.Count)
            {
                Console.Error.WriteLine($"onemedia: there is no entry {pick}; the file has {subs.Count}");
                return 1;
            }

            chosen.Add(pick);
        }

        string baseName = Path.GetFileNameWithoutExtension(input);
        foreach (int i in chosen)
        {
            SubtitleTrack s = subs[i];
            SubtitleDocument doc = s.Document;
            bool bitmap = s.Kind == SubtitleKind.Bitmap;
            string label = (s.Label ?? s.Codec).Replace(' ', '-');
            if (bitmap)
            {
                string dir = Path.Combine(output!, $"{baseName}.{i}.{label}");
                WriteBitmaps(doc, dir);
                Console.WriteLine($"{i} {s.Kind} {s.Codec}: {doc.Cues.Count} cue(s) written to {dir}");
                continue;
            }

            string target = all ? Path.Combine(output!, $"{baseName}.{i}.{label}.{format ?? "srt"}") : output!;
            SubtitleFormat fmt = (format is not null ? SubtitleFormats.Find(format) : SubtitleFormats.Find(Path.GetExtension(target)))
                ?? throw new UnsupportedMediaException("Cannot tell which format to write; use --format.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            if (File.Exists(target) && !overwrite)
            {
                Console.Error.WriteLine("onemedia: " + target + " exists; use --overwrite");
                return 1;
            }

            SubtitleOutput result = fmt.Write(doc);
            File.WriteAllBytes(target, result.Data);
            Console.WriteLine($"{i} {s.Kind} {s.Codec}: {doc.Cues.Count} cue(s) written to {target}{(result.Report.IsLossless ? "" : " (lossy; see subs <file> --report)")}");
        }

        return 0;
    }

    // One PNG per image of every cue, and an index of times and positions.
    private static void WriteBitmaps(SubtitleDocument doc, string dir)
    {
        Directory.CreateDirectory(dir);
        var index = new StringBuilder("cue\timage\tstart_ms\tend_ms\tx\ty\twidth\theight\tfile\n");
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            for (int k = 0; k < cue.Images.Count; k++)
            {
                SubtitleImage image = cue.Images[k];
                string file = string.Create(CultureInfo.InvariantCulture, $"cue-{c + 1:D5}-{k}.png");
                File.WriteAllBytes(Path.Combine(dir, file), SubtitleImagePng.Encode(image));
                index.Append(string.Create(CultureInfo.InvariantCulture, $"{c}\t{k}\t{cue.Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks}\t{(cue.End.IsKnown ? cue.End.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks : -1)}\t{image.X}\t{image.Y}\t{image.Width}\t{image.Height}\t{file}\n"));
            }
        }

        File.WriteAllText(Path.Combine(dir, "index.tsv"), index.ToString());
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

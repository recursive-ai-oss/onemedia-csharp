using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>
/// Equivalence check for tuning the subtitle layer: reads seed files and seeded damaged variants of them in every sidecar format
/// and prints one line per (input, format) with a SHA-256 of everything the model holds (cues, spans, styles, extras, defects with
/// offsets) and of what each writer produces. Run it on two builds and diff the output.
/// Usage: <c>subsdump [-v INDEX FORMAT] [-m MUTATIONS] FILE...</c>; with <c>-v</c> the full dump of one input is printed instead.
/// </summary>
internal static class SubtitleDump
{
    public static int Run(string[] args)
    {
        int mutations = 120;
        int verbose = -1;
        string verboseFormat = "";
        var files = new List<string>();
        for (int a = 0; a < args.Length; a++)
        {
            if (args[a] == "-m")
            {
                mutations = int.Parse(args[++a], CultureInfo.InvariantCulture);
            }
            else if (args[a] == "-v")
            {
                verbose = int.Parse(args[++a], CultureInfo.InvariantCulture);
                verboseFormat = args[++a];
            }
            else
            {
                files.Add(args[a]);
            }
        }

        var inputs = new List<(string Name, byte[] Data)>();
        var seeds = new List<(string Name, byte[] Data)>();
        foreach (string f in files)
        {
            seeds.Add((Path.GetFileName(f), File.ReadAllBytes(f)));
        }

        SubtitleDocument rich = RichDocument();
        foreach (SubtitleFormat format in new[] { SubtitleFormats.Srt, SubtitleFormats.WebVtt, SubtitleFormats.Ass, SubtitleFormats.Ttml, SubtitleFormats.Sbv })
        {
            seeds.Add(("rich." + format.Name, format.Write(rich).Data));
        }

        foreach ((string name, byte[] data) in seeds)
        {
            inputs.Add((name, data));
            string text = Encoding.UTF8.GetString(data);
            inputs.Add((name + "+crlf", Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"))));
            inputs.Add((name + "+cr", Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r"))));
            inputs.Add((name + "+bom", [0xEF, 0xBB, 0xBF, .. data]));
            inputs.Add((name + "+utf16", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]));
            var rng = new Random(7);
            for (int m = 0; m < mutations; m++)
            {
                inputs.Add(($"{name}+m{m}", Mutate(data, rng)));
            }
        }

        SubtitleFormat[] formats = [SubtitleFormats.Srt, SubtitleFormats.WebVtt, SubtitleFormats.Ass, SubtitleFormats.Sbv, SubtitleFormats.Lrc, SubtitleFormats.Ttml];
        var limits = new MediaLimits { MaxCues = 5000, MaxAllocationBytes = 1 << 24 };
        for (int i = 0; i < inputs.Count; i++)
        {
            foreach (SubtitleFormat format in formats)
            {
                var sb = new StringBuilder();
                try
                {
                    var log = new DefectLog(limits);
                    SubtitleDocument doc = format.Read(inputs[i].Data, log, limits);
                    Dump(sb, doc);
                    foreach (MediaDefect d in log)
                    {
                        sb.Append("defect ").Append(d.Kind).Append(' ').Append(d.Severity).Append(' ').Append(d.Offset).Append(' ').Append(d.Code).Append(' ').Append(d.Message).Append('\n');
                    }

                    sb.Append("dropped ").Append(log.Dropped).Append('\n');
                    foreach (SubtitleFormat target in formats)
                    {
                        if (!target.CanWrite)
                        {
                            continue;
                        }

                        SubtitleOutput o = target.Write(doc);
                        sb.Append("write ").Append(target.Name).Append(' ').Append(Hash(o.Data)).Append(' ').Append(o.Text.Length).Append(" losses=").Append(o.Report.Losses.Count).Append('/').Append(o.Report.Omitted).Append('\n');
                        foreach (ConversionLoss l in o.Report.Losses)
                        {
                            sb.Append("  loss ").Append(l.ToString()).Append('\n');
                        }

                        if (verbose == i && verboseFormat == format.Name + ">" + target.Name)
                        {
                            Console.WriteLine(o.Text);
                        }
                    }
                }
                catch (Exception e) when (e is MediaException)
                {
                    sb.Append("EX ").Append(e.GetType().Name).Append(' ').Append(e.Message).Append('\n');
                }

                if (verbose == i && verboseFormat == format.Name)
                {
                    Console.WriteLine(sb.ToString());
                }
                else if (verbose < 0)
                {
                    Console.WriteLine($"{i}\t{inputs[i].Name}\t{format.Name}\t{Hash(Encoding.UTF8.GetBytes(sb.ToString()))}");
                }
            }
        }

        return 0;
    }

    // Cues that use spans, styles, karaoke, voices, ruby, placement and odd text, built through the public model.
    private static SubtitleDocument RichDocument()
    {
        var cues = new List<SubtitleCue>();
        string[] texts = ["plain one", "two\nlines", "bold italic mix", "caf\u00e9 \u4e2d\u6587 \U0001F600 end", "a & b < c > d", "{brace} text", "", "x"];
        for (int i = 0; i < 40; i++)
        {
            string t = texts[i % texts.Length];
            var spans = new List<SubtitleSpan>();
            if (t.Length > 4 && i % 2 == 0)
            {
                spans.Add(new SubtitleSpan(0, 3) { Style = new TextStyle { Bold = true } });
                spans.Add(new SubtitleSpan(2, t.Length - 2) { Style = new TextStyle { Italic = true, Color = new SubtitleColor(255, 0, 0) } });
            }

            if (t.Length > 6 && i % 3 == 0)
            {
                spans.Add(new SubtitleSpan(1, 4) { WordStart = new MediaTime(1000 + (i * 1000), 1000), WordEnd = new MediaTime(1500 + (i * 1000), 1000) });
                spans.Add(new SubtitleSpan(0, 2) { Voice = "Bob", Style = new TextStyle { Underline = true, Strike = true, FontFamily = "Arial", FontSize = new SubtitleLength(24) } });
            }

            if (t.Length > 3 && i % 5 == 0)
            {
                spans.Add(new SubtitleSpan(0, 3) { Ruby = "ru", Language = "ja" });
                spans.Add(new SubtitleSpan(2, 0) { Raw = new SubtitleExtra("ass", "override", "\\blur2") });
            }

            cues.Add(new SubtitleCue
            {
                Id = i % 4 == 0 ? "id" + i : null,
                Start = new MediaTime((i * 3000L) + 17, 1000),
                End = new MediaTime((i * 3000L) + 2900, 1000),
                Text = t,
                Spans = SubtitleList.Of(spans.ToArray()),
                Speaker = i % 7 == 0 ? "Alice" : null,
                StyleName = i % 6 == 0 ? "Alt" : null,
                Placement = i % 5 == 1 ? new CuePlacement { Align = SubtitleAlign.Left, VerticalAlign = SubtitleVerticalAlign.Top, Line = new CueLine(20, CueLineUnit.Percent, false), Position = 10, Size = 80 } : null,
                Extras = i % 9 == 0 ? SubtitleList.Of(new SubtitleExtra("webvtt", "note", "NOTE hello"), new SubtitleExtra("ass", "Layer", "2")) : SubtitleList.Empty<SubtitleExtra>(),
            });
        }

        return new SubtitleDocument
        {
            Title = "Rich",
            Language = "en",
            ReferenceWidth = 640,
            ReferenceHeight = 480,
            Styles = SubtitleList.Of(new SubtitleStyle("Default"), new SubtitleStyle("Alt") { Text = new TextStyle { Bold = true, Color = new SubtitleColor(0, 255, 0), FontFamily = "Courier", FontSize = new SubtitleLength(30) } }),
            Regions = SubtitleList.Of(new SubtitleRegion("r1") { Width = 50, Lines = 3 }),
            Cues = SubtitleList.Of(cues.ToArray()),
        };
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    private static byte[] Mutate(byte[] data, Random rng)
    {
        var list = new List<byte>(data);
        int ops = rng.Next(1, 5);
        const string special = "<>{}\\&\r\n,:-> 0123456789.;[]=/#abcNnh";
        for (int o = 0; o < ops && list.Count > 4; o++)
        {
            int at = rng.Next(list.Count);
            switch (rng.Next(6))
            {
                case 0:
                    list.RemoveRange(at, Math.Min(rng.Next(1, 40), list.Count - at));
                    break;
                case 1:
                    list[at] = (byte)rng.Next(256);
                    break;
                case 2:
                    list.Insert(at, (byte)special[rng.Next(special.Length)]);
                    break;
                case 3:
                    {
                        int len = Math.Min(rng.Next(1, 200), list.Count - at);
                        list.InsertRange(at, list.GetRange(at, len));
                        break;
                    }

                case 4:
                    list.RemoveRange(at, list.Count - at);
                    break;
                default:
                    list.InsertRange(at, Encoding.UTF8.GetBytes("\n\n"));
                    break;
            }
        }

        return [.. list];
    }

    private static void Dump(StringBuilder sb, SubtitleDocument d)
    {
        sb.Append("doc ").Append(d.Format).Append('|').Append(d.Language).Append('|').Append(d.Title).Append('|').Append(d.ReferenceWidth).Append('x').Append(d.ReferenceHeight)
            .Append('|').Append(d.TextEncoding).Append('|').Append(d.ByteOrderMark).Append('|').Append(Esc(d.NewLine)).Append('\n');
        foreach (SubtitleStyle s in d.Styles)
        {
            sb.Append("style ").Append(s.Name).Append('|');
            Style(sb, s.Text);
            Placement(sb, s.Placement);
            Extras(sb, s.Extras);
            sb.Append('\n');
        }

        foreach (SubtitleRegion r in d.Regions)
        {
            sb.Append("region ").Append(r.Id).Append('|').Append(r.X).Append('|').Append(r.Y).Append('|').Append(r.Width).Append('|').Append(r.Height).Append('|').Append(r.Lines)
                .Append('|').Append(r.VerticalAlign).Append('|').Append(r.ScrollUp);
            Extras(sb, r.Extras);
            sb.Append('\n');
        }

        Extras(sb, d.Extras);
        sb.Append('\n');
        foreach (SubtitleCue c in d.Cues)
        {
            sb.Append("cue ").Append(c.Id).Append('|').Append(c.Start.Ticks).Append('/').Append(c.Start.Timescale).Append('|').Append(c.End.Ticks).Append('/').Append(c.End.Timescale)
                .Append('|').Append(Esc(c.Text)).Append('|').Append(c.Speaker).Append('|').Append(c.StyleName).Append('|').Append(c.Mode).Append('|').Append(c.Language).Append('|');
            Placement(sb, c.Placement);
            Extras(sb, c.Extras);
            sb.Append('\n');
            foreach (SubtitleSpan s in c.Spans)
            {
                sb.Append("  span ").Append(s.Start).Append('+').Append(s.Length).Append('|').Append(s.Voice).Append('|').Append(s.Language).Append('|').Append(s.Ruby).Append('|')
                    .Append(s.WordStart?.Ticks).Append('/').Append(s.WordStart?.Timescale).Append('-').Append(s.WordEnd?.Ticks).Append('/').Append(s.WordEnd?.Timescale).Append('|');
                if (s.Style is { } st)
                {
                    Style(sb, st);
                }

                if (s.Raw is { } raw)
                {
                    sb.Append("raw:").Append(raw.Format).Append(':').Append(raw.Key).Append('=').Append(Esc(raw.Value));
                }

                sb.Append('\n');
            }
        }
    }

    private static void Style(StringBuilder sb, TextStyle s)
    {
        sb.Append('[').Append(s.Bold).Append(s.Italic).Append(s.Underline).Append(s.Strike).Append(s.Color).Append(s.Background).Append(s.FontFamily).Append(s.FontSize).Append(s.Classes).Append(']');
    }

    private static void Placement(StringBuilder sb, CuePlacement? p)
    {
        if (p is null)
        {
            sb.Append("noplace|");
            return;
        }

        sb.Append("place:").Append(p.Region).Append(',').Append(p.Align).Append(',').Append(p.VerticalAlign).Append(',').Append(p.Line).Append(',').Append(p.Position).Append(',')
            .Append(p.PositionAnchor).Append(',').Append(p.Size).Append(',').Append(p.Writing).Append(',').Append(p.Row).Append(',').Append(p.Column).Append('|');
    }

    private static void Extras(StringBuilder sb, SubtitleList<SubtitleExtra> extras)
    {
        foreach (SubtitleExtra e in extras)
        {
            sb.Append(" extra:").Append(e.Format).Append(':').Append(e.Key).Append('=').Append(Esc(e.Value));
        }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
}

using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Subtitles;

// W3C WebVTT: the file, block and cue-text syntax (WebVTT §4 and §6.1 parsing, as a tolerant reader).
internal sealed class WebVttFormat : SubtitleFormat
{
    internal static readonly WebVttFormat Instance = new();

    private static readonly string[] Exts = [".vtt"];

    private WebVttFormat()
    {
    }

    public override string Name => "webvtt";

    public override string DisplayName => "WebVTT";

    public override IReadOnlyList<string> Extensions => Exts;

    public override bool RequiresUtf8 => true;

    public override SubtitleFeatures Features =>
        SubtitleFeatures.Bold | SubtitleFeatures.Italic | SubtitleFeatures.Underline | SubtitleFeatures.Color | SubtitleFeatures.Background
        | SubtitleFeatures.Ruby | SubtitleFeatures.WordTiming | SubtitleFeatures.Voice | SubtitleFeatures.Language | SubtitleFeatures.Classes
        | SubtitleFeatures.Alignment | SubtitleFeatures.Position | SubtitleFeatures.Region | SubtitleFeatures.Writing | SubtitleFeatures.Id
        | SubtitleFeatures.Extras;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        head = head.TrimStart('\uFEFF');
        return head.StartsWith("WEBVTT", StringComparison.Ordinal) && (head.Length == 6 || head[6] is ' ' or '\t' or '\r' or '\n') ? 100 : 0;
    }

    internal override SubtitleDocument ReadCore(SubtitleSource src)
    {
        string text = src.Text;
        var lines = new List<(int Start, int Length)>();
        int pos = 0;
        while (src.NextLine(ref pos, out int s, out int l))
        {
            lines.Add((s, l));
        }

        if (lines.Count > 0 && lines[0].Length > 0 && text[lines[0].Start] == '\uFEFF')
        {
            lines[0] = (lines[0].Start + 1, lines[0].Length - 1);
        }

        var cues = new List<SubtitleCue>();
        var regions = new List<SubtitleRegion>();
        var docExtras = new List<SubtitleExtra>();
        var pendingNotes = new List<SubtitleExtra>();
        int i = 0;
        int n = lines.Count;

        // The signature line and any header lines up to the first blank line.
        if (n > 0 && Sniff(text.AsSpan(lines[0].Start, lines[0].Length)) == 100)
        {
            int j = 0;
            var header = new StringBuilder();
            while (j < n && lines[j].Length > 0)
            {
                if (j > 0)
                {
                    header.Append('\n');
                }

                header.Append(text, lines[j].Start, lines[j].Length);
                j++;
            }

            if (header.ToString() != "WEBVTT")
            {
                docExtras.Add(new SubtitleExtra("webvtt", "header", header.ToString()));
            }

            i = j;
        }
        else
        {
            src.Report(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The file does not start with the WEBVTT signature.", "subtitles.webvtt.signature");
        }

        MediaTime lastStart = MediaTime.Unknown;
        while (i < n)
        {
            if (lines[i].Length == 0)
            {
                i++;
                continue;
            }

            int blockStart = i;
            while (i < n && lines[i].Length > 0)
            {
                i++;
            }

            ReadBlock(src, lines, blockStart, i, cues, regions, docExtras, pendingNotes, ref lastStart);
        }

        foreach (SubtitleExtra note in pendingNotes)
        {
            docExtras.Add(new SubtitleExtra("webvtt", "note-end", note.Value));
        }

        return new SubtitleDocument
        {
            Cues = SubtitleList.From(cues),
            Regions = SubtitleList.From(regions),
            Extras = SubtitleList.From(docExtras),
            NewLine = src.NewLine,
        };
    }

    private static bool IsKeyword(ReadOnlySpan<char> line, string keyword) =>
        line.StartsWith(keyword, StringComparison.Ordinal) && (line.Length == keyword.Length || line[keyword.Length] is ' ' or '\t');

    private static void ReadBlock(
        SubtitleSource src,
        List<(int Start, int Length)> lines,
        int first,
        int end,
        List<SubtitleCue> cues,
        List<SubtitleRegion> regions,
        List<SubtitleExtra> docExtras,
        List<SubtitleExtra> pendingNotes,
        ref MediaTime lastStart)
    {
        string text = src.Text;
        ReadOnlySpan<char> head = text.AsSpan(lines[first].Start, lines[first].Length);
        string BlockText()
        {
            var sb = new StringBuilder();
            for (int k = first; k < end; k++)
            {
                if (k > first)
                {
                    sb.Append('\n');
                }

                sb.Append(text, lines[k].Start, lines[k].Length);
            }

            return sb.ToString();
        }

        // A block whose first line holds "-->" is a cue even if it begins with a keyword-like word.
        bool timingFirst = head.Contains("-->", StringComparison.Ordinal);
        if (!timingFirst && IsKeyword(head, "NOTE"))
        {
            pendingNotes.Add(new SubtitleExtra("webvtt", "note", BlockText()));
            return;
        }

        if (!timingFirst && IsKeyword(head, "STYLE"))
        {
            docExtras.Add(new SubtitleExtra("webvtt", "style", BlockText()));
            return;
        }

        if (!timingFirst && IsKeyword(head, "REGION"))
        {
            regions.Add(ParseRegion(src, lines, first, end));
            return;
        }

        int timing = timingFirst ? first : -1;
        if (!timingFirst && end - first >= 2 && text.AsSpan(lines[first + 1].Start, lines[first + 1].Length).Contains("-->", StringComparison.Ordinal))
        {
            timing = first + 1;
        }

        if (timing < 0)
        {
            src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, lines[first].Start, "Block is neither a cue, NOTE, STYLE nor REGION; skipped.", "subtitles.webvtt.stray-block");
            return;
        }

        src.CheckCue(cues.Count + 1, lines[first].Start);
        string? id = timing > first ? text.Substring(lines[first].Start, lines[first].Length) : null;
        ReadOnlySpan<char> tl = text.AsSpan(lines[timing].Start, lines[timing].Length);
        int arrow = tl.IndexOf("-->", StringComparison.Ordinal);
        if (!SrtTime.TryParse(tl[..arrow], out MediaTime start, out bool comma1, out _))
        {
            src.Report(DefectKind.InvalidValue, DefectSeverity.Error, lines[timing].Start, "Cue start time is not valid; cue skipped.", "subtitles.webvtt.time");
            return;
        }

        ReadOnlySpan<char> after = tl[(arrow + 3)..].TrimStart();
        int sp = 0;
        while (sp < after.Length && !char.IsWhiteSpace(after[sp]))
        {
            sp++;
        }

        if (!SrtTime.TryParse(after[..sp], out MediaTime endTime, out bool comma2, out _))
        {
            src.Report(DefectKind.InvalidValue, DefectSeverity.Error, lines[timing].Start + arrow + 3, "Cue end time is not valid; cue skipped.", "subtitles.webvtt.time");
            return;
        }

        if (comma1 || comma2)
        {
            src.Report(DefectKind.InvalidValue, DefectSeverity.Info, lines[timing].Start, "Comma used where WebVTT requires a full stop in a timestamp.", "subtitles.webvtt.time-comma");
        }

        if (endTime < start)
        {
            src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, lines[timing].Start, "Cue ends before it starts.", "subtitles.webvtt.end-before-start");
        }

        if (lastStart.IsKnown && start < lastStart)
        {
            src.Report(DefectKind.InvalidOrder, DefectSeverity.Info, lines[timing].Start, "Cue starts before the previous cue.", "subtitles.webvtt.order");
        }

        lastStart = start;
        var extras = new List<SubtitleExtra>(pendingNotes);
        pendingNotes.Clear();
        CuePlacement? placement = ParseSettings(src, after[sp..], lines[timing].Start + arrow + 3 + (tl[(arrow + 3)..].Length - after.Length) + sp, extras);

        var payload = new StringBuilder();
        for (int k = timing + 1; k < end; k++)
        {
            if (k > timing + 1)
            {
                payload.Append('\n');
            }

            payload.Append(text, lines[k].Start, lines[k].Length);
        }

        VttCueText.Parse(payload.ToString(), src, timing + 1 < end ? lines[timing + 1].Start : text.Length, endTime, out string plain, out List<SubtitleSpan> spans, out string? speaker);
        cues.Add(new SubtitleCue
        {
            Id = id,
            Start = start,
            End = endTime,
            Text = plain,
            Spans = SubtitleList.From(spans),
            Speaker = speaker,
            Placement = placement,
            Extras = SubtitleList.From(extras),
        });
    }

    private static CuePlacement? ParseSettings(SubtitleSource src, ReadOnlySpan<char> settings, int baseIndex, List<SubtitleExtra> extras)
    {
        CuePlacement p = new();
        bool any = false;
        int i = 0;
        while (i < settings.Length)
        {
            while (i < settings.Length && char.IsWhiteSpace(settings[i]))
            {
                i++;
            }

            int s = i;
            while (i < settings.Length && !char.IsWhiteSpace(settings[i]))
            {
                i++;
            }

            if (i == s)
            {
                break;
            }

            ReadOnlySpan<char> token = settings[s..i];
            int colon = token.IndexOf(':');
            ReadOnlySpan<char> name = colon > 0 ? token[..colon] : default;
            ReadOnlySpan<char> value = colon > 0 ? token[(colon + 1)..] : default;
            bool ok = false;
            if (name.SequenceEqual("vertical"))
            {
                if (value.SequenceEqual("rl"))
                {
                    p = p with { Writing = SubtitleWriting.VerticalRightToLeft };
                    ok = true;
                }
                else if (value.SequenceEqual("lr"))
                {
                    p = p with { Writing = SubtitleWriting.VerticalLeftToRight };
                    ok = true;
                }
            }
            else if (name.SequenceEqual("line"))
            {
                int comma = value.IndexOf(',');
                ReadOnlySpan<char> num = comma >= 0 ? value[..comma] : value;
                bool percent = num.EndsWith("%");
                if (percent)
                {
                    num = num[..^1];
                }

                if (SubtitleNumbers.TryParseDouble(num, out double v))
                {
                    p = p with { Line = new CueLine(v, percent ? CueLineUnit.Percent : CueLineUnit.Lines, !percent) };
                    ok = true;
                    if (comma >= 0)
                    {
                        ReadOnlySpan<char> al = value[(comma + 1)..];
                        if (al.SequenceEqual("start"))
                        {
                            p = p with { VerticalAlign = SubtitleVerticalAlign.Top };
                        }
                        else if (al.SequenceEqual("center"))
                        {
                            p = p with { VerticalAlign = SubtitleVerticalAlign.Middle };
                        }
                        else if (al.SequenceEqual("end"))
                        {
                            p = p with { VerticalAlign = SubtitleVerticalAlign.Bottom };
                        }
                        else
                        {
                            ok = false;
                        }
                    }
                }
            }
            else if (name.SequenceEqual("position"))
            {
                int comma = value.IndexOf(',');
                ReadOnlySpan<char> num = comma >= 0 ? value[..comma] : value;
                if (num.EndsWith("%") && SubtitleNumbers.TryParseDouble(num[..^1], out double v))
                {
                    p = p with { Position = v };
                    ok = true;
                    if (comma >= 0)
                    {
                        ReadOnlySpan<char> al = value[(comma + 1)..];
                        if (al.SequenceEqual("line-left"))
                        {
                            p = p with { PositionAnchor = SubtitleAlign.Left };
                        }
                        else if (al.SequenceEqual("center"))
                        {
                            p = p with { PositionAnchor = SubtitleAlign.Center };
                        }
                        else if (al.SequenceEqual("line-right"))
                        {
                            p = p with { PositionAnchor = SubtitleAlign.Right };
                        }
                        else
                        {
                            ok = false;
                        }
                    }
                }
            }
            else if (name.SequenceEqual("size"))
            {
                if (value.EndsWith("%") && SubtitleNumbers.TryParseDouble(value[..^1], out double v))
                {
                    p = p with { Size = v };
                    ok = true;
                }
            }
            else if (name.SequenceEqual("align"))
            {
                SubtitleAlign? a = value switch
                {
                    "start" => SubtitleAlign.Start,
                    "center" => SubtitleAlign.Center,
                    "end" => SubtitleAlign.End,
                    "left" => SubtitleAlign.Left,
                    "right" => SubtitleAlign.Right,
                    _ => null,
                };
                if (a is not null)
                {
                    p = p with { Align = a.Value };
                    ok = true;
                }
            }
            else if (name.SequenceEqual("region") && !value.IsEmpty)
            {
                p = p with { Region = value.ToString() };
                ok = true;
            }

            if (ok)
            {
                any = true;
            }
            else
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, baseIndex + s, $"Cue setting '{token}' is not valid; kept as an extra.", "subtitles.webvtt.setting");
                extras.Add(new SubtitleExtra("webvtt", "setting", token.ToString()));
            }
        }

        return any ? p : null;
    }

    private static SubtitleRegion ParseRegion(SubtitleSource src, List<(int Start, int Length)> lines, int first, int end)
    {
        string text = src.Text;
        string id = "";
        double? width = null;
        int? count = null;
        bool scroll = false;
        var extras = new List<SubtitleExtra>();
        double[]? regionAnchor = null;
        double[]? viewportAnchor = null;
        for (int k = first; k < end; k++)
        {
            ReadOnlySpan<char> line = text.AsSpan(lines[k].Start, lines[k].Length);
            if (k == first)
            {
                line = line[6..];
            }

            foreach (Range r in SplitWhitespace(line))
            {
                ReadOnlySpan<char> token = line[r];
                int colon = token.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                ReadOnlySpan<char> name = token[..colon];
                ReadOnlySpan<char> value = token[(colon + 1)..];
                if (name.SequenceEqual("id"))
                {
                    id = value.ToString();
                }
                else if (name.SequenceEqual("width") && value.EndsWith("%") && SubtitleNumbers.TryParseDouble(value[..^1], out double w))
                {
                    width = w;
                }
                else if (name.SequenceEqual("lines") && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ln))
                {
                    count = ln;
                }
                else if (name.SequenceEqual("scroll") && value.SequenceEqual("up"))
                {
                    scroll = true;
                }
                else if ((name.SequenceEqual("regionanchor") || name.SequenceEqual("viewportanchor")) && ParsePair(value, out double[] pair))
                {
                    if (name.SequenceEqual("regionanchor"))
                    {
                        regionAnchor = pair;
                    }
                    else
                    {
                        viewportAnchor = pair;
                    }

                    extras.Add(new SubtitleExtra("webvtt", name.ToString(), value.ToString()));
                }
                else
                {
                    src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, lines[k].Start, $"Region setting '{token}' is not valid; kept as an extra.", "subtitles.webvtt.region-setting");
                    extras.Add(new SubtitleExtra("webvtt", "setting", token.ToString()));
                }
            }
        }

        // The region's top-left corner follows from the two anchors when the region sits on its top edge (anchor y = 0).
        double? x = null;
        double? y = null;
        if (viewportAnchor is { } va && regionAnchor is { } ra)
        {
            if (width is { } wd)
            {
                x = va[0] - (ra[0] * wd / 100);
            }

            if (ra[1] == 0)
            {
                y = va[1];
            }
        }

        return new SubtitleRegion(id)
        {
            Width = width,
            Lines = count,
            ScrollUp = scroll,
            X = x,
            Y = y,
            VerticalAlign = SubtitleVerticalAlign.Bottom,
            Extras = SubtitleList.From(extras),
        };
    }

    private static bool ParsePair(ReadOnlySpan<char> value, out double[] pair)
    {
        pair = [0, 0];
        int comma = value.IndexOf(',');
        if (comma <= 0 || !value[..comma].EndsWith("%") || !value[(comma + 1)..].EndsWith("%"))
        {
            return false;
        }

        if (!SubtitleNumbers.TryParseDouble(value[..(comma - 1)], out double a) || !SubtitleNumbers.TryParseDouble(value[(comma + 1)..^1], out double b))
        {
            return false;
        }

        pair = [a, b];
        return true;
    }

    private static List<Range> SplitWhitespace(ReadOnlySpan<char> s)
    {
        var list = new List<Range>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i > st)
            {
                list.Add(st..i);
            }
        }

        return list;
    }

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        var sb = new StringBuilder();
        string header = "WEBVTT";
        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name && e.Key == "header")
            {
                header = e.Value.Replace("\n", nl, StringComparison.Ordinal);
            }
        }

        sb.Append(header).Append(nl).Append(nl);
        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name && e.Key == "style")
            {
                sb.Append(e.Value.Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
            }
        }

        foreach (SubtitleRegion r in doc.Regions)
        {
            WriteRegion(sb, r, nl, report);
        }

        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            foreach (SubtitleExtra e in cue.Extras)
            {
                if (e.Format == Name && e.Key == "note")
                {
                    sb.Append(e.Value.Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
                }
            }

            if (cue.Id is { } id && id.Length > 0)
            {
                sb.Append(id.Replace("-->", "--&gt;", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')).Append(nl);
            }

            sb.Append(SrtTime.Format(cue.Start, c, report, '.')).Append(" --> ").Append(SrtTime.Format(cue.End, c, report, '.'));
            AppendSettings(sb, cue, c, report);
            sb.Append(nl);
            VttCueText.Write(sb, cue, c, nl, report);
            sb.Append(nl).Append(nl);
        }

        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name && e.Key == "note-end")
            {
                sb.Append(e.Value.Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
            }
        }

        return sb.ToString();
    }

    private static void WriteRegion(StringBuilder sb, SubtitleRegion r, string nl, ConversionReport report)
    {
        sb.Append("REGION").Append(nl).Append("id:").Append(r.Id).Append(nl);
        if (r.Width is { } w)
        {
            sb.Append("width:").Append(SubtitleNumbers.Format(w)).Append('%').Append(nl);
        }

        if (r.Lines is { } lines)
        {
            sb.Append("lines:").Append(lines.ToString(CultureInfo.InvariantCulture)).Append(nl);
        }

        string? regionAnchor = null;
        string? viewportAnchor = null;
        foreach (SubtitleExtra e in r.Extras)
        {
            if (e.Format == "webvtt" && e.Key == "regionanchor")
            {
                regionAnchor = e.Value;
            }
            else if (e.Format == "webvtt" && e.Key == "viewportanchor")
            {
                viewportAnchor = e.Value;
            }
        }

        if (regionAnchor is null && viewportAnchor is null && r.X is { } x && r.Y is { } y)
        {
            regionAnchor = "0%,0%";
            viewportAnchor = string.Create(CultureInfo.InvariantCulture, $"{SubtitleNumbers.Format(x)}%,{SubtitleNumbers.Format(y)}%");
        }

        if (regionAnchor is not null)
        {
            sb.Append("regionanchor:").Append(regionAnchor).Append(nl);
        }

        if (viewportAnchor is not null)
        {
            sb.Append("viewportanchor:").Append(viewportAnchor).Append(nl);
        }

        if (r.ScrollUp)
        {
            sb.Append("scroll:up").Append(nl);
        }

        if (r.Height is not null && r.Lines is null)
        {
            report.Add(-1, SubtitleFeatures.Region, LossKind.Approximated, $"Region '{r.Id}' height is not carried; WebVTT sizes a region in lines.");
        }

        sb.Append(nl);
    }

    private static void AppendSettings(StringBuilder sb, SubtitleCue cue, int cueIndex, ConversionReport report)
    {
        foreach (SubtitleExtra e in cue.Extras)
        {
            if (e.Format == "webvtt" && e.Key == "setting")
            {
                sb.Append(' ').Append(e.Value);
            }
        }

        if (cue.Placement is not { } p)
        {
            return;
        }

        if (p.Region is { } region)
        {
            sb.Append(" region:").Append(region);
        }

        if (p.Writing == SubtitleWriting.VerticalRightToLeft)
        {
            sb.Append(" vertical:rl");
        }
        else if (p.Writing == SubtitleWriting.VerticalLeftToRight)
        {
            sb.Append(" vertical:lr");
        }

        string? lineAlign = p.VerticalAlign switch
        {
            SubtitleVerticalAlign.Top => "start",
            SubtitleVerticalAlign.Middle => "center",
            _ => null,
        };
        if (p.Line is { } line)
        {
            switch (line.Unit)
            {
                case CueLineUnit.Row:
                    // A character-grid row has no WebVTT counterpart; rows of a 15-row grid map to percent of the height.
                    sb.Append(" line:").Append(SubtitleNumbers.Format((line.Value - 1) * 100 / 15)).Append('%');
                    report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Grid row mapped to a percentage of the height (15 rows assumed).");
                    break;
                case CueLineUnit.Percent:
                    sb.Append(" line:").Append(SubtitleNumbers.Format(line.Value)).Append('%');
                    break;
                default:
                    sb.Append(" line:").Append(SubtitleNumbers.Format(line.Value));
                    break;
            }

            if (p.VerticalAlign == SubtitleVerticalAlign.Bottom || lineAlign is not null)
            {
                sb.Append(',').Append(lineAlign ?? "end");
            }
        }
        else if (p.Row is { } row)
        {
            sb.Append(" line:").Append(SubtitleNumbers.Format((row - 1) * 100.0 / 15)).Append('%');
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Grid row mapped to a percentage of the height (15 rows assumed).");
        }
        else if (p.VerticalAlign == SubtitleVerticalAlign.Top)
        {
            sb.Append(" line:0,start");
            report.Add(cueIndex, SubtitleFeatures.Alignment, LossKind.Approximated, "Top alignment written as line 0.");
        }
        else if (p.VerticalAlign == SubtitleVerticalAlign.Middle)
        {
            sb.Append(" line:50%,center");
            report.Add(cueIndex, SubtitleFeatures.Alignment, LossKind.Approximated, "Middle alignment written as line 50%.");
        }

        if (p.Position is { } position)
        {
            sb.Append(" position:").Append(SubtitleNumbers.Format(position)).Append('%');
            AppendAnchor(sb, p.PositionAnchor);
        }
        else if (p.Column is { } col)
        {
            sb.Append(" position:").Append(SubtitleNumbers.Format(col * 100.0 / 32)).Append("%,line-left");
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Grid column mapped to a percentage of the width (32 columns assumed).");
        }

        if (p.Size is { } size)
        {
            sb.Append(" size:").Append(SubtitleNumbers.Format(size)).Append('%');
        }

        string? align = p.Align switch
        {
            SubtitleAlign.Start => "start",
            SubtitleAlign.Left => "left",
            SubtitleAlign.Center => "center",
            SubtitleAlign.Right => "right",
            SubtitleAlign.End => "end",
            _ => null,
        };
        if (align is not null)
        {
            sb.Append(" align:").Append(align);
        }
    }

    private static void AppendAnchor(StringBuilder sb, SubtitleAlign anchor)
    {
        string? a = anchor switch
        {
            SubtitleAlign.Left => "line-left",
            SubtitleAlign.Center => "center",
            SubtitleAlign.Right => "line-right",
            _ => null,
        };
        if (a is not null)
        {
            sb.Append(',').Append(a);
        }
    }
}

// WebVTT cue text (WebVTT §6.1 "cue text parsing"): tags, entities and timestamp tags.
internal static class VttCueText
{
    private sealed class Element
    {
        internal string Name = "";
        internal int Start;
        internal string? Classes;
        internal string? Annotation;
    }

    internal static void Parse(string raw, SubtitleSource src, int baseIndex, MediaTime cueEnd, out string text, out List<SubtitleSpan> spans, out string? speaker)
    {
        var sb = new StringBuilder(raw.Length);
        spans = [];
        speaker = null;
        var stack = new List<Element>();
        var marks = new List<(int Pos, MediaTime Time)>();
        StringBuilder? rt = null;
        int rubyBase = -1;
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '&')
            {
                int semi = raw.IndexOf(';', i + 1, Math.Min(8, raw.Length - i - 1));
                string? decoded = semi > i ? raw.AsSpan(i + 1, semi - i - 1) switch
                {
                    "amp" => "&",
                    "lt" => "<",
                    "gt" => ">",
                    "lrm" => "\u200E",
                    "rlm" => "\u200F",
                    "nbsp" => "\u00A0",
                    _ => null,
                } : null;
                if (decoded is not null)
                {
                    (rt ?? sb).Append(decoded);
                    i = semi + 1;
                    continue;
                }
            }
            else if (c == '<')
            {
                int close = raw.IndexOf('>', i + 1);
                if (close > i)
                {
                    ReadOnlySpan<char> body = raw.AsSpan(i + 1, close - i - 1);
                    if (ProcessTag(body, src, baseIndex + i, sb, spans, stack, marks, ref rt, ref rubyBase))
                    {
                        i = close + 1;
                        continue;
                    }
                }
            }

            (rt ?? sb).Append(c);
            i++;
        }

        // Elements still open at the end of the cue text close there (WebVTT §6.1: end of input closes everything).
        for (int k = stack.Count - 1; k >= 0; k--)
        {
            Close(stack[k], sb.Length, spans);
        }

        for (int k = 0; k < marks.Count; k++)
        {
            int endPos = k + 1 < marks.Count ? marks[k + 1].Pos : sb.Length;
            MediaTime wordEnd = k + 1 < marks.Count ? marks[k + 1].Time : cueEnd;
            spans.Add(new SubtitleSpan(marks[k].Pos, Math.Max(0, endPos - marks[k].Pos)) { WordStart = marks[k].Time, WordEnd = wordEnd });
        }

        text = sb.ToString();

        // A voice that wraps the whole cue is the cue's speaker.
        string full = text;
        int voice = spans.FindIndex(s => s.Voice is not null && s.Start == 0 && s.Length == full.Length && full.Length > 0 && s.Style is null && s.Language is null && s.Ruby is null);
        if (voice >= 0)
        {
            speaker = spans[voice].Voice;
            spans.RemoveAt(voice);
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
    }

    private static bool ProcessTag(
        ReadOnlySpan<char> body,
        SubtitleSource src,
        int index,
        StringBuilder sb,
        List<SubtitleSpan> spans,
        List<Element> stack,
        List<(int Pos, MediaTime Time)> marks,
        ref StringBuilder? rt,
        ref int rubyBase)
    {
        if (body.Length > 0 && body[0] == '/')
        {
            string name = TagName(body[1..], out _, out _);
            if (name == "rt" && rt is not null)
            {
                int rtIndex = stack.FindLastIndex(e => e.Name == "rt");
                if (rtIndex >= 0 && rubyBase >= 0)
                {
                    int baseEnd = stack[rtIndex].Start;
                    if (baseEnd > rubyBase)
                    {
                        spans.Add(new SubtitleSpan(rubyBase, baseEnd - rubyBase) { Ruby = rt.ToString() });
                    }

                    rubyBase = sb.Length;
                    stack.RemoveRange(rtIndex, stack.Count - rtIndex);
                }

                rt = null;
                return true;
            }

            int k = stack.Count - 1;
            if (k >= 0 && stack[k].Name == name)
            {
                Close(stack[k], sb.Length, spans);
                stack.RemoveAt(k);
                if (name == "ruby")
                {
                    rubyBase = -1;
                }
            }
            else
            {
                src.Report(DefectKind.InvalidOrder, DefectSeverity.Info, index, $"End tag </{name}> does not match the open element; ignored.", "subtitles.webvtt.tag-unmatched");
            }

            return true;
        }

        // Timestamp tag: <00:01.500> or <00:00:01.500>
        if (body.Length > 0 && body[0] is >= '0' and <= '9' && body.IndexOf(':') > 0)
        {
            if (SrtTime.TryParse(body, out MediaTime t, out _, out _))
            {
                marks.Add((sb.Length, t));
            }
            else
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, index, "Timestamp tag is not valid; ignored.", "subtitles.webvtt.timestamp");
            }

            return true;
        }

        string tag = TagName(body, out string? classes, out string? annotation);
        switch (tag)
        {
            case "c" or "i" or "b" or "u" or "v" or "lang" or "ruby":
                stack.Add(new Element { Name = tag, Start = sb.Length, Classes = classes, Annotation = annotation });
                if (tag == "ruby")
                {
                    rubyBase = sb.Length;
                }

                return true;
            case "rt":
                stack.Add(new Element { Name = "rt", Start = sb.Length });
                rt = new StringBuilder();
                return true;
            default:
                src.Report(DefectKind.Unsupported, DefectSeverity.Info, index, $"Unknown tag <{tag}> ignored.", "subtitles.webvtt.tag-unknown");
                return tag.Length > 0;
        }
    }

    private static string TagName(ReadOnlySpan<char> body, out string? classes, out string? annotation)
    {
        classes = null;
        annotation = null;
        int i = 0;
        while (i < body.Length && body[i] != '.' && !char.IsWhiteSpace(body[i]))
        {
            i++;
        }

        string name = body[..i].ToString();
        if (i < body.Length && body[i] == '.')
        {
            int s = i + 1;
            while (i < body.Length && !char.IsWhiteSpace(body[i]))
            {
                i++;
            }

            classes = body[s..i].ToString().Replace('.', ' ');
        }

        ReadOnlySpan<char> rest = body[i..].Trim();
        if (!rest.IsEmpty)
        {
            annotation = rest.ToString();
        }

        return name;
    }

    private static void Close(Element e, int end, List<SubtitleSpan> spans)
    {
        if (e.Name == "rt" || e.Name == "ruby")
        {
            return;
        }

        TextStyle? style = null;
        switch (e.Name)
        {
            case "i":
                style = new TextStyle { Italic = true };
                break;
            case "b":
                style = new TextStyle { Bold = true };
                break;
            case "u":
                style = new TextStyle { Underline = true };
                break;
        }

        string? leftover = null;
        SubtitleColor? color = null;
        SubtitleColor? background = null;
        if (e.Classes is not null)
        {
            var rest = new List<string>();
            foreach (string cls in e.Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // WebVTT's default colour classes: .white .lime … and .bg_white … (W3C WebVTT, default stylesheet).
                bool matched = false;
                foreach ((string n, SubtitleColor col) in SubtitleColors.WebVttClasses)
                {
                    if (cls == n)
                    {
                        color = col;
                        matched = true;
                        break;
                    }

                    if (cls == "bg_" + n)
                    {
                        background = col;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    rest.Add(cls);
                }
            }

            leftover = rest.Count > 0 ? string.Join(' ', rest) : null;
        }

        if (color is not null || background is not null || leftover is not null)
        {
            style = (style ?? new TextStyle()) with { Color = color, Background = background, Classes = leftover };
        }

        string? voice = e.Name == "v" ? (e.Annotation ?? "") : null;
        string? lang = e.Name == "lang" ? e.Annotation : null;
        if (style is null && voice is null && lang is null)
        {
            return;
        }

        int length = end - e.Start;
        if (length <= 0 && voice is null)
        {
            return;
        }

        spans.Add(new SubtitleSpan(e.Start, Math.Max(length, 0)) { Style = style, Voice = voice, Language = lang });
    }

    internal static void Write(StringBuilder sb, SubtitleCue cue, int cueIndex, string nl, ConversionReport report)
    {
        var intervals = new List<MarkupInterval>();
        var points = new List<MarkupPoint>();
        if (cue.Speaker is { } speaker && cue.Text.Length > 0)
        {
            intervals.Add(new MarkupInterval(0, cue.Text.Length, "<v " + Escape(speaker) + ">", "</v>", -2));
        }

        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.WordStart is { } ws)
            {
                points.Add(new MarkupPoint(span.Start, "<" + SrtTime.Format(ws, cueIndex, report, '.') + ">"));
            }

            if (span.Length == 0)
            {
                continue;
            }

            if (span.Voice is { } voice)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<v " + Escape(voice) + ">", "</v>", -1));
            }

            if (span.Language is { } lang)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<lang " + Escape(lang) + ">", "</lang>", 0));
            }

            if (span.Ruby is { } ruby)
            {
                var annotation = new StringBuilder();
                Escape(annotation, ruby);
                intervals.Add(new MarkupInterval(span.Start, span.End, "<ruby>", "<rt>" + annotation + "</rt></ruby>", 1));
            }

            if (span.Style is not { } st)
            {
                continue;
            }

            if (st.Bold == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<b>", "</b>", 2));
            }

            if (st.Italic == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<i>", "</i>", 3));
            }

            if (st.Underline == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<u>", "</u>", 4));
            }

            if (st.Color is not null || st.Background is not null || st.Classes is not null)
            {
                var cls = new StringBuilder("<c");
                if (st.Color is { } col)
                {
                    string name = SubtitleColors.NearestWebVttClass(col, out bool exact);
                    cls.Append('.').Append(name);
                    if (!exact || col.A != 255)
                    {
                        report.Add(cueIndex, SubtitleFeatures.Color, LossKind.Approximated, $"Colour mapped to the nearest WebVTT default colour class '{name}'.");
                    }
                }

                if (st.Background is { } bg)
                {
                    string name = SubtitleColors.NearestWebVttClass(bg, out bool exact);
                    cls.Append(".bg_").Append(name);
                    if (!exact || bg.A != 255)
                    {
                        report.Add(cueIndex, SubtitleFeatures.Background, LossKind.Approximated, $"Background mapped to the nearest WebVTT default colour class 'bg_{name}'.");
                    }
                }

                if (st.Classes is { } classes)
                {
                    foreach (string k in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        cls.Append('.').Append(k.Replace('.', '_').Replace('>', '_'));
                    }
                }

                cls.Append('>');
                intervals.Add(new MarkupInterval(span.Start, span.End, cls.ToString(), "</c>", 5));
            }
        }

        string plain = cue.Text;
        if (MarkupWriter.HasBlankLines(plain))
        {
            report.Add(cueIndex, SubtitleFeatures.Text, LossKind.Approximated, "Empty lines are not allowed inside a WebVTT cue; a no-break space was written.");
            plain = MarkupWriter.FillBlankLines(plain, intervals, points, '\u00A0');
        }

        MarkupWriter.Write(sb, plain, intervals, points, (o, s) =>
        {
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '&':
                        o.Append("&amp;");
                        break;
                    case '<':
                        o.Append("&lt;");
                        break;
                    case '>':
                        o.Append("&gt;");
                        break;
                    case '\n':
                        o.Append(nl);
                        break;
                    default:
                        o.Append(ch);
                        break;
                }
            }
        });
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        Escape(sb, s);
        return sb.ToString();
    }

    private static void Escape(StringBuilder sb, string s)
    {
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '\n' or '\r':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
    }
}

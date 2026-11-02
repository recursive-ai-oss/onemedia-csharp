using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Captions;

// SubRip (.srt). There is no formal standard; the layout is the one every player reads (numbered blocks, "-->" time lines,
// <b>/<i>/<u>/<s>/<font> markup). Observed in files written by ffmpeg 7.0.2 and by common subtitle editors.
internal sealed class SrtFormat : SubtitleFormat
{
    internal static readonly SrtFormat Instance = new();

    private static readonly string[] Exts = [".srt"];

    private SrtFormat()
    {
    }

    public override string Name => "srt";

    public override string DisplayName => "SubRip (SRT)";

    public override IReadOnlyList<string> Extensions => Exts;

    public override SubtitleFeatures Features =>
        SubtitleFeatures.Bold | SubtitleFeatures.Italic | SubtitleFeatures.Underline | SubtitleFeatures.Strike
        | SubtitleFeatures.Color | SubtitleFeatures.Font | SubtitleFeatures.Alignment | SubtitleFeatures.Extras;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        int pos = 0;
        int seen = 0;
        bool previousDigits = false;
        while (pos < head.Length && seen < 30)
        {
            int nl = head[pos..].IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = nl < 0 ? head[pos..] : head.Slice(pos, nl);
            pos += nl < 0 ? line.Length : nl + 1;
            line = line.Trim().Trim('\uFEFF');
            if (line.IsEmpty)
            {
                continue;
            }

            seen++;
            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow > 0 && SrtTime.TryParse(line[..arrow], out _, out bool comma, out _))
            {
                return comma ? (previousDigits ? 95 : 85) : 55;
            }

            previousDigits = IsDigits(line);
        }

        return 0;
    }

    internal static bool IsDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty || s.Length > 9)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
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
        int i = 0;
        int n = lines.Count;
        while (i < n)
        {
            if (IsBlank(text, lines[i]))
            {
                i++;
                continue;
            }

            string? index = null;
            ReadOnlySpan<char> first = text.AsSpan(lines[i].Start, lines[i].Length).Trim();
            if (IsDigits(first) && i + 1 < n && TimeLine(text, lines[i + 1]) is not null)
            {
                index = first.ToString();
                i++;
            }

            TimeLineInfo? tl = TimeLine(text, lines[i]);
            if (tl is null)
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, lines[i].Start, "Expected a cue time line; line skipped.", "subtitles.srt.stray-line");
                i++;
                continue;
            }

            src.CheckCue(cues.Count + 1, lines[i].Start);
            int timeLineStart = lines[i].Start;
            i++;
            var body = new StringBuilder();
            int bodyStart = i < n ? lines[i].Start : text.Length;
            bool firstLine = true;
            while (i < n)
            {
                if (IsBlank(text, lines[i]))
                {
                    int j = i;
                    while (j < n && IsBlank(text, lines[j]))
                    {
                        j++;
                    }

                    if (j >= n || StartsCue(text, lines, j))
                    {
                        i = j;
                        break;
                    }

                    src.Report(DefectKind.Inconsistent, DefectSeverity.Warning, lines[i].Start, "Blank line inside cue text; kept.", "subtitles.srt.blank-in-text");
                    for (int k = i; k < j; k++)
                    {
                        if (!firstLine)
                        {
                            body.Append('\n');
                        }
                    }

                    i = j;
                    continue;
                }

                if (!firstLine)
                {
                    body.Append('\n');
                }

                body.Append(text, lines[i].Start, lines[i].Length);
                firstLine = false;
                i++;
            }

            if (tl.Value.End < tl.Value.Start)
            {
                src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, timeLineStart, "Cue ends before it starts.", "subtitles.srt.end-before-start");
            }

            SrtMarkup.Parse(body.ToString(), src, bodyStart, out string plain, out List<SubtitleSpan> spans, out CuePlacement? placement);
            var extras = new List<SubtitleExtra>();
            string expected = (cues.Count + 1).ToString(CultureInfo.InvariantCulture);
            if (index is not null && index != expected)
            {
                extras.Add(new SubtitleExtra("srt", "index", index));
                src.Report(DefectKind.Inconsistent, DefectSeverity.Info, timeLineStart, $"Cue number {index} where {expected} was expected.", "subtitles.srt.index");
            }

            if (tl.Value.Coordinates is { } coords)
            {
                extras.Add(new SubtitleExtra("srt", "coordinates", coords));
            }

            cues.Add(new SubtitleCue
            {
                Start = tl.Value.Start,
                End = tl.Value.End,
                Text = plain,
                Spans = SubtitleList.From(spans),
                Placement = placement,
                Extras = SubtitleList.From(extras),
            });
        }

        return new SubtitleDocument { Cues = SubtitleList.From(cues), NewLine = src.NewLine };
    }

    private static bool IsBlank(string text, (int Start, int Length) line)
    {
        for (int k = 0; k < line.Length; k++)
        {
            if (!char.IsWhiteSpace(text[line.Start + k]) && text[line.Start + k] != '\uFEFF')
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsCue(string text, List<(int Start, int Length)> lines, int j)
    {
        ReadOnlySpan<char> l = text.AsSpan(lines[j].Start, lines[j].Length).Trim();
        if (IsDigits(l) && j + 1 < lines.Count && TimeLine(text, lines[j + 1]) is not null)
        {
            return true;
        }

        return TimeLine(text, lines[j]) is not null;
    }

    private readonly record struct TimeLineInfo(MediaTime Start, MediaTime End, string? Coordinates);

    private static TimeLineInfo? TimeLine(string text, (int Start, int Length) line)
    {
        ReadOnlySpan<char> l = text.AsSpan(line.Start, line.Length);
        int arrow = l.IndexOf("-->", StringComparison.Ordinal);
        if (arrow <= 0)
        {
            return null;
        }

        if (!SrtTime.TryParse(l[..arrow], out MediaTime start, out _, out _))
        {
            return null;
        }

        ReadOnlySpan<char> rest = l[(arrow + 3)..].TrimStart();
        int sp = 0;
        while (sp < rest.Length && !char.IsWhiteSpace(rest[sp]))
        {
            sp++;
        }

        if (!SrtTime.TryParse(rest[..sp], out MediaTime end, out _, out _))
        {
            return null;
        }

        ReadOnlySpan<char> tail = rest[sp..].Trim();
        return new TimeLineInfo(start, end, tail.IsEmpty ? null : tail.ToString());
    }

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        var sb = new StringBuilder();
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            string number = (c + 1).ToString(CultureInfo.InvariantCulture);
            string? coords = null;
            foreach (SubtitleExtra e in cue.Extras)
            {
                if (e.Format == "srt" && e.Key == "index")
                {
                    number = e.Value;
                }
                else if (e.Format == "srt" && e.Key == "coordinates")
                {
                    coords = e.Value;
                }
            }

            sb.Append(number).Append(nl);
            sb.Append(SrtTime.Format(cue.Start, c, report, ',')).Append(" --> ").Append(SrtTime.Format(cue.End, c, report, ','));
            if (coords is not null)
            {
                sb.Append(' ').Append(coords);
            }

            sb.Append(nl);
            SrtMarkup.Write(sb, cue, c, nl, report);
            sb.Append(nl).Append(nl);
        }

        return sb.ToString();
    }
}

// "HH:MM:SS,mmm". Shared by SRT and, with a dot, by the tolerant parts of other formats.
internal static class SrtTime
{
    /// <summary>Parses [H+:]MM:SS[,.]fff and reports whether the comma form (the SRT standard) was used and whether it was strictly standard.</summary>
    internal static bool TryParse(ReadOnlySpan<char> s, out MediaTime time, out bool comma, out bool standard)
    {
        time = default;
        comma = false;
        standard = false;
        s = s.Trim();
        if (s.Length < 4)
        {
            return false;
        }

        int sep = s.LastIndexOfAny(',', '.');
        ReadOnlySpan<char> clock = sep < 0 ? s : s[..sep];
        ReadOnlySpan<char> frac = sep < 0 ? default : s[(sep + 1)..];
        comma = sep >= 0 && s[sep] == ',';
        foreach (char ch in frac)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        if (sep >= 0 && frac.IsEmpty)
        {
            return false;
        }

        int c1 = clock.IndexOf(':');
        if (c1 < 0)
        {
            return false;
        }

        int c2 = clock[(c1 + 1)..].IndexOf(':');
        ReadOnlySpan<char> hh;
        ReadOnlySpan<char> mm;
        ReadOnlySpan<char> ss;
        if (c2 < 0)
        {
            hh = default;
            mm = clock[..c1];
            ss = clock[(c1 + 1)..];
        }
        else
        {
            c2 += c1 + 1;
            hh = clock[..c1];
            mm = clock[(c1 + 1)..c2];
            ss = clock[(c2 + 1)..];
        }

        if (!TryNumber(mm, out long m) || !TryNumber(ss, out long sec) || m > 59_999 || sec > 59_999)
        {
            return false;
        }

        long h = 0;
        if (!hh.IsEmpty && (!TryNumber(hh, out h) || h > 9_999_999))
        {
            return false;
        }

        long ms = SubtitleNumbers.FractionTicks(frac, 1000, out _);
        long total = (((((h * 60) + m) * 60) + sec) * 1000) + ms;
        time = new MediaTime(total, 1000);
        standard = comma && hh.Length >= 2 && mm.Length == 2 && ss.Length == 2 && frac.Length == 3;
        return true;
    }

    private static bool TryNumber(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        if (s.IsEmpty || s.Length > 9)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }

    internal static string Format(MediaTime t, int cue, ConversionReport report, char separator)
    {
        long ms = SubtitleLoss.Quantize(t, 1000, cue, SubtitleFeatures.Timing, report, "Time");
        if (ms < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            ms = 0;
        }

        long totalSeconds = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}{separator}{ms % 1000:000}");
    }
}

// ASS-style numpad alignment (\an1..\an9) shared by SRT's brace extension and ASS.
internal static class NumpadAlignment
{
    internal static void From(int an, out SubtitleAlign align, out SubtitleVerticalAlign vertical)
    {
        align = ((an - 1) % 3) switch { 0 => SubtitleAlign.Left, 1 => SubtitleAlign.Center, _ => SubtitleAlign.Right };
        vertical = ((an - 1) / 3) switch { 0 => SubtitleVerticalAlign.Bottom, 1 => SubtitleVerticalAlign.Middle, _ => SubtitleVerticalAlign.Top };
    }

    // 0 when the placement states neither alignment.
    internal static int To(SubtitleAlign align, SubtitleVerticalAlign vertical)
    {
        if (align == SubtitleAlign.Unspecified && vertical == SubtitleVerticalAlign.Unspecified)
        {
            return 0;
        }

        int col = align switch { SubtitleAlign.Left or SubtitleAlign.Start => 0, SubtitleAlign.Right or SubtitleAlign.End => 2, _ => 1 };
        int row = vertical switch { SubtitleVerticalAlign.Top => 2, SubtitleVerticalAlign.Middle => 1, _ => 0 };
        return (row * 3) + col + 1;
    }
}

internal static class SrtMarkup
{
    private sealed record Open(string Name, int Start, TextStyle Style);

    internal static void Parse(string raw, SubtitleSource src, int baseIndex, out string text, out List<SubtitleSpan> spans, out CuePlacement? placement)
    {
        var sb = new StringBuilder(raw.Length);
        spans = [];
        placement = null;
        var open = new List<Open>();
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '<')
            {
                int close = raw.IndexOf('>', i + 1);
                if (close > i && TryTag(raw.AsSpan(i + 1, close - i - 1), out bool closing, out string name, out TextStyle? style))
                {
                    if (!closing)
                    {
                        open.Add(new Open(name, sb.Length, style!));
                    }
                    else
                    {
                        int k = open.FindLastIndex(o => o.Name == name);
                        if (k < 0)
                        {
                            src.Report(DefectKind.InvalidOrder, DefectSeverity.Info, baseIndex + i, $"Closing </{name}> without an opening tag; ignored.", "subtitles.srt.tag-unmatched");
                        }
                        else
                        {
                            if (sb.Length > open[k].Start)
                            {
                                spans.Add(new SubtitleSpan(open[k].Start, sb.Length - open[k].Start) { Style = open[k].Style });
                            }

                            open.RemoveAt(k);
                        }
                    }

                    i = close + 1;
                    continue;
                }
            }
            else if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '\\')
            {
                int close = raw.IndexOf('}', i + 2);
                if (close > 0)
                {
                    string block = raw.Substring(i + 1, close - i - 1);
                    if (block.StartsWith("\\an", StringComparison.Ordinal) && block.Length >= 4 && block[3] is >= '1' and <= '9')
                    {
                        NumpadAlignment.From(block[3] - '0', out SubtitleAlign a, out SubtitleVerticalAlign v);
                        placement = new CuePlacement { Align = a, VerticalAlign = v };
                        block = block[4..];
                    }

                    if (block.Length > 0)
                    {
                        spans.Add(new SubtitleSpan(sb.Length, 0) { Raw = new SubtitleExtra("srt", "override", block) });
                    }

                    i = close + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        foreach (Open o in open)
        {
            src.Report(DefectKind.Truncated, DefectSeverity.Info, baseIndex + raw.Length, $"Tag <{o.Name}> never closed; closed at the end of the cue.", "subtitles.srt.tag-unclosed");
            if (sb.Length > o.Start)
            {
                spans.Add(new SubtitleSpan(o.Start, sb.Length - o.Start) { Style = o.Style });
            }
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        text = sb.ToString();
    }

    private static bool TryTag(ReadOnlySpan<char> body, out bool closing, out string name, out TextStyle? style)
    {
        closing = false;
        name = "";
        style = null;
        body = body.Trim();
        if (body.StartsWith("/"))
        {
            closing = true;
            body = body[1..].Trim();
        }

        int sp = 0;
        while (sp < body.Length && !char.IsWhiteSpace(body[sp]))
        {
            sp++;
        }

        string tag = body[..sp].ToString().ToLowerInvariant();
        ReadOnlySpan<char> attrs = body[sp..];
        switch (tag)
        {
            case "b":
                style = new TextStyle { Bold = true };
                break;
            case "i":
                style = new TextStyle { Italic = true };
                break;
            case "u":
                style = new TextStyle { Underline = true };
                break;
            case "s" or "strike":
                tag = "s";
                style = new TextStyle { Strike = true };
                break;
            case "font":
                style = closing ? new TextStyle() : FontStyle(attrs);
                break;
            default:
                return false;
        }

        name = tag;
        return true;
    }

    private static TextStyle FontStyle(ReadOnlySpan<char> attrs)
    {
        SubtitleColor? color = null;
        string? face = null;
        SubtitleLength? size = null;
        int i = 0;
        while (i < attrs.Length)
        {
            while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
            {
                i++;
            }

            int ks = i;
            while (i < attrs.Length && attrs[i] != '=' && !char.IsWhiteSpace(attrs[i]))
            {
                i++;
            }

            ReadOnlySpan<char> key = attrs[ks..i];
            while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
            {
                i++;
            }

            if (i >= attrs.Length || attrs[i] != '=')
            {
                continue;
            }

            i++;
            while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
            {
                i++;
            }

            ReadOnlySpan<char> value;
            if (i < attrs.Length && attrs[i] is '"' or '\'')
            {
                char q = attrs[i++];
                int vs = i;
                while (i < attrs.Length && attrs[i] != q)
                {
                    i++;
                }

                value = attrs[vs..i];
                if (i < attrs.Length)
                {
                    i++;
                }
            }
            else
            {
                int vs = i;
                while (i < attrs.Length && !char.IsWhiteSpace(attrs[i]))
                {
                    i++;
                }

                value = attrs[vs..i];
            }

            if (key.Equals("color", StringComparison.OrdinalIgnoreCase) && SubtitleColors.TryParse(value, out SubtitleColor col))
            {
                color = col;
            }
            else if (key.Equals("face", StringComparison.OrdinalIgnoreCase))
            {
                face = value.ToString();
            }
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase) && SubtitleNumbers.TryParseDouble(value, out double sz) && sz > 0)
            {
                size = new SubtitleLength(sz, SubtitleLengthUnit.Pixel);
            }
        }

        return new TextStyle { Color = color, FontFamily = face, FontSize = size };
    }

    internal static void Write(StringBuilder sb, SubtitleCue cue, int cueIndex, string nl, ConversionReport report)
    {
        var intervals = new List<MarkupInterval>();
        var points = new List<MarkupPoint>();
        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.Length == 0)
            {
                if (span.Raw is { Format: "srt", Key: "override" } raw)
                {
                    points.Add(new MarkupPoint(span.Start, "{" + raw.Value + "}"));
                }

                continue;
            }

            if (span.Style is not { } st)
            {
                continue;
            }

            if (st.Color is not null || st.FontFamily is not null || st.FontSize is not null)
            {
                var font = new StringBuilder("<font");
                if (st.Color is { } col)
                {
                    font.Append(" color=\"").Append(SubtitleColors.Hex(col)).Append('"');
                    if (col.A != 255)
                    {
                        report.Add(cueIndex, SubtitleFeatures.Color, LossKind.Approximated, "Colour opacity is not carried by SRT.");
                    }
                }

                if (st.FontFamily is { } face)
                {
                    font.Append(" face=\"").Append(face.Replace("\"", "'", StringComparison.Ordinal)).Append('"');
                }

                if (st.FontSize is { } size)
                {
                    font.Append(" size=\"").Append(SubtitleNumbers.Format(size.Value)).Append('"');
                    if (size.Unit != SubtitleLengthUnit.Pixel)
                    {
                        report.Add(cueIndex, SubtitleFeatures.Font, LossKind.Approximated, "Font size unit is not carried; the number is written as is.");
                    }
                }

                font.Append('>');
                intervals.Add(new MarkupInterval(span.Start, span.End, font.ToString(), "</font>", 0));
            }

            if (st.Bold == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<b>", "</b>", 1));
            }

            if (st.Italic == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<i>", "</i>", 2));
            }

            if (st.Underline == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<u>", "</u>", 3));
            }

            if (st.Strike == true)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<s>", "</s>", 4));
            }
        }

        string plain = cue.Text;
        if (MarkupWriter.HasBlankLines(plain))
        {
            report.Add(cueIndex, SubtitleFeatures.Text, LossKind.Approximated, "Empty lines are not allowed inside an SRT cue; a space was written.");
            plain = MarkupWriter.FillBlankLines(plain, intervals, points, ' ');
        }

        if (cue.Placement is { } p && NumpadAlignment.To(p.Align, p.VerticalAlign) is int an and > 0)
        {
            sb.Append("{\\an").Append(an.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        MarkupWriter.Write(sb, plain, intervals, points, (o, s) =>
        {
            foreach (char ch in s)
            {
                if (ch == '\n')
                {
                    o.Append(nl);
                }
                else
                {
                    o.Append(ch);
                }
            }
        });
    }
}

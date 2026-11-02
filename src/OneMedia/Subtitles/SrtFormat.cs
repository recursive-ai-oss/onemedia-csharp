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
        LineTable lines = src.ReadLines();

        if (lines.Count > 0 && lines[0].Length > 0 && text[lines[0].Start] == '\uFEFF')
        {
            lines[0] = (lines[0].Start + 1, lines[0].Length - 1);
        }

        // The line after a cue is looked at twice (as the end of the cue, then as the start of the next one): remember the last answer.
        int cachedLine = -1;
        TimeLineInfo? cachedInfo = null;
        TimeLineInfo? TimeLineAt(int k)
        {
            if (k != cachedLine)
            {
                cachedInfo = TimeLine(text, lines[k]);
                cachedLine = k;
            }

            return cachedInfo;
        }

        bool StartsCueAt(int j)
        {
            ReadOnlySpan<char> l = text.AsSpan(lines[j].Start, lines[j].Length).Trim();
            if (IsDigits(l) && j + 1 < lines.Count && TimeLineAt(j + 1) is not null)
            {
                return true;
            }

            return TimeLineAt(j) is not null;
        }

        var cues = new List<SubtitleCue>(Math.Min(Math.Max(4, lines.Count / 4), 1 << 20));
        int i = 0;
        int n = lines.Count;
        while (i < n)
        {
            if (IsBlank(text, lines[i]))
            {
                i++;
                continue;
            }

            ReadOnlySpan<char> first = text.AsSpan(lines[i].Start, lines[i].Length).Trim();
            bool hasIndex = false;
            if (IsDigits(first) && i + 1 < n && TimeLineAt(i + 1) is not null)
            {
                hasIndex = true;
                i++;
            }
            else
            {
                first = default;
            }

            TimeLineInfo? tl = TimeLineAt(i);
            if (tl is null)
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, lines[i].Start, "Expected a cue time line; line skipped.", "subtitles.srt.stray-line");
                i++;
                continue;
            }

            src.CheckCue(cues.Count + 1, lines[i].Start);
            int timeLineStart = lines[i].Start;
            i++;

            // The body is the run of non-blank lines, joined with \n. A blank line inside the text (kept, with a defect) is rare and
            // switches to a builder.
            StringBuilder? body = null;
            int bodyFirst = i;
            int bodyEnd = i;
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

                    if (j >= n || StartsCueAt(j))
                    {
                        i = j;
                        break;
                    }

                    src.Report(DefectKind.Inconsistent, DefectSeverity.Warning, lines[i].Start, "Blank line inside cue text; kept.", "subtitles.srt.blank-in-text");
                    if (body is null)
                    {
                        body = new StringBuilder();
                        for (int k = bodyFirst; k < bodyEnd; k++)
                        {
                            if (k > bodyFirst)
                            {
                                body.Append('\n');
                            }

                            body.Append(text, lines[k].Start, lines[k].Length);
                        }

                        firstLine = bodyEnd == bodyFirst;
                    }

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

                if (body is null)
                {
                    bodyEnd = i + 1;
                }
                else
                {
                    if (!firstLine)
                    {
                        body.Append('\n');
                    }

                    body.Append(text, lines[i].Start, lines[i].Length);
                }

                firstLine = false;
                i++;
            }

            if (tl.Value.End < tl.Value.Start)
            {
                src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, timeLineStart, "Cue ends before it starts.", "subtitles.srt.end-before-start");
            }

            string bodyText = body is null ? SubtitleSource.JoinLines(text, lines, bodyFirst, bodyEnd) : body.ToString();
            string plain;
            SubtitleList<SubtitleSpan> spanList;
            CuePlacement? placement = null;
            if (bodyText.AsSpan().IndexOfAny('<', '{') < 0)
            {
                plain = bodyText;
                spanList = SubtitleList.Empty<SubtitleSpan>();
            }
            else
            {
                SrtMarkup.Parse(bodyText, src, bodyStart, out plain, out List<SubtitleSpan> spans, out placement);
                spanList = SubtitleList.From(spans);
            }

            // The number is kept only when it is not the one a plain count would give ("007" is not "7").
            List<SubtitleExtra>? extras = null;
            int expected = cues.Count + 1;
            if (hasIndex && !IsExpectedIndex(first, expected))
            {
                string index = first.ToString();
                extras = [new SubtitleExtra("srt", "index", index)];
                src.Report(DefectKind.Inconsistent, DefectSeverity.Info, timeLineStart, $"Cue number {index} where {expected.ToString(CultureInfo.InvariantCulture)} was expected.", "subtitles.srt.index");
            }

            if (tl.Value.Coordinates is { } coords)
            {
                (extras ??= []).Add(new SubtitleExtra("srt", "coordinates", coords));
            }

            cues.Add(new SubtitleCue
            {
                Start = tl.Value.Start,
                End = tl.Value.End,
                Text = plain,
                Spans = spanList,
                Placement = placement,
                Extras = extras is null ? SubtitleList.Empty<SubtitleExtra>() : SubtitleList.From(extras),
            });
        }

        return new SubtitleDocument { Cues = SubtitleList.From(cues), NewLine = src.NewLine };
    }

    // digits (at most nine, no sign) that are the decimal form of value, with no leading zero.
    private static bool IsExpectedIndex(ReadOnlySpan<char> digits, int value)
    {
        if (digits.IsEmpty || digits[0] == '0')
        {
            return false;
        }

        int v = 0;
        foreach (char c in digits)
        {
            v = (v * 10) + (c - '0');
        }

        return v == value;
    }

    private static bool IsBlank(string text, (int Start, int Length) line)
    {
        for (int k = 0; k < line.Length; k++)
        {
            char c = text[line.Start + k];
            if (!char.IsWhiteSpace(c) && c != '\uFEFF')
            {
                return false;
            }
        }

        return true;
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
        var sb = new StringBuilder(MarkupWriter.EstimateCapacity(doc.Cues.Count, 96));
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            string? number = null;
            string? coords = null;
            for (int e = 0; e < cue.Extras.Count; e++)
            {
                SubtitleExtra extra = cue.Extras[e];
                if (extra.Format == "srt" && extra.Key == "index")
                {
                    number = extra.Value;
                }
                else if (extra.Format == "srt" && extra.Key == "coordinates")
                {
                    coords = extra.Value;
                }
            }

            if (number is null)
            {
                sb.Append(c + 1);
            }
            else
            {
                sb.Append(number);
            }

            sb.Append(nl);
            SrtTime.Append(sb, cue.Start, c, report, ',');
            sb.Append(" --> ");
            SrtTime.Append(sb, cue.End, c, report, ',');
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
        if (!s.IsEmpty && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])))
        {
            s = s.Trim();
        }

        if (TryParseSimple(s, out time, out comma, out standard))
        {
            return true;
        }

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

    // One pass over the usual shape: digits ':' digits [':' digits] [(',' | '.') digits], nothing else (no spaces, no signs). Anything
    // else, and every value outside the ranges below, goes to the general parser, which decides what is valid.
    private static bool TryParseSimple(ReadOnlySpan<char> s, out MediaTime time, out bool comma, out bool standard)
    {
        time = default;
        comma = false;
        standard = false;
        if (s.Length < 4 || s.Length > 24)
        {
            return false;
        }

        long f0 = 0;
        long f1 = 0;
        long f2 = 0;
        int d0 = 0;
        int d1 = 0;
        int d2 = 0;
        int colons = 0;
        int i = 0;
        for (; i < s.Length; i++)
        {
            uint d = (uint)(s[i] - '0');
            if (d <= 9)
            {
                switch (colons)
                {
                    case 0:
                        f0 = (f0 * 10) + d;
                        d0++;
                        break;
                    case 1:
                        f1 = (f1 * 10) + d;
                        d1++;
                        break;
                    default:
                        f2 = (f2 * 10) + d;
                        d2++;
                        break;
                }
            }
            else if (s[i] == ':' && colons < 2)
            {
                colons++;
            }
            else
            {
                break;
            }
        }

        if (colons == 0 || d0 == 0 || d1 == 0 || d0 > 9 || d1 > 9 || d2 > 9 || (colons == 2 && d2 == 0))
        {
            return false;
        }

        long hours;
        long minutes;
        long seconds;
        int hourDigits;
        int minuteDigits;
        int secondDigits;
        if (colons == 2)
        {
            hours = f0;
            minutes = f1;
            seconds = f2;
            hourDigits = d0;
            minuteDigits = d1;
            secondDigits = d2;
        }
        else
        {
            hours = 0;
            minutes = f0;
            seconds = f1;
            hourDigits = 0;
            minuteDigits = d0;
            secondDigits = d1;
        }

        int fracDigits = 0;
        long ms = 0;
        bool isComma = false;
        if (i < s.Length)
        {
            if (s[i] is not (',' or '.'))
            {
                return false;
            }

            isComma = s[i] == ',';
            int fracStart = ++i;
            if (fracStart >= s.Length)
            {
                return false;
            }

            long num = 0;
            long den = 1;
            for (; i < s.Length; i++)
            {
                uint d = (uint)(s[i] - '0');
                if (d > 9)
                {
                    return false;
                }

                fracDigits++;
                if (den < 1000)
                {
                    num = (num * 10) + d;
                    den *= 10;
                }
            }

            ms = num * (1000 / den);
        }

        if (minutes > 59_999 || seconds > 59_999 || hours > 9_999_999)
        {
            return false;
        }

        time = new MediaTime(((((((hours * 60) + minutes) * 60) + seconds) * 1000)) + ms, 1000);
        comma = isComma;
        standard = isComma && hourDigits >= 2 && minuteDigits == 2 && secondDigits == 2 && fracDigits == 3;
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

    /// <summary>Appends <c>HH:MM:SS,mmm</c> (what <see cref="Format"/> returns) without making a string.</summary>
    internal static void Append(StringBuilder sb, MediaTime t, int cue, ConversionReport report, char separator)
    {
        long ms = t.Timescale == 1000 ? t.Ticks : SubtitleLoss.Quantize(t, 1000, cue, SubtitleFeatures.Timing, report, "Time");
        if (ms < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            ms = 0;
        }

        long totalSeconds = ms / 1000;
        AppendPadded(sb, totalSeconds / 3600, 2);
        sb.Append(':');
        AppendPadded(sb, totalSeconds / 60 % 60, 2);
        sb.Append(':');
        AppendPadded(sb, totalSeconds % 60, 2);
        sb.Append(separator);
        AppendPadded(sb, ms % 1000, 3);
    }

    /// <summary>Appends a non-negative number with at least <paramref name="minDigits"/> digits.</summary>
    internal static void AppendPadded(StringBuilder sb, long value, int minDigits)
    {
        int digits = 1;
        for (long v = value; v >= 10; v /= 10)
        {
            digits++;
        }

        for (int k = digits; k < minDigits; k++)
        {
            sb.Append('0');
        }

        if (digits <= 2)
        {
            if (digits == 2)
            {
                sb.Append((char)('0' + (int)(value / 10)));
            }

            sb.Append((char)('0' + (int)(value % 10)));
        }
        else
        {
            sb.Append(value);
        }
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
    private readonly record struct Open(string Name, int Start, TextStyle Style);

    // TextStyle is immutable, so the four plain tags share one instance each.
    private static readonly TextStyle BoldStyle = new() { Bold = true };
    private static readonly TextStyle ItalicStyle = new() { Italic = true };
    private static readonly TextStyle UnderlineStyle = new() { Underline = true };
    private static readonly TextStyle StrikeStyle = new() { Strike = true };
    private static readonly TextStyle EmptyStyle = new();

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
                        int k = open.Count - 1;
                        while (k >= 0 && open[k].Name != name)
                        {
                            k--;
                        }

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

        ReadOnlySpan<char> tagSpan = body[..sp];
        ReadOnlySpan<char> attrs = body[sp..];
        string tag;
        if (tagSpan.Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            tag = "b";
            style = BoldStyle;
        }
        else if (tagSpan.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            tag = "i";
            style = ItalicStyle;
        }
        else if (tagSpan.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            tag = "u";
            style = UnderlineStyle;
        }
        else if (tagSpan.Equals("s", StringComparison.OrdinalIgnoreCase) || tagSpan.Equals("strike", StringComparison.OrdinalIgnoreCase))
        {
            tag = "s";
            style = StrikeStyle;
        }
        else if (tagSpan.Equals("font", StringComparison.OrdinalIgnoreCase))
        {
            tag = "font";
            style = closing ? EmptyStyle : FontStyle(attrs);
        }
        else
        {
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
        if (cue.Spans.Count == 0 && !MarkupWriter.HasBlankLines(cue.Text))
        {
            // No markup to place: the usual cue is its alignment tag (if any) and its text.
            AppendAlignment(sb, cue);
            MarkupWriter.AppendEscaped(sb, cue.Text, false, nl);
            return;
        }

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

        AppendAlignment(sb, cue);
        MarkupWriter.Write(sb, plain, intervals, points, false, nl);
    }

    private static void AppendAlignment(StringBuilder sb, SubtitleCue cue)
    {
        if (cue.Placement is { } p && NumpadAlignment.To(p.Align, p.VerticalAlign) is int an and > 0)
        {
            sb.Append("{\\an").Append(an.ToString(CultureInfo.InvariantCulture)).Append('}');
        }
    }
}

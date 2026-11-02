using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Captions;

// YouTube SBV: "H:MM:SS.mmm,H:MM:SS.mmm" then text lines, blocks separated by an empty line. Plain text only.
internal sealed class SbvFormat : SubtitleFormat
{
    internal static readonly SbvFormat Instance = new();

    private static readonly string[] Exts = [".sbv"];

    private SbvFormat()
    {
    }

    public override string Name => "sbv";

    public override string DisplayName => "YouTube SBV";

    public override IReadOnlyList<string> Extensions => Exts;

    public override SubtitleFeatures Features => SubtitleFeatures.None;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        int pos = 0;
        while (pos < head.Length)
        {
            int nl = head[pos..].IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = (nl < 0 ? head[pos..] : head.Slice(pos, nl)).Trim().Trim('\uFEFF');
            pos += nl < 0 ? head.Length : nl + 1;
            if (line.IsEmpty)
            {
                continue;
            }

            return TryTimes(line, out _, out _) ? 85 : 0;
        }

        return 0;
    }

    private static bool TryTimes(ReadOnlySpan<char> line, out MediaTime start, out MediaTime end)
    {
        start = end = default;
        if (line.Contains("-->", StringComparison.Ordinal))
        {
            return false;
        }

        int comma = line.IndexOf(',');
        if (comma <= 0 || line[(comma + 1)..].Contains(',') || line.IndexOf(':') < 0)
        {
            return false;
        }

        ReadOnlySpan<char> left = line[..comma];
        ReadOnlySpan<char> right = line[(comma + 1)..];
        return left.Contains(':') && right.Contains(':') && SrtTime.TryParse(left, out start, out _, out _) && SrtTime.TryParse(right, out end, out _, out _);
    }

    internal override SubtitleDocument ReadCore(SubtitleSource src)
    {
        string text = src.Text;
        var cues = new List<SubtitleCue>();
        int pos = 0;
        bool inCue = false;
        MediaTime start = default;
        MediaTime end = default;
        var body = new StringBuilder();
        bool first = true;

        void Finish()
        {
            if (inCue)
            {
                cues.Add(new SubtitleCue { Start = start, End = end, Text = body.ToString() });
                body.Clear();
                inCue = false;
            }
        }

        while (src.NextLine(ref pos, out int s, out int l))
        {
            ReadOnlySpan<char> line = text.AsSpan(s, l);
            if (first)
            {
                first = false;
                line = line.TrimStart('\uFEFF');
            }

            if (line.Trim().IsEmpty)
            {
                Finish();
                continue;
            }

            if (!inCue)
            {
                if (TryTimes(line.Trim(), out start, out end))
                {
                    src.CheckCue(cues.Count + 1, s);
                    if (end < start)
                    {
                        src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, s, "Cue ends before it starts.", "subtitles.sbv.end-before-start");
                    }

                    inCue = true;
                }
                else
                {
                    src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, s, "Expected a cue time line; line skipped.", "subtitles.sbv.stray-line");
                }

                continue;
            }

            if (body.Length > 0)
            {
                body.Append('\n');
            }

            body.Append(line);
        }

        Finish();
        return new SubtitleDocument { Cues = SubtitleList.From(cues), NewLine = src.NewLine };
    }

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        var sb = new StringBuilder();
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            sb.Append(Clock(cue.Start, c, report)).Append(',').Append(Clock(cue.End, c, report)).Append(nl);
            string text = cue.Text;
            if (MarkupWriter.HasBlankLines(text))
            {
                report.Add(c, SubtitleFeatures.Text, LossKind.Approximated, "Empty lines are not allowed inside an SBV cue; a space was written.");
                text = MarkupWriter.FillBlankLines(text, [], [], ' ');
            }

            sb.Append(text.Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
        }

        return sb.ToString();
    }

    private static string Clock(MediaTime t, int cue, ConversionReport report)
    {
        long ms = SubtitleLoss.Quantize(t, 1000, cue, SubtitleFeatures.Timing, report, "Time");
        if (ms < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            ms = 0;
        }

        long s = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}.{ms % 1000:000}");
    }
}

// LRC lyrics: "[mm:ss.xx]text" lines with [key:value] header tags and optional enhanced word times "<mm:ss.xx>".
// A line carries only a start time; a cue ends where the next one starts.
internal sealed class LrcFormat : SubtitleFormat
{
    internal static readonly LrcFormat Instance = new();

    private static readonly string[] Exts = [".lrc"];

    // A last line has no following line; it is shown for this long unless a [length:] tag says the song ends sooner.
    private static readonly MediaTime LastLineDuration = new(5, 1);

    private LrcFormat()
    {
    }

    public override string Name => "lrc";

    public override string DisplayName => "LRC lyrics";

    public override IReadOnlyList<string> Extensions => Exts;

    public override SubtitleFeatures Features => SubtitleFeatures.WordTiming | SubtitleFeatures.Header | SubtitleFeatures.Extras;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        int pos = 0;
        int seen = 0;
        while (pos < head.Length && seen < 8)
        {
            int nl = head[pos..].IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = (nl < 0 ? head[pos..] : head.Slice(pos, nl)).Trim().Trim('\uFEFF');
            pos += nl < 0 ? head.Length : nl + 1;
            if (line.IsEmpty)
            {
                continue;
            }

            seen++;
            if (TryTag(line, out _, out _, out _, out _))
            {
                return 75;
            }

            return 0;
        }

        return 0;
    }

    // Reads one leading [..] tag. Returns the time (isTime) or key/value; rest is the text after the tag.
    private static bool TryTag(ReadOnlySpan<char> line, out bool isTime, out MediaTime time, out (string Key, string Value) meta, out int length)
    {
        isTime = false;
        time = default;
        meta = default;
        length = 0;
        if (line.IsEmpty || line[0] != '[')
        {
            return false;
        }

        int close = line.IndexOf(']');
        if (close < 3)
        {
            return false;
        }

        ReadOnlySpan<char> inner = line[1..close];
        length = close + 1;
        if (TryClock(inner, out time))
        {
            isTime = true;
            return true;
        }

        int colon = inner.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> key = inner[..colon].Trim();
        foreach (char ch in key)
        {
            if (!char.IsLetter(ch) && ch != '_' && ch != '#')
            {
                return false;
            }
        }

        meta = (key.ToString(), inner[(colon + 1)..].Trim().ToString());
        return true;
    }

    private static bool TryClock(ReadOnlySpan<char> s, out MediaTime time)
    {
        time = default;
        int colon = s.IndexOf(':');
        if (colon <= 0 || colon > 4)
        {
            return false;
        }

        foreach (char ch in s[..colon])
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        // Reuse the shared parser for the seconds and fraction part: minutes may exceed 59 here.
        return SrtTime.TryParse(s, out time, out _, out _) && s[(colon + 1)..].Length >= 2 && s[colon + 1] is >= '0' and <= '9';
    }

    internal override SubtitleDocument ReadCore(SubtitleSource src)
    {
        string text = src.Text;
        var cues = new List<SubtitleCue>();
        var extras = new List<SubtitleExtra>();
        string? title = null;
        long offsetMs = 0;
        MediaTime? length = null;
        bool first = true;
        int pos = 0;
        while (src.NextLine(ref pos, out int s, out int l))
        {
            ReadOnlySpan<char> line = text.AsSpan(s, l);
            if (first)
            {
                first = false;
                line = line.TrimStart('\uFEFF');
            }

            line = line.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            var times = new List<MediaTime>();
            ReadOnlySpan<char> rest = line;
            (string Key, string Value) meta = ("", "");
            bool isMeta = false;
            while (TryTag(rest, out bool isTime, out MediaTime t, out (string Key, string Value) m, out int len))
            {
                rest = rest[len..];
                if (isTime)
                {
                    times.Add(t);
                }
                else
                {
                    meta = m;
                    isMeta = true;
                    break;
                }
            }

            if (isMeta && times.Count == 0)
            {
                switch (meta.Key.ToLowerInvariant())
                {
                    case "ti":
                        title = meta.Value;
                        break;
                    case "offset":
                        if (long.TryParse(meta.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long off))
                        {
                            offsetMs = off;
                            src.Report(DefectKind.Other, DefectSeverity.Info, s, "The [offset:] tag was applied to the cue times and is not kept.", "subtitles.lrc.offset");
                        }

                        break;
                    case "length":
                        if (TryClock(meta.Value, out MediaTime len2))
                        {
                            length = len2;
                        }

                        extras.Add(new SubtitleExtra("lrc", "length", meta.Value));
                        break;
                    default:
                        extras.Add(new SubtitleExtra("lrc", meta.Key, meta.Value));
                        break;
                }

                continue;
            }

            if (times.Count == 0)
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, s, "Line has no time tag; skipped.", "subtitles.lrc.stray-line");
                continue;
            }

            ParseWords(rest, src, s, out string plain, out List<SubtitleSpan> spans);
            foreach (MediaTime t in times)
            {
                src.CheckCue(cues.Count + 1, s);
                MediaTime start = offsetMs == 0 ? t : new MediaTime(t.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks - offsetMs, 1000);
                if (start < MediaTime.Zero)
                {
                    src.Report(DefectKind.InvalidValue, DefectSeverity.Info, s, "Time before zero after applying the offset; set to zero.", "subtitles.lrc.negative");
                    start = MediaTime.Zero;
                }

                cues.Add(new SubtitleCue { Start = start, Text = plain, Spans = SubtitleList.From(ShiftWords(spans, t, start)) });
            }
        }

        cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < cues.Count; i++)
        {
            MediaTime end = default;
            bool found = false;
            for (int j = i + 1; j < cues.Count; j++)
            {
                if (cues[j].Start > cues[i].Start)
                {
                    end = cues[j].Start;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                end = length is { } len3 && len3 > cues[i].Start ? len3 : SubtitleTimes.Add(cues[i].Start, LastLineDuration);
            }

            SubtitleCue cue = cues[i];
            var spans = new List<SubtitleSpan>();
            foreach (SubtitleSpan sp in cue.Spans)
            {
                spans.Add(sp.WordStart is not null && sp.WordEnd is null ? sp with { WordEnd = end } : sp);
            }

            cues[i] = cue with { End = end, Spans = SubtitleList.From(spans) };
        }

        return new SubtitleDocument { Title = title, Extras = SubtitleList.From(extras), Cues = SubtitleList.From(cues), NewLine = src.NewLine };
    }

    // Word times are absolute in the file; an offset moves them with the line.
    private static List<SubtitleSpan> ShiftWords(List<SubtitleSpan> spans, MediaTime lineTime, MediaTime start)
    {
        if (lineTime == start)
        {
            return spans;
        }

        MediaTime shift = SubtitleTimes.Subtract(start, lineTime);
        var moved = new List<SubtitleSpan>(spans.Count);
        foreach (SubtitleSpan sp in spans)
        {
            moved.Add(sp with
            {
                WordStart = sp.WordStart is { } wsx ? SubtitleTimes.Add(wsx, shift) : null,
                WordEnd = sp.WordEnd is { } wex ? SubtitleTimes.Add(wex, shift) : null,
            });
        }

        return moved;
    }

    private static void ParseWords(ReadOnlySpan<char> s, SubtitleSource src, int index, out string text, out List<SubtitleSpan> spans)
    {
        var sb = new StringBuilder(s.Length);
        var marks = new List<(int Pos, MediaTime Time)>();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int close = s[i..].IndexOf('>');
                if (close > 0 && TryClock(s.Slice(i + 1, close - 1), out MediaTime t))
                {
                    marks.Add((sb.Length, t));
                    i += close + 1;
                    continue;
                }
            }

            sb.Append(s[i]);
            i++;
        }

        spans = [];
        for (int k = 0; k < marks.Count; k++)
        {
            int end = k + 1 < marks.Count ? marks[k + 1].Pos : sb.Length;
            spans.Add(new SubtitleSpan(marks[k].Pos, end - marks[k].Pos)
            {
                WordStart = marks[k].Time,
                WordEnd = k + 1 < marks.Count ? marks[k + 1].Time : null,
            });
        }

        _ = src;
        _ = index;
        text = sb.ToString();
    }

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        var sb = new StringBuilder();
        if (doc.Title is { } title)
        {
            sb.Append("[ti:").Append(title.Replace('\n', ' ')).Append(']').Append(nl);
        }

        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name)
            {
                sb.Append('[').Append(e.Key).Append(':').Append(e.Value).Append(']').Append(nl);
            }
        }

        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            bool endsAtNext = c + 1 < doc.Cues.Count && doc.Cues[c + 1].Start == cue.End;
            if (!endsAtNext && !(c + 1 == doc.Cues.Count))
            {
                report.Add(c, SubtitleFeatures.Timing, LossKind.Approximated, "LRC stores no end time; the cue now ends where the next one starts.");
            }
            else if (c + 1 == doc.Cues.Count && cue.End != SubtitleTimes.Add(cue.Start, LastLineDuration))
            {
                report.Add(c, SubtitleFeatures.Timing, LossKind.Approximated, "LRC stores no end time; the last line is read back as lasting 5 seconds.");
            }

            string stamp = Clock(cue.Start, c, report, '[', ']');
            string[] lines = cue.Text.Split('\n');
            if (lines.Length > 1)
            {
                report.Add(c, SubtitleFeatures.Text, LossKind.Approximated, "LRC lines are single lines; each text line was written with the same time tag.");
            }

            int offset = 0;
            foreach (string line in lines)
            {
                sb.Append(stamp);
                int p = 0;
                foreach (SubtitleSpan span in cue.Spans)
                {
                    _ = span;
                }

                var at = new List<(int Pos, SubtitleSpan Span)>();
                foreach (SubtitleSpan span in cue.Spans)
                {
                    if (span.WordStart is not null && span.Start >= offset && span.Start <= offset + line.Length)
                    {
                        at.Add((span.Start - offset, span));
                    }
                }

                at.Sort(static (a, b) => a.Pos.CompareTo(b.Pos));
                foreach ((int pos, SubtitleSpan span) in at)
                {
                    sb.Append(line, p, pos - p).Append(Clock(span.WordStart!.Value, c, report, '<', '>'));
                    p = pos;
                }

                sb.Append(line, p, line.Length - p).Append(nl);
                offset += line.Length + 1;
            }
        }

        return sb.ToString();
    }

    private static string Clock(MediaTime t, int cue, ConversionReport report, char open, char close)
    {
        long cs = SubtitleLoss.Quantize(t, 100, cue, SubtitleFeatures.Timing, report, "Time");
        if (cs < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            cs = 0;
        }

        long s = cs / 100;
        return string.Create(CultureInfo.InvariantCulture, $"{open}{s / 60:00}:{s % 60:00}.{cs % 100:00}{close}");
    }
}

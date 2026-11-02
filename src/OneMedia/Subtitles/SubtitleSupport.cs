using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace RecursiveAI.OneMedia.Subtitles;

// Shared plumbing for the readers and writers: byte decoding, the cursor that turns text positions into byte offsets and line/column
// for defects, markup emission, colours and small parsing helpers.

internal static class SubtitleText
{
    static SubtitleText()
    {
        // Legacy code pages (windows-1250..1258, shift_jis, gbk, …) come from the BCL's code-pages provider, which is explicit and trim-safe.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string NameOf(Encoding e) => e.CodePage switch
    {
        65001 => "utf-8",
        1200 => "utf-16",
        1201 => "utf-16BE",
        12000 => "utf-32",
        12001 => "utf-32BE",
        _ => e.WebName,
    };

    internal static Encoding EncodingFor(string name, out bool known)
    {
        known = true;
        switch (name.ToLowerInvariant())
        {
            case "utf-8" or "utf8":
                return new UTF8Encoding(false);
            case "utf-16" or "utf-16le":
                return new UnicodeEncoding(false, false);
            case "utf-16be":
                return new UnicodeEncoding(true, false);
            case "utf-32" or "utf-32le":
                return new UTF32Encoding(false, false);
            case "utf-32be":
                return new UTF32Encoding(true, false);
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            known = false;
            return new UTF8Encoding(false);
        }
    }

    // Detection order (docs/subtitles.md): byte order mark; an explicit encoding; UTF-16 without a mark by where the zero bytes sit;
    // UTF-8 when the bytes are valid UTF-8; otherwise the fallback encoding (windows-1252 unless the caller says otherwise).
    internal static string Decode(ReadOnlySpan<byte> data, SubtitleReadOptions? options, DefectLog log, out Encoding encoding, out int bomLength, bool tolerateTruncatedEnd = false)
    {
        bomLength = 0;
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
        {
            encoding = new UTF32Encoding(false, false);
            bomLength = 4;
        }
        else if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF)
        {
            encoding = new UTF32Encoding(true, false);
            bomLength = 4;
        }
        else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false);
            bomLength = 3;
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, false);
            bomLength = 2;
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, false);
            bomLength = 2;
        }
        else if (options?.Encoding is { } forced)
        {
            encoding = EncodingFor(forced, out bool known);
            if (!known)
            {
                log.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 0, $"Unknown encoding '{forced}'; decoded as UTF-8.", "subtitles.encoding.unknown");
            }
        }
        else if (LooksUtf16(data, out bool bigEndian))
        {
            encoding = new UnicodeEncoding(bigEndian, false);
            log.Add(DefectKind.Other, DefectSeverity.Info, 0, "UTF-16 without a byte order mark, detected from zero bytes.", "subtitles.encoding.utf16-nobom");
        }
        else
        {
            ReadOnlySpan<byte> body = data;
            int bad = FirstInvalidUtf8(body, out bool incompleteEnd);
            if (bad < 0 || (tolerateTruncatedEnd && incompleteEnd))
            {
                encoding = new UTF8Encoding(false);
                if (bad >= 0)
                {
                    body = body[..bad];
                }

                return StrictUtf8.GetString(body);
            }

            string fallback = options?.FallbackEncoding ?? "windows-1252";
            encoding = EncodingFor(fallback, out bool known);
            if (!known)
            {
                encoding = EncodingFor("windows-1252", out _);
            }

            log.Add(DefectKind.InvalidValue, DefectSeverity.Info, bad, $"Not valid UTF-8; decoded as {NameOf(encoding)}.", "subtitles.encoding.legacy");
        }

        return encoding.GetString(data[bomLength..]);
    }

    private static bool LooksUtf16(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        int n = Math.Min(data.Length & ~1, 256);
        if (n < 4)
        {
            return false;
        }

        int evenZero = 0;
        int oddZero = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (data[i] == 0)
            {
                evenZero++;
            }

            if (data[i + 1] == 0)
            {
                oddZero++;
            }
        }

        int pairs = n / 2;
        if (oddZero * 2 > pairs && evenZero == 0)
        {
            return true;
        }

        if (evenZero * 2 > pairs && oddZero == 0)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }

    // Returns the offset of the first byte that is not part of valid UTF-8, or -1. incompleteEnd is true when the problem is only a
    // sequence cut off by the end of the data.
    private static int FirstInvalidUtf8(ReadOnlySpan<byte> data, out bool incompleteEnd)
    {
        incompleteEnd = false;
        if (Utf8.IsValid(data))
        {
            return -1;
        }

        // Binary search for the longest valid prefix is unnecessary: step through with the validating transcoder in chunks.
        int pos = 0;
        Span<char> scratch = stackalloc char[512];
        while (pos < data.Length)
        {
            OperationStatus status = Utf8.ToUtf16(data[pos..], scratch, out int read, out _, replaceInvalidSequences: false, isFinalBlock: true);
            pos += read;
            if (status == OperationStatus.InvalidData)
            {
                return pos;
            }

            if (status == OperationStatus.NeedMoreData)
            {
                incompleteEnd = true;
                return pos;
            }
        }

        return -1;
    }

    internal static byte[] Encode(string text, Encoding encoding, bool bom, ConversionReport report)
    {
        Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        byte[] body;
        try
        {
            body = strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            report.Add(-1, SubtitleFeatures.Text, LossKind.Approximated, $"Some characters cannot be represented in {NameOf(encoding)} and were replaced by '?'.");
            body = encoding.GetBytes(text);
        }

        if (!bom)
        {
            return body;
        }

        byte[] preamble = encoding.CodePage switch
        {
            65001 => [0xEF, 0xBB, 0xBF],
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            12000 => [0xFF, 0xFE, 0, 0],
            12001 => [0, 0, 0xFE, 0xFF],
            _ => [],
        };
        byte[] all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }
}

// The text being parsed, with the cursor that maps a character position to a byte offset and a line/column for defects.
// Parsers move forward, so the cursor does too; a defect behind it restarts from the top.
internal sealed class SubtitleSource
{
    private int _cursorChar;
    private long _cursorByte;
    private int _cursorLine = 1;
    private int _cursorLineStart;
    private bool _newLineSeen;
    private bool _mixedReported;

    internal SubtitleSource(string text, DefectLog log, MediaLimits limits, Encoding encoding, int bomLength)
    {
        Text = text;
        Log = log;
        Limits = limits;
        Encoding = encoding;
        BomLength = bomLength;
    }

    internal string Text { get; }

    internal DefectLog Log { get; }

    internal MediaLimits Limits { get; }

    internal Encoding Encoding { get; }

    internal int BomLength { get; }

    /// <summary>The first line ending seen, or <c>"\n"</c>.</summary>
    internal string NewLine { get; private set; } = "\n";

    /// <summary>Byte offset of the character at <paramref name="index"/> in the original file.</summary>
    internal long ByteOffset(int index)
    {
        Seek(index);
        return BomLength + _cursorByte;
    }

    internal void Report(DefectKind kind, DefectSeverity severity, int index, string message, string code)
    {
        Seek(index);
        int column = index - _cursorLineStart + 1;
        Log.Add(kind, severity, BomLength + _cursorByte, $"{message} (line {_cursorLine.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)})", code);
    }

    private void Seek(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        if (index < _cursorChar)
        {
            _cursorChar = 0;
            _cursorByte = 0;
            _cursorLine = 1;
            _cursorLineStart = 0;
        }

        if (index > _cursorChar)
        {
            int to = index;
            if (to < Text.Length && char.IsLowSurrogate(Text[to]) && to > _cursorChar)
            {
                to--;
            }

            for (int i = _cursorChar; i < to; i++)
            {
                char c = Text[i];
                if (c == '\n' || (c == '\r' && (i + 1 >= Text.Length || Text[i + 1] != '\n')))
                {
                    _cursorLine++;
                    _cursorLineStart = i + 1;
                }
            }

            _cursorByte += Encoding.GetByteCount(Text.AsSpan(_cursorChar, to - _cursorChar));
            _cursorChar = to;
            if (index > to)
            {
                index = to;
            }
        }
    }

    /// <summary>Next line (without its terminator). Returns false at the end of the text.</summary>
    internal bool NextLine(ref int pos, out int start, out int length)
    {
        start = pos;
        length = 0;
        if (pos >= Text.Length)
        {
            return false;
        }

        int i = pos;
        while (i < Text.Length && Text[i] != '\n' && Text[i] != '\r')
        {
            i++;
        }

        length = i - pos;
        if (i < Text.Length)
        {
            string terminator;
            if (Text[i] == '\r' && i + 1 < Text.Length && Text[i + 1] == '\n')
            {
                terminator = "\r\n";
            }
            else
            {
                terminator = Text[i] == '\r' ? "\r" : "\n";
            }

            i += terminator.Length;
            if (!_newLineSeen)
            {
                _newLineSeen = true;
                NewLine = terminator;
            }
            else if (terminator != NewLine && !_mixedReported)
            {
                _mixedReported = true;
                Report(DefectKind.Inconsistent, DefectSeverity.Info, start + length, "Mixed line endings.", "subtitles.newline.mixed");
            }
        }

        pos = i;
        return true;
    }

    private int[]? _lineStarts;

    /// <summary>The character index of a 1-based line and column (as XML readers count them), clamped to the text.</summary>
    internal int IndexOfLine(int line, int column)
    {
        if (_lineStarts is null)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < Text.Length; i++)
            {
                char c = Text[i];
                if (c == '\n' || (c == '\r' && (i + 1 >= Text.Length || Text[i + 1] != '\n')))
                {
                    starts.Add(i + 1);
                }
            }

            _lineStarts = starts.ToArray();
        }

        int l = Math.Clamp(line - 1, 0, _lineStarts.Length - 1);
        return Math.Min(_lineStarts[l] + Math.Max(column - 1, 0), Text.Length);
    }

    internal ReadOnlySpan<char> Slice(int start, int length) => Text.AsSpan(start, length);

    internal void CheckCue(int count, int index) => Limits.CheckCues(count, ByteOffset(index));
}

// One styled interval to be written as markup, and a point marker.
internal readonly record struct MarkupInterval(int Start, int End, string Open, string Close, int Priority);

internal readonly record struct MarkupPoint(int Position, string Markup);

internal static class MarkupWriter
{
    internal static bool HasBlankLines(string text) =>
        text.Contains("\n\n", StringComparison.Ordinal) || text.StartsWith('\n') || text.EndsWith('\n');

    // Formats that end a cue at an empty line cannot hold one inside the text: every empty line gets a filler character, and the
    // intervals and points move to match.
    internal static string FillBlankLines(string text, List<MarkupInterval> intervals, List<MarkupPoint> points, char filler)
    {
        var sb = new StringBuilder(text.Length + 2);
        for (int k = 0; k < text.Length; k++)
        {
            bool emptyLineBefore = text[k] == '\n' && (k == 0 || text[k - 1] == '\n');
            if (emptyLineBefore)
            {
                sb.Append(filler);
                ShiftAfter(intervals, points, sb.Length - 1);
            }

            sb.Append(text[k]);
        }

        if (text.Length > 0 && text[^1] == '\n')
        {
            sb.Append(filler);
            ShiftAfter(intervals, points, sb.Length - 1);
        }

        return sb.ToString();
    }

    // After inserting a character at position at (in the output text), intervals and points at or beyond it move by one.
    private static void ShiftAfter(List<MarkupInterval> intervals, List<MarkupPoint> points, int at)
    {
        for (int k = 0; k < intervals.Count; k++)
        {
            MarkupInterval iv = intervals[k];
            intervals[k] = iv with { Start = iv.Start >= at ? iv.Start + 1 : iv.Start, End = iv.End > at ? iv.End + 1 : iv.End };
        }

        for (int k = 0; k < points.Count; k++)
        {
            if (points[k].Position >= at)
            {
                points[k] = points[k] with { Position = points[k].Position + 1 };
            }
        }
    }

    // Writes text with nested markup from intervals that may overlap. Where two intervals cross, the inner one is closed and
    // reopened around the boundary so the output nests properly. Closing happens before opening at a position; points go between.
    internal static void Write(StringBuilder sb, string text, List<MarkupInterval> intervals, List<MarkupPoint> points, Action<StringBuilder, ReadOnlySpan<char>> escape)
    {
        intervals.RemoveAll(i => i.Start >= i.End || i.End <= 0 || i.Start >= text.Length);
        for (int k = 0; k < intervals.Count; k++)
        {
            MarkupInterval iv = intervals[k];
            intervals[k] = iv with { Start = Math.Max(iv.Start, 0), End = Math.Min(iv.End, text.Length) };
        }

        intervals.Sort(static (a, b) =>
        {
            int c = a.Start.CompareTo(b.Start);
            if (c != 0)
            {
                return c;
            }

            c = b.End.CompareTo(a.End);
            return c != 0 ? c : a.Priority.CompareTo(b.Priority);
        });
        points.Sort(static (a, b) => a.Position.CompareTo(b.Position));

        var boundaries = new SortedSet<int> { 0, text.Length };
        foreach (MarkupInterval iv in intervals)
        {
            boundaries.Add(iv.Start);
            boundaries.Add(iv.End);
        }

        foreach (MarkupPoint p in points)
        {
            boundaries.Add(Math.Clamp(p.Position, 0, text.Length));
        }

        var stack = new List<MarkupInterval>();
        int nextInterval = 0;
        int nextPoint = 0;
        int written = 0;
        foreach (int b in boundaries)
        {
            if (b > written)
            {
                escape(sb, text.AsSpan(written, b - written));
                written = b;
            }

            // Close everything that ends here, reopening the survivors that were above it.
            while (true)
            {
                int lowest = stack.FindIndex(s => s.End <= b);
                if (lowest < 0)
                {
                    break;
                }

                var reopen = new List<MarkupInterval>();
                for (int i = stack.Count - 1; i >= lowest; i--)
                {
                    sb.Append(stack[i].Close);
                    if (stack[i].End > b)
                    {
                        reopen.Insert(0, stack[i]);
                    }
                }

                stack.RemoveRange(lowest, stack.Count - lowest);
                foreach (MarkupInterval r in reopen)
                {
                    sb.Append(r.Open);
                    stack.Add(r);
                }
            }

            while (nextInterval < intervals.Count && intervals[nextInterval].Start <= b)
            {
                MarkupInterval iv = intervals[nextInterval++];
                sb.Append(iv.Open);
                stack.Add(iv);
            }

            while (nextPoint < points.Count && Math.Clamp(points[nextPoint].Position, 0, text.Length) <= b)
            {
                sb.Append(points[nextPoint++].Markup);
            }
        }

        for (int i = stack.Count - 1; i >= 0; i--)
        {
            sb.Append(stack[i].Close);
        }
    }
}

internal static class SubtitleColors
{
    private static readonly (string Name, uint Rgb)[] Names =
    [
        ("aqua", 0x00FFFF), ("black", 0x000000), ("blue", 0x0000FF), ("cyan", 0x00FFFF), ("fuchsia", 0xFF00FF),
        ("gray", 0x808080), ("green", 0x008000), ("grey", 0x808080), ("lime", 0x00FF00), ("magenta", 0xFF00FF),
        ("maroon", 0x800000), ("navy", 0x000080), ("olive", 0x808000), ("orange", 0xFFA500), ("purple", 0x800080),
        ("red", 0xFF0000), ("silver", 0xC0C0C0), ("teal", 0x008080), ("white", 0xFFFFFF), ("yellow", 0xFFFF00),
    ];

    // The eight colours of WebVTT's default colour classes (W3C WebVTT §7.? "default text colours").
    internal static readonly (string Name, SubtitleColor Color)[] WebVttClasses =
    [
        ("white", new SubtitleColor(255, 255, 255)),
        ("lime", new SubtitleColor(0, 255, 0)),
        ("cyan", new SubtitleColor(0, 255, 255)),
        ("red", new SubtitleColor(255, 0, 0)),
        ("yellow", new SubtitleColor(255, 255, 0)),
        ("magenta", new SubtitleColor(255, 0, 255)),
        ("blue", new SubtitleColor(0, 0, 255)),
        ("black", new SubtitleColor(0, 0, 0)),
    ];

    /// <summary>Parses #rgb, #rrggbb, #rrggbbaa, rgb(), rgba() and common names (HTML/CSS/TTML).</summary>
    internal static bool TryParse(ReadOnlySpan<char> s, out SubtitleColor color)
    {
        color = default;
        s = s.Trim();
        if (s.IsEmpty)
        {
            return false;
        }

        if (s[0] == '#')
        {
            ReadOnlySpan<char> h = s[1..];
            if (h.Length == 3 && TryHex(h, 1, out uint rgb3))
            {
                byte r = (byte)(((rgb3 >> 8) & 0xF) * 17);
                byte g = (byte)(((rgb3 >> 4) & 0xF) * 17);
                byte b = (byte)((rgb3 & 0xF) * 17);
                color = new SubtitleColor(r, g, b);
                return true;
            }

            if (h.Length == 6 && TryHex(h, 2, out uint rgb))
            {
                color = new SubtitleColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                return true;
            }

            if (h.Length == 8 && TryHex(h, 2, out uint rgba))
            {
                color = new SubtitleColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
                return true;
            }

            return false;
        }

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close < open)
            {
                return false;
            }

            ReadOnlySpan<char> inner = s[(open + 1)..close];
            Span<Range> parts = stackalloc Range[5];
            int n = inner.Split(parts, ',');
            if (n is < 3 or > 4)
            {
                return false;
            }

            Span<byte> v = stackalloc byte[4];
            v[3] = 255;
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<char> part = inner[parts[i]].Trim();
                if (i == 3)
                {
                    if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || a < 0 || a > 1)
                    {
                        return false;
                    }

                    v[3] = (byte)Math.Round(a * 255);
                }
                else if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                {
                    return false;
                }
            }

            color = new SubtitleColor(v[0], v[1], v[2], v[3]);
            return true;
        }

        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = new SubtitleColor(0, 0, 0, 0);
            return true;
        }

        foreach ((string name, uint rgb) in Names)
        {
            if (s.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                color = new SubtitleColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                return true;
            }
        }

        return false;
    }

    private static bool TryHex(ReadOnlySpan<char> digits, int _, out uint value)
    {
        value = 0;
        foreach (char c in digits)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (d < 0)
            {
                return false;
            }

            value = (value << 4) | (uint)d;
        }

        return true;
    }

    internal static string Hex(SubtitleColor c, bool withAlpha = false) =>
        withAlpha && c.A != 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}");

    internal static int Distance(SubtitleColor a, SubtitleColor b)
    {
        int dr = a.R - b.R;
        int dg = a.G - b.G;
        int db = a.B - b.B;
        return (dr * dr) + (dg * dg) + (db * db);
    }

    /// <summary>The WebVTT default colour class nearest to a colour, and whether it is exact.</summary>
    internal static string NearestWebVttClass(SubtitleColor c, out bool exact)
    {
        string best = "white";
        int bestDistance = int.MaxValue;
        foreach ((string name, SubtitleColor color) in WebVttClasses)
        {
            int d = Distance(c, color);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = name;
            }
        }

        exact = bestDistance == 0;
        return best;
    }
}

internal static class SubtitleTimes
{
    /// <summary>Exact sum: in a timescale both operands divide when one is small enough, otherwise rounded in the left operand's.</summary>
    internal static MediaTime Add(MediaTime a, MediaTime b)
    {
        if (!a.IsKnown || b.IsZero)
        {
            return a;
        }

        long gcd = (long)System.Numerics.BigInteger.GreatestCommonDivisor(a.Timescale, b.Timescale);
        System.Numerics.BigInteger lcm = a.Timescale / gcd * (System.Numerics.BigInteger)b.Timescale;
        if (lcm <= 1_000_000_000_000)
        {
            long scale = (long)lcm;
            return new MediaTime(a.Rescale(scale, MediaRounding.TowardZero).Ticks + b.Rescale(scale, MediaRounding.TowardZero).Ticks, scale);
        }

        return a.Add(b, MediaRounding.NearestAwayFromZero);
    }

    internal static MediaTime Subtract(MediaTime a, MediaTime b) => Add(a, new MediaTime(-b.Ticks, b.Timescale));
}

internal static class SubtitleNumbers
{
    internal static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static bool TryParseDouble(ReadOnlySpan<char> s, out double value) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Parses digits as a decimal fraction of a second into ticks of <paramref name="scale"/> (1000, 100, …), truncating extra digits.</summary>
    internal static long FractionTicks(ReadOnlySpan<char> digits, long scale, out bool truncated)
    {
        truncated = false;
        long num = 0;
        long den = 1;
        foreach (char c in digits)
        {
            if (den >= scale)
            {
                if (c != '0')
                {
                    truncated = true;
                }

                continue;
            }

            num = (num * 10) + (c - '0');
            den *= 10;
        }

        return num * (scale / den);
    }
}

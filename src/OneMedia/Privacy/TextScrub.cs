namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Finds coordinates and serial numbers written as text in subtitle and timed-text samples (the per-frame lines drone and action cameras store) and
/// overwrites each with spaces, keeping the sample's length. The rules are word and number patterns, not a language: <c>docs/strip.md</c> lists them.
/// </summary>
internal static class TextScrub
{
    private static readonly string[] LocationKeys = ["longitude", "latitude", "gps_alt", "abs_alt", "rel_alt", "altitude", "lng", "lon", "lat", "alt"];
    private static readonly string[] DeviceKeys = ["serial_number", "serialnumber", "serial number", "serial", "sn"];

    internal readonly record struct Span(int Start, int Length, StripCategories Category, string Name);

    /// <summary>Strips one sample's text.</summary>
    public static void Sample(ReadOnlySpan<byte> text, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink)
    {
        if (Utf16Start(text) is { } start)
        {
            Utf16Sample(text, start, fileOffset, system, trackId, cats, sink);
            return;
        }

        List<Span> spans = Find(text, cats);
        foreach (Span s in spans)
        {
            sink.Fill(s.Category, system, s.Name, trackId, fileOffset + s.Start, s.Length, (byte)' ');
        }
    }

    /// <summary>Whether the text holds anything the rules match (for formats where the sample cannot be edited).</summary>
    public static bool Any(ReadOnlySpan<byte> text, StripCategories cats) => Utf16Start(text) is { } start ? Find(Narrow(text, start, out _), cats).Count > 0 : Find(text, cats).Count > 0;

    // 3GPP TS 26.245: tx3g text may be UTF-16 and then starts with a byte order mark, after the two-byte length of the sample. The byte order mark
    // decides the byte order; the index returned is where the mark sits.
    private static int? Utf16Start(ReadOnlySpan<byte> t)
    {
        foreach (int at in new[] { 0, 2 })
        {
            if (t.Length >= at + 4 && ((t[at] == 0xFE && t[at + 1] == 0xFF) || (t[at] == 0xFF && t[at + 1] == 0xFE)))
            {
                return at;
            }
        }

        return null;
    }

    // The ASCII range of UTF-16 text as one byte per character (other characters become 0x01, which no rule treats as part of a word or number),
    // so the rules run unchanged; start is the byte order mark's index.
    private static byte[] Narrow(ReadOnlySpan<byte> t, int start, out bool bigEndian)
    {
        bigEndian = t[start] == 0xFE;
        int chars = (t.Length - start - 2) / 2;
        byte[] narrow = new byte[chars];
        for (int i = 0; i < chars; i++)
        {
            int p = start + 2 + (2 * i);
            byte hi = bigEndian ? t[p] : t[p + 1];
            byte lo = bigEndian ? t[p + 1] : t[p];
            narrow[i] = hi == 0 && lo < 0x80 ? lo : (byte)1;
        }

        return narrow;
    }

    private static void Utf16Sample(ReadOnlySpan<byte> text, int start, long fileOffset, string system, long trackId, StripCategories cats, StripSink sink)
    {
        byte[] narrow = Narrow(text, start, out bool bigEndian);
        foreach (Span s in Find(narrow, cats))
        {
            // Spaces as UTF-16 code units: U+0020 in the sample's byte order.
            byte[] blank = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                blank[(2 * i) + (bigEndian ? 1 : 0)] = (byte)' ';
            }

            long at = fileOffset + start + 2 + (2L * s.Start);
            sink.Patches.Write(at, blank);
            sink.Note(s.Category, system, s.Name, trackId, at, blank.Length, StripAction.Overwritten);
        }
    }

    // WebVTT in ISO files wraps each cue in boxes (vttc, payl, ...): the text follows the four type bytes of a payload box directly, so "payl" would
    // glue itself to the first word or number of the cue and hide it from the whole-word rules. Box headers are blanked to 0x01 in the copy the rules read.
    private static readonly string[] CueBoxes = ["payl", "sttg", "iden", "ctim", "vtta", "vttc", "vtte"];

    private static byte[]? MaskBoxHeaders(ReadOnlySpan<byte> t)
    {
        byte[]? masked = null;
        for (int i = 4; i + 4 <= t.Length; i++)
        {
            foreach (string kind in CueBoxes)
            {
                if (t[i] == kind[0] && t[i + 1] == kind[1] && t[i + 2] == kind[2] && t[i + 3] == kind[3])
                {
                    uint size = (uint)((t[i - 4] << 24) | (t[i - 3] << 16) | (t[i - 2] << 8) | t[i - 1]);
                    if (size >= 8 && size <= t.Length - (i - 4))
                    {
                        masked ??= t.ToArray();
                        masked.AsSpan(i - 4, 8).Fill(1);
                    }
                }
            }
        }

        return masked;
    }

    internal static List<Span> Find(ReadOnlySpan<byte> t0, StripCategories cats)
    {
        byte[]? maskedCopy = MaskBoxHeaders(t0);
        ReadOnlySpan<byte> t = maskedCopy ?? t0;
        List<Span> found = [];
        if ((cats & StripCategories.Location) != 0)
        {
            Keyed(t, LocationKeys, StripCategories.Location, "text position", found, number: true);
            Bracketed(t, found);
            Iso6709(t, found);
            Dms(t, found);
            Pairs(t, found);
        }

        if ((cats & StripCategories.DeviceIds) != 0)
        {
            Keyed(t, DeviceKeys, StripCategories.DeviceIds, "text serial number", found, number: false);
        }

        // Overlapping matches become one span (the first rule to claim a byte names it).
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<Span> merged = [];
        foreach (Span s in found)
        {
            if (merged.Count > 0 && s.Start < merged[^1].Start + merged[^1].Length)
            {
                Span p = merged[^1];
                int end = Math.Max(p.Start + p.Length, s.Start + s.Length);
                merged[^1] = p with { Length = end - p.Start };
            }
            else
            {
                merged.Add(s);
            }
        }

        return merged;
    }

    private static bool IsWord(byte b) => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_';

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    // key : value or key = value, the key a whole word; the span covers the key through its value.
    private static void Keyed(ReadOnlySpan<byte> t, string[] keys, StripCategories cat, string name, List<Span> found, bool number)
    {
        for (int i = 0; i < t.Length; i++)
        {
            if ((i > 0 && IsWord(t[i - 1])) || !IsWord(t[i]))
            {
                continue;
            }

            foreach (string key in keys)
            {
                if (i + key.Length > t.Length || !MatchesIgnoreCase(t.Slice(i, key.Length), key))
                {
                    continue;
                }

                int p = i + key.Length;
                if (p < t.Length && IsWord(t[p]))
                {
                    continue;
                }

                while (p < t.Length && t[p] == (byte)' ')
                {
                    p++;
                }

                // "latitude 47.62" (no colon) is taken for the long key words only: "lat 5" and "lon 3" are too common as plain text.
                bool spaced = number && key.Length >= 8 && p > i + key.Length && p < t.Length && (IsDigit(t[p]) || t[p] is (byte)'-' or (byte)'+');
                if (p >= t.Length || (t[p] != (byte)':' && t[p] != (byte)'=' && !spaced))
                {
                    continue;
                }

                if (!spaced)
                {
                    p++;
                }

                while (p < t.Length && t[p] == (byte)' ')
                {
                    p++;
                }

                int v = p;
                if (number)
                {
                    while (p < t.Length && (IsDigit(t[p]) || t[p] is (byte)'.' or (byte)'-' or (byte)'+'))
                    {
                        p++;
                    }

                    if (p == v)
                    {
                        continue;
                    }
                }
                else
                {
                    while (p < t.Length && IsWord(t[p]))
                    {
                        p++;
                    }

                    if (p - v < 4)
                    {
                        continue;
                    }
                }

                found.Add(new Span(i, p - i, cat, name));
                i = p - 1;
                break;
            }
        }
    }

    private static bool MatchesIgnoreCase(ReadOnlySpan<byte> t, string key)
    {
        for (int i = 0; i < key.Length; i++)
        {
            if (Lower(t[i]) != key[i])
            {
                return false;
            }
        }

        return true;
    }

    // GPS(39.9,116.3,69.8) and HOME (…): the keyword and a bracket holding only number characters.
    private static void Bracketed(ReadOnlySpan<byte> t, List<Span> found)
    {
        for (int i = 0; i < t.Length; i++)
        {
            if (i > 0 && IsWord(t[i - 1]))
            {
                continue;
            }

            int k = i + 3 <= t.Length && MatchesIgnoreCase(t[i..], "gps") ? 3 : i + 4 <= t.Length && MatchesIgnoreCase(t[i..], "home") ? 4 : 0;
            if (k == 0)
            {
                continue;
            }

            int p = i + k;
            while (p < t.Length && t[p] == (byte)' ')
            {
                p++;
            }

            if (p >= t.Length || t[p] != (byte)'(')
            {
                continue;
            }

            int q = p + 1;
            bool digits = false;
            while (q < t.Length && (IsDigit(t[q]) || t[q] is (byte)'.' or (byte)',' or (byte)'-' or (byte)'+' or (byte)' ' or (byte)'m' or (byte)'M' or (byte)';'))
            {
                digits |= IsDigit(t[q]);
                q++;
            }

            if (digits && q < t.Length && t[q] == (byte)')')
            {
                found.Add(new Span(i, q + 1 - i, StripCategories.Location, "text position"));
                i = q;
            }
        }
    }

    // ISO 6709 §6.1 string: ±DD[.d]±DDD[.d][±A[.d]]/ with degrees, minutes or seconds forms (2, 4 or 6 digits for latitude; 3, 5 or 7 for longitude).
    private static void Iso6709(ReadOnlySpan<byte> t, List<Span> found)
    {
        for (int i = 0; i < t.Length; i++)
        {
            if ((t[i] != (byte)'+' && t[i] != (byte)'-') || (i > 0 && (IsWord(t[i - 1]) || t[i - 1] == (byte)'.')))
            {
                continue;
            }

            int p = i + 1;
            if (!Part(t, ref p, 2, 4, 6, out _) || p >= t.Length || (t[p] != (byte)'+' && t[p] != (byte)'-'))
            {
                continue;
            }

            p++;
            if (!Part(t, ref p, 3, 5, 7, out _))
            {
                continue;
            }

            if (p < t.Length && (t[p] == (byte)'+' || t[p] == (byte)'-'))
            {
                int q = p + 1;
                int d = q;
                while (q < t.Length && IsDigit(t[q]))
                {
                    q++;
                }

                if (q > d && q < t.Length && t[q] == (byte)'.')
                {
                    q++;
                    while (q < t.Length && IsDigit(t[q]))
                    {
                        q++;
                    }
                }

                p = q > d ? q : p;
            }

            if (p < t.Length && t[p] == (byte)'/')
            {
                found.Add(new Span(i, p + 1 - i, StripCategories.Location, "text ISO 6709 position"));
                i = p;
            }
        }
    }

    // Digits (one of three allowed counts) and an optional fraction.
    private static bool Part(ReadOnlySpan<byte> t, ref int p, int a, int b, int c, out int digits)
    {
        int s = p;
        while (p < t.Length && IsDigit(t[p]))
        {
            p++;
        }

        digits = p - s;
        if (digits != a && digits != b && digits != c)
        {
            return false;
        }

        if (p < t.Length && t[p] == (byte)'.')
        {
            int f = ++p;
            while (p < t.Length && IsDigit(t[p]))
            {
                p++;
            }

            if (p == f)
            {
                return false;
            }
        }

        return true;
    }

    // Degrees, minutes and seconds with the usual marks: 47°37'14.8"N or 47° 37' 14.8" (UTF-8 degree sign, ASCII or prime marks); minutes are required,
    // seconds and the hemisphere letter are taken when present.
    private static void Dms(ReadOnlySpan<byte> t, List<Span> found)
    {
        for (int i = 0; i < t.Length; i++)
        {
            if (!IsDigit(t[i]) || (i > 0 && (IsWord(t[i - 1]) || t[i - 1] == (byte)'.')))
            {
                continue;
            }

            int p = i;
            int d = p;
            while (p < t.Length && IsDigit(t[p]) && p - d < 4)
            {
                p++;
            }

            if (p - d is < 1 or > 3 || !Mark(t, ref p, DegreeMarks))
            {
                continue;
            }

            Skip(t, ref p);
            int minutes = p;
            while (p < t.Length && IsDigit(t[p]) && p - minutes < 3)
            {
                p++;
            }

            if (p - minutes is < 1 or > 2)
            {
                continue;
            }

            if (p < t.Length && t[p] == (byte)'.')
            {
                p++;
                while (p < t.Length && IsDigit(t[p]))
                {
                    p++;
                }
            }

            if (!Mark(t, ref p, MinuteMarks))
            {
                continue;
            }

            int end = p;
            Skip(t, ref p);
            int seconds = p;
            while (p < t.Length && IsDigit(t[p]))
            {
                p++;
            }

            if (p > seconds)
            {
                if (p < t.Length && t[p] == (byte)'.')
                {
                    p++;
                    while (p < t.Length && IsDigit(t[p]))
                    {
                        p++;
                    }
                }

                if (Mark(t, ref p, SecondMarks))
                {
                    end = p;
                }
            }

            // An optional hemisphere letter, alone, straight after or one space away.
            int h = end;
            Skip(t, ref h);
            if (h < t.Length && t[h] is (byte)'N' or (byte)'S' or (byte)'E' or (byte)'W' or (byte)'n' or (byte)'s' or (byte)'e' or (byte)'w' && (h + 1 >= t.Length || !IsWord(t[h + 1])))
            {
                end = h + 1;
            }

            found.Add(new Span(i, end - i, StripCategories.Location, "text degrees-minutes-seconds position"));
            i = end - 1;
        }
    }

    private static readonly byte[][] DegreeMarks = [[0xC2, 0xB0], [0xC2, 0xBA], [0xE2, 0x81, 0xB0]];
    private static readonly byte[][] MinuteMarks = [[0x27], [0xE2, 0x80, 0xB2], [0xC2, 0xB4], [0xE2, 0x80, 0x99]];
    private static readonly byte[][] SecondMarks = [[0x22], [0x27, 0x27], [0xE2, 0x80, 0xB3], [0xE2, 0x80, 0x9D]];

    private static bool Mark(ReadOnlySpan<byte> t, ref int p, byte[][] marks)
    {
        foreach (byte[] m in marks)
        {
            if (p + m.Length <= t.Length && t.Slice(p, m.Length).SequenceEqual(m))
            {
                p += m.Length;
                return true;
            }
        }

        return false;
    }

    private static void Skip(ReadOnlySpan<byte> t, ref int p)
    {
        while (p < t.Length && t[p] == (byte)' ')
        {
            p++;
        }
    }

    // Two decimal numbers with at least three fraction digits, in the range of a latitude and a longitude, a few separator characters apart.
    private static void Pairs(ReadOnlySpan<byte> t, List<Span> found)
    {
        int i = 0;
        while (i < t.Length)
        {
            if (!TryDecimal(t, i, out int end, out int a, out int fraction) || fraction < 3 || Math.Abs(a) > 90)
            {
                i++;
                continue;
            }

            // Between the numbers: spaces, commas, semicolons, line breaks and tabs, a degree sign and a lone hemisphere letter (47.62° N, 122.34° W).
            int p = end, sep = 0;
            while (p < t.Length && sep < 6)
            {
                if (t[p] is (byte)' ' or (byte)',' or (byte)';' or 9 or 13 or 10)
                {
                    p++;
                }
                else if (p + 1 < t.Length && t[p] == 0xC2 && (t[p + 1] == 0xB0 || t[p + 1] == 0xBA))
                {
                    p += 2;
                }
                else if (t[p] is (byte)'N' or (byte)'S' or (byte)'E' or (byte)'W' or (byte)'n' or (byte)'s' or (byte)'e' or (byte)'w' && (p + 1 >= t.Length || !IsWord(t[p + 1])))
                {
                    p++;
                }
                else
                {
                    break;
                }

                sep++;
            }

            if (sep > 0 && TryDecimal(t, p, out int end2, out int b, out int fraction2) && fraction2 >= 3 && Math.Abs(b) <= 180)
            {
                found.Add(new Span(i, end2 - i, StripCategories.Location, "text coordinate pair"));
                i = end2;
            }
            else
            {
                i = end;
            }
        }
    }

    // A number as a human writes a coordinate: optional sign, digits, a point, digits; not the tail of a longer number or word, and not followed by
    // a second point (a version number or an address).
    private static bool TryDecimal(ReadOnlySpan<byte> t, int i, out int end, out int value, out int fraction)
    {
        end = i;
        value = 0;
        fraction = 0;
        if (i > 0 && (IsWord(t[i - 1]) || t[i - 1] == (byte)'.'))
        {
            return false;
        }

        int p = i;
        bool negative = false;
        if (p < t.Length && (t[p] == (byte)'-' || t[p] == (byte)'+'))
        {
            negative = t[p] == (byte)'-';
            p++;
        }

        int d = p;
        int whole = 0;
        while (p < t.Length && IsDigit(t[p]) && p - d < 9)
        {
            whole = (whole * 10) + (t[p] - (byte)'0');
            p++;
        }

        if (p == d || p >= t.Length || t[p] != (byte)'.')
        {
            return false;
        }

        int f = ++p;
        while (p < t.Length && IsDigit(t[p]))
        {
            p++;
        }

        fraction = p - f;
        if (fraction == 0 || (p + 1 < t.Length && t[p] == (byte)'.' && IsDigit(t[p + 1])))
        {
            return false;
        }

        end = p;
        value = negative ? -whole : whole;
        return true;
    }
}

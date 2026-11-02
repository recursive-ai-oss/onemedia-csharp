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
        List<Span> spans = Find(text, cats);
        foreach (Span s in spans)
        {
            sink.Fill(s.Category, system, s.Name, trackId, fileOffset + s.Start, s.Length, (byte)' ');
        }
    }

    /// <summary>Whether the text holds anything the rules match (for formats where the sample cannot be edited).</summary>
    public static bool Any(ReadOnlySpan<byte> text, StripCategories cats) => Find(text, cats).Count > 0;

    internal static List<Span> Find(ReadOnlySpan<byte> t, StripCategories cats)
    {
        List<Span> found = [];
        if ((cats & StripCategories.Location) != 0)
        {
            Keyed(t, LocationKeys, StripCategories.Location, "text position", found, number: true);
            Bracketed(t, found);
            Iso6709(t, found);
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

                if (p >= t.Length || (t[p] != (byte)':' && t[p] != (byte)'='))
                {
                    continue;
                }

                p++;
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

            int p = end, sep = 0;
            while (p < t.Length && sep < 4 && t[p] is (byte)' ' or (byte)',' or (byte)';')
            {
                p++;
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

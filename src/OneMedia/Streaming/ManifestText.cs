using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Text helpers shared by the manifest readers and writers.</summary>
internal static class ManifestText
{
    /// <summary>Writes a time as decimal seconds, exact to six places, without trailing zeros beyond the third place.</summary>
    public static string Seconds(MediaTime t)
    {
        if (!t.IsKnown)
        {
            return "0.000";
        }

        bool negative = t.Ticks < 0;
        Int128 ticks = Int128.Abs(t.Ticks);
        Int128 scale = t.Timescale;
        long whole = (long)(ticks / scale);
        Int128 remainder = ticks % scale;
        long micro = (long)(((remainder * 1_000_000) + (scale / 2)) / scale);
        if (micro >= 1_000_000)
        {
            whole++;
            micro -= 1_000_000;
        }

        if (whole == 0 && micro == 0)
        {
            negative = false; // a value that rounds to zero is written as zero, never as "-0.000"
        }

        string fraction = micro.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        if (fraction.Length < 3)
        {
            fraction = fraction.PadRight(3, '0');
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : "")}{whole}.{fraction}");
    }

    /// <summary>
    /// The sum of two times, exact: expressed in the least common multiple of the two timescales (EXTINF values are decimals, so their timescales
    /// are powers of ten and the sum keeps every digit). <see cref="MediaTime.Add"/> would round to the left operand's timescale, which for a
    /// running total that starts at zero (timescale 1) is whole seconds. When the common timescale does not fit, the finer of the two is used and the
    /// sum rounds to the nearest tick.
    /// </summary>
    public static MediaTime ExactSum(MediaTime a, MediaTime b)
    {
        long sa = a.Timescale;
        long sb = b.Timescale;
        Int128 gcd = Gcd(sa, sb);
        Int128 lcm = (Int128)sa / gcd * sb;
        if (lcm > long.MaxValue)
        {
            return sa >= sb ? a.Add(b, MediaRounding.NearestAwayFromZero) : b.Add(a, MediaRounding.NearestAwayFromZero);
        }

        Int128 ticks = ((Int128)a.Ticks * (lcm / sa)) + ((Int128)b.Ticks * (lcm / sb));
        if (ticks < long.MinValue + 1 || ticks > long.MaxValue)
        {
            throw new OverflowException("The sum does not fit in 64 bits.");
        }

        return new MediaTime((long)ticks, (long)lcm);
    }

    private static long Gcd(long x, long y)
    {
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return x;
    }

    /// <summary>Reads a decimal number of seconds ("6", "6.006", "0.0416667") exactly; the timescale is the matching power of ten.</summary>
    public static bool TryParseSeconds(ReadOnlySpan<char> text, out MediaTime time)
    {
        time = default;
        text = text.Trim();
        bool negative = false;
        if (text.Length > 0 && text[0] is '-' or '+')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        int dot = text.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? text : text[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? default : text[(dot + 1)..];
        if (whole.Length == 0 && fraction.Length == 0 || whole.Length > 12 || fraction.Length > 9)
        {
            return false;
        }

        long scale = 1;
        long value = 0;
        foreach (char c in whole)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        foreach (char c in fraction)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
            scale *= 10;
        }

        time = new MediaTime(negative ? -value : value, scale);
        return true;
    }

    public static string DateTime(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    public static bool TryParseDateTime(ReadOnlySpan<char> text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    public static string Join(IEnumerable<string> parts, char separator)
    {
        StringBuilder sb = new();
        foreach (string p in parts)
        {
            if (sb.Length > 0)
            {
                sb.Append(separator);
            }

            sb.Append(p);
        }

        return sb.ToString();
    }
}

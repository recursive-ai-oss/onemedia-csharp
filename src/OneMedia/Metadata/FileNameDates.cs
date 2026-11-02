namespace RecursiveAI.OneMedia.Metadata;

/// <summary>
/// Finds a capture date in a file name, the fifth rung of the default capture-time precedence. Recognised: <c>YYYYMMDD_HHMMSS</c> (Android, Pixel, many cameras,
/// separator <c>_</c>, <c>-</c>, <c>.</c> or space), <c>YYYYMMDDHHMMSS</c>, and <c>YYYY-MM-DD HH.MM.SS</c> / <c>YYYY-MM-DD_HH-MM-SS</c> / <c>YYYY-MM-DD HH:MM:SS</c>.
/// The result states no time zone (Pixel <c>PXL_</c> names are UTC, but that is a device fact, not something the name says). A bare date is not accepted.
/// </summary>
public static class FileNameDates
{
    /// <summary>Tries to read a date and time from a file name.</summary>
    /// <param name="fileName">The file name; a directory part is ignored.</param>
    /// <param name="value">The time, with unknown zone, when the result is true.</param>
    /// <param name="pattern">A description of the pattern that matched (<c>YYYYMMDD_HHMMSS</c> or <c>YYYY-MM-DD HH.MM.SS</c>).</param>
    /// <returns>True when a plausible date (1990–2100) and time of day was found.</returns>
    public static bool TryParse(string fileName, out MediaDateTime value, out string pattern)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        value = default;
        pattern = string.Empty;
        ReadOnlySpan<char> s = Path.GetFileName(fileName.AsSpan());
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i]) || (i > 0 && char.IsAsciiDigit(s[i - 1])))
            {
                continue;
            }

            // YYYY-MM-DD<sep>HH<sep>MM<sep>SS
            if (i + 19 <= s.Length && s[i + 4] == '-' && s[i + 7] == '-' && Num(s, i, 4, out int y) && Num(s, i + 5, 2, out int mo) && Num(s, i + 8, 2, out int d)
                && s[i + 10] is ' ' or '_' or 'T' && Num(s, i + 11, 2, out int h) && s[i + 13] is '.' or ':' or '-' && Num(s, i + 14, 2, out int mi) && s[i + 16] is '.' or ':' or '-' && Num(s, i + 17, 2, out int sec)
                && Make(y, mo, d, h, mi, sec, out value))
            {
                pattern = "YYYY-MM-DD HH.MM.SS";
                return true;
            }

            // YYYYMMDD<sep?>HHMMSS
            if (i + 8 <= s.Length && Num(s, i, 4, out y) && Num(s, i + 4, 2, out mo) && Num(s, i + 6, 2, out d))
            {
                int p = i + 8;
                if (p < s.Length && s[p] is '_' or '-' or '.' or ' ')
                {
                    p++;
                }

                if (p + 6 <= s.Length && Num(s, p, 6, out int hms) && (p + 6 == s.Length || !char.IsAsciiDigit(s[p + 6]) || p + 9 <= s.Length) && Make(y, mo, d, hms / 10000, hms / 100 % 100, hms % 100, out value))
                {
                    pattern = "YYYYMMDD_HHMMSS";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Num(ReadOnlySpan<char> s, int start, int count, out int value)
    {
        value = 0;
        if (start + count > s.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!char.IsAsciiDigit(s[start + i]))
            {
                return false;
            }

            value = (value * 10) + (s[start + i] - '0');
        }

        return true;
    }

    private static bool Make(int y, int mo, int d, int h, int mi, int sec, out MediaDateTime value)
    {
        value = default;
        if (y is < 1990 or > 2100 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59)
        {
            return false;
        }

        value = new MediaDateTime(new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Unspecified), null, TimeZoneCertainty.Unknown);
        return true;
    }
}

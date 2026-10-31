using System.Globalization;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>A place on Earth as a capture position records it. Latitude, longitude and altitude are display values in degrees and metres.</summary>
/// <param name="Latitude">Degrees north (negative south), -90 to 90.</param>
/// <param name="Longitude">Degrees east (negative west), -180 to 180.</param>
/// <param name="Altitude">Metres above the reference, or <c>null</c> when the value has none.</param>
public readonly record struct GeoLocation(double Latitude, double Longitude, double? Altitude = null)
{
    /// <summary>
    /// Parses an ISO 6709 point as QuickTime and Android write it: <c>+37.7749-122.4194/</c>, <c>+37.7749-122.4194+012.500/</c>, the degrees-and-minutes
    /// form <c>+3746.49-12225.16/</c> and the degrees-minutes-seconds form <c>+374629.4-1221625.2/</c>, with an optional <c>CRS…</c> suffix.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="location">The point, when the result is true.</param>
    /// <returns>False when the text is not a valid latitude/longitude pair within range.</returns>
    public static bool TryParseIso6709(ReadOnlySpan<char> text, out GeoLocation location)
    {
        location = default;
        text = text.Trim();
        int crs = text.IndexOf("CRS", StringComparison.Ordinal);
        if (crs >= 0)
        {
            text = text[..crs];
        }

        text = text.TrimEnd('/');
        Span<double> values = stackalloc double[3]; // latitude, longitude, altitude
        Span<int> intDigits = stackalloc int[3];
        int count = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            if (count == 3 || (text[pos] != '+' && text[pos] != '-'))
            {
                return false;
            }

            int start = pos++;
            int dot = -1;
            while (pos < text.Length && (char.IsAsciiDigit(text[pos]) || (text[pos] == '.' && dot < 0)))
            {
                if (text[pos] == '.')
                {
                    dot = pos;
                }

                pos++;
            }

            ReadOnlySpan<char> number = text[(start + 1)..pos];
            if (number.IsEmpty || !double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v))
            {
                return false;
            }

            intDigits[count] = (dot < 0 ? pos : dot) - start - 1;
            values[count++] = text[start] == '-' ? -v : v;
        }

        if (count < 2)
        {
            return false;
        }

        // The integer digit count tells the form: latitude has 2 (DD), 4 (DDMM) or 6 (DDMMSS), longitude 3, 5 or 7.
        if (!ToDegrees(values[0], intDigits[0], 2, out double lat) || !ToDegrees(values[1], intDigits[1], 3, out double lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            return false;
        }

        location = new GeoLocation(lat, lon, count == 3 ? values[2] : null);
        return true;
    }

    private static bool ToDegrees(double value, int digits, int degreeDigits, out double degrees)
    {
        degrees = 0;
        double abs = Math.Abs(value);
        double sign = value < 0 ? -1 : 1;
        int extra = digits - degreeDigits;
        switch (extra)
        {
            case 0:
                degrees = value;
                return true;
            case 2:
            {
                double d = Math.Floor(abs / 100);
                double m = abs - (d * 100);
                if (m >= 60)
                {
                    return false;
                }

                degrees = sign * (d + (m / 60));
                return true;
            }

            case 4:
            {
                double d = Math.Floor(abs / 10000);
                double rest = abs - (d * 10000);
                double m = Math.Floor(rest / 100);
                double s = rest - (m * 100);
                if (m >= 60 || s >= 60)
                {
                    return false;
                }

                degrees = sign * (d + (m / 60) + (s / 3600));
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Parses an XMP GPS coordinate pair (<c>exif:GPSLatitude</c> and <c>exif:GPSLongitude</c>): <c>DDD,MM.mmk</c> or <c>DDD,MM,SSk</c>, where <c>k</c> is
    /// <c>N</c>, <c>S</c>, <c>E</c> or <c>W</c>.
    /// </summary>
    /// <param name="latitude">The latitude text.</param>
    /// <param name="longitude">The longitude text.</param>
    /// <param name="location">The point, when the result is true.</param>
    /// <returns>False when either text is malformed or out of range.</returns>
    public static bool TryParseXmp(string latitude, string longitude, out GeoLocation location)
    {
        location = default;
        if (!TryParseXmpCoordinate(latitude, "NS", out double lat) || !TryParseXmpCoordinate(longitude, "EW", out double lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            return false;
        }

        location = new GeoLocation(lat, lon);
        return true;
    }

    private static bool TryParseXmpCoordinate(string text, string hemispheres, out double degrees)
    {
        degrees = 0;
        text = text.Trim();
        if (text.Length < 2)
        {
            return false;
        }

        int h = hemispheres.IndexOf(char.ToUpperInvariant(text[^1]), StringComparison.Ordinal);
        if (h < 0)
        {
            return false;
        }

        string[] parts = text[..^1].Split(',');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        double total = 0;
        double divisor = 1;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) || v < 0)
            {
                return false;
            }

            total += v / divisor;
            divisor *= 60;
        }

        degrees = h == 1 ? -total : total;
        return true;
    }
}

/// <summary>How much a date-time says about the time zone it was recorded in.</summary>
public enum TimeZoneCertainty
{
    /// <summary>The value is a wall-clock reading with no zone: it may be local time or UTC.</summary>
    Unknown = 0,

    /// <summary>The value is UTC.</summary>
    Utc,

    /// <summary>The value is known to be local time of an unknown zone.</summary>
    Local,

    /// <summary>The value carries an explicit offset from UTC.</summary>
    Offset,
}

/// <summary>
/// A date and time with what is known about its zone. <see cref="Value"/> is the wall-clock reading as written; it is UTC only when
/// <see cref="Certainty"/> is <see cref="TimeZoneCertainty.Utc"/>.
/// </summary>
/// <param name="Value">The date and time as written (kind Utc for UTC values, otherwise Unspecified).</param>
/// <param name="OffsetMinutes">The offset from UTC in minutes when <paramref name="Certainty"/> is <see cref="TimeZoneCertainty.Offset"/>, else <c>null</c>.</param>
/// <param name="Certainty">What the writer said about the zone.</param>
public readonly record struct MediaDateTime(DateTime Value, int? OffsetMinutes, TimeZoneCertainty Certainty)
{
    /// <summary>The instant, when the zone is known (UTC or an explicit offset); otherwise <c>null</c>.</summary>
    public DateTimeOffset? ToDateTimeOffset() => Certainty switch
    {
        TimeZoneCertainty.Utc => new DateTimeOffset(DateTime.SpecifyKind(Value, DateTimeKind.Utc)),
        TimeZoneCertainty.Offset when OffsetMinutes is { } m => new DateTimeOffset(DateTime.SpecifyKind(Value, DateTimeKind.Unspecified), TimeSpan.FromMinutes(m)),
        _ => null,
    };

    /// <inheritdoc />
    public override string ToString() => Certainty switch
    {
        TimeZoneCertainty.Utc => Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        TimeZoneCertainty.Offset when OffsetMinutes is { } m => Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (m < 0 ? "-" : "+") + (Math.Abs(m) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (Math.Abs(m) % 60).ToString("00", CultureInfo.InvariantCulture),
        _ => Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + " (zone unknown)",
    };

    /// <summary>
    /// Parses the date-time forms found in tags: ISO 8601 (<c>2023-04-05T10:11:12+0200</c>, <c>…Z</c>, <c>…+02:00</c>, with optional fraction), the Exif form
    /// <c>2023:04:05 10:11:12</c>, and a space instead of <c>T</c>. A date without a time reads as midnight and is reported by <paramref name="dateOnly"/>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The value, when the result is true.</param>
    /// <param name="dateOnly">True when the text had no time of day.</param>
    /// <returns>False when the text is not a date-time (a bare year does not count).</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out MediaDateTime value, out bool dateOnly)
    {
        value = default;
        dateOnly = false;
        text = text.Trim();
        if (text.Length < 10 || !Digits(text, 0, 4, out int y) || !Digits(text, 5, 2, out int mo) || !Digits(text, 8, 2, out int d) || text[4] is not ('-' or ':') || text[7] != text[4])
        {
            return false;
        }

        if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo))
        {
            return false;
        }

        if (text.Length == 10)
        {
            dateOnly = true;
            value = new MediaDateTime(new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified), null, TimeZoneCertainty.Unknown);
            return true;
        }

        if (text[10] is not ('T' or 't' or ' ') || text.Length < 19 || !Digits(text, 11, 2, out int h) || text[13] != ':' || !Digits(text, 14, 2, out int mi) || text[16] != ':' || !Digits(text, 17, 2, out int s))
        {
            return false;
        }

        if (h > 23 || mi > 59 || s > 60)
        {
            return false;
        }

        int pos = 19;
        long ticks = 0;
        if (pos < text.Length && text[pos] is '.' or ',')
        {
            pos++;
            long scale = 1_000_000; // the first fractional digit is 10^6 ticks of 100 ns
            while (pos < text.Length && char.IsAsciiDigit(text[pos]))
            {
                if (scale > 0)
                {
                    ticks += (text[pos] - '0') * scale;
                    scale /= 10;
                }

                pos++;
            }

        }

        DateTime dt = new DateTime(y, mo, d, h, mi, Math.Min(s, 59), DateTimeKind.Unspecified).AddTicks(ticks);
        ReadOnlySpan<char> zone = text[pos..].Trim();
        if (zone.IsEmpty)
        {
            value = new MediaDateTime(dt, null, TimeZoneCertainty.Unknown);
            return true;
        }

        if (zone is "Z" or "z")
        {
            value = new MediaDateTime(DateTime.SpecifyKind(dt, DateTimeKind.Utc), null, TimeZoneCertainty.Utc);
            return true;
        }

        if (zone[0] is '+' or '-' && zone.Length >= 3 && Digits(zone, 1, 2, out int oh))
        {
            int om = 0;
            if (zone.Length == 5 && !Digits(zone, 3, 2, out om))
            {
                return false;
            }

            if (zone.Length == 6 && (zone[3] != ':' || !Digits(zone, 4, 2, out om)))
            {
                return false;
            }

            if (zone.Length is not (3 or 5 or 6) || oh > 23 || om > 59)
            {
                return false;
            }

            int minutes = ((oh * 60) + om) * (zone[0] == '-' ? -1 : 1);
            value = new MediaDateTime(dt, minutes, TimeZoneCertainty.Offset);
            return true;
        }

        return false;
    }

    private static bool Digits(ReadOnlySpan<char> s, int start, int count, out int value)
    {
        value = 0;
        if (start + count > s.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            char c = s[start + i];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }
}

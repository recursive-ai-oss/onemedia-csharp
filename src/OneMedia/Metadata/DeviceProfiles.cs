namespace RecursiveAI.OneMedia.Metadata;

/// <summary>How a <see cref="DeviceProfile"/> row was established.</summary>
public enum ProfileBasis
{
    /// <summary>Seen in sample files from the device: the stated behaviour was checked against an independent time (for example GPS UTC in the same file).</summary>
    ObservedInFiles = 0,

    /// <summary>Stated by a published document (a specification or a vendor/tool documentation page); no sample file from the device was checked.</summary>
    Documented = 1,
}

/// <summary>
/// A statement about what a device's time fields mean. Profiles are data: they only reinterpret candidates (for example mark the movie header
/// as local time) and never invent values.
/// </summary>
/// <param name="Id">A short stable identifier (<c>gopro</c>).</param>
/// <param name="Make">The device maker to match, case-insensitive and exact after trimming, or <c>null</c> to match on <paramref name="ModelPrefix"/> alone.</param>
/// <param name="ModelPrefix">A model prefix to match, case-insensitive, or <c>null</c> for any model of the maker. A row needs a make or a model prefix.</param>
/// <param name="HeaderTimeIsLocal">True when the binary header times (<c>mvhd</c>, <c>tkhd</c>, <c>mdhd</c>) hold the camera's local wall-clock time instead of UTC.</param>
/// <param name="Basis">How the row was established.</param>
/// <param name="Evidence">What settled the row, in words: the files or documents and what they showed.</param>
public sealed record DeviceProfile(string Id, string? Make, string? ModelPrefix, bool HeaderTimeIsLocal, ProfileBasis Basis, string Evidence)
{
    /// <summary>
    /// Software (firmware) strings that identify the device when no maker or model is stored, as shapes: <c>A</c> a letter, <c>9</c> a digit, anything else literal
    /// (<c>AA9.99.99.99.99</c> matches <c>HD6.01.01.60.00</c>). Empty for rows matched by maker and model.
    /// </summary>
    public IReadOnlyList<string> SoftwareShapes { get; init; } = [];

    /// <summary>True when <paramref name="software"/> has one of <see cref="SoftwareShapes"/>.</summary>
    /// <param name="software">A software or firmware string, or <c>null</c>.</param>
    /// <returns>True when a shape matches the whole string.</returns>
    public bool MatchesSoftware(string? software)
    {
        if (software is null)
        {
            return false;
        }

        foreach (string shape in SoftwareShapes)
        {
            if (shape.Length == software.Length && ShapeMatches(shape, software))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShapeMatches(string shape, string s)
    {
        for (int i = 0; i < shape.Length; i++)
        {
            bool ok = shape[i] switch
            {
                'A' => char.IsAsciiLetterUpper(s[i]),
                '9' => char.IsAsciiDigit(s[i]),
                _ => s[i] == shape[i],
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this row applies to a device with the given make and model (either may be <c>null</c> when the file does not say).</summary>
    /// <param name="make">The device maker as found.</param>
    /// <param name="model">The device model as found.</param>
    /// <returns>True when the maker (if the row names one) and model prefix (if it names one) both match.</returns>
    public bool Matches(string? make, string? model)
    {
        if (Make is null && ModelPrefix is null)
        {
            return false;
        }

        if (Make is not null && !string.Equals(make?.Trim(), Make, StringComparison.OrdinalIgnoreCase))
        {
            // A model that identifies the product on its own (HERO6 Black) matches even when no maker is stored.
            if (!(make is null && ModelPrefix is not null))
            {
                return false;
            }
        }

        return ModelPrefix is null || (model is not null && model.AsSpan().Trim().StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>An ordered table of <see cref="DeviceProfile"/> rows; the first matching row wins.</summary>
public sealed class DeviceProfileTable
{
    private readonly DeviceProfile[] _rows;

    /// <summary>Creates a table.</summary>
    /// <param name="rows">The rows in match order; copied.</param>
    public DeviceProfileTable(IEnumerable<DeviceProfile> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = [.. rows];
    }

    /// <summary>The rows in match order.</summary>
    public IReadOnlyList<DeviceProfile> Rows => _rows;

    /// <summary>The empty table: no device is special.</summary>
    public static DeviceProfileTable Empty { get; } = new([]);

    /// <summary>
    /// The built-in rows. Each cites what settled it; rows for devices with no sample file or document that settles a time-field meaning are
    /// left out on purpose, so the default rules (movie header as UTC) apply to them.
    /// </summary>
    public static DeviceProfileTable Default { get; } = new(
    [
        // Observed: GoPro HERO5/6/7/8 and MAX files carry GPMF GPS UTC times. The movie header time differs from the first GPS UTC time by a whole-hour
        // zone offset (HERO6: header 11:27:32 vs GPS 19:27:58 UTC; HERO5: 10:31:43 vs 17:31:03; MAX: 15:45:32 vs 23:45:14), so the header holds local time.
        new DeviceProfile("gopro", null, "HERO", HeaderTimeIsLocal: true, ProfileBasis.ObservedInFiles,
            "HERO5/HERO6 sample files: mvhd = GPS UTC minus a whole-hour zone offset (-7 h, -8 h), so mvhd is local wall-clock time."),
        new DeviceProfile("gopro-max", null, "GoPro", HeaderTimeIsLocal: true, ProfileBasis.ObservedInFiles,
            "GoPro MAX sample files: mvhd 15:45:32 against GPS UTC 23:45:14 on the same day (-8 h)."),
        new DeviceProfile("gopro-make", "GoPro", null, HeaderTimeIsLocal: true, ProfileBasis.ObservedInFiles,
            "Same GoPro files as the model rows, matched by maker for models that store one."),
        // The movie files carry no maker or model of their own (the model sits in the GPMF track); the FIRM atom's firmware string does: HD5.02.02.00.00,
        // HD6.01.01.60.00, HD7.01.01.80.00, HD8.01.01.20.00, H19.03.01.30.00 (MAX) and FS1.04.01.70.00 (Fusion) in the ten sample files.
        new DeviceProfile("gopro-firmware", null, null, HeaderTimeIsLocal: true, ProfileBasis.ObservedInFiles,
            "Ten GoPro sample files (HERO5 to HERO8, MAX, Fusion): the FIRM atom has the shape letter-letter-digit or letter-digit-digit, then four dotted two-digit groups; mvhd is local time in the files that carry GPS UTC times.")
        { SoftwareShapes = ["AA9.99.99.99.99", "A99.99.99.99.99"] },

        // Documented: the QuickTime header times are seconds since 1904 and, per the file format, UTC; Apple devices follow it and additionally write
        // com.apple.quicktime.creationdate with a UTC offset (read as an offset-bearing vendor time).
        new DeviceProfile("apple", "Apple", null, HeaderTimeIsLocal: false, ProfileBasis.Documented,
            "ExifTool QuickTime tag documentation: by the specification header times are UTC; Apple writes creationdate as an ISO 8601 date-time with offset. No iPhone sample file was available to check."),

        // Documented: Android's MediaRecorder writes the header in UTC and the Samsung camera adds the zone as samsung.android.utc_offset.
        new DeviceProfile("android", "Google", null, HeaderTimeIsLocal: false, ProfileBasis.Documented,
            "Android keys com.android.manufacturer/model/version; header times are UTC per the specification. No Pixel sample file was available to check."),
        new DeviceProfile("samsung", "samsung", null, HeaderTimeIsLocal: false, ProfileBasis.Documented,
            "ExifTool QuickTime tag documentation lists the key samsung.android.utc_offset (AndroidTimeZone) beside UTC header times. No Samsung sample file was available to check."),
    ]);

    /// <summary>Finds the first row that applies to the device.</summary>
    /// <param name="make">The maker, or <c>null</c>.</param>
    /// <param name="model">The model, or <c>null</c>.</param>
    /// <param name="software">The software or firmware string, or <c>null</c>.</param>
    /// <returns>The row, or <c>null</c> when no row applies.</returns>
    public DeviceProfile? Find(string? make, string? model, string? software = null)
    {
        foreach (DeviceProfile row in _rows)
        {
            if (row.Matches(make, model) || row.MatchesSoftware(software))
            {
                return row;
            }
        }

        return null;
    }
}

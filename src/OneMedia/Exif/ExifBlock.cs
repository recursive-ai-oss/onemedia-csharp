using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Exif;

/// <summary>The value types of a TIFF/Exif directory entry (TIFF 6.0 §2, Exif 2.32 §4.6.2).</summary>
public enum ExifType : ushort
{
    /// <summary>8-bit unsigned integer.</summary>
    Byte = 1,

    /// <summary>NUL-terminated 7-bit text.</summary>
    Ascii = 2,

    /// <summary>16-bit unsigned integer.</summary>
    Unsigned16 = 3,

    /// <summary>32-bit unsigned integer.</summary>
    Unsigned32 = 4,

    /// <summary>Two 32-bit unsigned integers: numerator, denominator.</summary>
    Rational = 5,

    /// <summary>8-bit signed integer.</summary>
    SByte = 6,

    /// <summary>Bytes of any meaning.</summary>
    Undefined = 7,

    /// <summary>16-bit signed integer.</summary>
    SShort = 8,

    /// <summary>32-bit signed integer.</summary>
    SLong = 9,

    /// <summary>Two 32-bit signed integers.</summary>
    SRational = 10,

    /// <summary>32-bit IEEE float.</summary>
    Real32 = 11,

    /// <summary>64-bit IEEE float.</summary>
    Real64 = 12,

    /// <summary>A 32-bit offset to a sub-directory.</summary>
    Ifd = 13,
}

/// <summary>One directory entry with its value bytes, kept as stored (in the block's byte order).</summary>
/// <param name="Ifd">The directory: <c>IFD0</c>, <c>IFD1</c>, <c>ExifIFD</c>, <c>GPS</c>, <c>InteropIFD</c> or <c>SubIFD</c>.</param>
/// <param name="Tag">The tag number.</param>
/// <param name="Type">The value type; an unknown type number is kept as is.</param>
/// <param name="Count">The number of values.</param>
/// <param name="ValueRange">Where the value bytes lie in the source the block was read from, or <see cref="ByteRange.None"/> when the block's position is unknown.</param>
/// <param name="Value">The value bytes, a slice of the block; empty when the value lay outside the block.</param>
/// <param name="BigEndian">The byte order of <paramref name="Value"/>.</param>
public sealed record ExifEntry(string Ifd, ushort Tag, ExifType Type, uint Count, ByteRange ValueRange, ReadOnlyMemory<byte> Value, bool BigEndian)
{
    /// <summary>The value as text for <see cref="ExifType.Ascii"/> entries (up to the first NUL, trimmed), else <c>null</c>.</summary>
    /// <returns>The text.</returns>
    public string? AsText()
    {
        if (Type != ExifType.Ascii)
        {
            return null;
        }

        ReadOnlySpan<byte> v = Value.Span;
        int nul = v.IndexOf((byte)0);
        if (nul >= 0)
        {
            v = v[..nul];
        }

        return Encoding.Latin1.GetString(v).Trim();
    }

    /// <summary>Reads the value as an unsigned integer for byte, short and long entries.</summary>
    /// <param name="index">Which value.</param>
    /// <param name="value">The integer.</param>
    /// <returns>False when the type or index does not fit.</returns>
    public bool TryGetUInt(int index, out uint value)
    {
        value = 0;
        ReadOnlySpan<byte> v = Value.Span;
        switch (Type)
        {
            case ExifType.Byte when index >= 0 && index < v.Length:
                value = v[index];
                return true;
            case ExifType.Unsigned16 when index >= 0 && (index + 1) * 2 <= v.Length:
                value = BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(v[(index * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(v[(index * 2)..]);
                return true;
            case ExifType.Unsigned32 or ExifType.Ifd when index >= 0 && (index + 1) * 4 <= v.Length:
                value = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(v[(index * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(v[(index * 4)..]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads a rational (signed or unsigned) as a floating-point display value.</summary>
    /// <param name="index">Which rational.</param>
    /// <param name="value">The quotient.</param>
    /// <returns>False when the entry is not a rational, the index is out of range, or the denominator is zero.</returns>
    public bool TryGetRational(int index, out double value)
    {
        value = 0;
        ReadOnlySpan<byte> v = Value.Span;
        if (Type is not (ExifType.Rational or ExifType.SRational) || index < 0 || (index + 1) * 8 > v.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> r = v.Slice(index * 8, 8);
        long n;
        long d;
        if (Type == ExifType.Rational)
        {
            n = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(r) : BinaryPrimitives.ReadUInt32LittleEndian(r);
            d = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(r[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
        }
        else
        {
            n = BigEndian ? BinaryPrimitives.ReadInt32BigEndian(r) : BinaryPrimitives.ReadInt32LittleEndian(r);
            d = BigEndian ? BinaryPrimitives.ReadInt32BigEndian(r[4..]) : BinaryPrimitives.ReadInt32LittleEndian(r[4..]);
        }

        if (d == 0)
        {
            return false;
        }

        value = (double)n / d;
        return true;
    }
}

/// <summary>
/// A parsed TIFF/Exif block (CIPA DC-008 / Exif 2.32 §4.5–4.6, TIFF 6.0 §2): the entries of IFD0 and every directory reachable from it, with their
/// raw value bytes. Maker notes and thumbnails are kept as ordinary entries and never interpreted. No image is decoded.
/// </summary>
public sealed class ExifBlock
{
    private const int MaxDirectories = 16;
    private const int MaxEntriesPerDirectory = 4096;
    private const int MaxEntries = 20_000;

    private ExifBlock(bool bigEndian, IReadOnlyList<ExifEntry> entries)
    {
        BigEndian = bigEndian;
        Entries = entries;
    }

    /// <summary>True for a Motorola-order (<c>MM</c>) block.</summary>
    public bool BigEndian { get; }

    /// <summary>Every entry, directory by directory in the order they were reached.</summary>
    public IReadOnlyList<ExifEntry> Entries { get; }

    /// <summary>Finds the first entry with the given directory and tag.</summary>
    /// <param name="ifd">The directory name, as in <see cref="ExifEntry.Ifd"/>.</param>
    /// <param name="tag">The tag number.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public ExifEntry? Find(string ifd, ushort tag)
    {
        foreach (ExifEntry e in Entries)
        {
            if (e.Tag == tag && e.Ifd == ifd)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for a TIFF header (<c>II*\0</c> or <c>MM\0*</c>) at the start of <paramref name="data"/> or within the first <paramref name="searchBytes"/> bytes
    /// (container wrappers put a few bytes of their own, such as <c>Exif\0\0</c>, in front) and parses the block.
    /// </summary>
    /// <param name="data">The block bytes, held by the result (not copied).</param>
    /// <param name="dataOffset">Where <paramref name="data"/> starts in the source, for byte ranges; -1 when unknown.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.MaxTagBytes"/> bounds the block.</param>
    /// <param name="defects">Receives defects: <c>exif.header</c>, <c>exif.truncated</c>, <c>exif.loop</c>, <c>exif.limit</c>.</param>
    /// <param name="block">The parsed block when the result is true; entries read before a defect are kept.</param>
    /// <param name="searchBytes">How far into the data a TIFF header may start. Default 16.</param>
    /// <returns>False when no TIFF header is found.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, long dataOffset, MediaLimits limits, DefectLog defects, out ExifBlock block, int searchBytes = 16)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        block = null!;
        if (data.Length > limits.MaxTagBytes)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, dataOffset, "The Exif block is larger than the tag limit and is not parsed.", "exif.limit");
            return false;
        }

        int start = -1;
        ReadOnlySpan<byte> span = data.Span;
        for (int i = 0; i <= Math.Min(searchBytes, span.Length - 8); i++)
        {
            if ((span[i] == 'I' && span[i + 1] == 'I' && span[i + 2] == 0x2A && span[i + 3] == 0) || (span[i] == 'M' && span[i + 1] == 'M' && span[i + 2] == 0 && span[i + 3] == 0x2A))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        ReadOnlyMemory<byte> tiff = data[start..];
        bool big = tiff.Span[0] == 'M';
        long baseOffset = dataOffset < 0 ? -1 : dataOffset + start;
        List<ExifEntry> entries = [];
        HashSet<uint> seen = [];
        Queue<(string Name, uint Offset)> work = new();
        work.Enqueue(("IFD0", ReadU32(tiff.Span[4..], big)));
        while (work.Count > 0)
        {
            (string name, uint offset) = work.Dequeue();
            if (!seen.Add(offset))
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, baseOffset < 0 ? -1 : baseOffset + offset, "An Exif directory is reached twice (a loop).", "exif.loop");
                continue;
            }

            if (seen.Count > MaxDirectories)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, baseOffset, "The Exif block has too many directories; the rest are skipped.", "exif.limit");
                break;
            }

            ReadDirectory(tiff, baseOffset, big, name, offset, entries, work, defects);
            if (entries.Count >= MaxEntries)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, baseOffset, "The Exif block has too many entries; the rest are skipped.", "exif.limit");
                break;
            }
        }

        block = new ExifBlock(big, entries);
        return true;
    }

    private static void ReadDirectory(ReadOnlyMemory<byte> tiff, long baseOffset, bool big, string name, uint offset, List<ExifEntry> entries, Queue<(string, uint)> work, DefectLog defects)
    {
        ReadOnlySpan<byte> t = tiff.Span;
        if (t.Length < 2 || offset > (uint)t.Length - 2)
        {
            defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, baseOffset < 0 ? -1 : baseOffset + offset, $"The Exif directory {name} starts outside the block.", "exif.truncated");
            return;
        }

        int count = big ? BinaryPrimitives.ReadUInt16BigEndian(t[(int)offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(t[(int)offset..]);
        if (count > MaxEntriesPerDirectory)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, baseOffset < 0 ? -1 : baseOffset + offset, $"The Exif directory {name} has {count} entries; reading {MaxEntriesPerDirectory}.", "exif.limit");
            count = MaxEntriesPerDirectory;
        }

        long pos = (long)offset + 2;
        for (int i = 0; i < count; i++, pos += 12)
        {
            if (pos + 12 > t.Length)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, baseOffset < 0 ? -1 : baseOffset + pos, $"The Exif directory {name} ends inside its entries.", "exif.truncated");
                return;
            }

            ReadOnlySpan<byte> e = t.Slice((int)pos, 12);
            ushort tag = big ? BinaryPrimitives.ReadUInt16BigEndian(e) : BinaryPrimitives.ReadUInt16LittleEndian(e);
            ushort type = big ? BinaryPrimitives.ReadUInt16BigEndian(e[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            uint n = ReadU32(e[4..], big);
            int unit = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };
            ReadOnlyMemory<byte> value = default;
            ByteRange range = ByteRange.None;
            long size = (long)unit * n;
            if (unit > 0)
            {
                if (size <= 4)
                {
                    value = tiff.Slice((int)pos + 8, (int)size);
                    range = baseOffset < 0 ? ByteRange.None : new ByteRange(baseOffset + pos + 8, size);
                }
                else
                {
                    uint vo = ReadU32(e[8..], big);
                    if (vo + size <= t.Length)
                    {
                        value = tiff.Slice((int)vo, (int)size);
                        range = baseOffset < 0 ? ByteRange.None : new ByteRange(baseOffset + vo, size);
                    }
                    else
                    {
                        defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, baseOffset < 0 ? -1 : baseOffset + pos, $"Exif tag 0x{tag:X4} in {name} has a value outside the block.", "exif.truncated");
                    }
                }
            }

            entries.Add(new ExifEntry(name, tag, (ExifType)type, n, range, value, big));

            // Exif 2.32 §4.6.3: 0x8769 points to the Exif IFD, 0x8825 to the GPS IFD, 0xA005 (in the Exif IFD) to the Interoperability IFD; TIFF 6.0
            // adds SubIFDs (0x014A, one or more offsets).
            string? sub = (name, tag) switch
            {
                ("IFD0" or "IFD1" or "SubIFD", 0x8769) => "ExifIFD",
                ("IFD0" or "IFD1" or "SubIFD", 0x8825) => "GPS",
                ("ExifIFD", 0xA005) => "InteropIFD",
                ("IFD0", 0x014A) => "SubIFD",
                _ => null,
            };
            if (sub is not null && value.Length >= 4)
            {
                for (int k = 0; k < Math.Min(value.Length / 4, 4); k++)
                {
                    work.Enqueue((sub, ReadU32(value.Span[(k * 4)..], big)));
                }
            }
        }

        // The next-IFD offset after the entries links IFD0 to IFD1 (the thumbnail directory); only IFD0's link is followed.
        if (name == "IFD0" && pos + 4 <= t.Length)
        {
            uint next = ReadU32(t[(int)pos..], big);
            if (next != 0)
            {
                work.Enqueue(("IFD1", next));
            }
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    /// <summary>
    /// Turns the block into fact candidates: capture time (<c>DateTimeOriginal</c> with <c>OffsetTimeOriginal</c>, then <c>DateTimeDigitized</c>, then the GPS UTC date
    /// and time), position, maker, model, software and body serial number. Sources read <c>exif:IFD0.Make</c>, <c>exif:ExifIFD.DateTimeOriginal</c> and so on.
    /// </summary>
    /// <param name="sourcePrefix">The prefix for candidate sources (<c>exif</c>, or <c>sidecar:exif</c>).</param>
    /// <returns>The candidates.</returns>
    public EvidenceSet ToEvidence(string sourcePrefix = "exif")
    {
        EvidenceSet set = new(sourcePrefix);
        List<FactCandidate<MediaDateTime>> times = [];
        AddExifTime(times, sourcePrefix, 0x9003, 0x9011, 0x9291, "DateTimeOriginal");
        AddExifTime(times, sourcePrefix, 0x9004, 0x9012, 0x9292, "DateTimeDigitized");
        AddGpsTime(times, sourcePrefix);
        set.CaptureTime = times;

        if (TryGps(out GeoLocation g, out ExifEntry? at))
        {
            set.Location = [new FactCandidate<GeoLocation>(g, sourcePrefix + ":GPS", string.Create(CultureInfo.InvariantCulture, $"{g.Latitude} {g.Longitude}"), "GPS IFD degrees, minutes, seconds with hemisphere references", Range: at.ValueRange)];
        }

        set.Make = Text("IFD0", 0x010F, "Make", sourcePrefix);
        set.Model = Text("IFD0", 0x0110, "Model", sourcePrefix);
        set.Software = Text("IFD0", 0x0131, "Software", sourcePrefix);
        List<FactCandidate<string>> serial = [.. Text("ExifIFD", 0xA431, "BodySerialNumber", sourcePrefix)];
        serial.AddRange(Text("IFD0", 0xC62F, "CameraSerialNumber", sourcePrefix));
        set.Serial = serial;
        return set;
    }

    private FactCandidate<string>[] Text(string ifd, ushort tag, string name, string prefix) =>
        Find(ifd, tag) is { } e && e.AsText() is { Length: > 0 } s ? [new FactCandidate<string>(s, $"{prefix}:{ifd}.{name}", s, "Exif ASCII text", Range: e.ValueRange)] : [];

    private void AddExifTime(List<FactCandidate<MediaDateTime>> into, string prefix, ushort tag, ushort offsetTag, ushort subSecTag, string name)
    {
        if (Find("ExifIFD", tag) is not { } e || e.AsText() is not { } text)
        {
            return;
        }

        // Exif 2.32 §4.6.5: "YYYY:MM:DD HH:MM:SS"; blanks stand for unknown digits and make the value unusable.
        if (!MediaDateTime.TryParse(text, out MediaDateTime dt, out bool dateOnly) || dateOnly)
        {
            return;
        }

        string raw = text;
        if (Find("ExifIFD", subSecTag)?.AsText() is { Length: > 0 } sub && sub.Length <= 7 && IsDigits(sub))
        {
            long ticks = long.Parse(sub.PadRight(7, '0'), CultureInfo.InvariantCulture);
            dt = dt with { Value = dt.Value.AddTicks(ticks) };
            raw += "." + sub;
        }

        string how = "Exif date and time, local time of the camera";
        // Exif 2.31 added OffsetTime* tags: "+HH:MM".
        if (Find("ExifIFD", offsetTag)?.AsText() is { Length: > 0 } off && MediaDateTime.TryParse(text.Trim() + off, out MediaDateTime withZone, out _) && withZone.Certainty == TimeZoneCertainty.Offset)
        {
            dt = dt with { OffsetMinutes = withZone.OffsetMinutes, Certainty = TimeZoneCertainty.Offset };
            raw += off;
            how = "Exif date and time with its OffsetTime tag";
        }

        into.Add(new FactCandidate<MediaDateTime>(dt, $"{prefix}:ExifIFD.{name}", raw, how, dt.Certainty, e.ValueRange));
    }

    private static bool IsDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private void AddGpsTime(List<FactCandidate<MediaDateTime>> into, string prefix)
    {
        // Exif 2.32 §4.6.6: GPSTimeStamp is three rationals (hour, minute, second) in UTC; GPSDateStamp is "YYYY:MM:DD".
        if (Find("GPS", 0x0007) is not { } ts || Find("GPS", 0x001D)?.AsText() is not { } ds
            || !ts.TryGetRational(0, out double h) || !ts.TryGetRational(1, out double m) || !ts.TryGetRational(2, out double s)
            || !MediaDateTime.TryParse(ds, out MediaDateTime day, out bool dateOnly) || !dateOnly || h is < 0 or > 23 || m is < 0 or >= 60 || s is < 0 or >= 61)
        {
            return;
        }

        DateTime utc = DateTime.SpecifyKind(day.Value, DateTimeKind.Utc).AddTicks(((long)h * 3600L * 10_000_000L) + ((long)m * 60L * 10_000_000L) + (long)(Math.Min(s, 59.9999999) * 10_000_000.0));
        into.Add(new FactCandidate<MediaDateTime>(new MediaDateTime(utc, null, TimeZoneCertainty.Utc), $"{prefix}:GPS.DateTime", $"{ds} {h:0}:{m:0}:{s:0.###}",
            "GPS date stamp and UTC time stamp", TimeZoneCertainty.Utc, ts.ValueRange));
    }

    private bool TryGps(out GeoLocation location, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExifEntry? at)
    {
        location = default;
        at = Find("GPS", 0x0002);
        if (at is null || Find("GPS", 0x0004) is not { } lonE || Find("GPS", 0x0001)?.AsText() is not { Length: > 0 } latRef || Find("GPS", 0x0003)?.AsText() is not { Length: > 0 } lonRef)
        {
            return false;
        }

        // Exif 2.32 §4.6.6: GPSStatus "A" is a measurement in progress, "V" is interoperability (void: the position is not valid).
        if (Find("GPS", 0x0009)?.AsText() is "V")
        {
            return false;
        }

        if (!Dms(at, out double lat) || !Dms(lonE, out double lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || (lat == 0 && lon == 0))
        {
            return false;
        }

        lat = char.ToUpperInvariant(latRef[0]) == 'S' ? -lat : lat;
        lon = char.ToUpperInvariant(lonRef[0]) == 'W' ? -lon : lon;
        double? alt = null;
        if (Find("GPS", 0x0006) is { } a && a.TryGetRational(0, out double metres))
        {
            alt = Find("GPS", 0x0005) is { } r && r.TryGetUInt(0, out uint below) && below == 1 ? -metres : metres;
        }

        location = new GeoLocation(lat, lon, alt);
        return true;
    }

    private static bool Dms(ExifEntry e, out double degrees)
    {
        degrees = 0;
        if (!e.TryGetRational(0, out double d) || !e.TryGetRational(1, out double m) || !e.TryGetRational(2, out double s) || d < 0 || m < 0 || s < 0)
        {
            return false;
        }

        degrees = d + (m / 60) + (s / 3600);
        return true;
    }
}

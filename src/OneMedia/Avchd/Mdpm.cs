using System.Globalization;

using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Avchd;

/// <summary>One entry of the Modified Digital Video Pack Metadata: a one-byte tag and four data bytes.</summary>
/// <param name="Tag">The tag byte (for example 0x18 and 0x19 for the recording date and time, 0xE0 for the maker and model).</param>
/// <param name="Data">The four data bytes, exactly as stored.</param>
public readonly record struct MdpmEntry(byte Tag, uint Data)
{
    /// <summary>The data bytes as an 8-digit hexadecimal string.</summary>
    public string Hex => Data.ToString("x8", CultureInfo.InvariantCulture);
}

/// <summary>
/// The camera data that AVCHD camcorders put in an H.264 SEI user-data-unregistered message with the UUID
/// 17ee8c60-f84d-11d9-8cd6-0800200c9a66 and the marker <c>MDPM</c>, once per group of pictures. After the marker comes an entry
/// count and that many entries of a tag byte and four data bytes. Only layouts confirmed on real files are decoded; every entry
/// is kept as stored.
/// </summary>
public sealed class MdpmBlock
{
    internal MdpmBlock(List<MdpmEntry> entries) => Entries = entries;

    /// <summary>All entries in stored order.</summary>
    public IReadOnlyList<MdpmEntry> Entries { get; }

    /// <summary>Finds the first entry with a tag.</summary>
    /// <param name="tag">The tag byte.</param>
    /// <param name="entry">The entry when found.</param>
    /// <returns>True when the tag is present.</returns>
    public bool TryGet(byte tag, out MdpmEntry entry)
    {
        foreach (MdpmEntry e in Entries)
        {
            if (e.Tag == tag)
            {
                entry = e;
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// The recording date and time as the camera's clock showed it, from tags 0x18 (a flags byte, century, year, month, each BCD) and
    /// 0x19 (day, hour, minute, second, each BCD), or null when absent or not valid BCD dates. The flags byte may carry a time zone
    /// whose coding is not documented in the sources used here, so the value is reported as local time of unknown zone.
    /// </summary>
    public DateTime? RecordingTime
    {
        get
        {
            if (!TryGet(0x18, out MdpmEntry a) || !TryGet(0x19, out MdpmEntry b))
            {
                return null;
            }

            int century = Bcd((byte)(a.Data >> 16)), year = Bcd((byte)(a.Data >> 8)), month = Bcd((byte)a.Data);
            int day = Bcd((byte)(b.Data >> 24)), hour = Bcd((byte)(b.Data >> 16)), minute = Bcd((byte)(b.Data >> 8)), second = Bcd((byte)b.Data);
            if (century < 0 || year < 0 || month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 60)
            {
                return null;
            }

            int y = (century * 100) + year;
            if (y is < 1 or > 9999 || day > DateTime.DaysInMonth(y, month))
            {
                return null;
            }

            return new DateTime(y, month, day, hour, minute, Math.Min(second, 59), DateTimeKind.Unspecified);
        }
    }

    /// <summary>The raw flags byte of tag 0x18 (the first data byte), or -1 when the tag is absent.</summary>
    public int RecordingTimeFlags => TryGet(0x18, out MdpmEntry a) ? (int)(a.Data >> 24) : -1;

    /// <summary>The camera maker from tag 0xE0 (first two data bytes: 0x0103 Panasonic, 0x0108 Sony, 0x1011 Canon, 0x1104 JVC), or null.</summary>
    public string? Make
    {
        get
        {
            if (!TryGet(0xE0, out MdpmEntry e))
            {
                return null;
            }

            return (e.Data >> 16) switch
            {
                0x0103 => "Panasonic",
                0x0108 => "Sony",
                0x1011 => "Canon",
                0x1104 => "JVC",
                _ => null,
            };
        }
    }

    /// <summary>The two-byte maker code and the two-byte model code of tag 0xE0, or -1 each when absent.</summary>
    public (int Maker, int Model) MakerModelCodes => TryGet(0xE0, out MdpmEntry e) ? ((int)(e.Data >> 16), (int)(e.Data & 0xFFFF)) : (-1, -1);

    private static int Bcd(byte b) => (b >> 4) > 9 || (b & 0xF) > 9 ? -1 : ((b >> 4) * 10) + (b & 0xF);
}

/// <summary>Finds MDPM in SEI NAL units.</summary>
public static class Mdpm
{
    private static ReadOnlySpan<byte> Uuid => [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];

    /// <summary>
    /// Finds the MDPM entries of one SEI NAL unit without allocating: the unescaped bytes go to <paramref name="scratch"/> (at most 4096 are kept)
    /// and <paramref name="entries"/> is a slice of it holding the five-byte entries.
    /// </summary>
    /// <param name="sei">The NAL unit payload after the one-byte NAL header, as stored.</param>
    /// <param name="scratch">Working space of at least <c>Math.Min(sei.Length, 4096)</c> bytes.</param>
    /// <param name="entries">The entries (a tag byte and four data bytes each), a slice of <paramref name="scratch"/>.</param>
    /// <returns>True when the NAL unit holds an MDPM message with at least one whole entry.</returns>
    public static bool TryFindEntries(ReadOnlySpan<byte> sei, Span<byte> scratch, out ReadOnlySpan<byte> entries)
    {
        entries = default;
        if (sei.Length < 2 || sei.Length > 4096 || scratch.Length < sei.Length)
        {
            return false;
        }

        int n = EmulationPrevention.Remove(sei, scratch);
        SeiMessageReader reader = new(scratch[..n]);
        while (reader.TryRead(out int type, out ReadOnlySpan<byte> payload))
        {
            if (type == 5 && payload.Length >= 16 + 4 + 1 && payload[..16].SequenceEqual(Uuid) && payload.Slice(16, 4).SequenceEqual("MDPM"u8))
            {
                int count = Math.Min(payload[20], (payload.Length - 21) / 5);
                entries = payload.Slice(21, count * 5);
                return count > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Looks through the messages of one SEI NAL unit for MDPM. Emulation prevention bytes are removed first, because message sizes
    /// count the bytes after removal (ITU-T H.264 §7.4.1.1).
    /// </summary>
    /// <param name="sei">The NAL unit payload after the one-byte NAL header (the SEI messages and the trailing bits), as stored.</param>
    /// <param name="block">The entries when found.</param>
    /// <returns>True when the NAL unit holds an MDPM message.</returns>
    public static bool TryParse(ReadOnlySpan<byte> sei, out MdpmBlock? block)
    {
        block = null;
        if (sei.Length < 2 || sei.Length > 4096)
        {
            return false;
        }

        Span<byte> rbsp = stackalloc byte[sei.Length]; // bounded: SEI payloads are cut to 4096 bytes above
        int n = 0, zeros = 0;
        foreach (byte b in sei)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = b == 0 ? zeros + 1 : 0;
            rbsp[n++] = b;
        }

        ReadOnlySpan<byte> d = rbsp[..n];
        int p = 0;
        while (p + 2 <= d.Length)
        {
            int type = 0;
            while (p < d.Length && d[p] == 0xFF)
            {
                type += 255;
                p++;
            }

            if (p >= d.Length)
            {
                return false;
            }

            type += d[p++];
            int size = 0;
            while (p < d.Length && d[p] == 0xFF)
            {
                size += 255;
                p++;
            }

            if (p >= d.Length)
            {
                return false;
            }

            size += d[p++];
            if (p + size > d.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> payload = d.Slice(p, size);
            if (type == 5 && payload.Length >= 16 + 4 + 1 && payload[..16].SequenceEqual(Uuid) && payload.Slice(16, 4).SequenceEqual("MDPM"u8))
            {
                int count = payload[20];
                List<MdpmEntry> entries = new(count);
                for (int i = 0; i < count && 21 + ((i + 1) * 5) <= payload.Length; i++)
                {
                    int at = 21 + (i * 5);
                    entries.Add(new MdpmEntry(payload[at], (uint)((payload[at + 1] << 24) | (payload[at + 2] << 16) | (payload[at + 3] << 8) | payload[at + 4])));
                }

                block = new MdpmBlock(entries);
                return true;
            }

            p += size;
        }

        return false;
    }
}

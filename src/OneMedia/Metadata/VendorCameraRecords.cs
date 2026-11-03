using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>
/// The maker, model and software strings that compact cameras and DSLRs keep in their own user-data atoms and AVI chunks.
/// The layouts come from field offsets listed in ExifTool's tag documentation for these atoms and were checked against the bytes of
/// published camera files; each reader accepts only what it can validate and otherwise says nothing.
/// </summary>
internal static class VendorCameraRecords
{
    /// <summary>What a vendor atom says about the camera. A member is null when the atom does not carry it.</summary>
    internal readonly record struct Identity(string? Make, string? Model, string? Software)
    {
        public bool IsEmpty => Make is null && Model is null && Software is null;
    }

    /// <summary>How many leading bytes of a vendor atom the readers need. The atoms hold thumbnails and previews after the strings.</summary>
    public const int PrefixBytes = 16 * 1024;

    /// <summary>Reads the <c>NCDT</c> atom of Nikon movies: a list of atoms of which <c>NCTG</c> holds the camera's tag records.</summary>
    /// <param name="payload">The first bytes of the <c>NCDT</c> payload (at least up to the end of <c>NCTG</c>).</param>
    public static Identity ReadNikonNcdt(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        while (pos + 8 <= payload.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(payload[pos..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(payload[(pos + 4)..]);
            if (size < 8)
            {
                break;
            }

            if (type == 0x4E435447 /* NCTG */)
            {
                int end = (int)Math.Min(payload.Length, pos + (long)size);
                return ReadNikonTagRecords(payload[(pos + 8)..end]);
            }

            if (size > payload.Length - pos)
            {
                break;
            }

            pos += (int)size;
        }

        return default;
    }

    /// <summary>
    /// Reads Nikon tag records: a 32-bit tag, a 16-bit TIFF-style type, a 16-bit count and the values. A value shorter than four bytes
    /// still takes four (observed in the NCTG atoms of Coolpix and D-series files). Tags 1, 2 and 3 are maker, model and software.
    /// </summary>
    public static Identity ReadNikonTagRecords(ReadOnlySpan<byte> records)
    {
        string? make = null;
        string? model = null;
        string? software = null;
        int pos = 0;
        while (pos + 8 <= records.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(records[pos..]);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(records[(pos + 4)..]);
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(records[(pos + 6)..]);
            int unit = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };
            if (unit == 0 || tag > 3)
            {
                break;
            }

            int size = Math.Max(4, unit * count);
            pos += 8;
            if (size > records.Length - pos)
            {
                break;
            }

            if (type == 2)
            {
                string? text = CleanText(records.Slice(pos, size));
                switch (tag)
                {
                    case 1: make = text; break;
                    case 2: model = text; break;
                    case 3: software = text; break;
                }
            }

            pos += size;
        }

        return new Identity(make, model, software);
    }

    /// <summary>
    /// Reads the <c>TAGS</c> atom of Nikon Coolpix movies, which starts with a 24-byte maker string and a model string, with the
    /// software string at byte 175 (ExifTool's "Nikon MOV" layout).
    /// </summary>
    public static Identity ReadNikonTags(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 40 || !payload.StartsWith("NIKON"u8))
        {
            return default;
        }

        // The software string is not NUL-terminated in the files seen: a binary field follows it directly.
        string? software = payload.Length > 175 ? CleanText(payload[175..Math.Min(payload.Length, 175 + 32)], stopAtControl: true) : null;
        return new Identity(CleanText(payload[..24]), CleanText(payload[24..38]), software);
    }

    /// <summary>
    /// Reads the <c>TAGS</c> atom of recent Olympus movies: a 12-byte header, then an <c>OLYM</c> atom holding a <c>prms</c> atom whose
    /// data has the maker string at byte 18 and the camera type code at byte 44. The code is turned into a model name through
    /// <see cref="OlympusCameraTypes"/>; an unlisted code is returned as it stands.
    /// </summary>
    public static Identity ReadOlympusTags(ReadOnlySpan<byte> payload)
    {
        const int Olym = 12;
        if (payload.Length < Olym + 16 || !payload[(Olym + 4)..].StartsWith("OLYM"u8) || !payload[(Olym + 12)..].StartsWith("prms"u8))
        {
            return default;
        }

        uint prmsSize = BinaryPrimitives.ReadUInt32BigEndian(payload[(Olym + 8)..]);
        ReadOnlySpan<byte> prms = payload[(Olym + 16)..];
        if (prmsSize < 8 || prmsSize - 8 < 52 || prms.Length < 52)
        {
            return default;
        }

        string? make = CleanText(prms.Slice(18, 26));
        string? code = CleanText(prms.Slice(44, 8));
        string? model = code is null ? null : OlympusCameraTypes.Find(code) ?? code;
        return new Identity(make, model, null);
    }

    /// <summary>
    /// Reads the <c>PANA</c> atom of Panasonic movies: the model name is a zero-padded string at byte 12 (observed in a Lumix mirrorless
    /// file). The maker is Panasonic by the atom itself.
    /// </summary>
    public static Identity ReadPanasonicPana(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 28)
        {
            return default;
        }

        string? model = CleanText(payload.Slice(12, 16));
        return model is null ? default : new Identity("Panasonic", model, null);
    }

    /// <summary>Reads the <c>CNMN</c> atom of Canon movies, which holds the model name as a zero-padded string.</summary>
    public static Identity ReadCanonCnmn(ReadOnlySpan<byte> payload) => new(null, CleanText(payload[..Math.Min(payload.Length, 64)]), null);

    /// <summary>
    /// Reads the <c>nctg</c> chunk of Nikon AVI files: records of a 16-bit tag, a 16-bit size and the value; tags 3, 4 and 5 are
    /// maker, model and software (observed in a Nikon DSLR clip).
    /// </summary>
    public static Identity ReadNikonAviTags(ReadOnlySpan<byte> chunk)
    {
        string? make = null;
        string? model = null;
        string? software = null;
        int pos = 0;
        while (pos + 4 <= chunk.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(chunk[pos..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(pos + 2)..]);
            pos += 4;
            if (size > chunk.Length - pos)
            {
                break;
            }

            switch (tag)
            {
                case 3: make = CleanText(chunk.Slice(pos, size)); break;
                case 4: model = CleanText(chunk.Slice(pos, size)); break;
                case 5: software = CleanText(chunk.Slice(pos, size), joinParts: true); break;
            }

            pos += size;
        }

        return new Identity(make, model, software);
    }

    // A zero-padded text field: the part before the first NUL, trimmed, accepted only when it is printable ASCII or Latin-1.
    // stopAtControl ends the text at any control byte instead of rejecting it. joinParts drops NULs inside the text and keeps the
    // rest (the firmware field of a Nikon AVI holds "A1.00", a NUL, " B1.00" and is shown as one string).
    private static string? CleanText(ReadOnlySpan<byte> field, bool stopAtControl = false, bool joinParts = false)
    {
        if (joinParts)
        {
            field = field.TrimEnd((byte)0);
            byte[] joined = new byte[field.Length];
            int n = 0;
            foreach (byte x in field)
            {
                if (x != 0 && n < joined.Length)
                {
                    joined[n++] = x;
                }
            }

            field = joined.AsSpan(0, n);
        }
        else if (stopAtControl)
        {
            int end = 0;
            while (end < field.Length && field[end] >= 0x20 && field[end] != 0x7F)
            {
                end++;
            }

            field = field[..end];
        }

        int nul = field.IndexOf((byte)0);
        if (nul >= 0)
        {
            field = field[..nul];
        }

        foreach (byte b in field)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return null;
            }
        }

        string s = Encoding.Latin1.GetString(field).Trim();
        return s.Length == 0 ? null : s;
    }
}

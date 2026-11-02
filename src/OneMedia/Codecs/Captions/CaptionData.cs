namespace RecursiveAI.OneMedia.Codecs.Captions;

/// <summary>
/// One three-byte construct of an ATSC <c>cc_data()</c> structure (A/53 Part 4 §6.2.3.1): a validity flag, a type and two data bytes.
/// </summary>
/// <param name="Valid">The <c>cc_valid</c> bit; invalid triplets are padding.</param>
/// <param name="Type">0 = CEA-608 field 1 (line 21), 1 = CEA-608 field 2, 2 = DTVCC packet data, 3 = DTVCC packet start.</param>
/// <param name="Data1">First data byte, as carried (608 bytes keep their odd-parity bit).</param>
/// <param name="Data2">Second data byte.</param>
public readonly record struct CcTriplet(bool Valid, byte Type, byte Data1, byte Data2)
{
    /// <summary>True for the two CEA-608 types.</summary>
    public bool Is608 => Type < 2;
}

/// <summary>Where a block of caption data was found in the video stream.</summary>
public enum CaptionCarriage
{
    /// <summary>H.264 SEI <c>user_data_registered_itu_t_t35</c> carrying ATSC <c>GA94</c> data.</summary>
    H264Sei,

    /// <summary>HEVC prefix SEI <c>user_data_registered_itu_t_t35</c> carrying ATSC <c>GA94</c> data.</summary>
    HevcSei,

    /// <summary>MPEG-2 video user data (start code 0xB2) carrying ATSC <c>GA94</c> data.</summary>
    Mpeg2UserData,
}

/// <summary>Parsers for the caption data structures shared by every carriage (A/53 Part 4, A/72).</summary>
public static class CaptionData
{
    /// <summary>The ATSC user identifier "GA94".</summary>
    private static ReadOnlySpan<byte> Ga94 => "GA94"u8;

    /// <summary>
    /// Parses <c>cc_data()</c> (A/53 Part 4 §6.2.3.1): flags byte (process_cc_data_flag in bit 6, cc_count in bits 0–4), one
    /// <c>em_data</c> byte, then <c>cc_count</c> three-byte constructs.
    /// </summary>
    /// <param name="ccData">The structure's bytes, starting at the flags byte.</param>
    /// <param name="triplets">Receives the triplets, at most 31.</param>
    /// <returns>The number of triplets written. Zero when <c>process_cc_data_flag</c> is clear or the data is too short for its declared count
    /// (a count that runs past the data keeps the triplets that fit).</returns>
    public static int ParseCcData(ReadOnlySpan<byte> ccData, Span<CcTriplet> triplets)
    {
        if (ccData.Length < 2 || (ccData[0] & 0x40) == 0)
        {
            return 0;
        }

        int count = ccData[0] & 0x1f;
        int n = 0;
        for (int i = 0; i < count && n < triplets.Length; i++)
        {
            int at = 2 + (i * 3);
            if (at + 3 > ccData.Length)
            {
                break;
            }

            byte b = ccData[at];
            triplets[n++] = new CcTriplet((b & 4) != 0, (byte)(b & 3), ccData[at + 1], ccData[at + 2]);
        }

        return n;
    }

    /// <summary>
    /// Finds the <c>cc_data()</c> inside a <c>user_data_registered_itu_t_t35</c> SEI payload (H.264 §D.2.6 / H.265 §D.2.6; A/72 Part 1 §6.2.3):
    /// country code 0xB5, provider code 0x0031, "GA94", <c>user_data_type_code</c> 0x03.
    /// </summary>
    /// <param name="payload">The SEI payload bytes (payload type 4), emulation prevention already removed.</param>
    /// <param name="ccData">The <c>cc_data()</c> bytes on success.</param>
    /// <returns>False when the payload is not ATSC closed-caption data.</returns>
    public static bool TryGetFromItuT35(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> ccData)
    {
        ccData = default;
        if (payload.Length < 10 || payload[0] != 0xb5 || payload[1] != 0x00 || payload[2] != 0x31 || !payload.Slice(3, 4).SequenceEqual(Ga94) || payload[7] != 0x03)
        {
            return false;
        }

        ccData = payload[8..];
        return true;
    }

    /// <summary>
    /// Finds the <c>cc_data()</c> inside the body of an MPEG-2 user data section (the bytes after the 0xB2 start code): "GA94",
    /// <c>user_data_type_code</c> 0x03 (A/53 Part 4 §6.2.2).
    /// </summary>
    /// <param name="userData">The user data bytes after the start code.</param>
    /// <param name="ccData">The <c>cc_data()</c> bytes on success.</param>
    /// <returns>False when the section is not ATSC closed-caption data.</returns>
    public static bool TryGetFromMpeg2UserData(ReadOnlySpan<byte> userData, out ReadOnlySpan<byte> ccData)
    {
        ccData = default;
        if (userData.Length < 7 || !userData[..4].SequenceEqual(Ga94) || userData[4] != 0x03)
        {
            return false;
        }

        ccData = userData[5..];
        return true;
    }

    /// <summary>
    /// Builds the SEI payload bytes (payload type 4) for <c>cc_data()</c> (A/72 Part 1 §6.2.3): the T.35 header, then flags, <c>em_data</c>,
    /// the triplets and the 0xFF marker.
    /// </summary>
    /// <param name="triplets">At most 31 triplets.</param>
    /// <returns>The payload bytes.</returns>
    /// <exception cref="ArgumentException">More than 31 triplets.</exception>
    public static byte[] BuildItuT35Payload(ReadOnlySpan<CcTriplet> triplets)
    {
        if (triplets.Length > 31)
        {
            throw new ArgumentException("cc_count is a 5-bit field; at most 31 triplets fit.", nameof(triplets));
        }

        byte[] p = new byte[8 + 2 + (triplets.Length * 3) + 1];
        p[0] = 0xb5;
        p[2] = 0x31;
        "GA94"u8.CopyTo(p.AsSpan(3));
        p[7] = 0x03;
        p[8] = (byte)(0x40 | triplets.Length);
        p[9] = 0xff;
        for (int i = 0; i < triplets.Length; i++)
        {
            CcTriplet t = triplets[i];
            p[10 + (i * 3)] = (byte)(0xf8 | (t.Valid ? 4 : 0) | (t.Type & 3));
            p[11 + (i * 3)] = t.Data1;
            p[12 + (i * 3)] = t.Data2;
        }

        p[^1] = 0xff;
        return p;
    }

    /// <summary>The older MPEG-2 user data layouts that are not ATSC <c>GA94</c>.</summary>
    public enum LegacyLayout
    {
        /// <summary>Not a recognised layout.</summary>
        None,

        /// <summary>SCTE 20 style: type code 0x03 followed by bit-packed, bit-reversed line 21 elements (one 608 pair per element).</summary>
        Scte20,

        /// <summary>DVD-Video GOP user data (<c>43 43 01 F8</c>): the caption pairs of a whole GOP, two triplets (one per field) per frame.</summary>
        DvdGop,

        /// <summary>A "display order" layout (<c>02 09 .. .. 02 0A p1 p2</c>) seen in one published sample: one field 1 pair per picture.</summary>
        DisplayOrderPair,
    }

    /// <summary>
    /// Reads the user data layouts that predate or sit beside ATSC <c>GA94</c>. These come from black-box observation of published samples, not from a
    /// standard (see <c>docs/captions.md</c>): SCTE 20 is read from its bit layout as seen in a sample and checked against the same program carried as
    /// <c>GA94</c>; the DVD GOP layout and the display-order layout likewise.
    /// </summary>
    /// <param name="userData">The user data bytes after the 0xB2 start code.</param>
    /// <param name="into">Receives the triplets in transmission order.</param>
    /// <returns>Which layout matched, or <see cref="LegacyLayout.None"/> (nothing is added).</returns>
    public static LegacyLayout TryGetFromLegacyMpeg2UserData(ReadOnlySpan<byte> userData, List<CcTriplet> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (userData.Length >= 8 && userData[0] == 0x02 && userData[1] == 0x09 && userData[4] == 0x02 && userData[5] == 0x0a)
        {
            into.Add(new CcTriplet(true, 0, userData[6], userData[7]));
            return LegacyLayout.DisplayOrderPair;
        }

        if (userData.Length >= 6 && userData[0] == 0x43 && userData[1] == 0x43 && userData[2] == 0x01 && userData[3] == 0xf8)
        {
            // Byte 4: bit 7 set, bit 6 = the first field of each frame pair is field 1, bits 0-4 = number of triplets. Each triplet is a
            // marker (0xFF) and two bytes. Observed: in a sample whose captions are on field 1 the pairs sit in the second triplet of each
            // frame and bit 6 is clear, so a clear bit 6 puts field 2 first.
            int count = userData[4] & 0x1f;
            bool firstIsField1 = (userData[4] & 0x40) != 0;
            int added = 0;
            for (int i = 0; i < count && 5 + (i * 3) + 3 <= userData.Length; i++)
            {
                int at = 5 + (i * 3);
                bool field1 = (i & 1) == 0 ? firstIsField1 : !firstIsField1;
                into.Add(new CcTriplet((userData[at] & 1) != 0, (byte)(field1 ? 0 : 1), userData[at + 1], userData[at + 2]));
                added++;
            }

            return added > 0 ? LegacyLayout.DvdGop : LegacyLayout.None;
        }

        if (userData.Length >= 7 && userData[0] == 0x03)
        {
            // Observed layout after the type code: 8 header bits, a 5-bit element count, then per element 26 bits: priority (2), field_number (2;
            // 1 = field 1, 2 = field 2), line_offset (5; 11 for line 21), two caption bytes sent least significant bit first (8 + 8), one more bit.
            var bits = new BitCursor(userData[1..]);
            _ = bits.Read(8);
            int count = bits.Read(5);
            int added = 0;
            for (int i = 0; i < count && bits.Remaining >= 26; i++)
            {
                _ = bits.Read(2);
                int field = bits.Read(2);
                _ = bits.Read(5);
                byte b1 = Reverse((byte)bits.Read(8));
                byte b2 = Reverse((byte)bits.Read(8));
                _ = bits.Read(1);
                if (field is 1 or 2)
                {
                    into.Add(new CcTriplet(true, (byte)(field - 1), b1, b2));
                    added++;
                }
            }

            return added > 0 ? LegacyLayout.Scte20 : LegacyLayout.None;
        }

        return LegacyLayout.None;
    }

    private static byte Reverse(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            r = (r << 1) | ((b >> i) & 1);
        }

        return (byte)r;
    }

    private ref struct BitCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public BitCursor(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public readonly int Remaining => (_data.Length * 8) - _pos;

        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int bit = _pos < _data.Length * 8 ? (_data[_pos >> 3] >> (7 - (_pos & 7))) & 1 : 0;
                v = (v << 1) | bit;
                _pos++;
            }

            return v;
        }
    }
}

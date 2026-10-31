using System.Numerics;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// The EBML primitives of RFC 8794: variable-length element IDs and data sizes, and the integer and float payload types. All methods work
/// on caller-supplied spans, never throw on malformed input and report problems through their return value.
/// </summary>
public static class Ebml
{
    /// <summary>The longest element ID this reader accepts (RFC 8794 §4.1; the header's EBMLMaxIDLength defaults to 4).</summary>
    public const int MaxIdLength = 4;

    /// <summary>The longest data-size field (RFC 8794 §4.4; EBMLMaxSizeLength defaults to 8).</summary>
    public const int MaxSizeLength = 8;

    /// <summary>The size value returned for an element whose data size is "unknown" (all value bits set, RFC 8794 §6.2).</summary>
    public const long UnknownSize = -1;

    /// <summary>
    /// Reads an element ID (RFC 8794 §4.1). The ID keeps its length-marker bits, so <c>0x1A45DFA3</c> is the EBML header.
    /// </summary>
    /// <param name="data">Bytes starting at the ID.</param>
    /// <param name="id">The ID, marker bits included.</param>
    /// <param name="length">The number of bytes the ID takes (1 to 4).</param>
    /// <returns>False when the data is too short, starts with a zero byte, or the ID is longer than 4 bytes.</returns>
    public static bool TryReadId(ReadOnlySpan<byte> data, out uint id, out int length)
    {
        id = 0;
        length = 0;
        if (data.IsEmpty || data[0] == 0)
        {
            return false;
        }

        length = BitOperations.LeadingZeroCount((uint)data[0] << 24) + 1;
        if (length > MaxIdLength || data.Length < length)
        {
            length = 0;
            return false;
        }

        uint value = 0;
        for (int i = 0; i < length; i++)
        {
            value = (value << 8) | data[i];
        }

        id = value;
        return true;
    }

    /// <summary>
    /// Reads a variable-length integer with its length marker removed (RFC 8794 §4.4): element data sizes, Block track numbers and
    /// EBML-lacing sizes all use this encoding.
    /// </summary>
    /// <param name="data">Bytes starting at the integer.</param>
    /// <param name="value">The value with the marker bit cleared.</param>
    /// <param name="length">The number of bytes it takes (1 to 8).</param>
    /// <returns>False when the data is too short or starts with a zero byte.</returns>
    public static bool TryReadVint(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.IsEmpty || data[0] == 0)
        {
            return false;
        }

        int len = BitOperations.LeadingZeroCount((uint)data[0] << 24) + 1;
        if (data.Length < len)
        {
            return false;
        }

        ulong v = (ulong)data[0] & ((1UL << (8 - len)) - 1);
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | data[i];
        }

        value = v;
        length = len;
        return true;
    }

    /// <summary>
    /// Reads an element data size (RFC 8794 §4.4). A size with every value bit set means "unknown" (§6.2) and is returned as
    /// <see cref="UnknownSize"/>.
    /// </summary>
    /// <param name="data">Bytes starting at the size field.</param>
    /// <param name="size">The size in bytes, or <see cref="UnknownSize"/>.</param>
    /// <param name="length">The number of bytes the field takes (1 to 8).</param>
    /// <returns>False when the data is too short or starts with a zero byte.</returns>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out long size, out int length)
    {
        if (!TryReadVint(data, out ulong value, out length))
        {
            size = 0;
            return false;
        }

        ulong allOnes = (1UL << (7 * length)) - 1;
        size = value == allOnes ? UnknownSize : (long)value;
        return true;
    }

    /// <summary>Reads an unsigned integer payload of 0 to 8 bytes (RFC 8794 §7.1); an empty payload is zero.</summary>
    /// <param name="payload">The element payload.</param>
    /// <param name="value">The big-endian value.</param>
    /// <returns>False when the payload is longer than 8 bytes.</returns>
    public static bool TryReadUnsigned(ReadOnlySpan<byte> payload, out ulong value)
    {
        value = 0;
        if (payload.Length > 8)
        {
            return false;
        }

        foreach (byte b in payload)
        {
            value = (value << 8) | b;
        }

        return true;
    }

    /// <summary>Reads a signed integer payload of 0 to 8 bytes in two's complement (RFC 8794 §7.2); an empty payload is zero.</summary>
    /// <param name="payload">The element payload.</param>
    /// <param name="value">The sign-extended big-endian value.</param>
    /// <returns>False when the payload is longer than 8 bytes.</returns>
    public static bool TryReadSigned(ReadOnlySpan<byte> payload, out long value)
    {
        value = 0;
        if (payload.Length > 8)
        {
            return false;
        }

        if (payload.IsEmpty)
        {
            return true;
        }

        long v = (sbyte)payload[0];
        for (int i = 1; i < payload.Length; i++)
        {
            v = (v << 8) | payload[i];
        }

        value = v;
        return true;
    }

    /// <summary>
    /// Reads a float payload (RFC 8794 §7.3, 4 or 8 bytes, or empty for zero) and rounds it to the nearest integer, halves away from zero.
    /// Done with integer arithmetic so that media durations and sampling rates never pass through floating point.
    /// </summary>
    /// <param name="payload">The element payload.</param>
    /// <param name="value">The rounded value.</param>
    /// <returns>False for NaN, infinities, values outside the 64-bit range and payload lengths other than 0, 4 and 8.</returns>
    public static bool TryReadFloatRounded(ReadOnlySpan<byte> payload, out long value)
    {
        value = 0;
        if (payload.IsEmpty)
        {
            return true;
        }

        if (payload.Length == 4)
        {
            TryReadUnsigned(payload, out ulong bits32);
            return IeeeToInt64(bits32, 8, 23, out value);
        }

        if (payload.Length == 8)
        {
            TryReadUnsigned(payload, out ulong bits64);
            return IeeeToInt64(bits64, 11, 52, out value);
        }

        return false;
    }

    private static bool IeeeToInt64(ulong bits, int exponentBits, int mantissaBits, out long value)
    {
        value = 0;
        bool negative = ((bits >> (exponentBits + mantissaBits)) & 1) != 0;
        int exponentField = (int)((bits >> mantissaBits) & ((1UL << exponentBits) - 1));
        ulong mantissa = bits & ((1UL << mantissaBits) - 1);
        if (exponentField == (1 << exponentBits) - 1)
        {
            return false;
        }

        if (exponentField == 0)
        {
            return true; // zero and subnormals round to 0
        }

        int bias = (1 << (exponentBits - 1)) - 1;
        ulong significand = mantissa | (1UL << mantissaBits);
        int shift = exponentField - bias - mantissaBits; // value = significand * 2^shift
        ulong magnitude;
        if (shift >= 0)
        {
            if (shift > 62 - mantissaBits)
            {
                return false;
            }

            magnitude = significand << shift;
        }
        else
        {
            int right = -shift;
            if (right > mantissaBits + 1)
            {
                return true; // below one half
            }

            magnitude = (significand >> right) + ((significand >> (right - 1)) & 1);
        }

        value = negative ? -(long)magnitude : (long)magnitude;
        return true;
    }
}

/// <summary>CRC-32 as the EBML <c>CRC-32</c> element uses it: the IEEE 802.3 polynomial, stored little-endian (RFC 8794 §11.3.1).</summary>
public static class EbmlCrc32
{
    // Eight tables for "slice-by-8": table k holds the CRC of a byte followed by k zero bytes, so eight input bytes are folded with eight
    // lookups and no dependency between the bytes except through the one running value. Table 0 is the usual byte-at-a-time table.
    private static readonly uint[] Tables = BuildTables();

    /// <summary>Starts a running checksum.</summary>
    public const uint Initial = 0xFFFFFFFF;

    /// <summary>Adds bytes to a running checksum.</summary>
    /// <param name="state">The running state, starting at <see cref="Initial"/>.</param>
    /// <param name="data">The bytes to add.</param>
    /// <returns>The new running state.</returns>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        uint[] t = Tables;
        while (data.Length >= 8)
        {
            ulong v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data) ^ state;
            state = t[(7 << 8) + (int)(v & 0xFF)] ^ t[(6 << 8) + (int)((v >> 8) & 0xFF)] ^ t[(5 << 8) + (int)((v >> 16) & 0xFF)] ^ t[(4 << 8) + (int)((v >> 24) & 0xFF)]
                ^ t[(3 << 8) + (int)((v >> 32) & 0xFF)] ^ t[(2 << 8) + (int)((v >> 40) & 0xFF)] ^ t[(1 << 8) + (int)((v >> 48) & 0xFF)] ^ t[(int)(v >> 56)];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            state = t[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    /// <summary>Turns a running state into the final checksum.</summary>
    /// <param name="state">The running state.</param>
    /// <returns>The CRC-32 value.</returns>
    public static uint Finish(uint state) => ~state;

    /// <summary>Computes the CRC-32 of a whole buffer.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC-32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    private static uint[] BuildTables()
    {
        uint[] table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint previous = table[((k - 1) << 8) + i];
                table[(k << 8) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }
}

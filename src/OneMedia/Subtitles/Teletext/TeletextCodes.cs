namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>
/// The byte protections of World System Teletext (ETS 300 706 §8): odd parity, Hamming 8/4 and Hamming 24/18. Teletext transmits
/// each byte least significant bit first; in DVB (EN 300 472 §4.4) the bits are kept in transmission order, so every byte of a
/// data unit is bit-reversed before these functions see it.
/// </summary>
public static class TeletextCodes
{
    private static readonly byte[] Reverse = BuildReverse();

    // ETS 300 706 §8.2: data bits sit in bits 2, 4, 6 and 8 of the byte (1-based, LSB first); the other four bits protect them.
    // Built once from the specified parity tests instead of typing the 256 entries.
    private static readonly sbyte[] Hamming84 = BuildHamming84();

    /// <summary>Reverses the bit order of a byte (transmission order to numeric order).</summary>
    /// <param name="value">The byte.</param>
    public static byte ReverseBits(byte value) => Reverse[value];

    /// <summary>
    /// Checks the odd parity of a byte (§8.1) and returns its seven data bits.
    /// </summary>
    /// <param name="value">The byte as received, in numeric bit order (bit 8 is the parity bit).</param>
    /// <param name="data">The seven data bits, valid whether or not the parity holds.</param>
    /// <returns>True when the parity is correct.</returns>
    public static bool TryOddParity(byte value, out byte data)
    {
        data = (byte)(value & 0x7F);
        return (System.Numerics.BitOperations.PopCount(value) & 1) == 1;
    }

    /// <summary>
    /// Decodes a Hamming 8/4 byte (§8.2), correcting a single bit error.
    /// </summary>
    /// <param name="value">The byte, in numeric bit order.</param>
    /// <returns>The four data bits (0 to 15), or -1 when two bits are in error.</returns>
    public static int DecodeHamming84(byte value) => Hamming84[value];

    /// <summary>
    /// Decodes a Hamming 24/18 triplet (§8.3), correcting a single bit error.
    /// </summary>
    /// <param name="b0">First byte, in numeric bit order.</param>
    /// <param name="b1">Second byte.</param>
    /// <param name="b2">Third byte.</param>
    /// <returns>The 18 data bits (D1 in bit 0), or -1 when the triplet has two bit errors.</returns>
    public static int DecodeHamming2418(byte b0, byte b1, byte b2)
    {
        // The 24 bits in transmission order: bit 1 of the first byte is position 1.
        int word = b0 | (b1 << 8) | (b2 << 16);

        // §8.3 parity tests A to E cover the positions 1 to 23 whose number has bit 0 to 4 set (position 24 is P6, which only the
        // overall test F covers); F is odd parity over all 24 bits.
        int syndrome = 0;
        for (int bit = 0; bit < 5; bit++)
        {
            int mask = 0;
            for (int pos = 1; pos <= 23; pos++)
            {
                if ((pos & (1 << bit)) != 0)
                {
                    mask |= 1 << (pos - 1);
                }
            }

            // "Odd parity test" passes when an odd number of covered bits is set.
            if ((System.Numerics.BitOperations.PopCount((uint)(word & mask)) & 1) == 0)
            {
                syndrome |= 1 << bit;
            }
        }

        bool overall = (System.Numerics.BitOperations.PopCount((uint)word) & 1) == 1;
        if (syndrome != 0)
        {
            if (overall)
            {
                // Overall parity still holds although a test failed: two errors.
                return -1;
            }

            if (syndrome > 24)
            {
                return -1;
            }

            word ^= 1 << (syndrome - 1);
        }

        // Data bits are the positions that are not powers of two (and not position 24).
        int result = 0;
        int outBit = 0;
        for (int pos = 1; pos <= 23; pos++)
        {
            if ((pos & (pos - 1)) == 0)
            {
                continue;
            }

            if ((word & (1 << (pos - 1))) != 0)
            {
                result |= 1 << outBit;
            }

            outBit++;
        }

        // The 18 data bits sit at positions 3, 5, 6, 7, 9 to 15 and 17 to 23.
        return result;
    }

    private static byte[] BuildReverse()
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i, r = 0;
            for (int b = 0; b < 8; b++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }

            table[i] = (byte)r;
        }

        return table;
    }

    private static sbyte[] BuildHamming84()
    {
        sbyte[] table = new sbyte[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = -1;
        }

        // Encode every nibble (§8.2) and mark every one-bit variation as correctable to that nibble.
        for (int d = 0; d < 16; d++)
        {
            int d1 = d & 1, d2 = (d >> 1) & 1, d3 = (d >> 2) & 1, d4 = (d >> 3) & 1;
            int p1 = 1 ^ d1 ^ d3 ^ d4;
            int p2 = 1 ^ d1 ^ d2 ^ d4;
            int p3 = 1 ^ d1 ^ d2 ^ d3;
            int p4 = 1 ^ p1 ^ d1 ^ p2 ^ d2 ^ p3 ^ d3 ^ d4;

            // Bit 1 is P1, bit 2 is D1, bit 3 is P2, bit 4 is D2, ... bit 7 is P4 and bit 8 is D4.
            int code = p1 | (d1 << 1) | (p2 << 2) | (d2 << 3) | (p3 << 4) | (d3 << 5) | (p4 << 6) | (d4 << 7);
            table[code] = (sbyte)d;
        }

        for (int d = 0; d < 16; d++)
        {
            // Find the encoded form again, then flip each bit.
            int code = -1;
            for (int c = 0; c < 256; c++)
            {
                if (table[c] == d && IsExact(c))
                {
                    code = c;
                    break;
                }
            }

            for (int bit = 0; bit < 8; bit++)
            {
                int variant = code ^ (1 << bit);
                if (table[variant] < 0)
                {
                    table[variant] = (sbyte)d;
                }
            }
        }

        return table;

        // A byte is an exact codeword when its parity bits match its data bits.
        static bool IsExact(int c)
        {
            int p1 = c & 1, d1 = (c >> 1) & 1, p2 = (c >> 2) & 1, d2 = (c >> 3) & 1, p3 = (c >> 4) & 1, d3 = (c >> 5) & 1, p4 = (c >> 6) & 1, d4 = (c >> 7) & 1;
            return p1 == (1 ^ d1 ^ d3 ^ d4) && p2 == (1 ^ d1 ^ d2 ^ d4) && p3 == (1 ^ d1 ^ d2 ^ d3)
                && p4 == (1 ^ p1 ^ d1 ^ p2 ^ d2 ^ p3 ^ d3 ^ d4);
        }
    }
}

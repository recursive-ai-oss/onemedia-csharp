namespace RecursiveAI.OneMedia.Codecs;

/// <summary>
/// Reads bits, most significant bit first, from a span without allocating. Reading past the end never throws: the reader returns
/// zero bits and sets <see cref="Overrun"/>, so a parser can run to the end of its syntax and check once.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly bool _removeEmulationPrevention;
    private int _next;      // index of the next byte to load
    private int _current;   // the byte being consumed
    private int _bitsLeft;  // unread bits in _current
    private int _zeroRun;   // consecutive zero bytes loaded (emulation prevention)
    private long _consumed;

    /// <summary>Creates a reader over <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to read.</param>
    /// <param name="removeEmulationPrevention">When true, every 0x03 byte that follows two zero bytes is skipped, as in the raw byte
    /// sequence payload of an H.264, H.265 or H.266 NAL unit (ITU-T H.264 §7.4.1).</param>
    public BitReader(ReadOnlySpan<byte> data, bool removeEmulationPrevention = false)
    {
        _data = data;
        _removeEmulationPrevention = removeEmulationPrevention;
        _next = 0;
        _current = 0;
        _bitsLeft = 0;
        _zeroRun = 0;
        _consumed = 0;
        Overrun = false;
    }

    /// <summary>True once a read went past the end of the data. Values read after that point are zero.</summary>
    public bool Overrun { get; private set; }

    /// <summary>The number of bits consumed so far, not counting skipped emulation-prevention bytes.</summary>
    public readonly long BitsConsumed => _consumed;

    /// <summary>True when the next bit starts a byte.</summary>
    public readonly bool IsByteAligned => _bitsLeft == 0;

    /// <summary>The number of bits that can still be read, counting any emulation-prevention bytes as readable.</summary>
    public readonly long BitsRemaining => (((long)_data.Length - _next) * 8) + _bitsLeft;

    private bool LoadByte()
    {
        while (true)
        {
            if (_next >= _data.Length)
            {
                Overrun = true;
                return false;
            }

            int b = _data[_next++];
            if (_removeEmulationPrevention)
            {
                if (_zeroRun >= 2 && b == 3)
                {
                    _zeroRun = 0;
                    continue;
                }

                _zeroRun = b == 0 ? _zeroRun + 1 : 0;
            }

            _current = b;
            _bitsLeft = 8;
            return true;
        }
    }

    /// <summary>Reads one bit.</summary>
    /// <returns>0 or 1; 0 after an overrun.</returns>
    public int ReadBit()
    {
        if (_bitsLeft == 0 && !LoadByte())
        {
            return 0;
        }

        _bitsLeft--;
        _consumed++;
        return (_current >> _bitsLeft) & 1;
    }

    /// <summary>Reads a flag, u(1).</summary>
    /// <returns>True when the bit is set.</returns>
    public bool ReadFlag() => ReadBit() != 0;

    /// <summary>Reads an unsigned value of up to 32 bits, u(n).</summary>
    /// <param name="count">The number of bits, 0 to 32.</param>
    /// <returns>The value; 0 after an overrun.</returns>
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
        {
            Overrun = true;
            return 0;
        }

        ulong value = 0;
        int remaining = count;
        while (remaining > 0)
        {
            if (_bitsLeft == 0 && !LoadByte())
            {
                return 0;
            }

            int take = Math.Min(remaining, _bitsLeft);
            _bitsLeft -= take;
            value = (value << take) | (uint)((_current >> _bitsLeft) & ((1 << take) - 1));
            remaining -= take;
            _consumed += take;
        }

        return (uint)value;
    }

    /// <summary>Reads an unsigned value of up to 64 bits.</summary>
    /// <param name="count">The number of bits, 0 to 64.</param>
    /// <returns>The value; 0 after an overrun.</returns>
    public ulong ReadBits64(int count)
    {
        if ((uint)count > 64)
        {
            Overrun = true;
            return 0;
        }

        if (count <= 32)
        {
            return ReadBits(count);
        }

        ulong high = ReadBits(count - 32);
        return (high << 32) | ReadBits(32);
    }

    /// <summary>Skips <paramref name="count"/> bits.</summary>
    /// <param name="count">The number of bits to skip; negative values are ignored.</param>
    public void SkipBits(long count)
    {
        while (count >= 32)
        {
            ReadBits(32);
            count -= 32;
            if (Overrun)
            {
                return;
            }
        }

        if (count > 0)
        {
            ReadBits((int)count);
        }
    }

    /// <summary>Skips to the next byte boundary.</summary>
    public void ByteAlign()
    {
        _consumed += _bitsLeft;
        _bitsLeft = 0;
    }

    /// <summary>Reads an unsigned Exp-Golomb code, ue(v) (ITU-T H.264 §9.1).</summary>
    /// <returns>The value; 0 and <see cref="Overrun"/> set when the code is longer than 32 bits or the data ends.</returns>
    public uint ReadUe()
    {
        int zeros = 0;
        while (ReadBit() == 0)
        {
            if (Overrun || ++zeros > 32)
            {
                Overrun = true;
                return 0;
            }
        }

        if (zeros == 32)
        {
            // Only 0xFFFFFFFF has a 32-bit prefix; anything else overflows 32 bits.
            if (ReadBits(32) == 0)
            {
                return uint.MaxValue;
            }

            Overrun = true;
            return 0;
        }

        return (uint)((1UL << zeros) - 1) + ReadBits(zeros);
    }

    /// <summary>Reads a signed Exp-Golomb code, se(v) (ITU-T H.264 §9.1.1).</summary>
    /// <returns>The value; 0 after an overrun.</returns>
    public int ReadSe()
    {
        uint k = ReadUe();
        long magnitude = ((long)k + 1) / 2;
        return (int)((k & 1) != 0 ? magnitude : -magnitude);
    }

    /// <summary>Reads a variable-length unsigned code in the AV1 uvlc() form (AV1 bitstream specification §4.10.3).</summary>
    /// <returns>The value; 0 after an overrun.</returns>
    public uint ReadUvlc()
    {
        int zeros = 0;
        while (ReadBit() == 0)
        {
            if (Overrun)
            {
                return 0;
            }

            if (++zeros >= 32)
            {
                return uint.MaxValue;
            }
        }

        return ReadBits(zeros) + (uint)((1UL << zeros) - 1);
    }

    /// <summary>Reads an unsigned little-endian base-128 value of up to eight bytes, leb128() (AV1 bitstream specification §4.10.5).</summary>
    /// <returns>The value; 0 and <see cref="Overrun"/> set when it is malformed or the data ends.</returns>
    public ulong ReadLeb128()
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            uint b = ReadBits(8);
            if (Overrun)
            {
                return 0;
            }

            value |= (ulong)(b & 0x7f) << (i * 7);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        Overrun = true;
        return 0;
    }
}

/// <summary>
/// Helpers for the emulation-prevention bytes of H.264, H.265 and H.266 NAL units (ITU-T H.264 §7.4.1.1).
/// </summary>
public static class EmulationPrevention
{
    /// <summary>Copies <paramref name="nal"/> into <paramref name="destination"/> without the 0x03 bytes that follow two zero bytes.</summary>
    /// <param name="nal">Escaped bytes.</param>
    /// <param name="destination">Receives the raw byte sequence; it must be at least as long as <paramref name="nal"/> or the result is cut short.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Remove(ReadOnlySpan<byte> nal, Span<byte> destination)
    {
        int zeros = 0;
        int written = 0;
        foreach (byte b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            if (written >= destination.Length)
            {
                break;
            }

            destination[written++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return written;
    }
}

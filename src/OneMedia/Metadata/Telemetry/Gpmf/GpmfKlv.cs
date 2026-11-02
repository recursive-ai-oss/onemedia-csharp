using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;

/// <summary>
/// One key-length-value triplet of GPMF: a four-character key, a type character, the size of one structure and the number of
/// structures (the repeat), followed by <c>size × repeat</c> bytes padded to a multiple of four. Everything is big-endian. A type of 0
/// marks nested GPMF.
/// </summary>
public readonly ref struct GpmfKlv
{
    internal GpmfKlv(uint key, byte type, int structSize, int repeat, ReadOnlySpan<byte> data, int headerOffset, int next)
    {
        Key = key;
        Type = type;
        StructSize = structSize;
        Repeat = repeat;
        Data = data;
        HeaderOffset = headerOffset;
        NextOffset = next;
    }

    /// <summary>The key as a big-endian 32-bit value (<c>0x44455643</c> for <c>DEVC</c>).</summary>
    public uint Key { get; }

    /// <summary>The type character (<c>'f'</c>, <c>'s'</c>, <c>'?'</c> …), or 0 for nested data.</summary>
    public byte Type { get; }

    /// <summary>Bytes in one sample structure.</summary>
    public int StructSize { get; }

    /// <summary>Number of samples.</summary>
    public int Repeat { get; }

    /// <summary>The payload: exactly <c>StructSize × Repeat</c> bytes, without padding.</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>Offset of the 8-byte header from the start of the span being read.</summary>
    public int HeaderOffset { get; }

    /// <summary>Offset of the payload from the start of the span being read.</summary>
    public int DataOffset => HeaderOffset + 8;

    /// <summary>Offset of the next triplet from the start of the span being read.</summary>
    public int NextOffset { get; }

    /// <summary>True when the payload is itself GPMF.</summary>
    public bool IsNested => Type == 0;

    /// <summary>Builds a key value from four ASCII characters.</summary>
    /// <param name="fourCc">The four characters.</param>
    /// <returns>The key.</returns>
    public static uint MakeKey(ReadOnlySpan<char> fourCc) =>
        fourCc.Length == 4 ? (uint)((fourCc[0] << 24) | (fourCc[1] << 16) | (fourCc[2] << 8) | fourCc[3]) : throw new ArgumentException("A GPMF key has four characters.", nameof(fourCc));

    /// <summary>Spells a key as text (non-printable bytes become <c>?</c>).</summary>
    /// <param name="key">The key.</param>
    /// <returns>Four characters.</returns>
    public static string KeyText(uint key) => string.Create(4, key, static (span, k) =>
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(k >> (24 - (8 * i)));
            span[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
    });
}

/// <summary>Walks the triplets of one GPMF block (a payload, or the data of a nested triplet).</summary>
public ref struct GpmfKlvReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>Starts reading at the beginning of <paramref name="buffer"/>.</summary>
    /// <param name="buffer">The block's bytes.</param>
    public GpmfKlvReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Offset of the next triplet.</summary>
    public readonly int Position => _position;

    /// <summary>True when fewer than eight bytes remain, or a triplet declared more data than remains (the block ended inside it).</summary>
    public bool Truncated { get; private set; }

    /// <summary>Reads the next triplet. A triplet whose data runs past the end of the block ends the walk and sets <see cref="Truncated"/>; if it is nested data it is returned with the bytes that exist, otherwise it is dropped.</summary>
    /// <param name="klv">The triplet.</param>
    /// <returns>False at the end of the block.</returns>
    public bool TryRead(out GpmfKlv klv)
    {
        int remaining = _buffer.Length - _position;
        if (remaining < 8)
        {
            // GPMF payloads end with whole triplets; a few trailing zero bytes are padding, not damage.
            Truncated |= remaining > 0 && !IsZero(_buffer[_position..]);
            klv = default;
            return false;
        }

        ReadOnlySpan<byte> h = _buffer.Slice(_position, 8);
        uint key = BinaryPrimitives.ReadUInt32BigEndian(h);
        int size = h[5];
        int repeat = BinaryPrimitives.ReadUInt16BigEndian(h[6..]);
        long length = (long)size * repeat;
        long padded = (length + 3) & ~3L;
        if (key == 0 && h[4] == 0 && size == 0 && repeat == 0)
        {
            // A zero key is end-of-data padding.
            Truncated |= !IsZero(_buffer[_position..]);
            _position = _buffer.Length;
            klv = default;
            return false;
        }

        if (length > remaining - 8)
        {
            // The block ends inside this triplet. Nested data is still worth reading as far as it goes (a cut-off recording loses only its tail);
            // anything else is dropped.
            Truncated = true;
            int available = remaining - 8;
            int at = _position;
            _position = _buffer.Length;
            if (h[4] != 0 || available <= 0)
            {
                klv = default;
                return false;
            }

            klv = new GpmfKlv(key, 0, size, repeat, _buffer.Slice(at + 8, available), at, _buffer.Length);
            return true;
        }

        int next = _position + 8 + (int)Math.Min(padded, remaining - 8);
        klv = new GpmfKlv(key, h[4], size, repeat, _buffer.Slice(_position + 8, (int)length), _position, next);
        _position = next;
        return true;
    }

    private static bool IsZero(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The data types of GPMF (the type character of a triplet).</summary>
public static class GpmfTypes
{
    /// <summary>Size in bytes of one value of a fixed-size type, or 0 for types without one (<c>?</c>, nested, unknown).</summary>
    /// <param name="type">The type character.</param>
    /// <returns>The size.</returns>
    public static int SizeOf(byte type) => type switch
    {
        (byte)'b' or (byte)'B' or (byte)'c' => 1,
        (byte)'s' or (byte)'S' => 2,
        (byte)'l' or (byte)'L' or (byte)'f' or (byte)'F' or (byte)'q' => 4,
        (byte)'d' or (byte)'j' or (byte)'J' or (byte)'Q' => 8,
        (byte)'G' or (byte)'U' => 16,
        _ => 0,
    };

    /// <summary>True for types read as a number: integers, floats and the Q fixed-point types.</summary>
    /// <param name="type">The type character.</param>
    /// <returns>Whether <see cref="ReadNumber"/> accepts it.</returns>
    public static bool IsNumeric(byte type) => type is (byte)'b' or (byte)'B' or (byte)'s' or (byte)'S' or (byte)'l' or (byte)'L' or (byte)'f' or (byte)'d'
        or (byte)'j' or (byte)'J' or (byte)'q' or (byte)'Q';

    /// <summary>
    /// Reads one number. <c>q</c> is Q15.16 and <c>Q</c> is Q31.32 (signed two's complement fixed point). A four-character code (<c>F</c>) reads as its
    /// big-endian integer so that it keeps a place in a numeric structure.
    /// </summary>
    /// <param name="type">The type character; must satisfy <see cref="IsNumeric"/> or be <c>F</c>.</param>
    /// <param name="bytes">At least <see cref="SizeOf"/> bytes.</param>
    /// <returns>The value; NaN for an unsupported type.</returns>
    public static double ReadNumber(byte type, ReadOnlySpan<byte> bytes) => type switch
    {
        (byte)'b' => (sbyte)bytes[0],
        (byte)'B' => bytes[0],
        (byte)'s' => BinaryPrimitives.ReadInt16BigEndian(bytes),
        (byte)'S' => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        (byte)'l' => BinaryPrimitives.ReadInt32BigEndian(bytes),
        (byte)'L' or (byte)'F' => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        (byte)'f' => BinaryPrimitives.ReadSingleBigEndian(bytes),
        (byte)'d' => BinaryPrimitives.ReadDoubleBigEndian(bytes),
        (byte)'j' => BinaryPrimitives.ReadInt64BigEndian(bytes),
        (byte)'J' => BinaryPrimitives.ReadUInt64BigEndian(bytes),
        (byte)'q' => BinaryPrimitives.ReadInt32BigEndian(bytes) / 65536.0,
        (byte)'Q' => BinaryPrimitives.ReadInt64BigEndian(bytes) / 4294967296.0,
        _ => double.NaN,
    };
}

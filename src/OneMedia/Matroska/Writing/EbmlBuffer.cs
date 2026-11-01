using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// A growable byte buffer for building small EBML elements (headers, track entries, cues). Master elements are opened with
/// <see cref="Begin"/> and closed with <see cref="End"/>, which writes the shortest size field that fits. Not used for media payload.
/// </summary>
internal sealed class EbmlBuffer
{
    private byte[] _bytes;

    public EbmlBuffer(int capacity = 256) => _bytes = new byte[capacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

    public void Clear() => Length = 0;

    private Span<byte> Reserve(int count)
    {
        if (_bytes.Length - Length < count)
        {
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, Length + count));
        }

        Span<byte> span = _bytes.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public void Append(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    private void Header(uint id, long size)
    {
        int idLen = EbmlWriter.IdLength(id);
        int sizeLen = EbmlWriter.SizeLength(size);
        Span<byte> d = Reserve(idLen + sizeLen);
        EbmlWriter.WriteId(d, id);
        EbmlWriter.WriteSize(d[idLen..], size, sizeLen);
    }

    public void Binary(uint id, ReadOnlySpan<byte> data)
    {
        Header(id, data.Length);
        Append(data);
    }

    public void UInt(uint id, ulong value)
    {
        int n = EbmlWriter.UnsignedLength(value);
        Header(id, n);
        Span<byte> d = Reserve(n);
        for (int i = 0; i < n; i++)
        {
            d[i] = (byte)(value >> (8 * (n - 1 - i)));
        }
    }

    /// <summary>An unsigned integer in exactly 8 bytes, so a later rewrite of its value does not move anything.</summary>
    public void UInt64Fixed(uint id, ulong value)
    {
        Header(id, 8);
        BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);
    }

    /// <summary>Signed integer in the fewest bytes (RFC 8794 §7.1).</summary>
    public void Int(uint id, long value)
    {
        int n = 1;
        while (n < 8 && (value < -(1L << (8 * n - 1)) || value >= (1L << (8 * n - 1))))
        {
            n++;
        }

        Header(id, n);
        Span<byte> d = Reserve(n);
        for (int i = 0; i < n; i++)
        {
            d[i] = (byte)(value >> (8 * (n - 1 - i)));
        }
    }

    /// <summary>An 8-byte IEEE 754 float (RFC 8794 §7.3).</summary>
    public void Float64(uint id, double value)
    {
        Header(id, 8);
        BinaryPrimitives.WriteInt64BigEndian(Reserve(8), BitConverter.DoubleToInt64Bits(value));
    }

    public void Float32(uint id, float value)
    {
        Header(id, 4);
        BinaryPrimitives.WriteInt32BigEndian(Reserve(4), BitConverter.SingleToInt32Bits(value));
    }

    public void String(uint id, string value)
    {
        int n = Encoding.UTF8.GetByteCount(value);
        Header(id, n);
        Encoding.UTF8.GetBytes(value, Reserve(n));
    }

    /// <summary>Opens a master element; returns a token for <see cref="End"/>.</summary>
    public int Begin(uint id)
    {
        int idLen = EbmlWriter.IdLength(id);
        Span<byte> d = Reserve(idLen + 8);
        EbmlWriter.WriteId(d, id);
        return Length; // the position just after the 8-byte size placeholder
    }

    /// <summary>Closes a master element opened with <see cref="Begin"/>, shrinking its size field to the shortest that fits.</summary>
    public void End(int token)
    {
        int payload = Length - token;
        int sizeLen = EbmlWriter.SizeLength(payload);
        int sizeStart = token - 8;
        EbmlWriter.WriteSize(_bytes.AsSpan(sizeStart), payload, sizeLen);
        if (sizeLen < 8)
        {
            _bytes.AsSpan(token, payload).CopyTo(_bytes.AsSpan(sizeStart + sizeLen));
            Length -= 8 - sizeLen;
        }
    }

    /// <summary>
    /// Inserts a CRC-32 element (RFC 8794 §11.3.1) as the first child of the master element whose payload starts at
    /// <paramref name="payloadStart"/> and runs to the end of the buffer. Call before <see cref="End"/>.
    /// </summary>
    public void InsertCrc(int payloadStart)
    {
        uint crc = EbmlCrc32.Compute(_bytes.AsSpan(payloadStart, Length - payloadStart));
        Span<byte> hole = Reserve(6);
        _bytes.AsSpan(payloadStart, Length - 6 - payloadStart).CopyTo(_bytes.AsSpan(payloadStart + 6));
        hole = _bytes.AsSpan(payloadStart, 6);
        hole[0] = (byte)MatroskaIds.Crc32;
        hole[1] = 0x84;
        BinaryPrimitives.WriteUInt32LittleEndian(hole[2..], crc);
    }
}

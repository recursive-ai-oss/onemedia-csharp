using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Builds EBML elements (RFC 8794 §4): IDs with their marker bits, variable-length data sizes, unsigned integers and Void padding. It is
/// the writing half of <see cref="Ebml"/>.
/// </summary>
public static class EbmlWriter
{
    /// <summary>Number of bytes an element ID takes (1 to 4), from its marker bits.</summary>
    /// <param name="id">The ID with its marker bits, for example 0x1F43B675.</param>
    /// <returns>The length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is 0.</exception>
    public static int IdLength(uint id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        return id <= 0xFF ? 1 : id <= 0xFFFF ? 2 : id <= 0xFF_FFFF ? 3 : 4;
    }

    /// <summary>Writes an element ID.</summary>
    /// <param name="destination">At least <see cref="IdLength"/> bytes.</param>
    /// <param name="id">The ID with its marker bits.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteId(Span<byte> destination, uint id)
    {
        int n = IdLength(id);
        for (int i = 0; i < n; i++)
        {
            destination[i] = (byte)(id >> (8 * (n - 1 - i)));
        }

        return n;
    }

    /// <summary>The fewest bytes that can hold a data size (the all-ones pattern is reserved for "unknown", so a width of <c>w</c> holds sizes below 2^(7w) − 1).</summary>
    /// <param name="size">The size, 0 or more and below 2^56 − 1.</param>
    /// <returns>1 to 8.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is negative or too large.</exception>
    public static int SizeLength(long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        for (int w = 1; w <= 8; w++)
        {
            if (size < (1L << (7 * w)) - 1)
            {
                return w;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(size), "The size does not fit an 8-byte EBML size.");
    }

    /// <summary>Writes a data size.</summary>
    /// <param name="destination">At least <paramref name="width"/> (or <see cref="SizeLength"/>) bytes.</param>
    /// <param name="size">The size.</param>
    /// <param name="width">The number of bytes to use, 1 to 8, or 0 for the fewest. A wider-than-needed width is allowed and is how existing elements keep their form.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size does not fit the width.</exception>
    public static int WriteSize(Span<byte> destination, long size, int width = 0)
    {
        if (width == 0)
        {
            width = SizeLength(size);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 8);
        if (size < 0 || size >= (1L << (7 * width)) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"The size {size} does not fit {width} byte(s).");
        }

        ulong v = (ulong)size | (1UL << (7 * width));
        for (int i = 0; i < width; i++)
        {
            destination[i] = (byte)(v >> (8 * (width - 1 - i)));
        }

        return width;
    }

    /// <summary>Writes the "unknown size" pattern (§6.2) in a given width.</summary>
    /// <param name="destination">At least <paramref name="width"/> bytes.</param>
    /// <param name="width">1 to 8.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteUnknownSize(Span<byte> destination, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 8);
        ulong v = (1UL << (7 * width)) | ((1UL << (7 * width)) - 1);
        for (int i = 0; i < width; i++)
        {
            destination[i] = (byte)(v >> (8 * (width - 1 - i)));
        }

        return width;
    }

    /// <summary>Builds an element around a payload.</summary>
    /// <param name="id">The element ID.</param>
    /// <param name="payload">The content.</param>
    /// <param name="sizeWidth">Width of the size field, or 0 for the fewest bytes.</param>
    /// <returns>The complete element.</returns>
    public static byte[] Element(uint id, ReadOnlySpan<byte> payload, int sizeWidth = 0)
    {
        int idLen = IdLength(id);
        int w = sizeWidth == 0 ? SizeLength(payload.Length) : sizeWidth;
        byte[] bytes = new byte[idLen + w + payload.Length];
        WriteId(bytes, id);
        WriteSize(bytes.AsSpan(idLen), payload.Length, w);
        payload.CopyTo(bytes.AsSpan(idLen + w));
        return bytes;
    }

    /// <summary>Number of bytes an unsigned integer payload needs (at least 1, §7.1 allows 0 for the value 0 but readers differ).</summary>
    /// <param name="value">The value.</param>
    /// <returns>1 to 8.</returns>
    public static int UnsignedLength(ulong value) => value == 0 ? 1 : (64 - System.Numerics.BitOperations.LeadingZeroCount(value) + 7) / 8;

    /// <summary>Builds an unsigned integer element.</summary>
    /// <param name="id">The element ID.</param>
    /// <param name="value">The value.</param>
    /// <param name="minWidth">The fewest payload bytes to use; a wider integer keeps an existing element's size stable.</param>
    /// <returns>The complete element.</returns>
    public static byte[] UnsignedElement(uint id, ulong value, int minWidth = 1)
    {
        int w = Math.Min(8, Math.Max(minWidth, UnsignedLength(value)));
        Span<byte> payload = stackalloc byte[8]; // an unsigned integer payload is at most 8 bytes
        BinaryPrimitives.WriteUInt64BigEndian(payload, value);
        return Element(id, payload[(8 - w)..]);
    }

    /// <summary>Builds a Void element (§11.2) of an exact total size: the padding the in-place editor can later fill.</summary>
    /// <param name="totalSize">Size including the ID and size field; at least 2.</param>
    /// <returns>The element, with zero bytes as content.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalSize"/> is below 2.</exception>
    public static byte[] Void(long totalSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, 2);
        for (int w = 1; w <= 8; w++)
        {
            long payload = totalSize - 1 - w;
            if (payload >= 0 && payload < (1L << (7 * w)) - 1)
            {
                byte[] bytes = new byte[totalSize];
                bytes[0] = (byte)MatroskaIds.Void;
                WriteSize(bytes.AsSpan(1), payload, w);
                return bytes;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(totalSize));
    }

    /// <summary>Checks that the bytes hold exactly one element whose size field matches (unknown sizes are not accepted).</summary>
    /// <param name="element">The bytes.</param>
    /// <param name="id">The element's ID when valid.</param>
    /// <returns>True when valid.</returns>
    public static bool IsSingleElement(ReadOnlySpan<byte> element, out uint id)
    {
        id = 0;
        if (!Ebml.TryReadId(element, out id, out int idLen) || !Ebml.TryReadSize(element[idLen..], out long size, out int sizeLen) || size < 0)
        {
            return false;
        }

        return idLen + sizeLen + size == element.Length;
    }
}

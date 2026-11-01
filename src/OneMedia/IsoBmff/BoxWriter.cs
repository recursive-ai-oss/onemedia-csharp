using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Builds boxes (ISO/IEC 14496-12 §4.2): the size and type header, the extended <c>uuid</c> type, and the version and flags of a full
/// box. It is the writing half of <see cref="BoxReader"/>.
/// </summary>
public static class BoxBuilder
{
    /// <summary>Smallest box: a 32-bit size and a type.</summary>
    public const int MinimumSize = 8;

    /// <summary>
    /// Writes a box header into <paramref name="destination"/>. The 32-bit size field is used when the whole box fits in it and
    /// <paramref name="forceLargeSize"/> is false; otherwise the box gets the 64-bit <c>largesize</c> form (size field 1).
    /// </summary>
    /// <param name="destination">At least <see cref="HeaderSize"/> bytes.</param>
    /// <param name="type">The box type as a big-endian code (<see cref="BoxTypes.Make"/>).</param>
    /// <param name="totalSize">Size of the whole box, header included.</param>
    /// <param name="userType">The extended type of a <c>uuid</c> box; ignored for other types.</param>
    /// <param name="forceLargeSize">Use the 64-bit form even when the size fits 32 bits (to preserve an existing box's form).</param>
    /// <returns>The number of header bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalSize"/> is smaller than the header, or the destination is too short.</exception>
    public static int WriteHeader(Span<byte> destination, uint type, long totalSize, Guid? userType = null, bool forceLargeSize = false)
    {
        bool uuid = type == BoxTypes.Uuid;
        bool large = forceLargeSize || totalSize > uint.MaxValue;
        int size = HeaderSize(type, large);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, size);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, size);
        if (large)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, 1);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], type);
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], (ulong)totalSize);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], type);
        }

        if (uuid)
        {
            (userType ?? Guid.Empty).TryWriteBytes(destination[(large ? 16 : 8)..], bigEndian: true, out _);
        }

        return size;
    }

    /// <summary>The header length for a box of this type: 8, 16 with a <c>largesize</c>, plus 16 for a <c>uuid</c>'s extended type.</summary>
    /// <param name="type">The box type.</param>
    /// <param name="largeSize">Whether the 64-bit size form is used.</param>
    /// <returns>The header length in bytes.</returns>
    public static int HeaderSize(uint type, bool largeSize) => (largeSize ? 16 : 8) + (type == BoxTypes.Uuid ? 16 : 0);

    /// <summary>Builds a box around a payload.</summary>
    /// <param name="type">The box type.</param>
    /// <param name="payload">The content after the header.</param>
    /// <param name="userType">For a <c>uuid</c> box, its extended type.</param>
    /// <returns>The complete box.</returns>
    public static byte[] Create(uint type, ReadOnlySpan<byte> payload, Guid? userType = null)
    {
        bool large = payload.Length + (long)HeaderSize(type, false) > uint.MaxValue;
        int header = HeaderSize(type, large);
        byte[] box = new byte[header + payload.Length];
        WriteHeader(box, type, box.Length, userType, large);
        payload.CopyTo(box.AsSpan(header));
        return box;
    }

    /// <summary>Builds a full box (§4.2): a version byte and 24 flag bits before the payload.</summary>
    /// <param name="type">The box type.</param>
    /// <param name="version">The version.</param>
    /// <param name="flags">The flags; only the low 24 bits are used.</param>
    /// <param name="payload">The content after version and flags.</param>
    /// <returns>The complete box.</returns>
    public static byte[] CreateFull(uint type, byte version, uint flags, ReadOnlySpan<byte> payload)
    {
        byte[] body = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, ((uint)version << 24) | (flags & 0xFF_FFFF));
        payload.CopyTo(body.AsSpan(4));
        return Create(type, body);
    }

    /// <summary>Builds a <c>free</c> box of an exact total size, the padding the in-place editor can later fill (§8.1.2).</summary>
    /// <param name="totalSize">Size including the 8-byte header; at least <see cref="MinimumSize"/>.</param>
    /// <param name="type"><c>free</c> (default) or <c>skip</c>.</param>
    /// <returns>The box, with zero bytes as content.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalSize"/> is below 8 or above what the 32-bit size can state.</exception>
    public static byte[] CreateFree(long totalSize, uint type = BoxTypes.Free)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, MinimumSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalSize, int.MaxValue);
        byte[] box = new byte[totalSize];
        WriteHeader(box, type, totalSize);
        return box;
    }

    /// <summary>
    /// Checks that <paramref name="box"/> holds exactly one well-formed box whose size field matches its length.
    /// </summary>
    /// <param name="box">The bytes.</param>
    /// <param name="type">The box's type when valid.</param>
    /// <returns>True when valid.</returns>
    public static bool IsSingleBox(ReadOnlySpan<byte> box, out uint type)
    {
        type = 0;
        if (box.Length < MinimumSize)
        {
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(box);
        type = BinaryPrimitives.ReadUInt32BigEndian(box[4..]);
        if (size == 1)
        {
            if (box.Length < 16)
            {
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
        }

        return size == (ulong)box.Length;
    }
}

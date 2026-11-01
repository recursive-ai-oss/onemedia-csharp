using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Builds chunks for the chunk-file layouts of <see cref="ChunkLayout"/>: the id and size header in the layout's byte order and width,
/// the payload, and the pad byte (or bytes) that keep the next chunk aligned. It is the writing half of the chunk reader.
/// </summary>
/// <remarks>Every method returns a chunk's whole slot, pad included, because that is what takes the place of the old chunk in the file.</remarks>
public static class ChunkBuilder
{
    /// <summary>The Wave64 id of a <c>junk</c> chunk (Sony Wave64, as observed in files).</summary>
    public static readonly Guid Wave64Junk = new("6B6E756A-ACF3-11D3-8CD1-00C04F8EDB8A");

    /// <summary>Header length of a chunk in a layout: 8, or 24 in Wave64.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The length in bytes.</returns>
    public static int HeaderSize(ChunkLayout layout) => layout == ChunkLayout.Wave64 ? 24 : 8;

    /// <summary>The padded length of a chunk with a payload: even in RIFF and IFF, a multiple of 8 in Wave64 (where the header counts).</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="payloadLength">Payload length in bytes.</param>
    /// <returns>The slot length in bytes.</returns>
    public static long SlotSize(ChunkLayout layout, long payloadLength) =>
        layout == ChunkLayout.Wave64 ? (24 + payloadLength + 7) & ~7L : 8 + payloadLength + (payloadLength & 1);

    /// <summary>Builds a RIFF, RF64 or IFF chunk.</summary>
    /// <param name="layout">The layout; not <see cref="ChunkLayout.Wave64"/> (use <see cref="CreateWave64"/>).</param>
    /// <param name="id">Four ASCII characters.</param>
    /// <param name="payload">The content.</param>
    /// <returns>Header, payload and pad byte.</returns>
    /// <exception cref="ArgumentException">The id is not four characters, the layout is Wave64, or the payload is larger than a 32-bit size can state.</exception>
    public static byte[] Create(ChunkLayout layout, string id, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (layout == ChunkLayout.Wave64 || id.Length != 4)
        {
            throw new ArgumentException("A four-character id and a layout other than Wave64 are required.", nameof(id));
        }

                byte[] chunk = new byte[SlotSize(layout, payload.Length)];
        Encoding.Latin1.GetBytes(id, chunk);
        if (layout == ChunkLayout.Iff)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)payload.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        }

        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    /// <summary>Builds a Wave64 chunk: a 16-byte GUID id, a 64-bit size that includes the 24-byte header, and padding to 8 bytes.</summary>
    /// <param name="id">The chunk GUID.</param>
    /// <param name="payload">The content.</param>
    /// <returns>Header, payload and padding.</returns>
    public static byte[] CreateWave64(Guid id, ReadOnlySpan<byte> payload)
    {
        byte[] chunk = new byte[SlotSize(ChunkLayout.Wave64, payload.Length)];
        id.TryWriteBytes(chunk);
        BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(16), 24UL + (ulong)payload.Length);
        payload.CopyTo(chunk.AsSpan(24));
        return chunk;
    }

    /// <summary>Builds a <c>LIST</c> chunk (RIFF, RF64) or IFF <c>LIST</c> of a given type around already built child chunks.</summary>
    /// <param name="layout">RIFF, RF64 or IFF.</param>
    /// <param name="listType">Four ASCII characters, for example <c>INFO</c>.</param>
    /// <param name="children">The child chunks, each as built by <see cref="Create"/>.</param>
    /// <returns>The list chunk.</returns>
    public static byte[] CreateList(ChunkLayout layout, string listType, ReadOnlySpan<byte> children)
    {
        ArgumentNullException.ThrowIfNull(listType);
        if (listType.Length != 4)
        {
            throw new ArgumentException("A list type is four characters.", nameof(listType));
        }

        byte[] payload = new byte[4 + children.Length];
        Encoding.Latin1.GetBytes(listType, payload);
        children.CopyTo(payload.AsSpan(4));
        return Create(layout, "LIST", payload);
    }

    /// <summary>Builds a padding chunk (<c>JUNK</c>, or <c>junk</c> in Wave64) filling an exact slot, for the in-place editor to use later.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="slotSize">Whole slot length; at least the header length, and even (a multiple of 8 in Wave64).</param>
    /// <returns>The chunk, with zero bytes as content.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotSize"/> is below the header length or not aligned.</exception>
    public static byte[] CreatePadding(ChunkLayout layout, long slotSize)
    {
        int header = HeaderSize(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotSize, header);
        long align = layout == ChunkLayout.Wave64 ? 8 : 2;
        if (slotSize % align != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotSize), "A padding chunk's slot must keep the next chunk aligned.");
        }

        byte[] zeros = new byte[slotSize - header];
        return layout == ChunkLayout.Wave64 ? CreateWave64(Wave64Junk, zeros) : Create(layout, "JUNK", zeros);
    }

    /// <summary>Checks that the bytes are exactly one chunk slot of the layout (header, payload and pad).</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="chunk">The bytes.</param>
    /// <returns>True when the size field and the length agree.</returns>
    public static bool IsSingleChunk(ChunkLayout layout, ReadOnlySpan<byte> chunk)
    {
        int header = HeaderSize(layout);
        if (chunk.Length < header)
        {
            return false;
        }

        long payload;
        if (layout == ChunkLayout.Wave64)
        {
            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(chunk[16..]);
            if (total < 24 || total > long.MaxValue)
            {
                return false;
            }

            payload = (long)total - 24;
        }
        else if (layout == ChunkLayout.Iff)
        {
            payload = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
        }
        else
        {
            payload = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (payload == 0xFFFF_FFFF)
            {
                return false;
            }
        }

        return SlotSize(layout, payload) == chunk.Length;
    }
}

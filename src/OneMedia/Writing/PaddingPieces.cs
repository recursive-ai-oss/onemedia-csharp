using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia;

/// <summary>
/// Padding elements for a plan, built as a small header piece and a generated run of zeros. The builders in the format namespaces return
/// whole <c>byte[]</c> elements; for padding that would hold the padding in memory (an ID3v2 tag may carry 256 MiB of it), so the editors
/// use these instead.
/// </summary>
internal static class PaddingPieces
{
    /// <summary>A <c>free</c> or <c>skip</c> box of an exact total size (ISO/IEC 14496-12 §8.1.2).</summary>
    public static void AddFree(PieceList list, long totalSize, uint type = BoxTypes.Free)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, BoxBuilder.MinimumSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalSize, uint.MaxValue);
        byte[] header = new byte[BoxBuilder.MinimumSize];
        BoxBuilder.WriteHeader(header, type, totalSize);
        list.AddPadding(header, totalSize - BoxBuilder.MinimumSize);
    }

    /// <summary>A Void element of an exact total size (RFC 8794 §11.2), with the shortest size field that can state it.</summary>
    public static void AddVoid(PieceList list, long totalSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSize, 2);
        for (int w = 1; w <= 8; w++)
        {
            long payload = totalSize - 1 - w;
            if (payload >= 0 && payload < (1L << (7 * w)) - 1)
            {
                byte[] header = new byte[1 + w];
                header[0] = (byte)MatroskaIds.Void;
                EbmlWriter.WriteSize(header.AsSpan(1), payload, w);
                list.AddPadding(header, payload);
                return;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(totalSize));
    }

    /// <summary>A <c>JUNK</c> chunk (a <c>junk</c> chunk in Wave64) filling an exact slot; the slot keeps the next chunk aligned.</summary>
    public static void AddChunkPadding(PieceList list, ChunkLayout layout, long slotSize)
    {
        int header = ChunkBuilder.HeaderSize(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotSize, header);
        long align = layout == ChunkLayout.Wave64 ? 8 : 2;
        if (slotSize % align != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotSize), "A padding chunk's slot must keep the next chunk aligned.");
        }

        byte[] head = new byte[header];
        long payload = slotSize - header;
        if (layout == ChunkLayout.Wave64)
        {
            ChunkBuilder.Wave64Junk.TryWriteBytes(head);
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(16), (ulong)slotSize);
        }
        else
        {
            "JUNK"u8.CopyTo(head);
            if (payload > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(slotSize));
            }

            if (layout == ChunkLayout.Iff)
            {
                BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)payload);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)payload);
            }
        }

        list.AddPadding(head, payload);
    }
}

using System.Numerics;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Recognises EBML files (Matroska and WebM) by the EBML header element and reads the DocType from it when the head contains it.
/// </summary>
public sealed class EbmlSniffer : IMediaFormatSniffer
{
    // RFC 8794 §8.1: the EBML header element has ID 0x1A45DFA3; its DocType child (§11.2.6) has ID 0x4282.
    private const uint EbmlHeaderId = 0x1A45DFA3;
    private const uint DocTypeId = 0x4282;

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Matroska;

    /// <inheritdoc />
    public string DisplayName => "Matroska / WebM";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4 || head[0] != 0x1A || head[1] != 0x45 || head[2] != 0xDF || head[3] != 0xA3)
        {
            return SniffResult.None;
        }

        int pos = 4;
        if (!TryReadSize(head, ref pos, out ulong headerSize))
        {
            // The signature matches but the size is cut off by the end of the head or is not a valid variable-length integer.
            return new SniffResult(SniffConfidence.Likely);
        }

        ulong available = (ulong)(head.Length - pos);
        int end = headerSize >= available ? head.Length : pos + (int)headerSize;
        while (pos < end)
        {
            if (!TryReadId(head, ref pos, out uint id) || !TryReadSize(head, ref pos, out ulong size))
            {
                break;
            }

            if (id == DocTypeId)
            {
                if (size > (ulong)(head.Length - pos))
                {
                    break;
                }

                ReadOnlySpan<byte> text = head.Slice(pos, (int)size);
                int nul = text.IndexOf((byte)0);
                if (nul >= 0)
                {
                    text = text[..nul];
                }

                return new SniffResult(SniffConfidence.Certain, System.Text.Encoding.ASCII.GetString(text));
            }

            if (size > (ulong)(head.Length - pos))
            {
                break;
            }

            pos += (int)size;
        }

        return new SniffResult(SniffConfidence.Certain);
    }

    // RFC 8794 §4: an element ID is 1 to 4 bytes and keeps its length marker bits.
    private static bool TryReadId(ReadOnlySpan<byte> data, ref int pos, out uint id)
    {
        id = 0;
        if (pos >= data.Length)
        {
            return false;
        }

        int length = BitOperations.LeadingZeroCount((uint)data[pos] << 24) + 1;
        if (length > 4 || pos + length > data.Length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            id = (id << 8) | data[pos + i];
        }

        pos += length;
        return true;
    }

    // RFC 8794 §4: a data size is 1 to 8 bytes; the marker bit is removed; all value bits set means "unknown".
    private static bool TryReadSize(ReadOnlySpan<byte> data, ref int pos, out ulong size)
    {
        size = 0;
        if (pos >= data.Length || data[pos] == 0)
        {
            return false;
        }

        int length = BitOperations.LeadingZeroCount((uint)data[pos] << 24) + 1;
        if (pos + length > data.Length)
        {
            return false;
        }

        ulong value = (ulong)data[pos] & ((1UL << (8 - length)) - 1);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[pos + i];
        }

        pos += length;
        size = value;
        return true;
    }
}

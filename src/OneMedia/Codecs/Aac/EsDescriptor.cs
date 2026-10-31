using System.Buffers;

namespace RecursiveAI.OneMedia.Codecs.Aac;

/// <summary>
/// The ES_Descriptor of an MPEG-4 <c>esds</c> box (ISO/IEC 14496-1 §7.2.6.5 and §7.2.6.6): the elementary stream id, the object type
/// and stream type, bit rates and the decoder-specific info (for AAC, the AudioSpecificConfig).
/// </summary>
public sealed class EsDescriptor
{
    /// <summary>ES_ID.</summary>
    public int EsId { get; init; }

    /// <summary>objectTypeIndication (ISO/IEC 14496-1 Table 5): 0x40 is MPEG-4 audio, 0x6B MPEG-1 audio, 0x69 MPEG-2 audio, 0x21 AVC.</summary>
    public int ObjectTypeIndication { get; init; }

    /// <summary>streamType (6 bits): 4 visual, 5 audio.</summary>
    public int StreamType { get; init; } = 5;

    /// <summary>bufferSizeDB.</summary>
    public int BufferSize { get; init; }

    /// <summary>maxBitrate in bits per second.</summary>
    public uint MaxBitrate { get; init; }

    /// <summary>avgBitrate in bits per second.</summary>
    public uint AvgBitrate { get; init; }

    /// <summary>The DecoderSpecificInfo bytes, empty when absent. A slice of the parsed data.</summary>
    public ReadOnlyMemory<byte> DecoderSpecificInfo { get; init; }

    /// <summary>Parses the descriptor.</summary>
    /// <param name="data">Either the <c>esds</c> box content (a full box: four bytes of version and flags first) or the bare ES_Descriptor starting with tag 0x03.</param>
    /// <param name="descriptor">The result when true.</param>
    /// <returns>False when the descriptors are malformed or truncated. Never throws.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out EsDescriptor? descriptor)
    {
        descriptor = null;
        ReadOnlySpan<byte> s = data.Span;
        int pos = 0;
        if (s.Length >= 5 && s[0] == 0 && s[4] == 0x03)
        {
            pos = 4;
        }

        if (!ReadTag(s, ref pos, 0x03, out int esEnd))
        {
            return false;
        }

        if (esEnd - pos < 3)
        {
            return false;
        }

        int esId = (s[pos] << 8) | s[pos + 1];
        int flags = s[pos + 2];
        pos += 3;
        if ((flags & 0x80) != 0)
        {
            pos += 2;
        }

        if ((flags & 0x40) != 0)
        {
            if (pos >= esEnd)
            {
                return false;
            }

            pos += 1 + s[pos];
        }

        if ((flags & 0x20) != 0)
        {
            pos += 2;
        }

        if (!ReadTag(s, ref pos, 0x04, out int dcEnd) || dcEnd - pos < 13)
        {
            return false;
        }

        int objectType = s[pos];
        int streamType = s[pos + 1] >> 2;
        int bufferSize = (s[pos + 2] << 16) | (s[pos + 3] << 8) | s[pos + 4];
        uint max = (uint)((s[pos + 5] << 24) | (s[pos + 6] << 16) | (s[pos + 7] << 8) | s[pos + 8]);
        uint avg = (uint)((s[pos + 9] << 24) | (s[pos + 10] << 16) | (s[pos + 11] << 8) | s[pos + 12]);
        pos += 13;
        ReadOnlyMemory<byte> dsi = default;
        if (pos < dcEnd && ReadTag(s, ref pos, 0x05, out int dsiEnd))
        {
            dsi = data[pos..dsiEnd];
        }

        descriptor = new EsDescriptor
        {
            EsId = esId,
            ObjectTypeIndication = objectType,
            StreamType = streamType,
            BufferSize = bufferSize,
            MaxBitrate = max,
            AvgBitrate = avg,
            DecoderSpecificInfo = dsi,
        };
        return true;
    }

    // ISO/IEC 14496-1 §8.3.3: a tag byte then a size of one to four bytes, seven bits each, high bit meaning "more".
    private static bool ReadTag(ReadOnlySpan<byte> s, ref int pos, int tag, out int end)
    {
        end = 0;
        if (pos >= s.Length || s[pos] != tag)
        {
            return false;
        }

        int p = pos + 1;
        long size = 0;
        for (int i = 0; i < 4; i++)
        {
            if (p >= s.Length)
            {
                return false;
            }

            byte b = s[p++];
            size = (size << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        if (size > s.Length - p)
        {
            return false;
        }

        pos = p;
        end = p + (int)size;
        return true;
    }

    /// <summary>Writes an <c>esds</c> box payload: version and flags (zero), the ES_Descriptor, the DecoderConfigDescriptor, the
    /// decoder-specific info and an SLConfigDescriptor with predefined value 2, with four-byte sizes avoided (single-byte sizes where they fit).</summary>
    /// <param name="writer">Receives the bytes.</param>
    public void WriteBoxPayload(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int dsiLen = DecoderSpecificInfo.Length;
        int dsiPart = dsiLen == 0 ? 0 : 1 + SizeFieldLength(dsiLen) + dsiLen;
        int dcLen = 13 + dsiPart;
        int esLen = 3 + 1 + SizeFieldLength(dcLen) + dcLen + 3;
        int total = 4 + 1 + SizeFieldLength(esLen) + esLen;
        Span<byte> w = writer.GetSpan(total);
        w[..total].Clear();
        int p = 4;
        w[p++] = 0x03;
        p = PutSize(w, p, esLen);
        w[p++] = (byte)(EsId >> 8);
        w[p++] = (byte)EsId;
        w[p++] = 0;
        w[p++] = 0x04;
        p = PutSize(w, p, dcLen);
        w[p++] = (byte)ObjectTypeIndication;
        w[p++] = (byte)((StreamType << 2) | 1); // upStream = 0, reserved = 1
        w[p++] = (byte)(BufferSize >> 16);
        w[p++] = (byte)(BufferSize >> 8);
        w[p++] = (byte)BufferSize;
        for (int i = 0; i < 4; i++)
        {
            w[p++] = (byte)(MaxBitrate >> (24 - (8 * i)));
        }

        for (int i = 0; i < 4; i++)
        {
            w[p++] = (byte)(AvgBitrate >> (24 - (8 * i)));
        }

        if (dsiLen > 0)
        {
            w[p++] = 0x05;
            p = PutSize(w, p, dsiLen);
            DecoderSpecificInfo.Span.CopyTo(w[p..]);
            p += dsiLen;
        }

        w[p++] = 0x06;
        w[p++] = 1;
        w[p++] = 2;
        writer.Advance(p);
    }

    private static int SizeFieldLength(int size) => size < 0x80 ? 1 : size < 0x4000 ? 2 : size < 0x200000 ? 3 : 4;

    private static int PutSize(Span<byte> w, int p, int size)
    {
        int n = SizeFieldLength(size);
        for (int i = n - 1; i >= 0; i--)
        {
            w[p++] = (byte)(((size >> (7 * i)) & 0x7f) | (i > 0 ? 0x80 : 0));
        }

        return p;
    }
}

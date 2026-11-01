using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>One payload of a data packet: a piece of a media object (or, for a compressed payload, a run of whole small objects).</summary>
internal struct AsfPayload
{
    public int Stream;
    public bool Key;
    public uint ObjectNumber;
    public uint Offset;
    public uint ObjectSize;
    public long TimeMs;          // presentation time in milliseconds; -1 when the payload does not state one
    public int DataStart;        // within the packet
    public int DataLength;
    public bool Compressed;
    public int TimeDelta;        // compressed payloads: milliseconds between the sub-payloads
}

/// <summary>Reads the layout of one data packet: its header fields, then each payload's header. Pure functions over the packet bytes.</summary>
internal static class AsfPacketParser
{
    /// <summary>
    /// Parses one data packet. <paramref name="payloads"/> is cleared and filled. Returns null on success or a short reason code when the packet
    /// is damaged (the payloads found before the damage stay in the list).
    /// </summary>
    public static string? Parse(ReadOnlySpan<byte> p, int fixedSize, List<AsfPayload> payloads, out int packetLength)
    {
        payloads.Clear();
        packetLength = fixedSize;
        int pos = 0;
        if (p.Length < 4)
        {
            return "short";
        }

        // The first byte is either the error-correction flags (bit 7 set; the low four bits give the length of the error-correction data that follows)
        // or, when the packet has none, already the length-type flags.
        if ((p[0] & 0x80) != 0)
        {
            pos = 1 + (p[0] & 0x0F);
        }

        if (pos + 2 > p.Length)
        {
            return "short";
        }

        byte lengthFlags = p[pos++];
        byte propertyFlags = p[pos++];
        bool multiple = (lengthFlags & 1) != 0;
        int sequenceType = (lengthFlags >> 1) & 3;
        int paddingType = (lengthFlags >> 3) & 3;
        int packetLengthType = (lengthFlags >> 5) & 3;
        if (!TryRead(p, ref pos, packetLengthType, out uint stated) || !TryRead(p, ref pos, sequenceType, out _) || !TryRead(p, ref pos, paddingType, out uint padding) || pos + 6 > p.Length)
        {
            return "short";
        }

        pos += 6; // send time (4 bytes) and duration (2 bytes) of the packet; the payloads carry the presentation times
        int total = packetLengthType != 0 && stated > 0 ? (int)Math.Min(stated, int.MaxValue) : fixedSize;
        packetLength = total; // as stated, so that a caller can tell a short read from a short packet
        if (total > p.Length)
        {
            total = p.Length;
        }

        if (padding > total - pos)
        {
            return "padding";
        }

        int end = total - (int)padding;
        int count = 1;
        int payloadLengthType = 0;
        if (multiple)
        {
            if (pos >= end)
            {
                return "short";
            }

            byte payloadFlags = p[pos++];
            count = payloadFlags & 0x3F;
            payloadLengthType = payloadFlags >> 6;
        }

        int replicatedType = propertyFlags & 3;
        int offsetType = (propertyFlags >> 2) & 3;
        int objectNumberType = (propertyFlags >> 4) & 3;
        for (int i = 0; i < count; i++)
        {
            if (pos >= end)
            {
                return "short";
            }

            byte streamByte = p[pos++];
            if (!TryRead(p, ref pos, objectNumberType, out uint objectNumber) || !TryRead(p, ref pos, offsetType, out uint offset) || !TryRead(p, ref pos, replicatedType, out uint replicated) || pos + replicated > end)
            {
                return "short";
            }

            AsfPayload payload = new() { Stream = streamByte & 0x7F, Key = (streamByte & 0x80) != 0, ObjectNumber = objectNumber, Offset = offset, TimeMs = -1 };
            if (replicated == 1)
            {
                // A compressed payload: the "offset" field holds the presentation time and the one replicated byte the time step between sub-payloads.
                payload.Compressed = true;
                payload.TimeMs = offset;
                payload.TimeDelta = p[pos];
            }
            else if (replicated >= 8)
            {
                payload.ObjectSize = BinaryPrimitives.ReadUInt32LittleEndian(p[pos..]);
                payload.TimeMs = BinaryPrimitives.ReadUInt32LittleEndian(p[(pos + 4)..]);
            }

            pos += (int)replicated;
            uint length;
            if (multiple)
            {
                if (!TryRead(p, ref pos, payloadLengthType, out length))
                {
                    return "short";
                }
            }
            else
            {
                length = (uint)(end - pos);
            }

            if (length > end - pos)
            {
                payload.DataStart = pos;
                payload.DataLength = end - pos;
                payloads.Add(payload);
                return "payload-length";
            }

            payload.DataStart = pos;
            payload.DataLength = (int)length;
            pos += (int)length;
            payloads.Add(payload);
        }

        return null;
    }

    // A field whose width the packet's flags choose: absent (0), one, two or four bytes, little-endian.
    private static bool TryRead(ReadOnlySpan<byte> p, ref int pos, int type, out uint value)
    {
        value = 0;
        int width = type switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        if (pos + width > p.Length)
        {
            return false;
        }

        value = width switch
        {
            1 => p[pos],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(p[pos..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(p[pos..]),
            _ => 0,
        };
        pos += width;
        return true;
    }
}

using RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Strips DJI <c>djmd</c> samples (protocol buffer messages). The position message, the altitude fields and the aircraft serial number are
/// overwritten with zeros in place, and the field's key byte is changed to field number 15 of the same wire type, so a reader that looks the
/// field up by its number no longer finds it (a zero double would otherwise read back as a position at 0, 0). The message keeps its length.
/// </summary>
internal static class DjiScrub
{
    private const int MaxDepth = 8;

    /// <summary>Strips one sample.</summary>
    /// <returns>False when the sample names no protocol this library maps, so its position fields could not be located.</returns>
    public static bool Djmd(ReadOnlySpan<byte> packet, long fileOffset, long trackId, StripCategories cats, StripSink sink)
    {
        if (!FindProtocol(packet, 0, 0, out DjmdDecoder.Layout layout))
        {
            return false;
        }

        Walk(packet, 0, 0, 0, fileOffset, trackId, cats, sink, layout);
        return true;
    }

    // Pass 1: the protocol name sits at 1-1-1, in the first messages.
    private static bool FindProtocol(ReadOnlySpan<byte> message, ulong path, int depth, out DjmdDecoder.Layout layout)
    {
        layout = default;
        ProtobufReader r = new(message);
        while (r.TryRead(out int number, out ProtobufWireType wt, out _, out ReadOnlySpan<byte> payload, out _))
        {
            if (number > 255 || wt != ProtobufWireType.LengthDelimited)
            {
                continue;
            }

            ulong key = DjmdDecoder.Append(path, number);
            if (key == DjmdDecoder.Protocol)
            {
                ReadOnlySpan<byte> n = payload;
                if (n.StartsWith("dvtm_"u8))
                {
                    n = n[5..];
                }

                int dot = n.IndexOf((byte)'.');
                if (dot >= 0)
                {
                    n = n[..dot];
                }

                return DjmdDecoder.TryLayout(n, out layout);
            }

            if (depth < MaxDepth && (key == 1 || key == 0x0101) && FindProtocol(payload, key, depth + 1, out layout))
            {
                return true;
            }
        }

        return false;
    }

    private static void Walk(ReadOnlySpan<byte> message, ulong path, int depth, int baseOffset, long fileOffset, long trackId, StripCategories cats, StripSink sink, DjmdDecoder.Layout layout)
    {
        ProtobufReader r = new(message);
        while (r.TryRead(out int number, out ProtobufWireType wt, out ulong v, out ReadOnlySpan<byte> payload, out int start))
        {
            if (number > 255)
            {
                continue;
            }

            ulong key = DjmdDecoder.Append(path, number);
            int end = r.Position;
            string? name = null;
            StripCategories cat = StripCategories.Location;
            if (key == DjmdDecoder.Serial && wt == ProtobufWireType.LengthDelimited)
            {
                name = "djmd serial number";
                cat = StripCategories.DeviceIds;
            }
            else if (key == layout.Gps && wt == ProtobufWireType.LengthDelimited)
            {
                name = "djmd gps";
            }
            else if (key == layout.Alt && layout.Alt != 0 && wt is ProtobufWireType.Varint or ProtobufWireType.Fixed32 or ProtobufWireType.Fixed64)
            {
                name = "djmd altitude";
            }
            else if (key == layout.Rel && layout.Rel != 0 && wt is ProtobufWireType.Varint or ProtobufWireType.Fixed32 or ProtobufWireType.Fixed64)
            {
                name = "djmd relative altitude";
            }

            if (name is not null)
            {
                if ((cats & cat) != 0)
                {
                    Neutralise(message, baseOffset, start, end, wt, payload.Length, fileOffset, trackId, cat, name, sink);
                }

                continue;
            }

            if (wt == ProtobufWireType.LengthDelimited && depth < MaxDepth && Descends(key, layout))
            {
                Walk(payload, key, depth + 1, baseOffset + end - payload.Length, fileOffset, trackId, cats, sink, layout);
            }
        }
    }

    private static bool Descends(ulong key, DjmdDecoder.Layout l) =>
        key == 1 || key == 0x0101 || DjmdDecoder.IsPrefixOf(key, l.Gps) || DjmdDecoder.IsPrefixOf(key, l.Alt) || DjmdDecoder.IsPrefixOf(key, l.Rel);

    private static void Neutralise(ReadOnlySpan<byte> message, int baseOffset, int start, int end, ProtobufWireType wt, int payloadLength, long fileOffset, long trackId, StripCategories cat, string name, StripSink sink)
    {
        int keyLength = 1;
        while (start + keyLength - 1 < message.Length && (message[start + keyLength - 1] & 0x80) != 0)
        {
            keyLength++;
        }

        // The value: a length-delimited payload ends the field; a varint is rewritten as 0x80 ... 0x00 so it keeps its length and reads zero.
        int valueStart = wt == ProtobufWireType.LengthDelimited ? end - payloadLength : start + keyLength;
        int valueLength = end - valueStart;
        long at = fileOffset + baseOffset;
        if (wt == ProtobufWireType.Varint && valueLength > 0)
        {
            byte[] v = new byte[valueLength];
            for (int i = 0; i < valueLength - 1; i++)
            {
                v[i] = 0x80;
            }

            sink.Patches.Write(at + valueStart, v);
        }
        else
        {
            sink.Patches.Zero(at + valueStart, valueLength);
        }

        // A single-byte key gives field numbers below 16; field 15 of the same wire type is an unmapped slot.
        if (keyLength == 1)
        {
            sink.Patches.Write(at + start, [(byte)((15 << 3) | (int)wt)]);
        }

        sink.Note(cat, "djmd", name, trackId, at + start, end - start, StripAction.Overwritten);
    }
}

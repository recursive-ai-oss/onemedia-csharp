using RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Strips DJI <c>djmd</c> samples (protocol buffer messages). The position message, the altitude fields and the aircraft serial number are
/// overwritten with zeros in place, and the field's key byte is changed to field number 15 of the same wire type, so a reader that looks the
/// field up by its number no longer finds it (a zero double would otherwise read back as a position at 0, 0). The message keeps its length.
/// The serial number sits at the same path in every protocol seen; where the position is stored depends on the protocol. A sample whose protocol
/// is not in the table of known layouts (a sample that names none takes the protocol an earlier sample of the track named) is stripped
/// conservatively: the serial goes by its path, and so does everything outside the two groups known to hold only camera settings and a frame
/// counter (field 3, children 1 and 2) and the device and stream descriptions (fields 1 and 2), plus any double anywhere and any bytes that do not
/// parse as protocol buffer fields. Unknown content is removed, never left.
/// </summary>
internal static class DjiScrub
{
    private const int MaxDepth = 8;

    /// <summary>What a track's earlier samples said about the protocol.</summary>
    internal sealed class State
    {
        public bool Known;
        public DjmdDecoder.Layout Layout;
    }

    /// <summary>Strips one sample.</summary>
    /// <returns>True when the sample was stripped conservatively because its protocol layout is not known.</returns>
    public static bool Djmd(ReadOnlySpan<byte> packet, long fileOffset, long trackId, StripCategories cats, StripSink sink, State state)
    {
        if (FindProtocol(packet, 0, 0, out bool known, out DjmdDecoder.Layout layout))
        {
            state.Known = known;
            state.Layout = layout;
        }

        bool conservative = !state.Known;
        Walk(packet, 0, 0, 0, fileOffset, trackId, cats, sink, state.Layout, conservative);
        return conservative;
    }

    // Pass 1: the protocol name sits at 1-1-1, in the first messages.
    private static bool FindProtocol(ReadOnlySpan<byte> message, ulong path, int depth, out bool known, out DjmdDecoder.Layout layout)
    {
        layout = default;
        known = false;
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

                known = DjmdDecoder.TryLayout(n, out layout);
                return true;
            }

            if (depth < MaxDepth && (key == 1 || key == 0x0101) && FindProtocol(payload, key, depth + 1, out known, out layout))
            {
                return true;
            }
        }

        return false;
    }

    private static void Walk(ReadOnlySpan<byte> message, ulong path, int depth, int baseOffset, long fileOffset, long trackId, StripCategories cats, StripSink sink, DjmdDecoder.Layout layout, bool conservative)
    {
        ProtobufReader r = new(message);
        int lastEnd = 0;
        while (r.TryRead(out int number, out ProtobufWireType wt, out ulong v, out ReadOnlySpan<byte> payload, out int start))
        {
            int end = r.Position;
            lastEnd = end;
            if (number > 255)
            {
                if (conservative && depth == 0)
                {
                    NeutraliseUnknown(message, baseOffset, start, end, wt, payload.Length, fileOffset, trackId, cats, sink);
                }

                continue;
            }

            ulong key = DjmdDecoder.Append(path, number);
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

            if (conservative && IsUnknown(path, depth, number, wt))
            {
                NeutraliseUnknown(message, baseOffset, start, end, wt, payload.Length, fileOffset, trackId, cats, sink);
                continue;
            }

            if (wt == ProtobufWireType.LengthDelimited && depth < MaxDepth && (Descends(key, layout) || (conservative && DescendsConservatively(depth, payload))))
            {
                Walk(payload, key, depth + 1, baseOffset + end - payload.Length, fileOffset, trackId, cats, sink, layout, conservative);
            }
        }

        // Bytes that do not parse as fields at the top level (a truncated or foreign message) cannot be understood, so in a conservative pass they
        // are removed. A nested payload that does not parse is left: DJI stores some values (the shutter speed) as raw bytes in a nested field,
        // and a position is not stored that way.
        if (conservative && depth == 0 && lastEnd < message.Length && (cats & StripCategories.Location) != 0)
        {
            long at = fileOffset + baseOffset;
            sink.Patches.Zero(at + lastEnd, message.Length - lastEnd);
            sink.Note(StripCategories.Location, "djmd", "djmd unparsed bytes", trackId, at + lastEnd, message.Length - lastEnd, StripAction.Zeroed);
        }
    }

    // Conservative pass: the top level holds the device description (1), the stream description (2) and the per-frame group (3); inside the
    // per-frame group child 1 is the frame counter and child 2 the camera settings in every layout seen. Everything else is where positions,
    // aircraft and gimbal state live in the layouts that have them, so an unknown layout loses it. A double is how every layout stores a coordinate.
    private static bool IsUnknown(ulong path, int depth, int number, ProtobufWireType wt) =>
        wt == ProtobufWireType.Fixed64
        || (depth == 0 && number is not (1 or 2 or 3))
        || (depth == 1 && path == 3 && number is not (1 or 2));

    // Text (names, versions, the model) is read as text, not as nested messages, so a printable string is never rewritten by accident.
    private static bool DescendsConservatively(int depth, ReadOnlySpan<byte> payload)
    {
        if (depth > 2)
        {
            return false;
        }

        foreach (byte b in payload)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return true;
            }
        }

        return false;
    }

    private static void NeutraliseUnknown(ReadOnlySpan<byte> message, int baseOffset, int start, int end, ProtobufWireType wt, int payloadLength, long fileOffset, long trackId, StripCategories cats, StripSink sink)
    {
        if ((cats & StripCategories.Location) != 0)
        {
            Neutralise(message, baseOffset, start, end, wt, payloadLength, fileOffset, trackId, StripCategories.Location, "djmd unmapped field", sink);
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

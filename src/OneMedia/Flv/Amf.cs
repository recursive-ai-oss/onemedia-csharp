using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Flv;

/// <summary>The type of a value in an AMF0 or AMF3 stream, reduced to what <c>onMetaData</c> properties use.</summary>
public enum AmfValueKind
{
    /// <summary>No value (undefined, null, unsupported).</summary>
    None = 0,

    /// <summary>A 64-bit float or an integer.</summary>
    Number,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A string.</summary>
    Text,

    /// <summary>A date: milliseconds since 1970-01-01 UTC.</summary>
    Date,

    /// <summary>An array or object whose members are listed as further properties.</summary>
    Container,
}

/// <summary>One property of a script data tag, flattened: nested members have keys such as <c>keyframes/times</c> and array elements <c>keyframes/times/0</c>.</summary>
/// <param name="Key">The property path.</param>
/// <param name="Kind">The kind of value.</param>
/// <param name="Number">The number, boolean (0 or 1) or date (milliseconds) value.</param>
/// <param name="Text">The string value.</param>
/// <param name="Range">The bytes of the value in the source (for a number the 8 bytes of its IEEE-754 form in AMF0).</param>
public readonly record struct AmfProperty(string Key, AmfValueKind Kind, double Number, string? Text, ByteRange Range);

/// <summary>
/// Reads the AMF0 (Adobe's Action Message Format 0) values of FLV script data tags, and AMF3 values after the AMF0 switch marker 0x11.
/// Parsing is bounded by the limits and never throws on malformed data: it stops with a defect.
/// </summary>
public static class AmfReader
{
    // Arrays larger than this are not listed element by element (a keyframe index can hold tens of thousands); they are counted and kept as one container.
    internal const int MaxListedArrayElements = 64;

    /// <summary>
    /// Reads a script data tag body: a name (usually the string <c>onMetaData</c>) followed by values, the first of which is normally an
    /// ECMA array or object of properties.
    /// </summary>
    /// <param name="body">The tag body.</param>
    /// <param name="baseOffset">The source offset of <paramref name="body"/>, added to every property's range.</param>
    /// <param name="limits">Depth and element ceilings.</param>
    /// <param name="defects">Where problems are logged.</param>
    /// <param name="name">The name of the script tag, or null if the body does not start with a string.</param>
    /// <returns>The properties of the first value after the name, flattened.</returns>
    public static List<AmfProperty> ReadScriptData(ReadOnlySpan<byte> body, long baseOffset, MediaLimits limits, DefectLog defects, out string? name)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        List<AmfProperty> props = [];
        name = null;
        Cursor c = new(body, baseOffset, limits, defects);
        if (c.Remaining < 3 || body[0] != 2)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, baseOffset, "A script data tag does not start with a name string.", "flv.script-name");
            return props;
        }

        c.Pos = 1;
        name = c.ReadShortString();
        if (name is null)
        {
            return props;
        }

        // The value after the name: an ECMA array or object holds the properties; anything else is listed as one property called "value".
        if (c.Remaining > 0)
        {
            ReadValue(ref c, string.Empty, props, 0, top: true);
        }

        return props;
    }

    private ref struct Cursor
    {
        public ReadOnlySpan<byte> Data;
        public int Pos;
        public long Base;
        public MediaLimits Limits;
        public DefectLog Defects;
        public bool Failed;
        public long Count;

        public Cursor(ReadOnlySpan<byte> data, long baseOffset, MediaLimits limits, DefectLog defects)
        {
            Data = data;
            Pos = 0;
            Base = baseOffset;
            Limits = limits;
            Defects = defects;
            Failed = false;
            Count = 0;
        }

        public readonly int Remaining => Data.Length - Pos;

        public void Fail(string message, string code)
        {
            if (!Failed)
            {
                Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, Base + Pos, message, code);
            }

            Failed = true;
        }

        public string? ReadShortString()
        {
            if (Remaining < 2)
            {
                Fail("An AMF string is cut off.", "flv.amf-truncated");
                return null;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(Data[Pos..]);
            Pos += 2;
            if (len > Remaining)
            {
                Fail("An AMF string is longer than the tag.", "flv.amf-truncated");
                return null;
            }

            string s = Encoding.UTF8.GetString(Data.Slice(Pos, len));
            Pos += len;
            return s;
        }

        public string? ReadLongString()
        {
            if (Remaining < 4)
            {
                Fail("An AMF string is cut off.", "flv.amf-truncated");
                return null;
            }

            uint len = BinaryPrimitives.ReadUInt32BigEndian(Data[Pos..]);
            Pos += 4;
            if (len > (uint)Remaining)
            {
                Fail("An AMF string is longer than the tag.", "flv.amf-truncated");
                return null;
            }

            string s = Encoding.UTF8.GetString(Data.Slice(Pos, (int)len));
            Pos += (int)len;
            return s;
        }
    }

    private static string Join(string parent, string child) => parent.Length == 0 ? child : parent + "/" + child;

    // AMF0 value marker types (Adobe AMF0 specification §2): 0 number, 1 boolean, 2 string, 3 object, 5 null, 6 undefined, 7 reference,
    // 8 ECMA array, 9 object end, 10 strict array, 11 date, 12 long string, 13 unsupported, 15 XML document, 16 typed object, 17 AVM+ switch.
    private static void ReadValue(ref Cursor c, string key, List<AmfProperty> into, int depth, bool top = false)
    {
        c.Limits.CheckDepth(depth + 1, c.Base + c.Pos);
        c.Limits.CheckElementCount(++c.Count, c.Base + c.Pos);
        if (c.Failed || c.Remaining < 1)
        {
            c.Fail("An AMF value is missing.", "flv.amf-truncated");
            return;
        }

        int start = c.Pos;
        byte marker = c.Data[c.Pos++];
        string leaf = key.Length == 0 && top ? "value" : key;
        switch (marker)
        {
            case 0:
                if (c.Remaining < 8)
                {
                    c.Fail("An AMF number is cut off.", "flv.amf-truncated");
                    return;
                }

                into.Add(new AmfProperty(leaf, AmfValueKind.Number, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(c.Data[c.Pos..])), null, new ByteRange(c.Base + c.Pos, 8)));
                c.Pos += 8;
                return;
            case 1:
                if (c.Remaining < 1)
                {
                    c.Fail("An AMF boolean is cut off.", "flv.amf-truncated");
                    return;
                }

                into.Add(new AmfProperty(leaf, AmfValueKind.Boolean, c.Data[c.Pos] != 0 ? 1 : 0, null, new ByteRange(c.Base + c.Pos, 1)));
                c.Pos++;
                return;
            case 2:
                {
                    int at = c.Pos + 2;
                    string? s = c.ReadShortString();
                    if (s is not null)
                    {
                        into.Add(new AmfProperty(leaf, AmfValueKind.Text, 0, s, new ByteRange(c.Base + at, c.Pos - at)));
                    }

                    return;
                }

            case 12:
            case 15:
                {
                    int at = c.Pos + 4;
                    string? s = c.ReadLongString();
                    if (s is not null)
                    {
                        into.Add(new AmfProperty(leaf, AmfValueKind.Text, 0, s, new ByteRange(c.Base + at, c.Pos - at)));
                    }

                    return;
                }

            case 3:
                ReadMembers(ref c, key, into, depth, hasCount: false);
                return;
            case 8:
                if (c.Remaining < 4)
                {
                    c.Fail("An AMF array is cut off.", "flv.amf-truncated");
                    return;
                }

                c.Pos += 4; // the approximate count; the end marker is what ends the list
                ReadMembers(ref c, key, into, depth, hasCount: true);
                return;
            case 16:
                _ = c.ReadShortString(); // class name
                ReadMembers(ref c, key, into, depth, hasCount: false);
                return;
            case 10:
                {
                    if (c.Remaining < 4)
                    {
                        c.Fail("An AMF array is cut off.", "flv.amf-truncated");
                        return;
                    }

                    uint count = BinaryPrimitives.ReadUInt32BigEndian(c.Data[c.Pos..]);
                    c.Pos += 4;
                    c.Limits.CheckElementCount(c.Count + count, c.Base + c.Pos);
                    int listed = 0;
                    List<AmfProperty> scratch = [];
                    for (uint i = 0; i < count && !c.Failed; i++)
                    {
                        scratch.Clear();
                        int before = c.Pos;
                        ReadValue(ref c, i < MaxListedArrayElements ? Join(key, i.ToString(CultureInfo.InvariantCulture)) : key, scratch, depth + 1);
                        if (i < MaxListedArrayElements)
                        {
                            into.AddRange(scratch);
                            listed++;
                        }
                    }

                    into.Add(new AmfProperty(leaf, AmfValueKind.Container, count, listed < count ? $"{count} elements, {listed} listed" : null, new ByteRange(c.Base + start, c.Pos - start)));
                    return;
                }

            case 11:
                if (c.Remaining < 10)
                {
                    c.Fail("An AMF date is cut off.", "flv.amf-truncated");
                    return;
                }

                into.Add(new AmfProperty(leaf, AmfValueKind.Date, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(c.Data[c.Pos..])), null, new ByteRange(c.Base + c.Pos, 8)));
                c.Pos += 10; // double plus a 16-bit time zone offset that is always 0 in practice
                return;
            case 5:
            case 6:
            case 13:
                return;
            case 17:
                Amf3.ReadValue(ref c, leaf, into, depth);
                return;
            default:
                c.Fail($"AMF0 marker {marker} is not supported; the rest of the script data is not read.", "flv.amf-marker");
                return;
        }
    }

    private static void ReadMembers(ref Cursor c, string key, List<AmfProperty> into, int depth, bool hasCount)
    {
        int start = c.Pos;
        int members = 0;
        while (!c.Failed)
        {
            if (c.Remaining < 3)
            {
                // An object may end without its end marker when the tag is cut short; the members read so far stay.
                if (c.Remaining > 0)
                {
                    c.Fail("An AMF object is cut off.", "flv.amf-truncated");
                }

                break;
            }

            // AMF0 §2.5: members are name/value pairs ended by an empty name followed by the object-end marker 0x09.
            if (c.Data[c.Pos] == 0 && c.Data[c.Pos + 1] == 0 && c.Data[c.Pos + 2] == 9)
            {
                c.Pos += 3;
                break;
            }

            string? name = c.ReadShortString();
            if (name is null)
            {
                break;
            }

            ReadValue(ref c, Join(key, name), into, depth + 1);
            members++;
        }

        _ = start;
        _ = hasCount;
        _ = members;
    }

    // AMF3 (Adobe AMF3 specification): values reached from AMF0 through the 0x11 switch. Only what script data uses is read; references to
    // earlier strings, objects and traits are resolved with per-value tables.
    private static class Amf3
    {
        public static void ReadValue(ref Cursor c, string key, List<AmfProperty> into, int depth)
        {
            Tables t = new();
            Value(ref c, key, into, depth, t);
        }

        private sealed class Tables
        {
            public List<string> Strings { get; } = [];
            public List<(bool Dynamic, List<string> Names)> Traits { get; } = [];
            public int Objects;
        }

        private static bool U29(ref Cursor c, out int value)
        {
            value = 0;
            for (int i = 0; i < 4; i++)
            {
                if (c.Remaining < 1)
                {
                    c.Fail("An AMF3 integer is cut off.", "flv.amf-truncated");
                    return false;
                }

                byte b = c.Data[c.Pos++];
                if (i == 3)
                {
                    value = (value << 8) | b;
                    return true;
                }

                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return true;
                }
            }

            return true;
        }

        private static string? Str(ref Cursor c, Tables t)
        {
            if (!U29(ref c, out int header))
            {
                return null;
            }

            if ((header & 1) == 0)
            {
                int idx = header >> 1;
                if (idx >= t.Strings.Count)
                {
                    c.Fail("An AMF3 string reference is out of range.", "flv.amf-ref");
                    return null;
                }

                return t.Strings[idx];
            }

            int len = header >> 1;
            if (len > c.Remaining)
            {
                c.Fail("An AMF3 string is longer than the tag.", "flv.amf-truncated");
                return null;
            }

            string s = Encoding.UTF8.GetString(c.Data.Slice(c.Pos, len));
            c.Pos += len;
            if (len > 0)
            {
                t.Strings.Add(s);
            }

            return s;
        }

        // AMF3 markers: 0 undefined, 1 null, 2 false, 3 true, 4 integer, 5 double, 6 string, 7 XML document, 8 date, 9 array, 10 object, 11 XML, 12 byte array.
        private static void Value(ref Cursor c, string key, List<AmfProperty> into, int depth, Tables t)
        {
            c.Limits.CheckDepth(depth + 1, c.Base + c.Pos);
            c.Limits.CheckElementCount(++c.Count, c.Base + c.Pos);
            if (c.Failed || c.Remaining < 1)
            {
                c.Fail("An AMF3 value is missing.", "flv.amf-truncated");
                return;
            }

            int start = c.Pos;
            byte marker = c.Data[c.Pos++];
            switch (marker)
            {
                case 0:
                case 1:
                    return;
                case 2:
                case 3:
                    into.Add(new AmfProperty(key, AmfValueKind.Boolean, marker == 3 ? 1 : 0, null, new ByteRange(c.Base + start, 1)));
                    return;
                case 4:
                    if (U29(ref c, out int i))
                    {
                        // 29-bit two's complement
                        int signed = (i & 0x10000000) != 0 ? i - 0x20000000 : i;
                        into.Add(new AmfProperty(key, AmfValueKind.Number, signed, null, new ByteRange(c.Base + start + 1, c.Pos - start - 1)));
                    }

                    return;
                case 5:
                    if (c.Remaining < 8)
                    {
                        c.Fail("An AMF3 double is cut off.", "flv.amf-truncated");
                        return;
                    }

                    into.Add(new AmfProperty(key, AmfValueKind.Number, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(c.Data[c.Pos..])), null, new ByteRange(c.Base + c.Pos, 8)));
                    c.Pos += 8;
                    return;
                case 6:
                    {
                        int at = c.Pos;
                        string? s = Str(ref c, t);
                        if (s is not null)
                        {
                            into.Add(new AmfProperty(key, AmfValueKind.Text, 0, s, new ByteRange(c.Base + at, c.Pos - at)));
                        }

                        return;
                    }

                case 8:
                    {
                        if (!U29(ref c, out int h) || (h & 1) == 0 || c.Remaining < 8)
                        {
                            c.Fail("An AMF3 date is not readable.", "flv.amf-truncated");
                            return;
                        }

                        t.Objects++;
                        into.Add(new AmfProperty(key, AmfValueKind.Date, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(c.Data[c.Pos..])), null, new ByteRange(c.Base + c.Pos, 8)));
                        c.Pos += 8;
                        return;
                    }

                case 9:
                    {
                        if (!U29(ref c, out int h) || (h & 1) == 0)
                        {
                            c.Fail("An AMF3 array reference is not supported.", "flv.amf-ref");
                            return;
                        }

                        t.Objects++;
                        int dense = h >> 1;
                        // Associative part first: name/value pairs ended by an empty name.
                        while (!c.Failed)
                        {
                            string? name = Str(ref c, t);
                            if (name is null || name.Length == 0)
                            {
                                break;
                            }

                            Value(ref c, Join(key, name), into, depth + 1, t);
                        }

                        c.Limits.CheckElementCount(c.Count + dense, c.Base + c.Pos);
                        for (int k = 0; k < dense && !c.Failed; k++)
                        {
                            List<AmfProperty> scratch = [];
                            Value(ref c, k < MaxListedArrayElements ? Join(key, k.ToString(CultureInfo.InvariantCulture)) : key, scratch, depth + 1, t);
                            if (k < MaxListedArrayElements)
                            {
                                into.AddRange(scratch);
                            }
                        }

                        into.Add(new AmfProperty(key, AmfValueKind.Container, dense, dense > MaxListedArrayElements ? $"{dense} elements, {MaxListedArrayElements} listed" : null, new ByteRange(c.Base + start, c.Pos - start)));
                        return;
                    }

                case 10:
                    {
                        if (!U29(ref c, out int h))
                        {
                            return;
                        }

                        if ((h & 1) == 0)
                        {
                            c.Fail("An AMF3 object reference is not supported.", "flv.amf-ref");
                            return;
                        }

                        t.Objects++;
                        List<string> names;
                        bool isDynamic;
                        if ((h & 2) == 0)
                        {
                            int idx = h >> 2;
                            if (idx >= t.Traits.Count)
                            {
                                c.Fail("An AMF3 trait reference is out of range.", "flv.amf-ref");
                                return;
                            }

                            (isDynamic, names) = t.Traits[idx];
                        }
                        else
                        {
                            if ((h & 4) != 0)
                            {
                                c.Fail("An AMF3 externalizable object is not supported.", "flv.amf-marker");
                                return;
                            }

                            isDynamic = (h & 8) != 0;
                            int sealedCount = h >> 4;
                            c.Limits.CheckElementCount(c.Count + sealedCount, c.Base + c.Pos);
                            _ = Str(ref c, t); // class name
                            names = [];
                            for (int k = 0; k < sealedCount && !c.Failed; k++)
                            {
                                names.Add(Str(ref c, t) ?? string.Empty);
                            }

                            t.Traits.Add((isDynamic, names));
                        }

                        foreach (string n in names)
                        {
                            if (c.Failed)
                            {
                                return;
                            }

                            Value(ref c, Join(key, n), into, depth + 1, t);
                        }

                        if (isDynamic)
                        {
                            while (!c.Failed)
                            {
                                string? name = Str(ref c, t);
                                if (name is null || name.Length == 0)
                                {
                                    break;
                                }

                                Value(ref c, Join(key, name), into, depth + 1, t);
                            }
                        }

                        return;
                    }

                default:
                    c.Fail($"AMF3 marker {marker} is not supported; the rest of the script data is not read.", "flv.amf-marker");
                    return;
            }
        }
    }
}

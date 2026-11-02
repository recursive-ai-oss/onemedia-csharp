using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;

/// <summary>One key a <c>mebx</c> sample description declares: the local id used as the atom type in samples, the key itself and the data type of its values.</summary>
public sealed class MebxKey
{
    internal MebxKey(uint localId, uint keyNamespace, string key, uint dataTypeNamespace, uint dataType, string extendedDataType)
    {
        LocalId = localId;
        Namespace = keyNamespace;
        Key = key;
        DataTypeNamespace = dataTypeNamespace;
        DataType = dataType;
        ExtendedDataType = extendedDataType;
    }

    /// <summary>The 32-bit id (often a four-character code) that types the atoms holding this key's values in the samples.</summary>
    public uint LocalId { get; }

    /// <summary>The key namespace as a four-character code (<c>mdta</c> for reverse-DNS names such as <c>com.apple.quicktime.location.ISO6709</c>).</summary>
    public uint Namespace { get; }

    /// <summary>The key value as text (UTF-8); for a namespace that is not text, the bytes in Latin-1.</summary>
    public string Key { get; }

    /// <summary>Data type namespace from <c>dtyp</c>: 0 for a well-known type, 1 for a reverse-DNS extended type; 0 when the key declares no <c>dtyp</c>.</summary>
    public uint DataTypeNamespace { get; }

    /// <summary>The well-known type code when <see cref="DataTypeNamespace"/> is 0 (1 = UTF-8, 23 = float32, 67 = int32 …); 0 when undeclared.</summary>
    public uint DataType { get; }

    /// <summary>The extended data type name when <see cref="DataTypeNamespace"/> is 1, else empty.</summary>
    public string ExtendedDataType { get; }
}

/// <summary>The keys of one <c>mebx</c> sample description (its metadata key table).</summary>
public sealed class MebxKeyTable
{
    private readonly MebxKey[] _keys;

    internal MebxKeyTable(MebxKey[] keys) => _keys = keys;

    /// <summary>The keys in declaration order.</summary>
    public IReadOnlyList<MebxKey> Keys => _keys;

    /// <summary>Finds the key with a local id.</summary>
    /// <param name="localId">The atom type used in samples.</param>
    /// <returns>The key, or <c>null</c> when the table does not declare it.</returns>
    public MebxKey? Find(uint localId)
    {
        foreach (MebxKey k in _keys)
        {
            if (k.LocalId == localId)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the boxes that follow the eight common bytes of a <c>mebx</c> sample entry: a <c>keys</c> box holding one box per key (typed with
    /// the key's local id) that holds <c>keyd</c> (namespace and key), optionally <c>dtyp</c> and <c>loca</c>. Boxes it does not know are skipped.
    /// </summary>
    /// <param name="entryBody">The entry's bytes after its eight common bytes.</param>
    /// <param name="table">The table, when the result is true; empty when the entry has no key box.</param>
    /// <returns>False when the bytes are not well-formed boxes (the table holds the keys read before the damage).</returns>
    public static bool TryParse(ReadOnlySpan<byte> entryBody, out MebxKeyTable table)
    {
        List<MebxKey> keys = [];
        bool ok = true;
        int pos = 0;
        while (TryBox(entryBody, pos, out uint type, out int payload, out int end))
        {
            if (type == Fourcc("keys"))
            {
                ok &= ReadKeys(entryBody[payload..end], keys);
            }

            pos = end;
        }

        ok &= pos == entryBody.Length || entryBody.Length - pos < 8 && IsZero(entryBody[pos..]);
        table = new MebxKeyTable([.. keys]);
        return ok;
    }

    private static bool ReadKeys(ReadOnlySpan<byte> body, List<MebxKey> into)
    {
        int pos = 0;
        while (TryBox(body, pos, out uint localId, out int payload, out int end))
        {
            pos = end;
            if (localId == 0 || localId == 0xFFFFFFFF)
            {
                continue; // 0 marks an unused key; 0xFFFFFFFF is reserved.
            }

            ReadOnlySpan<byte> inner = body[payload..end];
            uint ns = 0;
            string key = string.Empty;
            uint dtNs = 0, dt = 0;
            string ext = string.Empty;
            bool haveKeyd = false;
            int ip = 0;
            while (TryBox(inner, ip, out uint t, out int ipayload, out int iend))
            {
                ReadOnlySpan<byte> p = inner[ipayload..iend];
                if (t == Fourcc("keyd") && p.Length >= 4)
                {
                    haveKeyd = true;
                    ns = BinaryPrimitives.ReadUInt32BigEndian(p);
                    key = ns == Fourcc("mdta") || ns == Fourcc("uiso") ? Encoding.UTF8.GetString(p[4..]) : Encoding.Latin1.GetString(p[4..]);
                }
                else if (t == Fourcc("dtyp") && p.Length >= 4)
                {
                    dtNs = BinaryPrimitives.ReadUInt32BigEndian(p);
                    if (dtNs == 0 && p.Length >= 8)
                    {
                        dt = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
                    }
                    else if (dtNs == 1)
                    {
                        ext = Encoding.UTF8.GetString(p[4..]);
                    }
                }

                ip = iend;
            }

            if (haveKeyd)
            {
                into.Add(new MebxKey(localId, ns, key, dtNs, dt, ext));
            }
        }

        return pos == body.Length;
    }

    internal static uint Fourcc(string s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    internal static bool IsZero(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one atom header (32-bit size including the header, 32-bit type) at <paramref name="pos"/>.</summary>
    internal static bool TryBox(ReadOnlySpan<byte> buf, int pos, out uint type, out int payload, out int end)
    {
        type = 0;
        payload = 0;
        end = 0;
        if (buf.Length - pos < 8)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(buf[pos..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(buf[(pos + 4)..]);
        if (size < 8 || size > buf.Length - pos)
        {
            return false;
        }

        payload = pos + 8;
        end = pos + (int)size;
        return true;
    }
}

/// <summary>Well-known data type codes of QuickTime metadata values.</summary>
public static class MebxDataTypes
{
    /// <summary>UTF-8 text without count or terminator.</summary>
    public const uint Utf8 = 1;

    /// <summary>UTF-16 (big-endian) text.</summary>
    public const uint Utf16 = 2;

    /// <summary>Big-endian signed integer in 1, 2, 3, 4 or 8 bytes.</summary>
    public const uint SignedInteger = 21;

    /// <summary>Big-endian unsigned integer in 1, 2, 3, 4 or 8 bytes.</summary>
    public const uint UnsignedInteger = 22;

    /// <summary>Big-endian float32.</summary>
    public const uint Real32 = 23;

    /// <summary>Big-endian float64.</summary>
    public const uint Real64 = 24;

    /// <summary>A metadata atom (nested atoms).</summary>
    public const uint MetadataAtom = 28;

    /// <summary>8-bit signed integer.</summary>
    public const uint Signed8 = 65;

    /// <summary>Big-endian 16-bit signed integer.</summary>
    public const uint Signed16 = 66;

    /// <summary>Big-endian 32-bit signed integer.</summary>
    public const uint Signed32 = 67;

    /// <summary>Point of two big-endian float32.</summary>
    public const uint PointF32 = 70;

    /// <summary>Width and height as two big-endian float32.</summary>
    public const uint DimensionsF32 = 71;

    /// <summary>Rectangle (x, y, width, height) as four big-endian float32.</summary>
    public const uint RectF32 = 72;

    /// <summary>Big-endian 64-bit signed integer.</summary>
    public const uint Signed64 = 74;

    /// <summary>8-bit unsigned integer.</summary>
    public const uint Unsigned8 = 75;

    /// <summary>Big-endian 16-bit unsigned integer.</summary>
    public const uint Unsigned16 = 76;

    /// <summary>Big-endian 32-bit unsigned integer.</summary>
    public const uint Unsigned32 = 77;

    /// <summary>Big-endian 64-bit unsigned integer.</summary>
    public const uint Unsigned64 = 78;
}

internal sealed class MebxDecoder
{
    private const int MaxDepth = 6;
    private readonly TelemetryCollector _collector;
    private readonly MebxKeyTable[] _tables;
    private readonly char[] _text = new char[256];
    private readonly double[] _scratch = new double[4];
    private int _entry;
    private long _fileBase;
    private MediaTime _pts;
    private MediaTime _duration;

    public MebxDecoder(TelemetryCollector collector, MebxKeyTable[] tables)
    {
        _collector = collector;
        _tables = tables;
    }

    public void Decode(ReadOnlySpan<byte> sample, long fileOffset, MediaTime pts, MediaTime duration, int entryIndex)
    {
        _fileBase = fileOffset;
        _pts = pts;
        _duration = duration;
        _entry = Math.Clamp(entryIndex, 0, Math.Max(_tables.Length - 1, 0));
        Walk(sample, 0, 0);
    }

    private void Walk(ReadOnlySpan<byte> body, int baseOffset, int depth)
    {
        int pos = 0;
        while (pos < body.Length)
        {
            if (!MebxKeyTable.TryBox(body, pos, out uint id, out int payload, out int end))
            {
                _collector.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, _fileBase >= 0 ? _fileBase + baseOffset + pos : 0, "A timed metadata atom has an invalid size.", "mebx.atom-size");
                return;
            }

            ReadOnlySpan<byte> value = body[payload..end];
            int valueOffset = baseOffset + payload;
            pos = end;
            if (id == 0 || id == 0xFFFFFFFF)
            {
                continue; // placeholder atoms carry nothing.
            }

            MebxKey? key = _tables.Length > 0 ? _tables[_entry].Find(id) : null;
            if (key is null || key.DataTypeNamespace != 0 || key.DataType == MebxDataTypes.MetadataAtom)
            {
                // A grouping atom (for example a detected face holds nested atoms typed by sub-key ids) or a key with no usable type.
                if (depth < MaxDepth && value.Length >= 8 && LooksLikeAtoms(value))
                {
                    Walk(value, valueOffset, depth + 1);
                    continue;
                }
            }

            Emit(id, key, value, valueOffset);
        }
    }

    private static bool LooksLikeAtoms(ReadOnlySpan<byte> v)
    {
        int pos = 0;
        while (pos < v.Length)
        {
            if (!MebxKeyTable.TryBox(v, pos, out _, out _, out int end))
            {
                return false;
            }

            pos = end;
        }

        return true;
    }

    private void Emit(uint id, MebxKey? key, ReadOnlySpan<byte> value, int valueOffset)
    {
        TelemetryKind kind = TelemetryKind.Unknown;
        int comps = 0;
        double[] scratch = _scratch;
        bool isLocation = false;
        if (key is { DataTypeNamespace: 0 })
        {
            comps = Numbers(key.DataType, value, scratch);
            if (comps > 0)
            {
                kind = TelemetryKind.Scalar;
            }
            else if (key.DataType == MebxDataTypes.Utf8 && key.Key.EndsWith("location.ISO6709", StringComparison.Ordinal))
            {
                isLocation = true;
                kind = TelemetryKind.Gps;
            }
        }

        TelemetryChannelInfo stream = StreamOf(id, key, kind);
        ref TelemetryItem item = ref _collector.Add(stream);
        item.Time = _pts;
        item.Duration = _duration;
        item.Range = _fileBase >= 0 ? new ByteRange(_fileBase + valueOffset, value.Length) : ByteRange.None;
        if (isLocation && TryLocation(value, ref item))
        {
            return;
        }

        if (comps > 0)
        {
            Span<double> v = item.Writable;
            for (int i = 0; i < comps; i++)
            {
                v[i] = scratch[i];
            }

            item.Count = comps;
            return;
        }

        // Text, images and unknown types stay as the bytes stored.
        item.Kind = TelemetryKind.Unknown;
        item.RawOffset = valueOffset;
        item.RawLength = value.Length;
        item.TypeCode = key is { DataTypeNamespace: 0 } ? (int)key.DataType : -1;
    }

    private bool TryLocation(ReadOnlySpan<byte> utf8, ref TelemetryItem item)
    {
        // ISO 6709 text such as +27.5916+086.5640+8850/ ; ASCII only, so a byte widens to a char.
        if (utf8.Length > _text.Length)
        {
            return false;
        }

        for (int i = 0; i < utf8.Length; i++)
        {
            _text[i] = (char)utf8[i];
        }

        if (!GeoLocation.TryParseIso6709(_text.AsSpan(0, utf8.Length), out GeoLocation g))
        {
            return false;
        }

        Span<double> v = item.Writable;
        v[0] = g.Latitude;
        v[1] = g.Longitude;
        v[2] = g.Altitude ?? double.NaN;
        v[3] = double.NaN;
        v[4] = double.NaN;
        v[5] = double.NaN;
        v[6] = g.Altitude is null ? 2 : 3;
        item.Count = 7;
        item.Fix = g.Altitude is null ? GpsFixKind.Fix2D : GpsFixKind.Fix3D;
        return true;
    }

    // Reads a well-known numeric type into at most four components; returns 0 for other types or a wrong length.
    private static int Numbers(uint type, ReadOnlySpan<byte> b, double[] into)
    {
        switch (type)
        {
            case MebxDataTypes.SignedInteger or MebxDataTypes.UnsignedInteger:
                if (b.Length is < 1 or > 8)
                {
                    return 0;
                }

                long raw = 0;
                foreach (byte x in b)
                {
                    raw = (raw << 8) | x;
                }

                if (type == MebxDataTypes.SignedInteger && b.Length < 8 && (b[0] & 0x80) != 0)
                {
                    raw -= 1L << (8 * b.Length);
                }

                into[0] = type == MebxDataTypes.UnsignedInteger && b.Length == 8 ? (ulong)raw : raw;
                return 1;
            case MebxDataTypes.Real32 when b.Length == 4:
                into[0] = BinaryPrimitives.ReadSingleBigEndian(b);
                return 1;
            case MebxDataTypes.Real64 when b.Length == 8:
                into[0] = BinaryPrimitives.ReadDoubleBigEndian(b);
                return 1;
            case MebxDataTypes.Signed8 when b.Length == 1:
                into[0] = (sbyte)b[0];
                return 1;
            case MebxDataTypes.Unsigned8 when b.Length == 1:
                into[0] = b[0];
                return 1;
            case MebxDataTypes.Signed16 when b.Length == 2:
                into[0] = BinaryPrimitives.ReadInt16BigEndian(b);
                return 1;
            case MebxDataTypes.Unsigned16 when b.Length == 2:
                into[0] = BinaryPrimitives.ReadUInt16BigEndian(b);
                return 1;
            case MebxDataTypes.Signed32 when b.Length == 4:
                into[0] = BinaryPrimitives.ReadInt32BigEndian(b);
                return 1;
            case MebxDataTypes.Unsigned32 when b.Length == 4:
                into[0] = BinaryPrimitives.ReadUInt32BigEndian(b);
                return 1;
            case MebxDataTypes.Signed64 when b.Length == 8:
                into[0] = BinaryPrimitives.ReadInt64BigEndian(b);
                return 1;
            case MebxDataTypes.Unsigned64 when b.Length == 8:
                into[0] = BinaryPrimitives.ReadUInt64BigEndian(b);
                return 1;
            case MebxDataTypes.PointF32 or MebxDataTypes.DimensionsF32 when b.Length == 8:
                into[0] = BinaryPrimitives.ReadSingleBigEndian(b);
                into[1] = BinaryPrimitives.ReadSingleBigEndian(b[4..]);
                return 2;
            case MebxDataTypes.RectF32 when b.Length == 16:
                for (int i = 0; i < 4; i++)
                {
                    into[i] = BinaryPrimitives.ReadSingleBigEndian(b[(4 * i)..]);
                }

                return 4;
            default:
                return 0;
        }
    }

    private TelemetryChannelInfo StreamOf(uint id, MebxKey? key, TelemetryKind kind)
    {
        TelemetryChannelInfo? s = _collector.Find(TelemetryFormat.Mebx, 0, id, _entry);
        if (s is not null && s.Kind == kind)
        {
            return s;
        }

        // A key that sometimes fails to decode (a wrong length) appears as a second stream of the same name with kind Unknown.
        string name = key is not null ? key.Key : Encoding.Latin1.GetString([(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]);
        int variant = _entry + (s is not null ? 1000 : 0);
        s = _collector.Find(TelemetryFormat.Mebx, 0, id, variant);
        return s ?? _collector.Create(TelemetryFormat.Mebx, kind, name, 0, id, variant);
    }
}

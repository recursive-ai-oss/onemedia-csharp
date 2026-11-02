namespace RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

/// <summary>
/// Maps the protocol buffer messages in DJI <c>djmd</c> timed metadata samples. The messages have no public schema: a field is identified by
/// its path of field numbers (for example 3-3-4-1), the protocol name the message states in its first string (<c>dvtm_wm265e.proto</c>),
/// and the table of paths that ExifTool's tag documentation lists per protocol. Value conventions were read back from ExifTool 13.60 on
/// synthetic messages: coordinates are doubles (radians when the coordinate-unit field is 0, else degrees), altitudes are integers in
/// millimetres, attitude angles are signed integers in tenths of a degree. A protocol not in the table yields only the raw message.
/// </summary>
internal sealed class DjmdDecoder
{
    private const int MaxDepth = 6;

    private readonly TelemetryCollector _collector;
    private Layout _layout;
    private bool _hasLayout;
    private TelemetryChannelInfo? _gps, _alt, _camera, _drone, _gimbal, _raw, _serial, _model, _frameInfo;

    private double _lat, _lon, _altAbs, _altRel, _iso, _ct;
    private double _dRoll, _dPitch, _dYaw, _gPitch, _gRoll, _gYaw;
    private double _units;
    private double _frame;

    public DjmdDecoder(TelemetryCollector collector) => _collector = collector;

    // Paths are packed one byte per field number (the numbers DJI uses are below 256), first level in the highest byte used.
    internal static ulong P(int a, int b = 0, int c = 0, int d = 0, int e = 0) => Append(Append(Append(Append(Append(0, a), b), c), d), e);

    internal static ulong Append(ulong path, int number) => number == 0 ? path : (path << 8) | (uint)number;

    internal readonly record struct Layout(ulong Gps, ulong Alt, ulong Rel, ulong Drone, ulong Gimbal, ulong Iso, ulong Ct, ulong Quat);

    internal static readonly ulong Protocol = P(1, 1, 1);
    internal static readonly ulong Serial = P(1, 1, 5);
    internal static readonly ulong Model = P(1, 1, 10);
    private static readonly ulong Frame = P(3, 1, 1);

    // The paths the tag documentation lists per protocol, grouped by family. Mavic 3, Air 3, Mini 4 Pro, Mavic 4, Matrice 30/4E share one
    // arrangement of the GPS, altitude, drone and gimbal messages; they differ in where ISO and colour temperature sit.
    internal static bool TryLayout(ReadOnlySpan<byte> name, out Layout layout)
    {
        ulong gps = P(3, 3, 4, 1), alt = P(3, 3, 4, 2), rel = P(3, 3, 5, 1), drone = P(3, 3, 3), gimbal = P(3, 4, 3);
        if (name.SequenceEqual("wm265e"u8) || name.SequenceEqual("pm320"u8))
        {
            layout = new Layout(gps, alt, rel, drone, gimbal, P(3, 2, 2, 1), 0, 0);
        }
        else if (name.SequenceEqual("wm260"u8) || name.SequenceEqual("wm261"u8) || name.SequenceEqual("Mavic4"u8))
        {
            layout = new Layout(gps, alt, rel, drone, gimbal, P(3, 2, 9, 1), name.SequenceEqual("Mavic4"u8) ? P(3, 2, 24, 1) : 0, 0);
        }
        else if (name.SequenceEqual("Air3"u8) || name.SequenceEqual("Air3s"u8) || name.SequenceEqual("Lito_X1"u8) || name.SequenceEqual("Mini4_Pro"u8))
        {
            layout = new Layout(gps, alt, rel, drone, gimbal, P(3, 2, 7, 1), P(3, 2, 32, 1), 0);
        }
        else if (name.SequenceEqual("wa345e"u8))
        {
            layout = new Layout(gps, alt, rel, drone, gimbal, 0, 0, 0);
        }
        else if (name.SequenceEqual("AVATA2"u8) || name.SequenceEqual("NEO2"u8) || name.SequenceEqual("dji_neo"u8))
        {
            layout = new Layout(P(3, 4, 4, 1), P(3, 4, 4, 2), name.SequenceEqual("AVATA2"u8) ? P(3, 4, 5, 1) : 0, P(3, 4, 3), 0, P(3, 2, 2, 1), P(3, 2, 6, 1), 0);
        }
        else if (name.SequenceEqual("ac203"u8) || name.SequenceEqual("ac204"u8) || name.SequenceEqual("ac206"u8) || name.SequenceEqual("oq101"u8))
        {
            layout = new Layout(P(3, 4, 2, 1), P(3, 4, 2, 2), 0, 0, 0, P(3, 2, 3, 1), P(3, 2, 6, 1), P(3, 2, 9));
        }
        else
        {
            layout = default;
            return false;
        }

        return true;
    }

    /// <summary>Decodes one <c>djmd</c> sample: the mapped fields become items, and the whole message stays available as a raw item.</summary>
    public void Decode(ReadOnlySpan<byte> packet, long fileOffset, MediaTime pts, MediaTime duration)
    {
        _lat = _lon = _altAbs = _altRel = _iso = _ct = _dRoll = _dPitch = _dYaw = _gPitch = _gRoll = _gYaw = _frame = double.NaN;
        _units = 1;
        WalkMessage(packet, 0, 0, 0, fileOffset, pts, duration);
        ByteRange range = fileOffset >= 0 ? new ByteRange(fileOffset, packet.Length) : ByteRange.None;
        if (_hasLayout)
        {
            Emit(pts, duration, range);
        }

        // Everything, mapped or not, stays reachable: the message as stored (ProtobufReader walks it).
        ref TelemetryItem raw = ref _collector.Add(_raw ??= _collector.Create(TelemetryFormat.Djmd, TelemetryKind.Unknown, "djmd message", 0, 1, 0));
        raw.Time = pts;
        raw.Duration = duration;
        raw.Range = range;
        raw.RawOffset = 0;
        raw.RawLength = packet.Length;
        raw.TypeCode = 'm';
    }

    private void WalkMessage(ReadOnlySpan<byte> message, ulong path, int depth, int baseOffset, long fileOffset, MediaTime pts, MediaTime duration)
    {
        ProtobufReader r = new(message);
        while (r.TryRead(out int number, out ProtobufWireType wt, out ulong v, out ReadOnlySpan<byte> payload, out _))
        {
            if (number > 255)
            {
                continue;
            }

            ulong key = Append(path, number);
            switch (wt)
            {
                case ProtobufWireType.LengthDelimited:
                    int payloadAt = baseOffset + r.Position - payload.Length;
                    if (key == Protocol)
                    {
                        SetProtocol(payload);
                    }
                    else if (key == Serial || key == Model)
                    {
                        // Personal data (the aircraft's serial number): delivered as raw items that keep their byte range for a strip.
                        ref TelemetryItem s = ref _collector.Add(key == Serial ? _serial ??= _collector.Create(TelemetryFormat.Djmd, TelemetryKind.Unknown, "serial number", 0, 2, 0)
                            : _model ??= _collector.Create(TelemetryFormat.Djmd, TelemetryKind.Unknown, "model", 0, 3, 0));
                        s.Time = pts;
                        s.Duration = duration;
                        s.Range = fileOffset >= 0 ? new ByteRange(fileOffset + payloadAt, payload.Length) : ByteRange.None;
                        s.RawOffset = payloadAt;
                        s.RawLength = payload.Length;
                        s.TypeCode = 's';
                    }
                    else if (depth < MaxDepth && IsPrefix(key))
                    {
                        WalkMessage(payload, key, depth + 1, payloadAt, fileOffset, pts, duration);
                    }

                    break;
                default:
                    Leaf(key, wt, v);
                    break;
            }
        }
    }

    private void SetProtocol(ReadOnlySpan<byte> text)
    {
        // "dvtm_wm265e.proto" -> "wm265e"
        ReadOnlySpan<byte> n = text;
        if (n.StartsWith("dvtm_"u8))
        {
            n = n[5..];
        }

        int dot = n.IndexOf((byte)'.');
        if (dot >= 0)
        {
            n = n[..dot];
        }

        _hasLayout = TryLayout(n, out _layout);
    }

    private bool IsPrefix(ulong key)
    {
        if (!_hasLayout)
        {
            return key is 1 or 0x0101;
        }

        return IsPrefixOf(key, _layout.Gps) || IsPrefixOf(key, _layout.Alt) || IsPrefixOf(key, _layout.Rel) || IsPrefixOf(key, _layout.Drone) || IsPrefixOf(key, _layout.Gimbal)
            || IsPrefixOf(key, _layout.Iso) || IsPrefixOf(key, _layout.Ct) || IsPrefixOf(key, _layout.Quat) || IsPrefixOf(key, Frame) || key == 1 || key == 0x0101;
    }

    internal static bool IsPrefixOf(ulong prefix, ulong full)
    {
        if (full == 0)
        {
            return false;
        }

        while (full > prefix)
        {
            full >>= 8;
        }

        return full == prefix;
    }

    private void Leaf(ulong key, ProtobufWireType wt, ulong v)
    {
        if (!_hasLayout)
        {
            return;
        }

        if (key == Frame && wt == ProtobufWireType.Varint)
        {
            _frame = v;
        }
        else if (key == _layout.Iso && wt == ProtobufWireType.Varint)
        {
            _iso = v;
        }
        else if (_layout.Ct != 0 && key == _layout.Ct && wt == ProtobufWireType.Varint)
        {
            _ct = v;
        }
        else if (key == Append(_layout.Gps, 1) && wt == ProtobufWireType.Varint)
        {
            _units = v;
        }
        else if (key == Append(_layout.Gps, 2) && wt == ProtobufWireType.Fixed64)
        {
            _lat = BitConverter.UInt64BitsToDouble(v);
        }
        else if (key == Append(_layout.Gps, 3) && wt == ProtobufWireType.Fixed64)
        {
            _lon = BitConverter.UInt64BitsToDouble(v);
        }
        else if (key == _layout.Alt && wt == ProtobufWireType.Varint)
        {
            _altAbs = (long)v / 1000.0;
        }
        else if (_layout.Rel != 0 && key == _layout.Rel)
        {
            _altRel = wt == ProtobufWireType.Fixed32 ? BitConverter.UInt32BitsToSingle((uint)v) / 1000.0 : wt == ProtobufWireType.Varint ? (long)v / 1000.0 : double.NaN;
        }
        else if (_layout.Drone != 0 && wt == ProtobufWireType.Varint && Tenths(key, _layout.Drone, out int dIndex, out double dValue))
        {
            ref double target = ref dIndex == 1 ? ref _dRoll : ref dIndex == 2 ? ref _dPitch : ref _dYaw;
            target = dValue;
        }
        else if (_layout.Gimbal != 0 && wt == ProtobufWireType.Varint && Tenths(key, _layout.Gimbal, out int gIndex, out double gValue))
        {
            ref double target = ref gIndex == 1 ? ref _gPitch : ref gIndex == 2 ? ref _gRoll : ref _gYaw;
            target = gValue;
        }
        else
        {
            // Not mapped here (shutter, f-number, quaternions, accelerometer, GPS time): they remain in the raw message item.
        }

        bool Tenths(ulong k, ulong parent, out int index, out double value)
        {
            index = 0;
            value = 0;
            for (int i = 1; i <= 3; i++)
            {
                if (k == Append(parent, i))
                {
                    index = i;
                    value = (long)v / 10.0;
                    return true;
                }
            }

            return false;
        }
    }

    private void Emit(MediaTime pts, MediaTime duration, ByteRange range)
    {
        if (!double.IsNaN(_lat) && !double.IsNaN(_lon))
        {
            bool radians = _units == 0;
            ref TelemetryItem g = ref Item(ref _gps, TelemetryKind.Gps, "djmd gps", 4, pts, duration, range);
            Span<double> w = g.Writable;
            w[0] = radians ? _lat * (180 / Math.PI) : _lat;
            w[1] = radians ? _lon * (180 / Math.PI) : _lon;
            w[2] = _altAbs;
            w[3] = w[4] = w[5] = double.NaN;
            w[6] = 0;
            g.Count = 7;
        }

        if (!double.IsNaN(_altAbs) || !double.IsNaN(_altRel))
        {
            ref TelemetryItem a = ref Item(ref _alt, TelemetryKind.Scalar, "djmd altitude", 5, pts, duration, range);
            a.Writable[0] = _altRel;
            a.Writable[1] = _altAbs;
            a.Count = 2;
        }

        if (!double.IsNaN(_iso) || !double.IsNaN(_ct))
        {
            ref TelemetryItem c = ref Item(ref _camera, TelemetryKind.CameraSettings, "djmd camera", 6, pts, duration, range);
            Span<double> w = c.Writable;
            (w[0], w[1], w[2], w[3], w[4], w[5]) = (_iso, double.NaN, double.NaN, double.NaN, _ct, double.NaN);
            c.Count = 6;
        }

        if (!double.IsNaN(_dRoll) || !double.IsNaN(_dPitch) || !double.IsNaN(_dYaw))
        {
            ref TelemetryItem o = ref Item(ref _drone, TelemetryKind.Orientation, "djmd aircraft", 7, pts, duration, range);
            (o.Writable[0], o.Writable[1], o.Writable[2]) = (_dYaw, _dPitch, _dRoll);
            o.Count = 3;
        }

        if (!double.IsNaN(_gPitch) || !double.IsNaN(_gRoll) || !double.IsNaN(_gYaw))
        {
            ref TelemetryItem o = ref Item(ref _gimbal, TelemetryKind.Orientation, "djmd gimbal", 8, pts, duration, range);
            (o.Writable[0], o.Writable[1], o.Writable[2]) = (_gYaw, _gPitch, _gRoll);
            o.Count = 3;
        }

        if (!double.IsNaN(_frame))
        {
            ref TelemetryItem f = ref Item(ref _frameInfo, TelemetryKind.Scalar, "djmd frame", 9, pts, duration, range);
            f.Writable[0] = _frame;
            f.Count = 1;
        }
    }

    private ref TelemetryItem Item(ref TelemetryChannelInfo? slot, TelemetryKind kind, string name, uint key, MediaTime pts, MediaTime duration, ByteRange range)
    {
        slot ??= _collector.Create(TelemetryFormat.Djmd, kind, name, 0, key, 0);
        ref TelemetryItem item = ref _collector.Add(slot);
        item.Time = pts;
        item.Duration = duration;
        item.Range = range;
        return ref item;
    }
}

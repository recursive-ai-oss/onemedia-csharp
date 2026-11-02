using System.Buffers.Text;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

/// <summary>
/// Reads the per-frame text DJI drones write into subtitle tracks and <c>.SRT</c> sidecars. Every layout seen in published descriptions is
/// handled by one tolerant scan: bracketed <c>[key: value]</c> groups (several pairs may share a group), the older <c>GPS(lat,lon,alt)</c>
/// and <c>BAROMETER</c> forms, the compact <c>F/5.6, SS 400, ISO 100, EV 0</c> line, <c>F.PRY</c>/<c>G.PRY</c> attitude triples, the
/// counter line (<c>SrtCnt</c>/<c>FrameCnt</c> and <c>DiffTime</c>) and the clock line (<c>2024-01-15 14:30:22,123</c>). Unknown keys are
/// skipped. One text block is one sample; it yields up to six items, none allocated per call.
/// </summary>
/// <remarks>
/// Component order of the items (documented in <c>docs/telemetry.md</c>): GPS = latitude, longitude, absolute altitude, then NaN for speeds
/// and DOP; relative altitude = relative, absolute; camera = ISO, shutter seconds, f-number, EV, colour temperature, focal length;
/// attitude = yaw, pitch, roll in degrees; clock = year, month, day, hour, minute, second, millisecond, frame counter, DiffTime ms.
/// </remarks>
internal sealed class DjiTextDecoder
{
    private readonly TelemetryCollector _collector;
    private TelemetryChannelInfo? _gps;
    private TelemetryChannelInfo? _relAlt;
    private TelemetryChannelInfo? _camera;
    private TelemetryChannelInfo? _gimbal;
    private TelemetryChannelInfo? _aircraft;
    private TelemetryChannelInfo? _clock;
    private TelemetryChannelInfo? _baro;

    private double _lat, _lon, _rel, _abs, _iso, _shutter, _fnum, _ev, _ct, _focal, _baroValue;
    private double _gbYaw, _gbPitch, _gbRoll, _acYaw, _acPitch, _acRoll;
    private double _counter, _diff;
    private readonly double[] _list = new double[3];
    private int _year, _month, _day, _hour, _minute, _second, _milli;

    public DjiTextDecoder(TelemetryCollector collector) => _collector = collector;

    /// <summary>True when the text looks like DJI telemetry: it has one of the keys or forms the decoder reads.</summary>
    public static bool Looks(ReadOnlySpan<byte> text) =>
        text.IndexOf("latitude"u8) >= 0 || text.IndexOf("GPS("u8) >= 0 || text.IndexOf("GPS ("u8) >= 0 || text.IndexOf("SrtCnt"u8) >= 0 || text.IndexOf("FrameCnt"u8) >= 0
        || text.IndexOf("[iso"u8) >= 0;

    /// <summary>Decodes one text sample (with or without the two-byte length a <c>tx3g</c> sample starts with).</summary>
    /// <param name="sample">The sample bytes.</param>
    /// <param name="fileOffset">Offset of the sample in the file, or -1.</param>
    /// <param name="pts">Presentation time.</param>
    /// <param name="duration">Duration.</param>
    public void Decode(ReadOnlySpan<byte> sample, long fileOffset, MediaTime pts, MediaTime duration)
    {
        ReadOnlySpan<byte> text = StripLength(sample);
        ResetFields();
        ScanText(text);
        long length = sample.Length;
        ByteRange range = fileOffset >= 0 ? new ByteRange(fileOffset, length) : ByteRange.None;
        Emit(pts, duration, range);
    }

    // 3GPP TS 26.245 §5.17: a tx3g sample starts with a 16-bit text length. Plain text (a sidecar block) never starts with a zero byte.
    private static ReadOnlySpan<byte> StripLength(ReadOnlySpan<byte> sample)
    {
        if (sample.Length >= 2 && sample[0] == 0)
        {
            int len = (sample[0] << 8) | sample[1];
            return sample.Slice(2, Math.Min(len, sample.Length - 2));
        }

        return sample;
    }

    private void ResetFields()
    {
        _lat = _lon = _rel = _abs = _iso = _shutter = _fnum = _ev = _ct = _focal = _baroValue = double.NaN;
        _gbYaw = _gbPitch = _gbRoll = _acYaw = _acPitch = _acRoll = double.NaN;
        _counter = _diff = double.NaN;
        _year = 0;
    }

    private void ScanText(ReadOnlySpan<byte> t)
    {
        int i = 0;
        while (i < t.Length)
        {
            byte c = t[i];
            if (c == (byte)'[')
            {
                int end = t[i..].IndexOf((byte)']');
                int stop = end < 0 ? t.Length : i + end;
                ScanBracket(t[(i + 1)..stop]);
                i = stop + 1;
                continue;
            }

            bool boundary = i == 0 || !IsWordByte(t[i - 1]);
            if (boundary)
            {
                int used = TryWord(t, i);
                if (used > 0)
                {
                    i += used;
                    continue;
                }
            }

            i++;
        }
    }

    private static bool IsWordByte(byte b) => (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || b == (byte)'_' || b == (byte)'.';

    // Reads "key: value" pairs inside one bracket group: "iso : 100", "rel_alt: 1.300 abs_alt: 132.860", "ct: 5562, tint: 0".
    private void ScanBracket(ReadOnlySpan<byte> g)
    {
        int i = 0;
        while (i < g.Length)
        {
            while (i < g.Length && (g[i] == (byte)' ' || g[i] == (byte)',' || g[i] == (byte)'\t'))
            {
                i++;
            }

            int keyStart = i;
            while (i < g.Length && g[i] != (byte)':' && g[i] != (byte)' ')
            {
                i++;
            }

            ReadOnlySpan<byte> key = g[keyStart..i];
            while (i < g.Length && g[i] == (byte)' ')
            {
                i++;
            }

            if (i >= g.Length || g[i] != (byte)':')
            {
                // A key without a colon ("color_md" never lacks one; this is a stray word): move on.
                continue;
            }

            i++;
            while (i < g.Length && g[i] == (byte)' ')
            {
                i++;
            }

            int valueStart = i;
            while (i < g.Length && g[i] != (byte)' ' && g[i] != (byte)',')
            {
                i++;
            }

            Assign(key, g[valueStart..i]);
        }
    }

    private void Assign(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.SequenceEqual("latitude"u8))
        {
            _lat = Num(value);
        }
        else if (key.SequenceEqual("longitude"u8) || key.SequenceEqual("longtitude"u8))
        {
            _lon = Num(value);
        }
        else if (key.SequenceEqual("rel_alt"u8))
        {
            _rel = Num(value);
        }
        else if (key.SequenceEqual("abs_alt"u8))
        {
            _abs = Num(value);
        }
        else if (key.SequenceEqual("iso"u8))
        {
            _iso = Num(value);
        }
        else if (key.SequenceEqual("shutter"u8))
        {
            _shutter = Shutter(value);
        }
        else if (key.SequenceEqual("fnum"u8))
        {
            // Older firmware writes the f-number times 100 as an integer ("280" is f/2.8), newer firmware the decimal itself ("1.8").
            _fnum = Scaled(value, 100);
        }
        else if (key.SequenceEqual("ev"u8))
        {
            _ev = Num(value);
        }
        else if (key.SequenceEqual("ct"u8))
        {
            _ct = Num(value);
        }
        else if (key.SequenceEqual("focal_len"u8))
        {
            // Older firmware: millimetres times 10 as an integer ("240"); newer firmware: the decimal ("24.00").
            _focal = Scaled(value, 10);
        }
        else if (key.SequenceEqual("gb_yaw"u8))
        {
            _gbYaw = Num(value);
        }
        else if (key.SequenceEqual("gb_pitch"u8))
        {
            _gbPitch = Num(value);
        }
        else if (key.SequenceEqual("gb_roll"u8))
        {
            _gbRoll = Num(value);
        }
    }

    // Handles the non-bracket forms at a word boundary; returns bytes consumed, or 0 when nothing matched.
    private int TryWord(ReadOnlySpan<byte> t, int i)
    {
        ReadOnlySpan<byte> r = t[i..];
        byte c = r[0];
        if (c >= (byte)'0' && c <= (byte)'9')
        {
            return TryClock(r);
        }

        if (StartsWith(r, "SrtCnt"u8) || StartsWith(r, "FrameCnt"u8))
        {
            int colon = r.IndexOf((byte)':');
            if (colon < 0)
            {
                return 0;
            }

            int p = colon + 1;
            _counter = NumAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "DiffTime"u8))
        {
            int colon = r.IndexOf((byte)':');
            if (colon < 0)
            {
                return 0;
            }

            int p = colon + 1;
            _diff = NumAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "GPS"u8))
        {
            return ListAfter(r, 3, out ReadOnlySpan<double> v) && v.Length >= 2 ? SetGps(v, r) : 0;
        }

        if (StartsWith(r, "BAROMETER"u8))
        {
            int p = 9;
            if (p < r.Length && (r[p] == (byte)'(' || r[p] == (byte)':'))
            {
                p++;
            }

            _baroValue = NumAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "F.PRY"u8) && ListAfter(r, 5, out ReadOnlySpan<double> f) && f.Length == 3)
        {
            (_acPitch, _acRoll, _acYaw) = (f[0], f[1], f[2]);
            return r.IndexOf((byte)')') + 1;
        }

        if (StartsWith(r, "G.PRY"u8) && ListAfter(r, 5, out ReadOnlySpan<double> g) && g.Length == 3)
        {
            (_gbPitch, _gbRoll, _gbYaw) = (g[0], g[1], g[2]);
            return r.IndexOf((byte)')') + 1;
        }

        if (StartsWith(r, "ISO "u8))
        {
            int p = 4;
            _iso = NumAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "SS "u8))
        {
            int p = 3;
            _shutter = ShutterAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "EV "u8))
        {
            int p = 3;
            _ev = NumAt(r, ref p);
            return p;
        }

        if (StartsWith(r, "F/"u8))
        {
            int p = 2;
            _fnum = NumAt(r, ref p);
            return p;
        }

        return 0;
    }

    private int SetGps(ReadOnlySpan<double> v, ReadOnlySpan<byte> r)
    {
        _lat = v[0];
        _lon = v[1];
        if (v.Length >= 3)
        {
            _abs = v[2];
        }

        return r.IndexOf((byte)')') + 1;
    }

    // "GPS(39.9,116.3,69.8M)" or "GPS (-58.8, -34.2, 15)": up to three numbers after the opening parenthesis; a unit letter after a number is ignored.
    private bool ListAfter(ReadOnlySpan<byte> r, int from, out ReadOnlySpan<double> values)
    {
        int p = from;
        while (p < r.Length && r[p] == (byte)' ')
        {
            p++;
        }

        if (p >= r.Length || r[p] != (byte)'(' || r.IndexOf((byte)')') < 0)
        {
            values = default;
            return false;
        }

        p++;
        double[] buf = _list;
        int n = 0;
        int close = r.IndexOf((byte)')');
        while (p < close && n < 3)
        {
            while (p < close && (r[p] == (byte)' ' || r[p] == (byte)','))
            {
                p++;
            }

            int s = p;
            while (p < close && r[p] != (byte)',' && r[p] != (byte)' ')
            {
                p++;
            }

            if (p == s)
            {
                break;
            }

            double d = Num(TrimUnit(r[s..p]));
            if (double.IsNaN(d))
            {
                values = default;
                return false;
            }

            buf[n++] = d;
        }

        values = buf.AsSpan(0, n);
        return n > 0;
    }

    private static ReadOnlySpan<byte> TrimUnit(ReadOnlySpan<byte> s)
    {
        // Drops trailing unit characters: letters, the degree sign (UTF-8 C2 B0) and "°".
        int e = s.Length;
        while (e > 0 && (s[e - 1] >= 0x80 || (s[e - 1] >= (byte)'A' && s[e - 1] <= (byte)'z')))
        {
            e--;
        }

        return s[..e];
    }

    // 2024-01-15 14:30:22,123 (comma or dot before the milliseconds, which may be absent).
    private int TryClock(ReadOnlySpan<byte> r)
    {
        if (_year != 0 || r.Length < 19 || r[4] != (byte)'-' || r[7] != (byte)'-' || r[10] != (byte)' ' || r[13] != (byte)':' || r[16] != (byte)':')
        {
            return 0;
        }

        if (!Digits(r, 0, 4, out int y) || !Digits(r, 5, 2, out int mo) || !Digits(r, 8, 2, out int d) || !Digits(r, 11, 2, out int h) || !Digits(r, 14, 2, out int mi)
            || !Digits(r, 17, 2, out int s) || mo is < 1 or > 12 || d is < 1 or > 31 || h > 23 || mi > 59 || s > 60)
        {
            return 0;
        }

        int used = 19;
        int ms = 0;
        if (r.Length > 20 && (r[19] == (byte)',' || r[19] == (byte)'.'))
        {
            int p = 20;
            int scale = 100;
            while (p < r.Length && r[p] >= (byte)'0' && r[p] <= (byte)'9' && scale > 0)
            {
                ms += (r[p] - (byte)'0') * scale;
                scale /= 10;
                p++;
            }

            used = p;
        }

        (_year, _month, _day, _hour, _minute, _second, _milli) = (y, mo, d, h, mi, s, ms);
        return used;
    }

    private static bool Digits(ReadOnlySpan<byte> r, int at, int count, out int value)
    {
        value = 0;
        for (int k = 0; k < count; k++)
        {
            byte b = r[at + k];
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }

            value = (value * 10) + (b - (byte)'0');
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> r, ReadOnlySpan<byte> prefix) => r.StartsWith(prefix);

    private static double Num(ReadOnlySpan<byte> s) => Utf8Parser.TryParse(s, out double d, out int used) && used == s.Length && double.IsFinite(d) ? d : double.NaN;

    private static double NumAt(ReadOnlySpan<byte> r, ref int p)
    {
        while (p < r.Length && r[p] == (byte)' ')
        {
            p++;
        }

        int s = p;
        while (p < r.Length && (r[p] == (byte)'-' || r[p] == (byte)'+' || r[p] == (byte)'.' || (r[p] >= (byte)'0' && r[p] <= (byte)'9')))
        {
            p++;
        }

        return Num(r[s..p]);
    }

    private static double Scaled(ReadOnlySpan<byte> value, int factor)
    {
        double d = Num(value);
        return !double.IsNaN(d) && value.IndexOf((byte)'.') < 0 ? d / factor : d;
    }

    // "1/1000", "1/30.0" or seconds ("2.0"); the result is in seconds.
    private static double Shutter(ReadOnlySpan<byte> v)
    {
        int slash = v.IndexOf((byte)'/');
        if (slash < 0)
        {
            return Num(v);
        }

        double n = Num(v[..slash]);
        double d = Num(v[(slash + 1)..]);
        return d > 0 ? n / d : double.NaN;
    }

    private static double ShutterAt(ReadOnlySpan<byte> r, ref int p)
    {
        int s = p;
        while (p < r.Length && (r[p] == (byte)'/' || r[p] == (byte)'.' || (r[p] >= (byte)'0' && r[p] <= (byte)'9')))
        {
            p++;
        }

        double v = Shutter(r[s..p]);
        // The compact line writes the denominator only ("SS 400" is 1/400 s).
        return !double.IsNaN(v) && r[s..p].IndexOf((byte)'/') < 0 && v >= 1 ? 1.0 / v : v;
    }

    private void Emit(MediaTime pts, MediaTime duration, ByteRange range)
    {
        if (!double.IsNaN(_lat) && !double.IsNaN(_lon))
        {
            ref TelemetryItem g = ref Item(ref _gps, TelemetryKind.Gps, "dji gps", pts, duration, range);
            Span<double> v = g.Writable;
            v[0] = _lat;
            v[1] = _lon;
            v[2] = _abs;
            v[3] = v[4] = v[5] = double.NaN;
            v[6] = 0;
            g.Count = 7;
        }

        if (!double.IsNaN(_rel) || !double.IsNaN(_abs))
        {
            ref TelemetryItem a = ref Item(ref _relAlt, TelemetryKind.Scalar, "dji altitude", pts, duration, range);
            a.Writable[0] = _rel;
            a.Writable[1] = _abs;
            a.Count = 2;
        }

        if (!double.IsNaN(_iso) || !double.IsNaN(_shutter) || !double.IsNaN(_fnum) || !double.IsNaN(_ev) || !double.IsNaN(_ct) || !double.IsNaN(_focal))
        {
            ref TelemetryItem c = ref Item(ref _camera, TelemetryKind.CameraSettings, "dji camera", pts, duration, range);
            Span<double> v = c.Writable;
            (v[0], v[1], v[2], v[3], v[4], v[5]) = (_iso, _shutter, _fnum, _ev, _ct, _focal);
            c.Count = 6;
        }

        if (!double.IsNaN(_gbYaw) || !double.IsNaN(_gbPitch) || !double.IsNaN(_gbRoll))
        {
            ref TelemetryItem o = ref Item(ref _gimbal, TelemetryKind.Orientation, "dji gimbal", pts, duration, range);
            (o.Writable[0], o.Writable[1], o.Writable[2]) = (_gbYaw, _gbPitch, _gbRoll);
            o.Count = 3;
        }

        if (!double.IsNaN(_acYaw) || !double.IsNaN(_acPitch) || !double.IsNaN(_acRoll))
        {
            ref TelemetryItem o = ref Item(ref _aircraft, TelemetryKind.Orientation, "dji aircraft", pts, duration, range);
            (o.Writable[0], o.Writable[1], o.Writable[2]) = (_acYaw, _acPitch, _acRoll);
            o.Count = 3;
        }

        if (!double.IsNaN(_baroValue))
        {
            ref TelemetryItem b = ref Item(ref _baro, TelemetryKind.Scalar, "dji barometer", pts, duration, range);
            b.Writable[0] = _baroValue;
            b.Count = 1;
        }

        if (_year != 0 || !double.IsNaN(_counter))
        {
            ref TelemetryItem k = ref Item(ref _clock, TelemetryKind.Scalar, "dji clock", pts, duration, range);
            Span<double> v = k.Writable;
            (v[0], v[1], v[2], v[3], v[4], v[5], v[6]) = (_year == 0 ? double.NaN : _year, _month, _day, _hour, _minute, _second, _milli);
            (v[7], v[8]) = (_counter, _diff);
            k.Count = 9;
        }
    }

    private ref TelemetryItem Item(ref TelemetryChannelInfo? slot, TelemetryKind kind, string name, MediaTime pts, MediaTime duration, ByteRange range)
    {
        slot ??= _collector.Create(TelemetryFormat.DjiText, kind, name, 0, (uint)_collector.Channels.Count, 0);
        ref TelemetryItem item = ref _collector.Add(slot);
        item.Time = pts;
        item.Duration = duration;
        item.Range = range;
        return ref item;
    }
}

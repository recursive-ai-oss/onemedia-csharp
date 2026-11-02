using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;

/// <summary>
/// Decodes the payloads of a GPMF track into telemetry items. One instance decodes the packets of one track in order. The structure follows
/// GoPro's published format description: <c>DEVC</c> devices hold <c>STRM</c> streams; inside a stream the modifiers <c>SCAL</c>, <c>TYPE</c>,
/// <c>SIUN</c> and <c>UNIT</c> apply to the data triplets that follow them, while <c>STNM</c>, <c>GPSU</c>, <c>GPSF</c>, <c>GPSP</c> and <c>ORIN</c> describe the
/// stream as a whole wherever they stand.
/// </summary>
internal sealed class GpmfDecoder
{
    private const int MaxDepth = 6;
    private const int MaxScales = 32;
    private const int MaxTypeChars = 64;

    private static readonly uint Devc = GpmfKlv.MakeKey("DEVC");
    private static readonly uint Dvid = GpmfKlv.MakeKey("DVID");
    private static readonly uint Dvnm = GpmfKlv.MakeKey("DVNM");
    private static readonly uint Strm = GpmfKlv.MakeKey("STRM");
    private static readonly uint Stnm = GpmfKlv.MakeKey("STNM");
    private static readonly uint Scal = GpmfKlv.MakeKey("SCAL");
    private static readonly uint Siun = GpmfKlv.MakeKey("SIUN");
    private static readonly uint Unit = GpmfKlv.MakeKey("UNIT");
    private static readonly uint TypeKey = GpmfKlv.MakeKey("TYPE");
    private static readonly uint Gpsu = GpmfKlv.MakeKey("GPSU");
    private static readonly uint Gpsf = GpmfKlv.MakeKey("GPSF");
    private static readonly uint Gpsp = GpmfKlv.MakeKey("GPSP");
    private static readonly uint Orin = GpmfKlv.MakeKey("ORIN");
    private static readonly uint Orio = GpmfKlv.MakeKey("ORIO");
    private static readonly uint Gps5 = GpmfKlv.MakeKey("GPS5");
    private static readonly uint Gps9 = GpmfKlv.MakeKey("GPS9");

    // Triplets inside a stream or device that describe or time the data rather than carry it.
    private static readonly HashSet<uint> Modifiers = new()
    {
        Stnm, Scal, Siun, Unit, TypeKey, Gpsu, Gpsf, Gpsp, Orin,
        GpmfKlv.MakeKey("ORIO"), GpmfKlv.MakeKey("MTRX"), GpmfKlv.MakeKey("TSMP"), GpmfKlv.MakeKey("STMP"), GpmfKlv.MakeKey("TMPC"),
        GpmfKlv.MakeKey("TICK"), GpmfKlv.MakeKey("TOCK"), GpmfKlv.MakeKey("TIMO"), GpmfKlv.MakeKey("EMPT"), GpmfKlv.MakeKey("RMRK"),
        Dvid, Dvnm,
    };

    private static readonly Dictionary<uint, TelemetryKind> Kinds = new()
    {
        [Gps5] = TelemetryKind.Gps,
        [Gps9] = TelemetryKind.Gps,
        [GpmfKlv.MakeKey("ACCL")] = TelemetryKind.Accelerometer,
        [GpmfKlv.MakeKey("GYRO")] = TelemetryKind.Gyroscope,
        [GpmfKlv.MakeKey("MAGN")] = TelemetryKind.Magnetometer,
        [GpmfKlv.MakeKey("GRAV")] = TelemetryKind.Gravity,
        [GpmfKlv.MakeKey("CORI")] = TelemetryKind.Orientation,
        [GpmfKlv.MakeKey("IORI")] = TelemetryKind.Orientation,
        [GpmfKlv.MakeKey("SHUT")] = TelemetryKind.CameraSettings,
        [GpmfKlv.MakeKey("ISOG")] = TelemetryKind.CameraSettings,
        [GpmfKlv.MakeKey("ISOE")] = TelemetryKind.CameraSettings,
        [GpmfKlv.MakeKey("WBAL")] = TelemetryKind.CameraSettings,
    };

    private readonly TelemetryCollector _collector;
    private readonly double[] _scales = new double[MaxScales];
    private readonly byte[] _typeChars = new byte[MaxTypeChars];
    private readonly List<(uint Device, uint Key)> _deviceFieldsSeen = [];
    private int _scaleCount;
    private int _typeCount;
    private (int Offset, int Length, int Size, int Repeat) _siun;
    private (int Offset, int Length, int Size, int Repeat) _unit;
    private long _gpsuTicks;
    private int _gpsFix;
    private int _gpsPrecision;
    private uint _deviceId;
    private string _deviceName = string.Empty;
    private long _fileBase;
    private MediaTime _pts;
    private MediaTime _duration;

    public GpmfDecoder(TelemetryCollector collector) => _collector = collector;

    /// <summary>Decodes one payload (one sample of the <c>gpmd</c> track).</summary>
    /// <param name="payload">The payload bytes.</param>
    /// <param name="fileOffset">Where the payload starts in the file, or -1.</param>
    /// <param name="pts">The sample's time.</param>
    /// <param name="duration">The sample's duration.</param>
    public void Decode(ReadOnlySpan<byte> payload, long fileOffset, MediaTime pts, MediaTime duration)
    {
        _fileBase = fileOffset;
        _pts = pts;
        _duration = duration;
        GpmfKlvReader r = new(payload);
        while (r.TryRead(out GpmfKlv klv))
        {
            if (klv.Key == Devc && klv.IsNested)
            {
                Device(payload, klv.DataOffset, klv.Data.Length);
            }
            else
            {
                DeviceField(payload, klv, 0);
            }
        }

        if (r.Truncated)
        {
            _collector.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Abs(0), "A GPMF payload ends inside a triplet.", "gpmf.truncated");
        }
    }

    private long Abs(int payloadOffset) => _fileBase >= 0 ? _fileBase + payloadOffset : 0;

    private ByteRange RangeOf(int payloadOffset, int length) => _fileBase >= 0 ? new ByteRange(_fileBase + payloadOffset, length) : ByteRange.None;

    private void Device(ReadOnlySpan<byte> payload, int offset, int length)
    {
        ReadOnlySpan<byte> body = payload.Slice(offset, length);
        _deviceId = 0;
        string name = _deviceName;
        GpmfKlvReader r = new(body);
        while (r.TryRead(out GpmfKlv klv))
        {
            if (klv.Key == Dvid && klv.Type == (byte)'L' && klv.Data.Length >= 4)
            {
                _deviceId = BinaryPrimitives.ReadUInt32BigEndian(klv.Data);
            }
            else if (klv.Key == Dvnm && klv.Type == (byte)'c')
            {
                ReadOnlySpan<byte> text = TrimNul(klv.Data);
                if (!Latin1Equals(text, name))
                {
                    name = Latin1(text);
                }
            }
        }

        _deviceName = name;
        r = new GpmfKlvReader(body);
        while (r.TryRead(out GpmfKlv klv))
        {
            if (klv.Key == Strm && klv.IsNested)
            {
                Stream(payload, offset + klv.DataOffset, klv.Data.Length);
            }
            else if (!Modifiers.Contains(klv.Key))
            {
                DeviceField(payload, klv, offset);
            }
        }

        if (r.Truncated)
        {
            _collector.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Abs(offset), "A GPMF device block ends inside a triplet.", "gpmf.truncated");
        }
    }

    // Device-level triplets that are not streams (camera model, serial number, settings): delivered once as raw items, so a reader can find
    // them (and a privacy strip can locate them) without every payload repeating them.
    private void DeviceField(ReadOnlySpan<byte> payload, in GpmfKlv klv, int baseOffset)
    {
        if (klv.Data.Length == 0 || _deviceFieldsSeen.Contains((_deviceId, klv.Key)))
        {
            return;
        }

        if (_deviceFieldsSeen.Count >= 4096)
        {
            return;
        }

        _deviceFieldsSeen.Add((_deviceId, klv.Key));
        TelemetryChannelInfo s = StreamFor(klv.Key, 1, TelemetryKind.Unknown, 0, payload);
        ref TelemetryItem item = ref _collector.Add(s);
        item.Time = _pts;
        item.Duration = _duration;
        item.Range = RangeOf(baseOffset + klv.DataOffset, klv.Data.Length);
        item.RawOffset = baseOffset + klv.DataOffset;
        item.RawLength = klv.Data.Length;
        item.TypeCode = klv.Type;
    }

    private void Stream(ReadOnlySpan<byte> payload, int offset, int length)
    {
        ReadOnlySpan<byte> body = payload.Slice(offset, length);
        (int Offset, int Length, int Size, int Repeat) stnm = (-1, 0, 0, 0);
        (int Offset, int Length, int Size, int Repeat) orin = (-1, 0, 0, 0);
        (int Offset, int Length, int Size, int Repeat) orio = (-1, 0, 0, 0);
        _gpsuTicks = 0;
        _gpsFix = -1;
        _gpsPrecision = -1;
        GpmfKlvReader r = new(body);
        while (r.TryRead(out GpmfKlv klv))
        {
            if (klv.Key == Stnm)
            {
                stnm = (offset + klv.DataOffset, klv.Data.Length, klv.StructSize, klv.Repeat);
            }
            else if (klv.Key == Orin)
            {
                orin = (offset + klv.DataOffset, klv.Data.Length, klv.StructSize, klv.Repeat);
            }
            else if (klv.Key == Orio)
            {
                orio = (offset + klv.DataOffset, klv.Data.Length, klv.StructSize, klv.Repeat);
            }
            else if (klv.Key == Gpsu && klv.Type == (byte)'U' && klv.Data.Length >= 16)
            {
                _gpsuTicks = ParseUtc(klv.Data);
            }
            else if (klv.Key == Gpsf && klv.Type == (byte)'L' && klv.Data.Length >= 4)
            {
                _gpsFix = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(klv.Data), int.MaxValue);
            }
            else if (klv.Key == Gpsp && klv.Type == (byte)'S' && klv.Data.Length >= 2)
            {
                _gpsPrecision = BinaryPrimitives.ReadUInt16BigEndian(klv.Data);
            }
        }

        _scaleCount = 0;
        _typeCount = 0;
        _siun = (-1, 0, 0, 0);
        _unit = (-1, 0, 0, 0);
        r = new GpmfKlvReader(body);
        while (r.TryRead(out GpmfKlv klv))
        {
            if (klv.Key == Scal)
            {
                ReadScales(klv);
            }
            else if (klv.Key == TypeKey && klv.Type == (byte)'c')
            {
                ReadTypeString(klv.Data);
            }
            else if (klv.Key == Siun)
            {
                _siun = (offset + klv.DataOffset, klv.Data.Length, klv.StructSize, klv.Repeat);
            }
            else if (klv.Key == Unit)
            {
                _unit = (offset + klv.DataOffset, klv.Data.Length, klv.StructSize, klv.Repeat);
            }
            else if (!Modifiers.Contains(klv.Key))
            {
                EmitData(payload, klv, offset, stnm, orin, orio, 0);
            }
        }

        if (r.Truncated)
        {
            _collector.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Abs(offset), "A GPMF stream ends inside a triplet.", "gpmf.truncated");
        }
    }

    private void ReadScales(in GpmfKlv klv)
    {
        _scaleCount = 0;
        int size = GpmfTypes.SizeOf(klv.Type);
        if (size == 0 || !GpmfTypes.IsNumeric(klv.Type))
        {
            return;
        }

        // SCAL holds one value per component (or a single value for all of them).
        int n = Math.Min(klv.Data.Length / size, MaxScales);
        for (int i = 0; i < n; i++)
        {
            _scales[i] = GpmfTypes.ReadNumber(klv.Type, klv.Data[(i * size)..]);
        }

        _scaleCount = n;
    }

    // TYPE spells the components of a '?' structure with type characters; "[n]" repeats the character before it.
    private void ReadTypeString(ReadOnlySpan<byte> text)
    {
        _typeCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            byte c = text[i];
            if (c == 0)
            {
                break;
            }

            if (c == (byte)'[')
            {
                int n = 0;
                int j = i + 1;
                while (j < text.Length && text[j] is >= (byte)'0' and <= (byte)'9' && n < 1000)
                {
                    n = (n * 10) + (text[j] - '0');
                    j++;
                }

                if (_typeCount > 0 && n > 1)
                {
                    byte last = _typeChars[_typeCount - 1];
                    for (int k = 1; k < n && _typeCount < MaxTypeChars; k++)
                    {
                        _typeChars[_typeCount++] = last;
                    }
                }

                i = j;
                continue;
            }

            if (_typeCount < MaxTypeChars)
            {
                _typeChars[_typeCount++] = c;
            }
        }
    }

    private void EmitData(ReadOnlySpan<byte> payload, in GpmfKlv klv, int baseOffset, (int Offset, int Length, int Size, int Repeat) stnm, (int Offset, int Length, int Size, int Repeat) orin,
        (int Offset, int Length, int Size, int Repeat) orio, int depth)
    {
        if (klv.IsNested)
        {
            // A nested data key (for example FACE blocks with their own TICK and TOCK) holds the samples inside it.
            if (depth >= MaxDepth)
            {
                _collector.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, Abs(baseOffset + klv.HeaderOffset), "GPMF data is nested too deeply.", "gpmf.depth");
                return;
            }

            GpmfKlvReader inner = new(klv.Data);
            int innerBase = baseOffset + klv.DataOffset;
            while (inner.TryRead(out GpmfKlv child))
            {
                if (!Modifiers.Contains(child.Key))
                {
                    EmitData(payload, child, innerBase, stnm, orin, orio, depth + 1);
                }
            }

            return;
        }

        if (klv.Repeat == 0 || klv.StructSize == 0)
        {
            return;
        }

        int dataOffset = baseOffset + klv.DataOffset;
        int comps = ComponentCount(klv);
        TelemetryKind kind = comps > 0 && Kinds.TryGetValue(klv.Key, out TelemetryKind k) ? k : comps > 0 ? TelemetryKind.Scalar : TelemetryKind.Unknown;
        if (comps > TelemetryItem.MaxComponents)
        {
            comps = 0;
            kind = TelemetryKind.Unknown;
        }

        TelemetryChannelInfo stream = StreamFor(klv.Key, 0, kind, comps, payload, stnm, orin, orio);
        if (comps == 0)
        {
            ref TelemetryItem raw = ref _collector.Add(stream);
            raw.Kind = TelemetryKind.Unknown; // the stream may be typed (an earlier payload gave this key numbers); this sample is only bytes.
            raw.Time = _pts;
            raw.Duration = _duration;
            raw.Range = RangeOf(dataOffset, klv.Data.Length);
            raw.RawOffset = dataOffset;
            raw.RawLength = klv.Data.Length;
            raw.TypeCode = klv.Type;
            return;
        }

        bool complex = klv.Type == (byte)'?';
        int typeSize = complex ? 0 : GpmfTypes.SizeOf(klv.Type);
        for (int i = 0; i < klv.Repeat; i++)
        {
            ref TelemetryItem item = ref _collector.Add(stream);
            ReadOnlySpan<byte> sample = klv.Data.Slice(i * klv.StructSize, klv.StructSize);
            TelemetryTime.Spread(_pts, _duration, i, klv.Repeat, out MediaTime start, out MediaTime each);
            item.Time = start;
            item.Duration = each;
            item.Range = RangeOf(dataOffset + (i * klv.StructSize), klv.StructSize);
            Span<double> v = item.Writable;
            int pos = 0;
            for (int c = 0; c < comps; c++)
            {
                byte t = complex ? _typeChars[c] : klv.Type;
                int size = complex ? GpmfTypes.SizeOf(t) : typeSize;
                double value = GpmfTypes.IsNumeric(t) || t == (byte)'F' ? GpmfTypes.ReadNumber(t, sample[pos..]) : double.NaN;
                if (t != (byte)'F' && _scaleCount > 0)
                {
                    double scale = _scales[c % _scaleCount];
                    if (scale != 0 && scale != 1)
                    {
                        value /= scale;
                    }
                }

                v[c] = value;
                pos += size;
            }

            item.Count = comps;
            if (kind == TelemetryKind.Gps)
            {
                FinishGps(ref item, klv.Key, i, klv.Repeat);
            }
        }
    }

    // Number of numeric components per sample, or 0 when the data is not numeric (text, ids, malformed structure).
    private int ComponentCount(in GpmfKlv klv)
    {
        if (klv.Type == (byte)'?')
        {
            if (_typeCount == 0)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < _typeCount; i++)
            {
                int s = GpmfTypes.SizeOf(_typeChars[i]);
                if (s == 0)
                {
                    return 0;
                }

                total += s;
            }

            if (total != klv.StructSize)
            {
                _collector.Defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, 0, "A GPMF TYPE string does not add up to the structure size.", "gpmf.type-size");
                return 0;
            }

            return _typeCount;
        }

        if (!GpmfTypes.IsNumeric(klv.Type))
        {
            return 0;
        }

        int size = GpmfTypes.SizeOf(klv.Type);
        return klv.StructSize % size == 0 ? klv.StructSize / size : 0;
    }

    private void FinishGps(ref TelemetryItem item, uint key, int index, int repeat)
    {
        Span<double> v = item.Writable;
        if (key == Gps9 && item.Count >= 9)
        {
            // GPS9: latitude, longitude, altitude, 2D speed, 3D speed, days since 2000-01-01, seconds since midnight, DOP, fix.
            double days = v[5];
            double secs = v[6];
            double dop = v[7];
            double fix = v[8];
            v[5] = dop;
            v[6] = fix;
            v[7] = days;
            v[8] = secs;
            item.Count = 9;
            item.GpsTimeTicks = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + (long)Math.Round(days * TimeSpan.TicksPerDay) + (long)Math.Round(secs * TimeSpan.TicksPerSecond);
            item.Fix = FixOf((int)fix);
        }
        else
        {
            // GPS5 carries five values; the dilution of precision and fix come from the stream's GPSP and GPSF.
            while (item.Count < 7)
            {
                v[item.Count++] = double.NaN;
            }

            CompleteGps5(ref item, index, repeat);
        }
    }

    private static GpsFixKind FixOf(int fix) => fix switch { 2 => GpsFixKind.Fix2D, 3 => GpsFixKind.Fix3D, _ => GpsFixKind.None };

    // GPS5 samples get the stream-wide fix, precision and UTC time (the time of the first sample; later ones follow the payload's even spread).
    private void CompleteGps5(ref TelemetryItem item, int index, int repeat)
    {
        Span<double> v = item.Writable;
        v[5] = _gpsPrecision >= 0 ? _gpsPrecision / 100.0 : double.NaN;
        v[6] = _gpsFix >= 0 ? _gpsFix : 0;
        item.Fix = FixOf(_gpsFix);
        if (_gpsuTicks > 0)
        {
            long offset = 0;
            if (_duration.IsKnown && repeat > 0)
            {
                offset = new MediaTime(_duration.Ticks * index, _duration.Timescale * repeat).ToTicks(TimeSpan.TicksPerSecond, MediaRounding.NearestAwayFromZero);
            }

            item.GpsTimeTicks = _gpsuTicks + offset;
        }
    }

    private TelemetryChannelInfo StreamFor(uint key, int variant, TelemetryKind kind, int comps, ReadOnlySpan<byte> payload,
        (int Offset, int Length, int Size, int Repeat) stnm = default, (int Offset, int Length, int Size, int Repeat) orin = default,
        (int Offset, int Length, int Size, int Repeat) orio = default)
    {
        TelemetryChannelInfo? s = _collector.Find(TelemetryFormat.Gpmf, _deviceId, key, variant);
        if (s is not null)
        {
            return s;
        }

        s = _collector.Create(TelemetryFormat.Gpmf, kind, GpmfKlv.KeyText(key), _deviceId, key, variant);
        s.DeviceId = _deviceId;
        s.DeviceName = _deviceName;
        if (stnm.Offset >= 0 && stnm.Length > 0 && stnm.Offset + stnm.Length <= payload.Length)
        {
            s.Label = Latin1(TrimNul(payload.Slice(stnm.Offset, stnm.Length)));
        }

        if (orin.Offset >= 0 && orin.Length > 0 && orin.Offset + orin.Length <= payload.Length)
        {
            s.AxisOrder = Latin1(TrimNul(payload.Slice(orin.Offset, orin.Length)));
        }

        if (orio.Offset >= 0 && orio.Length > 0 && orio.Offset + orio.Length <= payload.Length)
        {
            s.AxisOutputOrder = Latin1(TrimNul(payload.Slice(orio.Offset, orio.Length)));
        }

        string units = comps > 0 ? ComponentUnits(payload, comps) : string.Empty;
        if (units.Length > 0)
        {
            s.Units = units.Split('\n');
        }

        return s;
    }

    // Units come from SIUN (preferred) or UNIT: one string of Size characters per component, or a single string for all components.
    private string ComponentUnits(ReadOnlySpan<byte> payload, int comps)
    {
        (int Offset, int Length, int Size, int Repeat) u = _siun.Offset >= 0 ? _siun : _unit;
        if (u.Offset < 0 || u.Size == 0 || u.Repeat == 0 || u.Offset + u.Length > payload.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> bytes = payload.Slice(u.Offset, u.Length);
        int n = Math.Min(u.Repeat, comps);
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(Latin1(TrimNul(bytes.Slice(i * u.Size, u.Size))).TrimEnd());
        }

        return sb.ToString();
    }

    private static ReadOnlySpan<byte> TrimNul(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        while (n > 0 && s[n - 1] == 0)
        {
            n--;
        }

        return s[..n];
    }

    // GPMF text is ASCII; the units use single bytes 0xB0, 0xB2, 0xB3 and 0xB5 for the degree sign, squared, cubed and micro (ISO 8859-1).
    private static string Latin1(ReadOnlySpan<byte> s) => System.Text.Encoding.Latin1.GetString(s);

    private static bool Latin1Equals(ReadOnlySpan<byte> s, string text)
    {
        if (s.Length != text.Length)
        {
            return false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            if (text[i] != s[i])
            {
                return false;
            }
        }

        return true;
    }

    // GPSU is "yymmddhhmmss.sss" in UTC; the years are 20xx.
    private static long ParseUtc(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < 16; i++)
        {
            if (i == 12)
            {
                if (s[i] != (byte)'.')
                {
                    return 0;
                }
            }
            else if (s[i] is < (byte)'0' or > (byte)'9')
            {
                return 0;
            }
        }

        static int D2(ReadOnlySpan<byte> b, int at) => ((b[at] - '0') * 10) + (b[at + 1] - '0');
        int year = 2000 + D2(s, 0);
        int month = D2(s, 2);
        int day = D2(s, 4);
        int hour = D2(s, 6);
        int minute = D2(s, 8);
        int second = D2(s, 10);
        int millis = ((s[13] - '0') * 100) + ((s[14] - '0') * 10) + (s[15] - '0');
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return 0;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks + (millis * TimeSpan.TicksPerMillisecond);
    }
}

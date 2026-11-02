using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Camm;

/// <summary>The packet types of camera motion metadata (CAMM): the <c>type</c> field of each sample.</summary>
public enum CammPacketType
{
    /// <summary>Angle-axis orientation, three floats (radians).</summary>
    AngleAxis = 0,

    /// <summary>Pixel exposure time and rolling-shutter skew time, two int32 in nanoseconds.</summary>
    Exposure = 1,

    /// <summary>Gyroscope, three floats (radians per second).</summary>
    Gyroscope = 2,

    /// <summary>Accelerometer, three floats (metres per second squared).</summary>
    Accelerometer = 3,

    /// <summary>Position of the camera, three floats.</summary>
    Position = 4,

    /// <summary>Minimal GPS: latitude, longitude and altitude as doubles.</summary>
    GpsMinimal = 5,

    /// <summary>Full GPS: time, fix type, position, accuracies and velocity.</summary>
    GpsFull = 6,

    /// <summary>Magnetic field, three floats (microtesla).</summary>
    MagneticField = 7,
}

/// <summary>
/// Reads camera motion metadata (Google's CAMM format): each sample of a <c>camm</c> track is one packet holding a 16-bit reserved
/// field, a 16-bit type and the fields of that type, all little-endian. One packet is one telemetry sample.
/// </summary>
public static class CammPacket
{
    /// <summary>Size of the packet header in bytes.</summary>
    public const int HeaderSize = 4;

    /// <summary>Total size of a packet of a known type, header included, or 0 for an unknown type.</summary>
    /// <param name="type">The type field.</param>
    /// <returns>The size in bytes.</returns>
    public static int PacketSize(int type) => type switch
    {
        0 or 2 or 3 or 4 or 7 => HeaderSize + 12,
        1 => HeaderSize + 8,
        5 => HeaderSize + 24,
        6 => HeaderSize + 56,
        _ => 0,
    };

    // Whole seconds the GPS time scale runs ahead of UTC, from the date of each leap second (all inserted at 00:00:00 UTC on these days; GPS
    // time started level with UTC on 1980-01-06).
    private static readonly (int Year, int Month, int Day)[] LeapDays =
    [
        (1981, 7, 1), (1982, 7, 1), (1983, 7, 1), (1985, 7, 1), (1988, 1, 1), (1990, 1, 1), (1991, 1, 1), (1992, 7, 1), (1993, 7, 1),
        (1994, 7, 1), (1996, 1, 1), (1997, 7, 1), (1999, 1, 1), (2006, 1, 1), (2009, 1, 1), (2012, 7, 1), (2015, 7, 1), (2017, 1, 1),
    ];

    /// <summary>Converts a time in seconds since the GPS epoch (1980-01-06 00:00:00, no leap seconds) to UTC.</summary>
    /// <param name="gpsSeconds">Seconds on the GPS time scale.</param>
    /// <returns>UTC as <see cref="DateTime.Ticks"/>, or 0 when the value is not a plausible time.</returns>
    public static long GpsSecondsToUtcTicks(double gpsSeconds)
    {
        if (double.IsNaN(gpsSeconds) || gpsSeconds < 0 || gpsSeconds > 8e9)
        {
            return 0;
        }

        long epoch = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long ticks = epoch + (long)Math.Round(gpsSeconds * TimeSpan.TicksPerSecond);
        int leap = 0;
        for (int i = 0; i < LeapDays.Length; i++)
        {
            (int y, int m, int d) = LeapDays[i];
            long inserted = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).Ticks;
            if (ticks - ((i + 1) * TimeSpan.TicksPerSecond) >= inserted)
            {
                leap = i + 1;
            }
        }

        return ticks - (leap * TimeSpan.TicksPerSecond);
    }

    /// <summary>Reads the type field of a packet.</summary>
    /// <param name="packet">The packet bytes.</param>
    /// <param name="type">The type, when the result is true.</param>
    /// <returns>False when the packet is shorter than its header.</returns>
    public static bool TryReadType(ReadOnlySpan<byte> packet, out int type)
    {
        if (packet.Length < HeaderSize)
        {
            type = -1;
            return false;
        }

        type = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        return true;
    }
}

internal sealed class CammDecoder
{
    private readonly TelemetryCollector _collector;

    public CammDecoder(TelemetryCollector collector) => _collector = collector;

    public void Decode(ReadOnlySpan<byte> packet, long fileOffset, MediaTime pts, MediaTime duration)
    {
        if (!CammPacket.TryReadType(packet, out int type))
        {
            _collector.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Math.Max(fileOffset, 0), "A CAMM sample is shorter than its four-byte header.", "camm.short");
            return;
        }

        int want = CammPacket.PacketSize(type);
        ByteRange range = fileOffset >= 0 ? new ByteRange(fileOffset, packet.Length) : ByteRange.None;
        if (want == 0)
        {
            // A type from a later revision of the format: keep the bytes after the header.
            ref TelemetryItem raw = ref _collector.Add(StreamOf(type, TelemetryKind.Unknown));
            raw.Time = pts;
            raw.Duration = duration;
            raw.Range = range;
            raw.RawOffset = CammPacket.HeaderSize;
            raw.RawLength = packet.Length - CammPacket.HeaderSize;
            raw.TypeCode = type;
            return;
        }

        if (packet.Length < want)
        {
            _collector.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Math.Max(fileOffset, 0), $"A CAMM type {type} sample has {packet.Length} bytes; {want} are needed.", "camm.short");
            return;
        }

        if (packet.Length > want)
        {
            _collector.Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, Math.Max(fileOffset, 0) + want, $"A CAMM type {type} sample has {packet.Length - want} bytes after its fields.", "camm.trailing");
        }

        ReadOnlySpan<byte> b = packet[CammPacket.HeaderSize..];
        TelemetryKind kind = type switch
        {
            0 => TelemetryKind.Orientation,
            1 => TelemetryKind.CameraSettings,
            2 => TelemetryKind.Gyroscope,
            3 => TelemetryKind.Accelerometer,
            4 => TelemetryKind.Scalar,
            5 or 6 => TelemetryKind.Gps,
            _ => TelemetryKind.Magnetometer,
        };
        ref TelemetryItem item = ref _collector.Add(StreamOf(type, kind));
        item.Time = pts;
        item.Duration = duration;
        item.Range = range;
        Span<double> v = item.Writable;
        switch (type)
        {
            case 0:
                // The three values are the rotation axis scaled by the angle in radians; the quaternion is derived, the raw vector kept after it.
                double x = F32(b, 0), y = F32(b, 4), z = F32(b, 8);
                double angle = Math.Sqrt((x * x) + (y * y) + (z * z));
                double half = angle / 2;
                double k = angle > 1e-12 ? Math.Sin(half) / angle : 0.5;
                v[0] = Math.Cos(half);
                v[1] = x * k;
                v[2] = y * k;
                v[3] = z * k;
                v[4] = x;
                v[5] = y;
                v[6] = z;
                item.Count = 7;
                break;
            case 1:
                v[0] = BinaryPrimitives.ReadInt32LittleEndian(b);
                v[1] = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
                item.Count = 2;
                break;
            case 5:
                v[0] = BinaryPrimitives.ReadDoubleLittleEndian(b);
                v[1] = BinaryPrimitives.ReadDoubleLittleEndian(b[8..]);
                v[2] = BinaryPrimitives.ReadDoubleLittleEndian(b[16..]);
                v[3] = double.NaN;
                v[4] = double.NaN;
                v[5] = double.NaN;
                v[6] = 0;
                item.Count = 7;
                break;
            case 6:
                double gpsSeconds = BinaryPrimitives.ReadDoubleLittleEndian(b);
                int fix = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
                v[0] = BinaryPrimitives.ReadDoubleLittleEndian(b[12..]);
                v[1] = BinaryPrimitives.ReadDoubleLittleEndian(b[20..]);
                v[2] = F32(b, 28);
                double ve = F32(b, 40), vn = F32(b, 44), vu = F32(b, 48);
                v[3] = Math.Sqrt((ve * ve) + (vn * vn));
                v[4] = Math.Sqrt((ve * ve) + (vn * vn) + (vu * vu));
                v[5] = double.NaN;
                v[6] = fix;
                v[7] = F32(b, 32);
                v[8] = F32(b, 36);
                v[9] = ve;
                v[10] = vn;
                v[11] = vu;
                v[12] = F32(b, 52);
                v[13] = gpsSeconds;
                item.Count = 14;
                item.Fix = fix switch { 2 => GpsFixKind.Fix2D, 3 => GpsFixKind.Fix3D, _ => GpsFixKind.None };
                item.GpsTimeTicks = CammPacket.GpsSecondsToUtcTicks(gpsSeconds);
                break;
            default:
                v[0] = F32(b, 0);
                v[1] = F32(b, 4);
                v[2] = F32(b, 8);
                item.Count = 3;
                break;
        }
    }

    private static double F32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b[at..]);

    private TelemetryChannelInfo StreamOf(int type, TelemetryKind kind)
    {
        TelemetryChannelInfo? s = _collector.Find(TelemetryFormat.Camm, 0, (uint)type, 0);
        if (s is not null)
        {
            return s;
        }

        s = _collector.Create(TelemetryFormat.Camm, kind, "camm type " + type.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, (uint)type, 0);
        s.Label = type switch
        {
            0 => "angle axis",
            1 => "exposure",
            2 => "gyroscope",
            3 => "accelerometer",
            4 => "position",
            5 => "GPS minimal",
            6 => "GPS full",
            7 => "magnetic field",
            _ => string.Empty,
        };
        s.Units = type switch
        {
            0 => ["rad", "rad", "rad"],
            1 => ["ns", "ns"],
            2 => ["rad/s", "rad/s", "rad/s"],
            3 => ["m/s²", "m/s²", "m/s²"],
            5 or 6 => ["deg", "deg", "m"],
            7 => ["µT", "µT", "µT"],
            _ => [],
        };
        return s;
    }
}

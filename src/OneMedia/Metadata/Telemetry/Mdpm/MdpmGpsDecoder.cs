using System.Buffers.Binary;

using AvchdMdpm = RecursiveAI.OneMedia.Avchd.Mdpm;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Mdpm;

/// <summary>
/// Reads the GPS entries of AVCHD camera data (MDPM) from the SEI messages inside the access units of an H.264 video track. A camera
/// repeats the block about once per group of pictures; each block with a latitude and longitude becomes one GPS item timed at the
/// picture it precedes. Entry layout as listed in the tag documentation for the format (tags 0xB0 to 0xCC, rationals as two big-endian
/// 16-bit halves, references as single ASCII characters) and confirmed by reading synthetic streams back with ExifTool.
/// </summary>
internal sealed class MdpmGpsDecoder
{
    private readonly TelemetryCollector _collector;
    private readonly byte[] _scratch = new byte[4096];
    private TelemetryChannelInfo? _gps;

    public MdpmGpsDecoder(TelemetryCollector collector) => _collector = collector;

    public void Decode(ReadOnlySpan<byte> packet, long fileOffset, MediaTime pts, MediaTime duration)
    {
        ReadOnlySpan<byte> startCode = [0, 0, 1];
        int pos = 0;
        while (pos < packet.Length)
        {
            int at = packet[pos..].IndexOf(startCode);
            if (at < 0)
            {
                return;
            }

            int nalStart = pos + at + 3;
            int next = nalStart < packet.Length ? packet[nalStart..].IndexOf(startCode) : -1;
            int nalEnd = next < 0 ? packet.Length : nalStart + next;
            pos = nalEnd;
            if (nalStart >= nalEnd)
            {
                continue;
            }

            ReadOnlySpan<byte> nal = packet[nalStart..nalEnd];
            if ((nal[0] & 0x1F) != 6)
            {
                continue;
            }

            // A 4-byte start code leaves a zero at the end of the previous unit; trailing zero bytes are not part of a NAL unit (H.264 Annex B).
            int len = nal.Length;
            while (len > 1 && nal[len - 1] == 0)
            {
                len--;
            }

            if (AvchdMdpm.TryFindEntries(nal[1..len], _scratch, out ReadOnlySpan<byte> entries))
            {
                EmitGps(entries, fileOffset, packet.Length, pts, duration);
            }
        }
    }

    private void EmitGps(ReadOnlySpan<byte> e, long fileOffset, int packetLength, MediaTime pts, MediaTime duration)
    {
        double latD = double.NaN, latM = 0, latS = 0, lonD = double.NaN, lonM = 0, lonS = 0, alt = double.NaN, dop = double.NaN, speed = double.NaN;
        double h = double.NaN, m = 0, s = 0;
        char latRef = 'N', lonRef = 'E', altRef = '\0', status = '\0', mode = '\0', speedRef = 'K';
        int digitCount = 0;
        Span<char> digits = stackalloc char[16]; // the three date entries hold at most 4 characters each
        for (int i = 0; i + 5 <= e.Length; i += 5)
        {
            ReadOnlySpan<byte> d = e.Slice(i + 1, 4);
            switch (e[i])
            {
                case 0xB1: latRef = (char)d[0]; break;
                case 0xB2: latD = Rational(d); break;
                case 0xB3: latM = Rational(d); break;
                case 0xB4: latS = Rational(d); break;
                case 0xB5: lonRef = (char)d[0]; break;
                case 0xB6: lonD = Rational(d); break;
                case 0xB7: lonM = Rational(d); break;
                case 0xB8: lonS = Rational(d); break;
                case 0xB9: altRef = d[0] == 1 ? 'B' : 'A'; break;
                case 0xBA: alt = Rational(d); break;
                case 0xBB: h = Rational(d); break;
                case 0xBC: m = Rational(d); break;
                case 0xBD: s = Rational(d); break;
                case 0xBE: status = (char)d[0]; break;
                case 0xBF: mode = (char)d[0]; break;
                case 0xC0: dop = Rational(d); break;
                case 0xC1: speedRef = (char)d[0]; break;
                case 0xC2: speed = Rational(d); break;
                case 0xCA or 0xCB or 0xCC:
                    foreach (byte b in d)
                    {
                        if (b >= (byte)'0' && b <= (byte)'9' && digitCount < digits.Length)
                        {
                            digits[digitCount++] = (char)b;
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        if (double.IsNaN(latD) || double.IsNaN(lonD))
        {
            return;
        }

        double lat = latD + (latM / 60) + (latS / 3600);
        double lon = lonD + (lonM / 60) + (lonS / 3600);
        if (latRef == 'S')
        {
            lat = -lat;
        }

        if (lonRef == 'W')
        {
            lon = -lon;
        }

        if (altRef == 'B' && !double.IsNaN(alt))
        {
            alt = -alt;
        }

        // EXIF GPS speed units: K km/h, M mph, N knots; the item holds metres per second.
        double mps = speedRef switch { 'K' => speed / 3.6, 'M' => speed * 0.44704, 'N' => speed * 1852.0 / 3600.0, _ => double.NaN };

        _gps ??= _collector.Create(TelemetryFormat.Mdpm, TelemetryKind.Gps, "mdpm gps", 0, 0, 0);
        ref TelemetryItem item = ref _collector.Add(_gps);
        item.Time = pts;
        item.Duration = duration;
        item.Range = fileOffset >= 0 ? new ByteRange(fileOffset, packetLength) : ByteRange.None;
        Span<double> v = item.Writable;
        v[0] = lat;
        v[1] = lon;
        v[2] = alt;
        v[3] = mps;
        v[4] = double.NaN;
        v[5] = dop;
        v[6] = mode == '3' ? 3 : mode == '2' ? 2 : 0;
        item.Count = 7;
        item.Fix = status == 'A' ? mode == '3' ? GpsFixKind.Fix3D : mode == '2' ? GpsFixKind.Fix2D : GpsFixKind.None : GpsFixKind.None;
        item.GpsTimeTicks = UtcTicks(digits[..digitCount], h, m, s);
    }

    // The date entries (0xCA to 0xCC) carry the year, month and day as ASCII digits; only a complete YYYYMMDD is believed. The layout is
    // not confirmed on a camera file (ExifTool joins the three entries as text), so a stream without it reports no GPS time.
    private static long UtcTicks(ReadOnlySpan<char> digits, double h, double m, double s)
    {
        if (digits.Length != 8 || double.IsNaN(h) || h > 23 || m > 59 || s >= 61)
        {
            return 0;
        }

        int y = Int(digits[..4]), mo = Int(digits.Slice(4, 2)), d = Int(digits.Slice(6, 2));
        if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo))
        {
            return 0;
        }

        long ticks = new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return ticks + (long)Math.Round(((h * 3600) + (m * 60) + s) * TimeSpan.TicksPerSecond);
    }

    private static int Int(ReadOnlySpan<char> c)
    {
        int v = 0;
        foreach (char ch in c)
        {
            v = (v * 10) + (ch - '0');
        }

        return v;
    }

    // Two big-endian 16-bit halves: numerator, denominator. A zero denominator is "unknown".
    private static double Rational(ReadOnlySpan<byte> d)
    {
        int den = BinaryPrimitives.ReadUInt16BigEndian(d[2..]);
        return den == 0 ? double.NaN : BinaryPrimitives.ReadUInt16BigEndian(d) / (double)den;
    }
}

using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>The fields of a packetised elementary stream header that the demuxers use (ISO/IEC 13818-1 §2.4.3.6, ISO/IEC 11172-1 §2.4.3.3).</summary>
internal readonly struct PesHeader
{
    public int StreamId { get; init; }

    /// <summary>PES_packet_length, 0 when unbounded (allowed for video in transport streams).</summary>
    public int PacketLength { get; init; }

    /// <summary>Bytes from the start code to the first payload byte.</summary>
    public int HeaderLength { get; init; }

    /// <summary>33-bit PTS in 90 kHz ticks, or -1.</summary>
    public long Pts { get; init; }

    /// <summary>33-bit DTS in 90 kHz ticks, or -1.</summary>
    public long Dts { get; init; }

    public bool DataAlignment { get; init; }

    /// <summary>False for stream ids whose packets have no optional header (program stream map, padding, private_stream_2, ECM, EMM, directory…).</summary>
    public bool HasOptionalHeader { get; init; }

    /// <summary>The ids of §2.4.3.7 Table 2-22 that carry no PES header extension.</summary>
    public static bool IdHasHeader(int streamId) =>
        streamId is not (0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xFF or 0xF2 or 0xF8);

    /// <summary>
    /// Parses the header at the start of <paramref name="data"/>. Returns 0 when it is not a PES start code, -1 when more bytes are
    /// needed, otherwise the header length.
    /// </summary>
    public static int TryParse(ReadOnlySpan<byte> data, bool mpeg1, out PesHeader header)
    {
        header = default;
        if (data.Length < 6)
        {
            return -1;
        }

        if (data[0] != 0 || data[1] != 0 || data[2] != 1)
        {
            return 0;
        }

        int id = data[3];
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (!IdHasHeader(id))
        {
            header = new PesHeader { StreamId = id, PacketLength = length, HeaderLength = 6, Pts = -1, Dts = -1 };
            return 6;
        }

        if (mpeg1)
        {
            return ParseMpeg1(data, id, length, out header);
        }

        if (data.Length < 9)
        {
            return -1;
        }

        if ((data[6] & 0xC0) != 0x80)
        {
            return 0;
        }

        int flags = data[7] >> 6;
        int extra = data[8];
        int total = 9 + extra;
        if (data.Length < total)
        {
            return -1;
        }

        long pts = -1, dts = -1;
        // §2.4.3.7: PTS_DTS_flags '10' = PTS only, '11' = both; '01' is forbidden.
        if ((flags & 2) != 0 && extra >= 5)
        {
            pts = ReadTimestamp(data[9..]);
            if (flags == 3 && extra >= 10)
            {
                dts = ReadTimestamp(data[14..]);
            }
        }

        header = new PesHeader
        {
            StreamId = id,
            PacketLength = length,
            HeaderLength = total,
            Pts = pts,
            Dts = dts,
            DataAlignment = (data[6] & 4) != 0,
            HasOptionalHeader = true,
        };
        return total;
    }

    private static int ParseMpeg1(ReadOnlySpan<byte> data, int id, int length, out PesHeader header)
    {
        header = default;
        // ISO/IEC 11172-1 §2.4.3.3: stuffing bytes 0xFF, an optional STD buffer field, then PTS/DTS or a single 0x0F.
        int p = 6;
        while (true)
        {
            if (data.Length <= p)
            {
                return -1;
            }

            if (data[p] != 0xFF)
            {
                break;
            }

            p++;
            if (p > 6 + 16)
            {
                return 0;
            }
        }

        if ((data[p] & 0xC0) == 0x40)
        {
            p += 2;
            if (data.Length <= p)
            {
                return -1;
            }
        }

        long pts = -1, dts = -1;
        int top = data[p] & 0xF0;
        if (top == 0x20)
        {
            if (data.Length < p + 5)
            {
                return -1;
            }

            pts = ReadTimestamp(data[p..]);
            p += 5;
        }
        else if (top == 0x30)
        {
            if (data.Length < p + 10)
            {
                return -1;
            }

            pts = ReadTimestamp(data[p..]);
            dts = ReadTimestamp(data[(p + 5)..]);
            p += 10;
        }
        else if (data[p] == 0x0F)
        {
            p++;
        }
        else
        {
            return 0;
        }

        header = new PesHeader { StreamId = id, PacketLength = length, HeaderLength = p, Pts = pts, Dts = dts, HasOptionalHeader = true };
        return p;
    }

    /// <summary>Decodes the 33-bit timestamp spread over five bytes with marker bits (§2.4.3.7).</summary>
    public static long ReadTimestamp(ReadOnlySpan<byte> b) =>
        ((long)((b[0] >> 1) & 7) << 30) | ((long)b[1] << 22) | ((long)(b[2] >> 1) << 15) | ((long)b[3] << 7) | (long)(b[4] >> 1);
}

/// <summary>Turns 33-bit timestamps that wrap every 26.5 hours into a continuing 64-bit count (§2.4.3.7: PTS and DTS are modulo 2^33).</summary>
internal struct TimestampUnwrapper
{
    private const long Modulus = 1L << 33;
    private long _last;
    private bool _has;

    public long Unwrap(long raw)
    {
        if (raw < 0)
        {
            return raw;
        }

        if (!_has)
        {
            _has = true;
            _last = raw;
            return raw;
        }

        long delta = (raw - _last) & (Modulus - 1);
        if (delta >= Modulus / 2)
        {
            delta -= Modulus;
        }

        _last += delta;
        return _last;
    }

    public void Reset() => _has = false;

    /// <summary>Continues from a known unwrapped value, so a reader that starts mid-stream lands on the same timeline.</summary>
    public void Seed(long value)
    {
        _has = true;
        _last = value;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Builds small EBML/Matroska byte sequences for tests, so malformed and unusual layouts need no binary fixtures.</summary>
internal static class Ebl
{
    public static byte[] Id(uint id)
    {
        int len = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        byte[] b = new byte[len];
        for (int i = 0; i < len; i++)
        {
            b[len - 1 - i] = (byte)(id >> (8 * i));
        }

        return b;
    }

    /// <summary>A data size in the shortest form, or in exactly <paramref name="length"/> bytes when given.</summary>
    public static byte[] Size(long value, int length = 0)
    {
        if (length == 0)
        {
            length = 1;
            while (length < 8 && value >= (1L << (7 * length)) - 1)
            {
                length++;
            }
        }

        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[length - 1 - i] = (byte)(value >> (8 * i));
        }

        b[0] |= (byte)(0x80 >> (length - 1));
        return b;
    }

    public static byte[] UnknownSize(int length = 8)
    {
        byte[] b = new byte[length];
        Array.Fill(b, (byte)0xFF);
        b[0] = (byte)((0x80 >> (length - 1)) | (0xFF >> length));
        return b;
    }

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] El(uint id, params byte[][] parts)
    {
        byte[] payload = Cat(parts);
        return Cat(Id(id), Size(payload.Length), payload);
    }

    public static byte[] ElUnknown(uint id, params byte[][] parts) => Cat(Id(id), UnknownSize(), Cat(parts));

    public static byte[] U(uint id, ulong value)
    {
        int len = 1;
        while (len < 8 && value >> (8 * len) != 0)
        {
            len++;
        }

        byte[] p = new byte[len];
        for (int i = 0; i < len; i++)
        {
            p[len - 1 - i] = (byte)(value >> (8 * i));
        }

        return El(id, p);
    }

    public static byte[] S(uint id, string text) => El(id, Encoding.UTF8.GetBytes(text));

    public static byte[] F64(uint id, double value)
    {
        byte[] p = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(p, value);
        return El(id, p);
    }

    public static byte[] Bin(uint id, byte[] data) => El(id, data);

    /// <summary>Wraps the children in a master element behind a CRC-32 child; <paramref name="corrupt"/> flips a bit in the stored checksum.</summary>
    public static byte[] WithCrc(uint id, bool corrupt, params byte[][] children)
    {
        byte[] body = Cat(children);
        uint crc = Matroska.EbmlCrc32.Compute(body);
        if (corrupt)
        {
            crc ^= 1;
        }

        byte[] crcElement = [0xBF, 0x84, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)];
        return El(id, crcElement, body);
    }

    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint Segment = 0x18538067;
    public const uint Info = 0x1549A966;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint Cluster = 0x1F43B675;
    public const uint Cues = 0x1C53BB6B;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;

    public static byte[] Header(string docType = "matroska") =>
        El(EbmlHeader, U(0x4286, 1), U(0x42F7, 1), U(0x42F2, 4), U(0x42F3, 8), S(0x4282, docType), U(0x4287, 4), U(0x4285, 2));

    public static byte[] InfoElement(ulong timestampScale = 1_000_000, double duration = 3000) =>
        El(Info, U(0x2AD7B1, timestampScale), F64(0x4489, duration));

    /// <summary>A TrackEntry; <paramref name="extra"/> adds elements such as DefaultDuration or CodecDelay.</summary>
    public static byte[] Track(ulong number, ulong type, string codec, params byte[][] extra) =>
        El(TrackEntry, U(0xD7, number), U(0x73C5, number + 1000), U(0x83, type), S(0x86, codec), Cat(extra));

    public static byte[] TracksElement(params byte[][] entries) => El(Tracks, entries);

    public static byte[] Simple(ulong track, short relative, byte flags, byte[] payload)
    {
        byte[] head = [(byte)(0x80 | track), (byte)(relative >> 8), (byte)relative, flags];
        return El(SimpleBlock, head, payload);
    }

    public static byte[] BlockBytes(ulong track, short relative, byte flags, byte[] payload)
    {
        byte[] head = [(byte)(0x80 | track), (byte)(relative >> 8), (byte)relative, flags];
        return El(Block, head, payload);
    }

    public static byte[] ClusterElement(ulong timestamp, params byte[][] blocks) => El(Cluster, U(0xE7, timestamp), Cat(blocks));

    public static byte[] SegmentElement(params byte[][] children) => El(Segment, children);

    /// <summary>A Segment of unknown size, as a live recording writes it.</summary>
    public static byte[] SegmentUnknown(params byte[][] children) => ElUnknown(Segment, children);

    public static byte[] Fill(int length, byte seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)(seed + i);
        }

        return b;
    }
}

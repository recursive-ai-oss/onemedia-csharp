using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Tests.Codecs;

/// <summary>
/// Just enough ISO base media and Matroska reading to pull a track's codec configuration and first sample out of a small test file.
/// The real container readers live elsewhere; these helpers keep the codec-layer tests independent of them.
/// </summary>
internal static class MiniContainers
{
    private static readonly string[] ConfigBoxes = ["avcC", "hvcC", "vvcC", "av1C", "vpcC", "esds", "dOps", "dfLa", "dac3", "dec3", "damr"];
    private static readonly string[] VisualEntries = ["avc1", "avc3", "hvc1", "hev1", "vvc1", "vvi1", "av01", "vp09", "vp08", "mp4v", "encv"];

    public sealed record Mp4Track(string EntryType, string? ConfigType, byte[] Config, byte[] FirstSample);

    public static Mp4Track? ReadMp4(byte[] file)
    {
        int moov = FindBox(file, 0, file.Length, "moov");
        if (moov < 0)
        {
            return null;
        }

        (int bodyStart, int end) = Body(file, moov);
        int trak = FindBox(file, bodyStart, end, "trak");
        if (trak < 0)
        {
            return null;
        }

        int p = trak;
        foreach (string name in new[] { "mdia", "minf", "stbl" })
        {
            (int s, int e) = Body(file, p);
            p = FindBox(file, s, e, name);
            if (p < 0)
            {
                return null;
            }
        }

        (int stblStart, int stblEnd) = Body(file, p);
        int stsd = FindBox(file, stblStart, stblEnd, "stsd");
        (int stsdStart, int stsdEnd) = Body(file, stsd);
        int entry = stsdStart + 8;
        string entryType = Fourcc(file, entry + 4);
        int entrySize = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(entry));
        bool visual = VisualEntries.Contains(entryType);
        int childStart = entry + 8 + (visual ? 78 : 28);
        if (!visual && BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(entry + 8 + 8)) == 1)
        {
            childStart += 16;
        }

        string? configType = null;
        byte[] config = [];
        foreach (string t in ConfigBoxes)
        {
            int b = FindBox(file, childStart, Math.Min(entry + entrySize, stsdEnd), t);
            if (b >= 0)
            {
                (int s, int e) = Body(file, b);
                configType = t;
                config = file.AsSpan(s, e - s).ToArray();
                break;
            }
        }

        // first sample: first chunk offset and the first sample size
        int stsz = FindBox(file, stblStart, stblEnd, "stsz");
        (int zs, _) = Body(file, stsz);
        int constant = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(zs + 4));
        int firstSize = constant != 0 ? constant : BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(zs + 12));
        long offset;
        int stco = FindBox(file, stblStart, stblEnd, "stco");
        if (stco >= 0)
        {
            (int cs, _) = Body(file, stco);
            offset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(cs + 8));
        }
        else
        {
            int co64 = FindBox(file, stblStart, stblEnd, "co64");
            (int cs, _) = Body(file, co64);
            offset = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(cs + 8));
        }

        return new Mp4Track(entryType, configType, config, file.AsSpan((int)offset, firstSize).ToArray());
    }

    /// <summary>Every sample of the first track, in file order, by walking stsz, stsc and stco/co64.</summary>
    public static List<byte[]> ReadMp4Samples(byte[] file)
    {
        int moov = FindBox(file, 0, file.Length, "moov");
        (int bodyStart, int end) = Body(file, moov);
        int p = FindBox(file, bodyStart, end, "trak");
        foreach (string name in new[] { "mdia", "minf", "stbl" })
        {
            (int s, int e) = Body(file, p);
            p = FindBox(file, s, e, name);
        }

        (int stblStart, int stblEnd) = Body(file, p);
        (int zs, _) = Body(file, FindBox(file, stblStart, stblEnd, "stsz"));
        int constant = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(zs + 4));
        int count = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(zs + 8));
        (int ss, _) = Body(file, FindBox(file, stblStart, stblEnd, "stsc"));
        int stscCount = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(ss + 4));
        List<long> chunkOffsets = [];
        int stco = FindBox(file, stblStart, stblEnd, "stco");
        if (stco >= 0)
        {
            (int cs, _) = Body(file, stco);
            int n = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(cs + 4));
            for (int i = 0; i < n; i++)
            {
                chunkOffsets.Add(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(cs + 8 + (4 * i))));
            }
        }
        else
        {
            (int cs, _) = Body(file, FindBox(file, stblStart, stblEnd, "co64"));
            int n = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(cs + 4));
            for (int i = 0; i < n; i++)
            {
                chunkOffsets.Add((long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(cs + 8 + (8 * i))));
            }
        }

        List<byte[]> samples = [];
        int sample = 0;
        for (int chunk = 0; chunk < chunkOffsets.Count && sample < count; chunk++)
        {
            int perChunk = 0;
            for (int e = 0; e < stscCount; e++)
            {
                int first = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(ss + 8 + (12 * e)));
                if (first <= chunk + 1)
                {
                    perChunk = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(ss + 8 + (12 * e) + 4));
                }
            }

            long offset = chunkOffsets[chunk];
            for (int k = 0; k < perChunk && sample < count; k++, sample++)
            {
                int size = constant != 0 ? constant : BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(zs + 12 + (4 * sample)));
                samples.Add(file.AsSpan((int)offset, size).ToArray());
                offset += size;
            }
        }

        return samples;
    }

    private static string Fourcc(byte[] f, int at) => System.Text.Encoding.Latin1.GetString(f, at, 4);

    private static (int Start, int End) Body(byte[] f, int box)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(box));
        int header = 8;
        if (size == 1)
        {
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(box + 8));
            header = 16;
        }
        else if (size == 0)
        {
            size = f.Length - box;
        }

        return (box + header, (int)Math.Min(box + size, f.Length));
    }

    private static int FindBox(byte[] f, int start, int end, string type)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(p));
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(p + 8));
            }
            else if (size == 0)
            {
                size = end - p;
            }

            if (Fourcc(f, p + 4) == type)
            {
                return p;
            }

            if (size < 8)
            {
                return -1;
            }

            p += (int)size;
        }

        return -1;
    }

    public sealed record MkvTrack(string CodecId, byte[] CodecPrivate, byte[] FirstFrame);

    /// <summary>Reads the first track's CodecID and CodecPrivate and the first frame of the first block, assuming no lacing.</summary>
    public static MkvTrack? ReadMkv(byte[] f)
    {
        string codecId = "";
        byte[] priv = [];
        byte[]? frame = null;
        WalkMkv(f, 0, f.Length, ref codecId, ref priv, ref frame);
        return frame is null ? null : new MkvTrack(codecId, priv, frame);
    }

    private static void WalkMkv(byte[] f, int start, int end, ref string codecId, ref byte[] priv, ref byte[]? frame)
    {
        int p = start;
        while (p < end && frame is null)
        {
            (long id, int idn) = VintId(f, p);
            (long size, int sn) = Vint(f, p + idn);
            int body = p + idn + sn;
            long bodyEnd = size == (1L << (7 * sn)) - 1 ? end : Math.Min(end, body + size);
            switch (id)
            {
                case 0x18538067 or 0x1654ae6b or 0xae or 0x1f43b675 or 0xa0:
                    WalkMkv(f, body, (int)bodyEnd, ref codecId, ref priv, ref frame);
                    break;
                case 0x86 when codecId.Length == 0:
                    codecId = System.Text.Encoding.ASCII.GetString(f, body, (int)size);
                    break;
                case 0x63a2 when priv.Length == 0:
                    priv = f.AsSpan(body, (int)size).ToArray();
                    break;
                case 0xa3 or 0xa1:
                    (long _, int tn) = Vint(f, body);
                    frame = f.AsSpan(body + tn + 3, (int)(bodyEnd - body - tn - 3)).ToArray();
                    break;
            }

            p = (int)bodyEnd;
        }
    }

    private static (long Value, int Length) Vint(byte[] f, int at)
    {
        int first = f[at];
        int len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0)
        {
            len++;
        }

        long v = first & (0xff >> len);
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | f[at + i];
        }

        return (v, len);
    }

    private static (long Id, int Length) VintId(byte[] f, int at)
    {
        int first = f[at];
        int len = 1;
        while (len <= 4 && (first & (0x80 >> (len - 1))) == 0)
        {
            len++;
        }

        long v = first;
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | f[at + i];
        }

        return (v, len);
    }
}

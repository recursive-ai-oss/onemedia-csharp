using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tests.Avi;

/// <summary>
/// Writes small AVI files for tests: classic files with an <c>idx1</c> (relative or absolute offsets, or none) and OpenDML files whose
/// frames continue in <c>RIFF AVIX</c> chunks and are indexed by <c>indx</c> super indexes and <c>ix##</c> standard indexes.
/// </summary>
internal sealed class AviBuilder
{
    public sealed record StreamSpec(string Type, string Handler, uint Scale, uint Rate, uint SampleSize, byte[] Format, string? Name = null, uint Start = 0);

    public sealed record ChunkSpec(int Stream, byte[] Data, bool Key = true, string? Kind = null);

    public List<StreamSpec> Streams { get; } = [];

    public List<ChunkSpec> Chunks { get; } = [];

    public bool WriteIdx1 { get; set; } = true;

    public bool AbsoluteIdx1 { get; set; }

    public bool RecLists { get; set; }

    public byte[]? InfoList { get; set; }

    public byte[]? Idit { get; set; }

    public uint MainFlags { get; set; } = 0x10;

    public static byte[] Bitmap(int width, int height, string fourcc, int extra = 0)
    {
        byte[] b = new byte[40 + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 24);
        Encoding.ASCII.GetBytes(fourcc).CopyTo(b.AsSpan(16));
        return b;
    }

    public static byte[] Wave(ushort tag, ushort channels, uint rate, ushort blockAlign, ushort bits, byte[]? extra = null)
    {
        extra ??= [];
        byte[] b = new byte[18 + extra.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), rate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bits);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), (ushort)extra.Length);
        extra.CopyTo(b.AsSpan(18));
        return b;
    }

    public static byte[] Chunk(string id, ReadOnlySpan<byte> data)
    {
        byte[] b = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
        data.CopyTo(b.AsSpan(8));
        return b;
    }

    public static byte[] List(string id, string type, params byte[][] children)
    {
        int size = 4 + children.Sum(c => c.Length);
        byte[] b = new byte[8 + size];
        Encoding.ASCII.GetBytes(id).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 8);
        int at = 12;
        foreach (byte[] c in children)
        {
            c.CopyTo(b, at);
            at += c.Length;
        }

        return b;
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private byte[] StreamList(int index, byte[]? superIndex)
    {
        StreamSpec s = Streams[index];
        long length = Chunks.Count(c => c.Stream == index);
        if (s.SampleSize > 0)
        {
            length = Chunks.Where(c => c.Stream == index).Sum(c => (long)c.Data.Length) / s.SampleSize;
        }

        byte[] strh = new byte[56];
        Encoding.ASCII.GetBytes(s.Type).CopyTo(strh, 0);
        Encoding.ASCII.GetBytes(s.Handler).CopyTo(strh, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(20), s.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(24), s.Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(28), s.Start);
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(32), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(44), s.SampleSize);
        List<byte[]> parts = [Chunk("strh", strh), Chunk("strf", s.Format)];
        if (s.Name is not null)
        {
            parts.Add(Chunk("strn", Encoding.ASCII.GetBytes(s.Name + "\0")));
        }

        if (superIndex is not null)
        {
            parts.Add(superIndex);
        }

        return List("LIST", "strl", [.. parts]);
    }

    private byte[] Hdrl(byte[][]? superIndexes, uint totalFrames)
    {
        byte[] avih = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 40000);
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(12), MainFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), totalFrames);
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), (uint)Streams.Count);
        List<byte[]> parts = [Chunk("avih", avih)];
        for (int i = 0; i < Streams.Count; i++)
        {
            parts.Add(StreamList(i, superIndexes?[i]));
        }

        if (superIndexes is not null)
        {
            byte[] dmlh = new byte[248];
            BinaryPrimitives.WriteUInt32LittleEndian(dmlh, totalFrames);
            parts.Add(List("LIST", "odml", Chunk("dmlh", dmlh)));
        }

        return List("LIST", "hdrl", [.. parts]);
    }

    private static string Id(int stream, ChunkSpec c)
    {
        string kind = c.Kind ?? "dc";
        return $"{stream:00}{kind}";
    }

    private List<byte[]> Extra()
    {
        List<byte[]> extra = [];
        if (InfoList is not null)
        {
            extra.Add(InfoList);
        }

        if (Idit is not null)
        {
            extra.Add(Idit);
        }

        return extra;
    }

    /// <summary>A classic AVI: one RIFF with hdrl, optional INFO, movi and idx1.</summary>
    public byte[] Build()
    {
        byte[] hdrl = Hdrl(null, (uint)Chunks.Count(c => c.Stream == 0));
        List<byte[]> top = [hdrl, .. Extra()];
        long moviOffset = 12 + top.Sum(t => t.Length);
        List<byte[]> moviParts = [];
        List<(string Id, uint Flags, uint Offset, uint Size)> index = [];
        long at = 4; // offsets in idx1 are relative to the movi list type
        foreach (ChunkSpec c in Chunks)
        {
            byte[] chunk = Chunk(Id(c.Stream, c), c.Data);
            index.Add((Id(c.Stream, c), c.Key ? 0x10u : 0, (uint)(AbsoluteIdx1 ? moviOffset + 8 + at : at), (uint)c.Data.Length));
            moviParts.Add(chunk);
            at += chunk.Length;
        }

        if (RecLists)
        {
            moviParts = [List("LIST", "rec ", [.. moviParts])];
        }

        top.Add(List("LIST", "movi", [.. moviParts]));
        if (WriteIdx1)
        {
            byte[] idx = new byte[index.Count * 16];
            for (int i = 0; i < index.Count; i++)
            {
                Encoding.ASCII.GetBytes(index[i].Id).CopyTo(idx, i * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan((i * 16) + 4), index[i].Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan((i * 16) + 8), index[i].Offset);
                BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan((i * 16) + 12), index[i].Size);
            }

            top.Add(Chunk("idx1", idx));
        }

        return Riff("AVI ", top);
    }

    private static byte[] Riff(string form, List<byte[]> children) => List("RIFF", form, [.. children]);

    /// <summary>An OpenDML AVI: the chunks are split into parts of <paramref name="chunksPerRiff"/>; the first part lives in RIFF AVI and the rest in RIFF AVIX.</summary>
    public byte[] BuildOpenDml(int chunksPerRiff, bool writeIdx1ForFirst = false)
    {
        const int capacity = 16;
        byte[][] placeholder = [.. Streams.Select((_, i) => SuperIndex(i, capacity, []))];
        int hdrlSize = Hdrl(placeholder, 0).Length;
        List<List<ChunkSpec>> parts = [];
        for (int i = 0; i < Chunks.Count; i += chunksPerRiff)
        {
            parts.Add(Chunks.Skip(i).Take(chunksPerRiff).ToList());
        }

        List<List<(long Offset, int Size, bool Key)>>[] entries = [.. Streams.Select(_ => new List<List<(long, int, bool)>>())];
        List<byte[]>[] superEntries = [.. Streams.Select(_ => new List<byte[]>())];
        List<SuperEntry>[] supers = [.. Streams.Select(_ => new List<SuperEntry>())];
        MemoryStream file = new();
        List<byte[]> extra = Extra();
        long position = 12 + hdrlSize + extra.Sum(e => e.Length);
        file.SetLength(position);
        file.Position = position;
        long[] ticks = new long[Streams.Count];
        long[] byteTotals = new long[Streams.Count];
        List<(long RiffStart, long Size)> riffs = [];
        long riffStart = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            if (p > 0)
            {
                riffStart = position;
                position += 12;
                file.SetLength(position);
                file.Position = position;
            }

            long moviStart = position;
            position += 12;
            file.SetLength(position);
            file.Position = position;
            List<(long Offset, int Size, bool Key)>[] perStream = [.. Streams.Select(_ => new List<(long, int, bool)>())];
            foreach (ChunkSpec c in parts[p])
            {
                byte[] chunk = Chunk(Id(c.Stream, c), c.Data);
                perStream[c.Stream].Add((position + 8, c.Data.Length, c.Key));
                file.Write(chunk);
                position += chunk.Length;
            }

            for (int s = 0; s < Streams.Count; s++)
            {
                if (perStream[s].Count == 0)
                {
                    continue;
                }

                byte[] ix = StandardIndex(s, perStream[s]);
                supers[s].Add(new SuperEntry(position, (uint)ix.Length, (uint)perStream[s].Count));
                file.Write(ix);
                position += ix.Length;
            }

            long moviSize = position - moviStart - 8;
            WriteAt(file, moviStart, List("LIST", "movi").AsSpan(0, 12), (uint)moviSize);
            if (p == 0 && writeIdx1ForFirst)
            {
                // Not needed by the tests that use this builder; OpenDML files commonly carry one for the first RIFF.
            }

            if (p > 0)
            {
                byte[] head = new byte[12];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(head, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)(position - riffStart - 8));
                Encoding.ASCII.GetBytes("AVIX").CopyTo(head, 8);
                file.Position = riffStart;
                file.Write(head);
                file.Position = position;
            }
            else
            {
                riffs.Add((0, position));
            }
        }

        byte[][] real = [.. Streams.Select((_, i) => SuperIndex(i, capacity, supers[i]))];
        byte[] hdrl = Hdrl(real, (uint)Chunks.Count(c => c.Stream == 0));
        byte[] front = Concat([Encoding.ASCII.GetBytes("RIFF"), new byte[4], Encoding.ASCII.GetBytes("AVI "), hdrl, .. extra]);
        BinaryPrimitives.WriteUInt32LittleEndian(front.AsSpan(4), (uint)(riffs[0].Size - 8));
        file.Position = 0;
        file.Write(front);
        _ = ticks;
        _ = byteTotals;
        _ = entries;
        _ = superEntries;
        return file.ToArray();
    }

    private sealed record SuperEntry(long Offset, uint Size, uint Duration);

    private static void WriteAt(MemoryStream file, long at, ReadOnlySpan<byte> listHead, uint size)
    {
        byte[] head = listHead.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), size);
        long keep = file.Position;
        file.Position = at;
        file.Write(head);
        file.Position = keep;
    }

    private static byte[] SuperIndex(int stream, int capacity, List<SuperEntry> entries)
    {
        byte[] b = new byte[24 + (capacity * 16)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 4);
        b[3] = 0; // AVI_INDEX_OF_INDEXES
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)entries.Count);
        Encoding.ASCII.GetBytes($"{stream:00}dc").CopyTo(b, 8);
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24 + (i * 16)), (ulong)entries[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32 + (i * 16)), entries[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36 + (i * 16)), entries[i].Duration);
        }

        return Chunk("indx", b);
    }

    private static byte[] StandardIndex(int stream, List<(long Offset, int Size, bool Key)> entries)
    {
        byte[] b = new byte[24 + (entries.Count * 8)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 2);
        b[3] = 1; // AVI_INDEX_OF_CHUNKS
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)entries.Count);
        Encoding.ASCII.GetBytes($"{stream:00}dc").CopyTo(b, 8);
        // One base for the whole index; each entry's offset is from it to the chunk's data.
        long baseOffset = entries[0].Offset;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), (ulong)baseOffset);
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + (i * 8)), (uint)(entries[i].Offset - baseOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28 + (i * 8)), (uint)entries[i].Size | (entries[i].Key ? 0u : 0x80000000u));
        }

        return Chunk($"ix{stream:00}", b);
    }
}

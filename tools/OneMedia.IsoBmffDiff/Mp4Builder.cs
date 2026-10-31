using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RecursiveAI.OneMedia.Tests.IsoBmffSupport;

/// <summary>Writes small ISO BMFF files from a description, for tests and for sparse large files. Not a muxer: it makes the tables the readers need.</summary>
public static class Mp4Builder
{
    public static byte[] U32(params uint[] values)
    {
        byte[] b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), values[i]);
        }

        return b;
    }

    public static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];

    public static byte[] U64(ulong v)
    {
        byte[] b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    public static byte[] Cat(params byte[][] parts)
    {
        int n = 0;
        foreach (byte[] p in parts)
        {
            n += p.Length;
        }

        byte[] r = new byte[n];
        int o = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(r, o);
            o += p.Length;
        }

        return r;
    }

    public static byte[] Box(string type, params byte[][] parts)
    {
        byte[] payload = Cat(parts);
        return Cat(U32((uint)(payload.Length + 8)), Encoding.Latin1.GetBytes(type), payload);
    }

    public static byte[] Full(string type, byte version, uint flags, params byte[][] parts) =>
        Box(type, [U32(((uint)version << 24) | flags), .. parts]);

    /// <summary>One track to write.</summary>
    public sealed class TrackSpec
    {
        public uint Id { get; set; } = 1;
        public uint Timescale { get; set; } = 1000;
        public string Handler { get; set; } = "vide";
        public int SampleCount { get; set; } = 10;
        public Func<int, int> SizeOf { get; set; } = _ => 100;
        public Func<int, uint> DurationOf { get; set; } = _ => 40;
        public Func<int, int>? CompositionOffsetOf { get; set; }
        public bool CompositionVersion1 { get; set; }
        public Func<int, bool>? IsSync { get; set; }
        public int SamplesPerChunk { get; set; } = 1;
        public bool Co64 { get; set; }
        public bool CompactSizes { get; set; }
        public bool RepeatSyncEntries { get; set; }
        public bool ConstantSizeBox { get; set; }
        public byte[]? SampleEntries { get; set; }
        public int EntryCount { get; set; } = 1;
        public (uint SegmentDuration, int MediaTime)[] Edits { get; set; } = [];
        public byte[]? ExtraStblBoxes { get; set; }

        /// <summary>1-based sample description index for each chunk (default: 1 for all).</summary>
        public Func<int, int>? DescriptionOfChunk { get; set; }
    }

    public static byte[] DefaultVideoEntry(string format = "avc1", int width = 64, int height = 48) =>
        Box(format, new byte[6], U16(1), new byte[16], U16((ushort)width), U16((ushort)height), U32(0x00480000, 0x00480000, 0), U16(1), new byte[32], U16(0x18), U16(0xFFFF),
            Box("avcC", [1, 0x42, 0, 0x1E, 0xFF, 0xE1, 0, 4, 0x67, 0x42, 0, 0x1E, 1, 0, 4, 0x68, 0xCE, 0x3C, 0x80]));

    public static byte[] DefaultAudioEntry(string format = "mp4a", int channels = 2, int rate = 44100) =>
        Box(format, new byte[6], U16(1), new byte[8], U16((ushort)channels), U16(16), U16(0), U16(0), U32((uint)rate << 16),
            Full("esds", 0, 0, [3, 0x19, 0, 1, 0, 4, 0x11, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 2, 0x12, 0x10, 6, 1, 2]));

    public static byte PayloadByte(uint trackId, int sample) => (byte)((trackId * 31) + sample);

    /// <summary>The result of <see cref="Build"/>: the file's pieces and where the media data sits.</summary>
    public sealed record Layout(byte[] Head, long MdatPayloadOffset, long MdatPayloadSize, byte[] Tail, long[][] SampleOffsets)
    {
        public long Length => Head.Length + MdatPayloadSize + Tail.Length;

        /// <summary>The whole file in memory, with each sample's bytes filled by <see cref="PayloadByte"/>.</summary>
        public byte[] ToArray(IReadOnlyList<TrackSpec> tracks)
        {
            byte[] file = new byte[Length];
            Head.CopyTo(file, 0);
            for (int t = 0; t < tracks.Count; t++)
            {
                for (int s = 0; s < tracks[t].SampleCount; s++)
                {
                    int size = tracks[t].SizeOf(s);
                    Array.Fill(file, PayloadByte(tracks[t].Id, s), (int)SampleOffsets[t][s], size);
                }
            }

            Tail.CopyTo(file, Head.Length + (int)MdatPayloadSize);
            return file;
        }
    }

    /// <summary>
    /// Lays out <c>ftyp</c>, the media data and the movie box (before the data when <paramref name="moovFirst"/>, else after), interleaving the tracks chunk by chunk.
    /// The media data box uses a 64-bit size when the payload needs it.
    /// </summary>
    public static Layout Build(IReadOnlyList<TrackSpec> tracks, bool moovFirst, string brand = "isom", byte[]? extraMoovBoxes = null)
    {
        byte[] ftyp = Box("ftyp", Encoding.Latin1.GetBytes(brand), U32(512), Encoding.Latin1.GetBytes(brand), Encoding.Latin1.GetBytes("mp41"));
        // chunk order: round robin over the tracks
        List<(int Track, int Chunk)> order = [];
        int[] chunkCounts = new int[tracks.Count];
        for (int t = 0; t < tracks.Count; t++)
        {
            chunkCounts[t] = (tracks[t].SampleCount + tracks[t].SamplesPerChunk - 1) / tracks[t].SamplesPerChunk;
        }

        for (int round = 0; ; round++)
        {
            bool any = false;
            for (int t = 0; t < tracks.Count; t++)
            {
                if (round < chunkCounts[t])
                {
                    order.Add((t, round));
                    any = true;
                }
            }

            if (!any)
            {
                break;
            }
        }

        // relative offsets of every sample and chunk inside the media data payload
        long[][] sampleRel = new long[tracks.Count][];
        long[][] chunkRel = new long[tracks.Count][];
        for (int t = 0; t < tracks.Count; t++)
        {
            sampleRel[t] = new long[tracks[t].SampleCount];
            chunkRel[t] = new long[chunkCounts[t]];
        }

        long pos = 0;
        foreach ((int t, int c) in order)
        {
            chunkRel[t][c] = pos;
            int first = c * tracks[t].SamplesPerChunk;
            int last = Math.Min(first + tracks[t].SamplesPerChunk, tracks[t].SampleCount);
            for (int s = first; s < last; s++)
            {
                sampleRel[t][s] = pos;
                pos += tracks[t].SizeOf(s);
            }
        }

        long mdatPayload = pos;
        int mdatHeader = mdatPayload + 8 > uint.MaxValue ? 16 : 8;
        byte[] moovProbe = BuildMoov(tracks, chunkRel, 0, extraMoovBoxes);
        long mdatStart = moovFirst ? ftyp.Length + moovProbe.Length : ftyp.Length;
        long payloadStart = mdatStart + mdatHeader;
        byte[] moov = BuildMoov(tracks, chunkRel, payloadStart, extraMoovBoxes);
        byte[] mdatHead = mdatHeader == 16
            ? Cat(U32(1), Encoding.Latin1.GetBytes("mdat"), U64((ulong)(mdatPayload + 16)))
            : Cat(U32((uint)(mdatPayload + 8)), Encoding.Latin1.GetBytes("mdat"));
        byte[] head = moovFirst ? Cat(ftyp, moov, mdatHead) : Cat(ftyp, mdatHead);
        long[][] abs = new long[tracks.Count][];
        for (int t = 0; t < tracks.Count; t++)
        {
            abs[t] = new long[tracks[t].SampleCount];
            for (int s = 0; s < abs[t].Length; s++)
            {
                abs[t][s] = payloadStart + sampleRel[t][s];
            }
        }

        return new Layout(head, payloadStart, mdatPayload, moovFirst ? [] : moov, abs);
    }

    private static byte[] BuildMoov(IReadOnlyList<TrackSpec> tracks, long[][] chunkRel, long payloadStart, byte[]? extra)
    {
        List<byte[]> parts = [Full("mvhd", 0, 0, U32(0, 0, 1000, 10000, 0x00010000), [0, 1, 0, 0], new byte[10], U32(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000), new byte[24], U32((uint)tracks.Count + 1))];
        for (int t = 0; t < tracks.Count; t++)
        {
            parts.Add(BuildTrak(tracks[t], chunkRel[t], payloadStart));
        }

        if (extra is not null)
        {
            parts.Add(extra);
        }

        return Box("moov", [.. parts]);
    }

    private static byte[] BuildTrak(TrackSpec s, long[] chunkRel, long payloadStart)
    {
        bool video = s.Handler == "vide";
        byte[] tkhd = Full("tkhd", 0, 3, U32(0, 0, s.Id, 0, 10000), new byte[8], U16(0), U16(0), U16(0), U16(0),
            U32(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000), U32(video ? 64u << 16 : 0, video ? 48u << 16 : 0));
        List<byte[]> trakParts = [tkhd];
        if (s.Edits.Length > 0)
        {
            List<byte[]> e = [U32((uint)s.Edits.Length)];
            foreach ((uint dur, int mt) in s.Edits)
            {
                e.Add(U32(dur, unchecked((uint)mt), 0x00010000));
            }

            trakParts.Add(Box("edts", Full("elst", 0, 0, [.. e])));
        }

        byte[] mdhd = Full("mdhd", 0, 0, U32(0, 0, s.Timescale, 0), U16(0x55C4), U16(0));
        byte[] hdlr = Full("hdlr", 0, 0, U32(0), Encoding.Latin1.GetBytes(s.Handler), new byte[12], Encoding.Latin1.GetBytes("h\0"));
        byte[] dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1)));
        byte[] media = Full(video ? "vmhd" : "smhd", 0, video ? 1u : 0u, new byte[8]);

        // sample tables
        byte[] entries = s.SampleEntries ?? (video ? DefaultVideoEntry() : DefaultAudioEntry());
        byte[] stsd = Full("stsd", 0, 0, U32((uint)s.EntryCount), entries);
        List<byte> stts = [];
        uint runs = 0;
        for (int i = 0; i < s.SampleCount;)
        {
            uint d = s.DurationOf(i);
            int j = i;
            while (j < s.SampleCount && s.DurationOf(j) == d)
            {
                j++;
            }

            stts.AddRange(U32((uint)(j - i), d));
            runs++;
            i = j;
        }

        byte[] sttsBox = Full("stts", 0, 0, U32(runs), [.. stts]);
        byte[] cttsBox = [];
        if (s.CompositionOffsetOf is { } co)
        {
            List<byte> c = [];
            uint cruns = 0;
            for (int i = 0; i < s.SampleCount;)
            {
                int d = co(i);
                int j = i;
                while (j < s.SampleCount && co(j) == d)
                {
                    j++;
                }

                c.AddRange(U32((uint)(j - i), unchecked((uint)d)));
                cruns++;
                i = j;
            }

            cttsBox = Full("ctts", s.CompositionVersion1 ? (byte)1 : (byte)0, 0, U32(cruns), [.. c]);
        }

        byte[] stssBox = [];
        if (s.IsSync is { } sync)
        {
            List<uint> k = [];
            for (int i = 0; i < s.SampleCount; i++)
            {
                if (sync(i))
                {
                    k.Add((uint)i + 1);
                    if (s.RepeatSyncEntries)
                    {
                        k.Add((uint)i + 1);
                    }
                }
            }

            stssBox = Full("stss", 0, 0, U32((uint)k.Count), U32([.. k]));
        }

        byte[] stszBox;
        if (s.ConstantSizeBox)
        {
            stszBox = Full("stsz", 0, 0, U32((uint)s.SizeOf(0), (uint)s.SampleCount));
        }
        else if (s.CompactSizes)
        {
            List<byte> z = [];
            for (int i = 0; i < s.SampleCount; i++)
            {
                z.AddRange(U16((ushort)s.SizeOf(i)));
            }

            stszBox = Full("stz2", 0, 0, [0, 0, 0, 16], U32((uint)s.SampleCount), [.. z]);
        }
        else
        {
            uint[] sizes = new uint[s.SampleCount];
            for (int i = 0; i < sizes.Length; i++)
            {
                sizes[i] = (uint)s.SizeOf(i);
            }

            stszBox = Full("stsz", 0, 0, U32(0, (uint)s.SampleCount), U32(sizes));
        }

        List<byte> sc = [];
        uint scEntries = 0;
        int chunkTotal = (s.SampleCount + s.SamplesPerChunk - 1) / s.SamplesPerChunk;
        (int Spc, int Desc) previous = (-1, -1);
        for (int c = 0; c < chunkTotal; c++)
        {
            (int Spc, int Desc) now = (Math.Min(s.SamplesPerChunk, s.SampleCount - (c * s.SamplesPerChunk)), s.DescriptionOfChunk?.Invoke(c) ?? 1);
            if (now != previous)
            {
                sc.AddRange(U32((uint)c + 1, (uint)now.Spc, (uint)now.Desc));
                scEntries++;
                previous = now;
            }
        }

        byte[] stscBox = Full("stsc", 0, 0, U32(scEntries), [.. sc]);
        byte[] offsetsBox;
        if (s.Co64)
        {
            List<byte> o = [];
            foreach (long r in chunkRel)
            {
                o.AddRange(U64((ulong)(payloadStart + r)));
            }

            offsetsBox = Full("co64", 0, 0, U32((uint)chunkRel.Length), [.. o]);
        }
        else
        {
            uint[] o = new uint[chunkRel.Length];
            for (int i = 0; i < o.Length; i++)
            {
                o[i] = (uint)(payloadStart + chunkRel[i]);
            }

            offsetsBox = Full("stco", 0, 0, U32((uint)o.Length), U32(o));
        }

        byte[] stbl = Box("stbl", stsd, sttsBox, cttsBox, stssBox, stscBox, stszBox, offsetsBox, s.ExtraStblBoxes ?? []);
        byte[] minf = Box("minf", media, dinf, stbl);
        trakParts.Add(Box("mdia", mdhd, hdlr, minf));
        return Box("trak", [.. trakParts]);
    }
}

/// <summary>A file made of real head bytes, a run of zeros (the media data of a large sparse file) and real tail bytes, without storing the zeros.</summary>
public sealed class VirtualFileSource(byte[] head, long gap, byte[] tail) : IMediaSource
{
    public long Length => head.Length + gap + tail.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        int total = 0;
        while (total < destination.Length && offset + total < Length)
        {
            long at = offset + total;
            Span<byte> dest = destination[total..];
            if (at < head.Length)
            {
                int n = (int)Math.Min(dest.Length, head.Length - at);
                head.AsSpan((int)at, n).CopyTo(dest);
                total += n;
            }
            else if (at < head.Length + gap)
            {
                int n = (int)Math.Min(dest.Length, head.Length + gap - at);
                dest[..n].Clear();
                total += n;
            }
            else
            {
                long rel = at - head.Length - gap;
                int n = (int)Math.Min(dest.Length, tail.Length - rel);
                tail.AsSpan((int)rel, n).CopyTo(dest);
                total += n;
            }
        }

        return total;
    }

    public void Dispose()
    {
    }
}

using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// ISO base media (MP4/MOV/3GP) index repair, ISO/IEC 14496-12. Progressive files keep every byte and get their chunk offset tables
/// (<c>stco</c>/<c>co64</c>) and durations (<c>mdhd</c>, <c>tkhd</c>, <c>mvhd</c>) corrected in place; a truncated <c>mdat</c> size is corrected too.
/// Fragmented files get a fresh <c>mfra</c> (<c>tfra</c> per track, §8.8.10-8.8.11) and, when the input had one, a fresh <c>sidx</c> (§8.16.3);
/// a last fragment that is cut off is left out. A file whose <c>moov</c> is missing needs reconstruction and is reported as such.
/// </summary>
internal static partial class IsoRepair
{
    private const uint Moov = 0x6D6F6F76, Mdat = 0x6D646174, Moof = 0x6D6F6F66, Mfra = 0x6D667261, Sidx = 0x73696478, Trak = 0x7472616B, Tkhd = 0x746B6864;
    private const uint Mdia = 0x6D646961, Mdhd = 0x6D646864, Hdlr = 0x68646C72, Minf = 0x6D696E66, Stbl = 0x7374626C, Stts = 0x73747473, Stsz = 0x7374737A, Stz2 = 0x73747A32;
    private const uint Stsc = 0x73747363, Stco = 0x7374636F, Co64 = 0x636F3634, Edts = 0x65647473, Elst = 0x656C7374, Mvhd = 0x6D766864, Mvex = 0x6D766578, Trex = 0x74726578;
    private const uint Traf = 0x74726166, Tfhd = 0x74666864, Tfdt = 0x74666474, Trun = 0x7472756E, Mehd = 0x6D656864, Free = 0x66726565, Skip = 0x736B6970;
    private const uint Vide = 0x76696465, Stsd = 0x73747364;

    private readonly record struct BoxHeader(uint Type, long Offset, int HeaderSize, long Size, bool Truncated);

    private sealed class TrackInfo
    {
        public uint Id;
        public int Order;
        public uint Handler;
        public uint Timescale;
        public long TkhdDurationAt = -1;      // absolute file offset of the tkhd duration field
        public int TkhdDurationWidth;
        public long TkhdDuration;
        public long MdhdDurationAt = -1;
        public int MdhdDurationWidth;
        public long MdhdDuration;
        public long ElstSum = -1;
        public long SttsDuration;
        public long Samples;
        public long ChunkTableAt = -1;         // absolute file offset of the first stco/co64 entry
        public bool Co64;
        public int VideoCheck;  // how a chunk start of this video track is recognised: 1 length-prefixed NAL units (avc1, hvc1, ...), 2 start code (MPEG-4 part 2, MPEG-2), 0 not at all
        public long[] Offsets = [];
        public long[] ChunkSizes = [];
        public long[] ChunkStart = [];         // decode time of each chunk's first sample, in media timescale
        public long[] ChunkFirstSample = [];   // sample index of each chunk's first sample
        public long[] FirstSampleSize = [];
    }

    public static RepairReport Run(RepairInput input, Stream destination, MediaLimits limits, CancellationToken ct)
    {
        var report = new RepairReportBuilder("isobmff", input.Length);
        var boxes = new List<BoxHeader>();
        long pos = 0;
        long validEnd = 0;
        long count = 0;
        while (pos + 8 <= input.Length)
        {
            ct.ThrowIfCancellationRequested();
            limits.CheckElementCount(++count, pos);
            if (!TryBox(input, pos, out BoxHeader b))
            {
                break;
            }

            boxes.Add(b);
            long end = b.Truncated ? input.Length : pos + b.Size;
            validEnd = end;
            pos = end;
            if (b.Truncated)
            {
                break;
            }
        }

        if (boxes.Count == 0)
        {
            report.Warn("No box could be read from the start of the file.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        long tail = input.Length - validEnd;
        bool fragmented = boxes.Exists(x => x.Type == Moof);
        BoxHeader? moov = boxes.Exists(x => x.Type == Moov) ? boxes.Find(x => x.Type == Moov) : null;
        if (moov is null || moov.Value.Truncated)
        {
            report.Warn(moov is null
                ? "The movie box (moov) is missing: the file was cut before it was written. Reconstructing the index from the media data needs the truncated-file reconstruction (with a reference file from the same device where possible)."
                : "The movie box (moov) is cut off at the end of the file; reconstruction from the media data is needed.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        return fragmented
            ? RunFragmented(input, destination, limits, report, boxes, moov.Value, tail, ct)
            : RunProgressive(input, destination, limits, report, boxes, moov.Value, tail, ct);
    }

    // ---------------------------------------------------------------- boxes

    private static bool TryBox(RepairInput input, long pos, out BoxHeader box)
    {
        box = default;
        ReadOnlySpan<byte> h = input.Peek(pos, 16);
        if (h.Length < 8)
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(h);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        int hdr = 8;
        if (!ValidType(type))
        {
            return false;
        }

        if (size == 1)
        {
            if (h.Length < 16)
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
            hdr = 16;
            if (size < 16)
            {
                return false;
            }
        }
        else if (size == 0)
        {
            size = input.Length - pos;
        }
        else if (size < 8)
        {
            return false;
        }

        box = new BoxHeader(type, pos, hdr, size, pos + size > input.Length);
        return true;
    }

    private static bool ValidType(uint t)
    {
        for (int i = 0; i < 4; i++)
        {
            byte c = (byte)(t >> (24 - (8 * i)));
            if (c < 0x20 || c > 0x7E)
            {
                if (!(i == 0 && c == 0xA9))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Child boxes of a payload held in memory: (type, offset of header within the span, header size, total size).
    private static List<(uint Type, int At, int Hdr, int Size)> Children(ReadOnlySpan<byte> data, int from, int to)
    {
        var list = new List<(uint, int, int, int)>();
        int p = from;
        while (p + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(p + 4)..]);
            int hdr = 8;
            if (size == 1 && p + 16 <= to)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(p + 8)..]);
                hdr = 16;
            }
            else if (size == 0)
            {
                size = to - p;
            }

            if (size < hdr || p + size > to)
            {
                break;
            }

            list.Add((type, p, hdr, (int)size));
            p += (int)size;
        }

        return list;
    }

    // ---------------------------------------------------------------- progressive

    private static RepairReport RunProgressive(RepairInput input, Stream destination, MediaLimits limits, RepairReportBuilder report,
        List<BoxHeader> boxes, BoxHeader moov, long tail, CancellationToken ct)
    {
        limits.CheckAllocation(moov.Size, moov.Offset);
        byte[] m = new byte[moov.Size];
        input.Read(moov.Offset, m);
        var tracks = new List<TrackInfo>();
        long movieTimescale = 1000;
        long mvhdDurationAt = -1;
        int mvhdWidth = 0;
        long mvhdDuration = 0;
        foreach ((uint type, int at, int hdr, int size) in Children(m, moov.HeaderSize, m.Length))
        {
            if (type == Mvhd)
            {
                int v = m[at + hdr];
                int tsAt = at + hdr + 4 + (v == 1 ? 16 : 8);
                movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(tsAt));
                mvhdDurationAt = tsAt + 4;
                mvhdWidth = v == 1 ? 8 : 4;
                mvhdDuration = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(tsAt + 4)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(tsAt + 4));
            }
            else if (type == Trak)
            {
                limits.CheckTracks(tracks.Count + 1, moov.Offset + at);
                TrackInfo? t = ParseTrak(m, at, hdr, size, moov.Offset, tracks.Count, limits);
                if (t is not null)
                {
                    tracks.Add(t);
                }
            }
        }

        if (tracks.Count == 0)
        {
            report.Warn("The movie box holds no usable track.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        // mdat payloads
        var mdats = new List<(long Start, long End)>();
        foreach (BoxHeader b in boxes)
        {
            if (b.Type == Mdat)
            {
                mdats.Add((b.Offset + b.HeaderSize, b.Truncated ? input.Length : b.Offset + b.Size));
            }
        }

        long mdatBytes = 0;
        foreach ((long s, long e) in mdats)
        {
            mdatBytes += e - s;
        }

        // ---- decide the chunk offsets
        long[][]? chosen = null;
        string how = string.Empty;
        RepairConfidence conf = RepairConfidence.High;
        bool originalOk = Validate(input, tracks, t => t.Offsets, mdats, out bool originalVerified);
        if (originalOk)
        {
            chosen = null; // keep
            how = "kept";
        }
        else
        {
            long min = long.MaxValue;
            foreach (TrackInfo t in tracks)
            {
                foreach (long o in t.Offsets)
                {
                    min = Math.Min(min, o);
                }
            }

            bool found = false;
            if (mdats.Count > 0 && min != long.MaxValue && min > 0)
            {
                long delta = mdats[0].Start - min;
                long[][] shifted = [.. tracks.ConvertAll(t => Array.ConvertAll(t.Offsets, o => o + delta))];
                if (delta != 0 && Validate(input, tracks, t => shifted[tracks.IndexOf(t)], mdats, out bool verifiedShift) && verifiedShift)
                {
                    chosen = shifted;
                    how = $"every offset moved by {delta} bytes";
                    found = true;
                }
            }

            // the first chunk need not sit at the start of the media data (some writers put a few bytes of their own first), so the layout
            // is also tried flush with the end of the box
            long chunkBytes = 0;
            foreach (TrackInfo t in tracks)
            {
                foreach (long c in t.ChunkSizes)
                {
                    chunkBytes += c;
                }
            }

            var starts = new List<long>();
            if (mdats.Count == 1)
            {
                starts.Add(mdats[0].Start);
                long flush = mdats[0].End - chunkBytes;
                if (flush > mdats[0].Start && flush - mdats[0].Start <= 1 << 16)
                {
                    starts.Add(flush);
                }
            }
            else
            {
                starts.Add(0);
            }

            foreach (long layoutStart in starts)
            {
                if (found)
                {
                    break;
                }

                foreach (bool descending in new[] { false, true })
                {
                    long[][] laid = LayoutByTime(tracks, mdats.Count > 0 ? layoutStart : 0, descending);
                    bool fits = mdats.Count == 1 && LayoutEnd(tracks, laid) <= mdats[0].End;
                    if (fits && Validate(input, tracks, t => laid[tracks.IndexOf(t)], mdats, out bool verified) && (tracks.Count == 1 || !tracks.Exists(t => t.Handler == Vide && t.VideoCheck == 0)))
                    {
                        chosen = laid;
                        how = "chunks laid out one after another in the order of their decode times";
                        conf = verified ? RepairConfidence.High : RepairConfidence.Low;
                        if (verified && tracks.Count > 1 && !descending)
                        {
                            conf = RepairConfidence.Medium;
                        }

                        found = true;
                        break;
                    }
                }
            }

            if (!found && mdats.Count == 1)
            {
                foreach (long walkStart in starts)
                {
                    long[][]? walked = WalkLayout(input, tracks, (walkStart, mdats[0].End));
                    if (walked is not null && Validate(input, tracks, t => walked[tracks.IndexOf(t)], mdats, out bool verifiedWalk) && verifiedWalk)
                    {
                        chosen = walked;
                        how = "chunks found one after another in the media data: each video chunk is the one that starts with a plausible sample, the others follow in decode-time order";
                        conf = RepairConfidence.Medium;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                report.Warn("The chunk offsets do not point at media data and could not be rebuilt from the sample tables (the interleaving is not recoverable here).");
                report.Add(RepairAction.Kept, "stco", -1, moov.Offset, moov.Size, "Offsets left as they were.", RepairConfidence.None);
                return report.Build(RepairStatus.Unrepairable, 0);
            }
        }

        _ = originalVerified;
        // ---- plan patches
        var patches = new List<(long At, byte[] Bytes)>();
        for (int i = 0; i < tracks.Count; i++)
        {
            TrackInfo t = tracks[i];
            if (chosen is not null && t.ChunkTableAt >= 0)
            {
                byte[] b = new byte[t.Offsets.Length * (t.Co64 ? 8 : 4)];
                for (int k = 0; k < t.Offsets.Length; k++)
                {
                    long o = chosen[i][k];
                    if (t.Co64)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(k * 8), (ulong)o);
                    }
                    else if (o > uint.MaxValue)
                    {
                        report.Warn($"Track {t.Id} needs 64-bit chunk offsets (co64); rewriting the movie box is not done here.");
                        return report.Build(RepairStatus.PartiallyRepaired, 0);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(k * 4), (uint)o);
                    }
                }

                patches.Add((t.ChunkTableAt, b));
                report.Add(RepairAction.Rebuilt, t.Co64 ? "co64" : "stco", (int)t.Id, t.ChunkTableAt, b.Length, $"{t.Offsets.Length} chunk offsets rebuilt from the sample tables: {how}.", conf);
            }

            // durations
            if (t.MdhdDurationAt >= 0 && t.SttsDuration > 0 && t.MdhdDuration != t.SttsDuration)
            {
                patches.Add((t.MdhdDurationAt, Number(t.SttsDuration, t.MdhdDurationWidth)));
                report.Add(RepairAction.Fixed, "mdhd", (int)t.Id, t.MdhdDurationAt, t.MdhdDurationWidth, $"Media duration {t.MdhdDuration} set to {t.SttsDuration} (the sum of the sample durations).", RepairConfidence.High);
                t.MdhdDuration = t.SttsDuration;
            }

            if (t.TkhdDurationAt >= 0 && t.Timescale > 0)
            {
                long want = t.ElstSum >= 0 ? t.ElstSum : RescaleRounded(t.MdhdDuration, movieTimescale, t.Timescale);
                if (want > 0 && Math.Abs(t.TkhdDuration - want) > 1)
                {
                    patches.Add((t.TkhdDurationAt, Number(want, t.TkhdDurationWidth)));
                    report.Add(RepairAction.Fixed, "tkhd", (int)t.Id, t.TkhdDurationAt, t.TkhdDurationWidth, $"Track duration {t.TkhdDuration} set to {want}.", RepairConfidence.High);
                    t.TkhdDuration = want;
                }
            }
        }

        long movieWant = 0;
        foreach (TrackInfo t in tracks)
        {
            movieWant = Math.Max(movieWant, t.TkhdDuration);
        }

        if (mvhdDurationAt >= 0 && movieWant > 0 && mvhdDuration != movieWant)
        {
            patches.Add((moov.Offset + mvhdDurationAt, Number(movieWant, mvhdWidth)));
            report.Add(RepairAction.Fixed, "mvhd", -1, moov.Offset + mvhdDurationAt, mvhdWidth, $"Movie duration {mvhdDuration} set to {movieWant}.", RepairConfidence.High);
        }

        long outLength = input.Length - tail;
        foreach (BoxHeader b in boxes)
        {
            if (b.Truncated && b.Type == Mdat)
            {
                long real = input.Length - b.Offset;
                if (b.HeaderSize == 8 && real <= uint.MaxValue)
                {
                    patches.Add((b.Offset, Number(real, 4)));
                    report.Add(RepairAction.Fixed, "mdat", -1, b.Offset, 4, $"The media data box states {b.Size} bytes but {real} are present; its size is corrected.", RepairConfidence.High);
                }
                else if (b.HeaderSize == 16)
                {
                    patches.Add((b.Offset + 8, Number(real, 8)));
                    report.Add(RepairAction.Fixed, "mdat", -1, b.Offset + 8, 8, $"The media data box states {b.Size} bytes but {real} are present; its size is corrected.", RepairConfidence.High);
                }
            }
        }

        if (tail > 0)
        {
            report.Add(RepairAction.Dropped, "garbage", -1, outLength, tail, "Bytes after the last box do not form a box.", RepairConfidence.High);
        }

        long beyond = 0;
        foreach (TrackInfo t in tracks)
        {
            report.Tracks.Add(new RepairTrackReport((int)t.Id, string.Empty, t.Samples, 0, chosen is null ? RepairConfidence.High : conf,
                mdats.ConvertAll(x => new ByteRange(x.Start, x.End - x.Start))));
            beyond += CountBeyond(t, chosen is null ? t.Offsets : chosen[tracks.IndexOf(t)], input.Length);
        }

        if (beyond > 0)
        {
            report.Warn($"{beyond} chunks lie beyond the end of the file (the media data is cut off); removing them from the sample tables needs reconstruction.");
        }

        if (patches.Count == 0 && tail == 0)
        {
            report.Add(RepairAction.Kept, "stco", -1, moov.Offset, moov.Size, "Chunk offsets point at media data and the durations agree with the sample tables.", RepairConfidence.High);
            using (RepairOutput copy = new(destination, ct))
            {
                copy.Copy(input, 0, input.Length);
            }

            return report.Build(beyond > 0 ? RepairStatus.PartiallyRepaired : RepairStatus.NothingToRepair, input.Length);
        }

        {
            using var output = new RepairOutput(destination, ct);
            output.Copy(input, 0, outLength);
            foreach ((long at, byte[] bytes) in patches)
            {
                output.Patch(at, bytes);
            }

            output.Flush();
            return report.Build(beyond > 0 ? RepairStatus.PartiallyRepaired : RepairStatus.Repaired, output.Position);
        }
    }

    private static long CountBeyond(TrackInfo t, long[] offsets, long length)
    {
        long n = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] + t.ChunkSizes[i] > length)
            {
                n++;
            }
        }

        return n;
    }

    private static byte[] Number(long value, int width)
    {
        byte[] b = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            b[i] = (byte)value;
            value >>= 8;
        }

        return b;
    }

    private static TrackInfo? ParseTrak(byte[] m, int at, int hdr, int size, long moovOffset, int order, MediaLimits limits)
    {
        var t = new TrackInfo { Order = order };
        byte[] stszData = [];
        int stszAt = -1;
        List<(uint First, uint PerChunk)> stsc = [];
        List<(uint Count, uint Delta)> stts = [];
        int stcoAt = -1;
        int stcoCount = 0;
        foreach ((uint type, int a, int h, int s) in Children(m, at + hdr, at + size))
        {
            if (type == Tkhd)
            {
                int v = m[a + h];
                t.Id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(a + h + 4 + (v == 1 ? 16 : 8)));
                int durAt = a + h + 4 + (v == 1 ? 16 : 8) + 4 + 4;
                t.TkhdDurationAt = moovOffset + durAt;
                t.TkhdDurationWidth = v == 1 ? 8 : 4;
                t.TkhdDuration = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(durAt)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(durAt));
            }
            else if (type == Edts)
            {
                foreach ((uint et, int ea, int eh, int es) in Children(m, a + h, a + s))
                {
                    if (et == Elst)
                    {
                        int v = m[ea + eh];
                        uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ea + eh + 4));
                        int entry = v == 1 ? 20 : 12;
                        long sum = 0;
                        for (uint k = 0; k < n && ea + eh + 8 + ((k + 1) * entry) <= ea + es; k++)
                        {
                            int p = ea + eh + 8 + (int)(k * entry);
                            sum += v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p));
                        }

                        t.ElstSum = sum;
                    }
                }
            }
            else if (type == Mdia)
            {
                foreach ((uint mt, int ma, int mh, int ms) in Children(m, a + h, a + s))
                {
                    if (mt == Mdhd)
                    {
                        int v = m[ma + mh];
                        int tsAt = ma + mh + 4 + (v == 1 ? 16 : 8);
                        t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(tsAt));
                        t.MdhdDurationAt = moovOffset + tsAt + 4;
                        t.MdhdDurationWidth = v == 1 ? 8 : 4;
                        t.MdhdDuration = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(tsAt + 4)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(tsAt + 4));
                    }
                    else if (mt == Hdlr)
                    {
                        t.Handler = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ma + mh + 8));
                    }
                    else if (mt == Minf)
                    {
                        foreach ((uint nt, int na, int nh, int ns) in Children(m, ma + mh, ma + ms))
                        {
                            if (nt != Stbl)
                            {
                                continue;
                            }

                            foreach ((uint st, int sa, int sh, int ss) in Children(m, na + nh, na + ns))
                            {
                                int body = sa + sh + 4;
                                if (st == Stsd && ss >= sh + 8 + 8 + 4)
                                {
                                    // 14496-15: these sample entries carry length-prefixed NAL units
                                    uint entry = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(sa + sh + 12));
                                    t.VideoCheck = entry is 0x6D703476 or 0x6D703276 or 0x6D703176 ? 2 : entry is 0x61766331 or 0x61766333 or 0x68766331 or 0x68657631 or 0x76766331 or 0x76766931 or 0x64766831 or 0x64766865 or 0x64766131 or 0x64766176 ? 1 : 0;
                                }
                                else if (st == Stsz && ss >= sh + 12)
                                {
                                    stszAt = sa + sh;
                                }
                                else if (st == Stsc)
                                {
                                    uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body));
                                    limits.CheckSampleTable(n, moovOffset + sa);
                                    for (uint k = 0; k < n && body + 4 + ((k + 1) * 12) <= sa + ss; k++)
                                    {
                                        stsc.Add((BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4 + (int)(k * 12))), BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 8 + (int)(k * 12)))));
                                    }
                                }
                                else if (st == Stts)
                                {
                                    uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body));
                                    limits.CheckSampleTable(n, moovOffset + sa);
                                    for (uint k = 0; k < n && body + 4 + ((k + 1) * 8) <= sa + ss; k++)
                                    {
                                        stts.Add((BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4 + (int)(k * 8))), BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 8 + (int)(k * 8)))));
                                    }
                                }
                                else if (st == Stco || st == Co64)
                                {
                                    stcoCount = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body));
                                    limits.CheckSampleTable(stcoCount, moovOffset + sa);
                                    t.Co64 = st == Co64;
                                    int w = t.Co64 ? 8 : 4;
                                    stcoCount = Math.Min(stcoCount, (ss - sh - 8) / w);
                                    stcoAt = body + 4;
                                    t.ChunkTableAt = moovOffset + stcoAt;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (t.Id == 0 || stszAt < 0 || stsc.Count == 0 || stcoAt < 0)
        {
            return null;
        }

        // sample sizes
        uint constant = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(stszAt + 4));
        uint total = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(stszAt + 8));
        limits.CheckSampleTable(total, moovOffset + stszAt);
        if (constant == 0 && stszAt + 12 + (total * 4L) > m.Length)
        {
            total = (uint)Math.Max(0, (m.Length - stszAt - 12) / 4);
        }

        _ = stszData;
        t.Samples = total;
        long Size(long i) => constant != 0 ? constant : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(stszAt + 12 + (int)(i * 4)));

        t.Offsets = new long[stcoCount];
        t.ChunkSizes = new long[stcoCount];
        t.ChunkStart = new long[stcoCount];
        t.ChunkFirstSample = new long[stcoCount];
        t.FirstSampleSize = new long[stcoCount];
        for (int i = 0; i < stcoCount; i++)
        {
            t.Offsets[i] = t.Co64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(stcoAt + (i * 8))) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(stcoAt + (i * 4)));
        }

        // samples per chunk and decode times
        long sample = 0;
        int sttsRun = 0;
        long sttsLeft = stts.Count > 0 ? stts[0].Count : 0;
        long time = 0;
        int sc = 0;
        for (int c = 0; c < stcoCount; c++)
        {
            while (sc + 1 < stsc.Count && c + 1 >= stsc[sc + 1].First)
            {
                sc++;
            }

            long per = stsc[sc].PerChunk;
            t.ChunkFirstSample[c] = sample;
            t.ChunkStart[c] = time;
            long bytes = 0;
            for (long k = 0; k < per && sample < total; k++, sample++)
            {
                long sz = Size(sample);
                if (k == 0)
                {
                    t.FirstSampleSize[c] = sz;
                }

                bytes += sz;
                while (sttsLeft == 0 && sttsRun + 1 < stts.Count)
                {
                    sttsLeft = stts[++sttsRun].Count;
                }

                if (sttsLeft > 0)
                {
                    time += stts[sttsRun].Delta;
                    sttsLeft--;
                }
            }

            t.ChunkSizes[c] = bytes;
        }

        foreach ((uint n, uint d) in stts)
        {
            t.SttsDuration += (long)n * d;
        }

        return t;
    }

    // The chunk table must put every chunk inside the media data without overlap; for video, every chunk must begin with a plausible
    // length-prefixed NAL unit (14496-15: a 4-byte length no longer than the sample), which is how a wrong guess shows up.
    private static bool Validate(RepairInput input, List<TrackInfo> tracks, Func<TrackInfo, long[]> offsets, List<(long Start, long End)> mdats, out bool verified)
    {
        verified = false;
        var spans = new List<(long Start, long End)>();
        bool anyVideo = false;
        bool allVideoOk = true;
        foreach (TrackInfo t in tracks)
        {
            long[] o = offsets(t);
            for (int i = 0; i < o.Length; i++)
            {
                if (t.ChunkSizes[i] == 0)
                {
                    continue;
                }

                // a chunk that starts inside the media data but runs past the end of the file is a cut-off recording: it is reported, not a wrong offset
                bool cut = o[i] + t.ChunkSizes[i] > input.Length;
                if (o[i] >= input.Length && mdats.Count > 0 && o[i] > mdats[0].Start)
                {
                    continue; // the whole chunk is missing
                }

                if (o[i] <= 0 || o[i] >= input.Length || !mdats.Exists(x => o[i] >= x.Start && (cut ? o[i] < x.End : o[i] + t.ChunkSizes[i] <= x.End)))
                {
                    return false;
                }

                if (cut)
                {
                    continue;
                }

                spans.Add((o[i], o[i] + t.ChunkSizes[i]));
                if (t.Handler == Vide && t.VideoCheck != 0)
                {
                    anyVideo = true;
                    ReadOnlySpan<byte> h = input.Peek(o[i], 8);
                    if (h.Length < 5 || t.FirstSampleSize[i] < 5)
                    {
                        allVideoOk = false;
                        continue;
                    }

                    allVideoOk &= PlausibleStart(t, h, t.FirstSampleSize[i]);
                }
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start < spans[i - 1].End)
            {
                return false;
            }
        }

        verified = anyVideo && allVideoOk;
        return !anyVideo || allVideoOk;
    }

    // A chunk of video starts with a sample whose first bytes look like the codec's framing.
    private static bool PlausibleStart(TrackInfo t, ReadOnlySpan<byte> h, long firstSampleSize)
    {
        if (t.VideoCheck == 2)
        {
            return h.Length >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1;
        }

        uint len = BinaryPrimitives.ReadUInt32BigEndian(h);
        return len >= 1 && len + 4 <= firstSampleSize && (h[4] & 0x80) == 0;
    }

    private static long[][] LayoutByTime(List<TrackInfo> tracks, long start, bool descendingTies)
    {
        var all = new List<(int Track, int Chunk)>();
        for (int i = 0; i < tracks.Count; i++)
        {
            for (int c = 0; c < tracks[i].Offsets.Length; c++)
            {
                all.Add((i, c));
            }
        }

        all.Sort((a, b) =>
        {
            TrackInfo x = tracks[a.Track];
            TrackInfo y = tracks[b.Track];
            Int128 l = (Int128)x.ChunkStart[a.Chunk] * Math.Max(1, y.Timescale);
            Int128 r = (Int128)y.ChunkStart[b.Chunk] * Math.Max(1, x.Timescale);
            int cmp = l.CompareTo(r);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = descendingTies ? b.Track.CompareTo(a.Track) : a.Track.CompareTo(b.Track);
            return cmp != 0 ? cmp : a.Chunk.CompareTo(b.Chunk);
        });
        long[][] result = [.. tracks.ConvertAll(t => new long[t.Offsets.Length])];
        long pos = start;
        foreach ((int tr, int ch) in all)
        {
            result[tr][ch] = pos;
            pos += tracks[tr].ChunkSizes[ch];
        }

        return result;
    }

    // Walks the media data from its start. Chunk sizes are known from the sample tables, only the order of the tracks' chunks is not.
    // At each position a video chunk (length-prefixed NAL units) is recognised by its first sample starting with a plausible length; when
    // none does, the next chunk of the non-NAL track that is earliest in time is taken. Returns null when the walk does not fit.
    private static long[][]? WalkLayout(RepairInput input, List<TrackInfo> tracks, (long Start, long End) mdat)
    {
        if (!tracks.Exists(t => t.Handler == Vide && t.VideoCheck != 0) || tracks.Exists(t => t.Handler == Vide && t.VideoCheck == 0))
        {
            return null;
        }

        long[][] result = [.. tracks.ConvertAll(t => new long[t.Offsets.Length])];
        int[] next = new int[tracks.Count];
        long pos = mdat.Start;
        while (true)
        {
            int pick = -1;
            for (int i = 0; i < tracks.Count; i++)
            {
                TrackInfo t = tracks[i];
                if (next[i] >= t.Offsets.Length || t.Handler != Vide || t.VideoCheck == 0 || t.ChunkSizes[next[i]] == 0)
                {
                    continue;
                }

                ReadOnlySpan<byte> h = input.Peek(pos, 8);
                long first = t.FirstSampleSize[next[i]];
                if (h.Length >= 5 && first >= 5 && pos + t.ChunkSizes[next[i]] <= mdat.End)
                {
                    if (PlausibleStart(t, h, first))
                    {
                        pick = i;
                        break;
                    }
                }
            }

            if (pick < 0)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    TrackInfo t = tracks[i];
                    if (next[i] >= t.Offsets.Length || (t.Handler == Vide && t.VideoCheck != 0))
                    {
                        continue;
                    }

                    if (pick < 0)
                    {
                        pick = i;
                        continue;
                    }

                    TrackInfo q = tracks[pick];
                    Int128 l = (Int128)t.ChunkStart[next[i]] * Math.Max(1, q.Timescale);
                    Int128 r = (Int128)q.ChunkStart[next[pick]] * Math.Max(1, t.Timescale);
                    if (l < r)
                    {
                        pick = i;
                    }
                }
            }

            if (pick < 0)
            {
                break;
            }

            result[pick][next[pick]] = pos;
            pos += tracks[pick].ChunkSizes[next[pick]];
            next[pick]++;
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            if (next[i] < tracks[i].Offsets.Length)
            {
                return null; // a video chunk that no position matched
            }
        }

        return pos <= mdat.End ? result : null;
    }

    private static long LayoutEnd(List<TrackInfo> tracks, long[][] laid)
    {
        long end = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            for (int c = 0; c < laid[i].Length; c++)
            {
                end = Math.Max(end, laid[i][c] + tracks[i].ChunkSizes[c]);
            }
        }

        return end;
    }
}

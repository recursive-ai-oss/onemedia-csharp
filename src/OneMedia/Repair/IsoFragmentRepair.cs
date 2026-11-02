using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Repair;

internal static partial class IsoRepair
{
    private sealed class FragTraf
    {
        public uint TrackId;
        public long Time;
        public long Duration;
        public bool Sync;
        public int Number;
        public int BaseOffsetAt = -1; // position of base_data_offset inside the moof, when the tfhd carries one
    }

    private sealed class Fragment
    {
        public BoxHeader Moof;
        public long End;
        public byte[]? MoofBytes;
        public List<FragTraf> Trafs = [];
        public long OutOffset;
    }

    private static RepairReport RunFragmented(RepairInput input, string outputPath, MediaLimits limits, RepairReportBuilder report,
        List<BoxHeader> boxes, BoxHeader moov, long tail, CancellationToken ct)
    {
        limits.CheckAllocation(moov.Size, moov.Offset);
        byte[] m = new byte[moov.Size];
        input.Read(moov.Offset, m);
        var timescale = new Dictionary<uint, uint>();
        var trexDuration = new Dictionary<uint, uint>();
        var handler = new Dictionary<uint, uint>();
        long movieTimescale = 1000;
        long mehdAt = -1;
        int mehdWidth = 0;
        long mehdValue = 0;
        foreach ((uint type, int at, int hdr, int size) in Children(m, moov.HeaderSize, m.Length))
        {
            if (type == Mvhd)
            {
                int v = m[at + hdr];
                movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at + hdr + 4 + (v == 1 ? 16 : 8)));
            }
            else if (type == Trak)
            {
                TrackInfo? t = ParseTrak(m, at, hdr, size, moov.Offset, 0, limits);
                if (t is null)
                {
                    // fragmented files have empty sample tables; read id and timescale only
                    t = ParseTrakHeaderOnly(m, at, hdr, size);
                }

                if (t is not null)
                {
                    timescale[t.Id] = t.Timescale;
                    handler[t.Id] = t.Handler;
                }
            }
            else if (type == Mvex)
            {
                foreach ((uint et, int ea, int eh, int es) in Children(m, at + hdr, at + size))
                {
                    if (et == Trex && es >= eh + 24)
                    {
                        trexDuration[BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ea + eh + 4))] = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ea + eh + 12));
                    }
                    else if (et == Mehd)
                    {
                        int v = m[ea + eh];
                        mehdAt = moov.Offset + ea + eh + 4;
                        mehdWidth = v == 1 ? 8 : 4;
                        mehdValue = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(ea + eh + 4)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ea + eh + 4));
                    }
                }
            }
        }

        // ---- fragments
        var frags = new List<Fragment>();
        var oldSidx = new List<BoxHeader>();
        BoxHeader? oldMfra = null;
        var nextTime = new Dictionary<uint, long>();
        for (int i = 0; i < boxes.Count; i++)
        {
            BoxHeader b = boxes[i];
            ct.ThrowIfCancellationRequested();
            if (b.Type == Sidx)
            {
                oldSidx.Add(b);
            }
            else if (b.Type == Mfra)
            {
                oldMfra = b;
            }
            else if (b.Type == Moof)
            {
                BoxHeader? next = i + 1 < boxes.Count ? boxes[i + 1] : null;
                if (b.Truncated || next is null || next.Value.Type != Mdat || next.Value.Truncated)
                {
                    bool toEnd = b.Truncated || next is null || next.Value.Truncated;
                    long dropEnd = toEnd ? input.Length - tail : b.Offset + b.Size;
                    report.Add(RepairAction.Dropped, "moof", -1, b.Offset, dropEnd - b.Offset,
                        "A fragment is cut off (its movie fragment box or media data box is incomplete); it is left out.", RepairConfidence.High);
                    if (toEnd)
                    {
                        break;
                    }

                    continue;
                }

                if (b.Size > limits.MaxAllocationBytes)
                {
                    limits.CheckAllocation(b.Size, b.Offset);
                }

                var f = new Fragment { Moof = b, End = next.Value.Offset + next.Value.Size, MoofBytes = new byte[b.Size] };
                input.Read(b.Offset, f.MoofBytes);
                ParseMoof(f, timescale, trexDuration, nextTime);
                frags.Add(f);
            }
        }

        if (frags.Count == 0)
        {
            report.Warn("The file holds no complete fragment.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        bool droppedTail = report.Changed;
        long dataEnd = frags[^1].End;
        foreach (BoxHeader b in boxes)
        {
            // boxes after the last kept fragment that are not mfra: kept as they are
            if (b.Offset >= dataEnd && b.Type != Mfra && !b.Truncated && b.Type != Moof && b.Type != Mdat)
            {
                dataEnd = Math.Max(dataEnd, b.Offset + b.Size);
            }
        }

        var trackIds = new SortedSet<uint>();
        foreach (Fragment f in frags)
        {
            foreach (FragTraf t in f.Trafs)
            {
                trackIds.Add(t.TrackId);
            }
        }

        bool writeSidx = oldSidx.Count == 1 && trackIds.Count == 1 && !oldSidx[0].Truncated && oldSidx[0].Offset + oldSidx[0].Size == frags[0].Moof.Offset;
        if (oldSidx.Count > 0 && !writeSidx)
        {
            report.Warn("The segment index (sidx) is kept as it was: it is not a single index directly before the first fragment of a one-track file.");
        }

        uint sidxTrack = trackIds.Min;
        long sidxSize = writeSidx ? 40 + (12L * frags.Count) : 0;

        // ---- is it healthy?
        bool mehdOk = true;
        long fragDuration = 0;
        foreach (uint id in trackIds)
        {
            long sum = 0;
            foreach (Fragment f in frags)
            {
                foreach (FragTraf t in f.Trafs)
                {
                    if (t.TrackId == id)
                    {
                        sum += t.Duration;
                    }
                }
            }

            uint ts = timescale.TryGetValue(id, out uint v) && v != 0 ? v : 1;
            fragDuration = Math.Max(fragDuration, (long)Math.Round((double)sum * movieTimescale / ts));
        }

        if (mehdAt >= 0 && Math.Abs(mehdValue - fragDuration) > 1)
        {
            mehdOk = false;
        }

        bool indexOk = oldMfra is not null && MfraMatches(input, oldMfra.Value, frags) && (oldSidx.Count == 0 || !writeSidx || SidxMatches(input, oldSidx, frags, sidxTrack));
        if (tail == 0 && !droppedTail && mehdOk && indexOk && oldMfra!.Value.Offset + oldMfra.Value.Size == input.Length)
        {
            report.Add(RepairAction.Kept, "mfra", -1, oldMfra.Value.Offset, oldMfra.Value.Size, "Every random access entry points at a fragment with the same decode time.", RepairConfidence.High);
            using (RepairOutput copy = new(new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), ct))
            {
                copy.Copy(input, 0, input.Length);
            }

            AddFragmentTracks(report, trackIds, frags, 0);
            return report.Build(RepairStatus.NothingToRepair, input.Length);
        }

        // ---- write
        bool ok = false;
        try
        {
            using var output = new RepairOutput(new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16), ct);
            long outMoov = -1;
            bool sidxPlaced = false;
            var fragByMoof = new Dictionary<long, Fragment>();
            foreach (Fragment f in frags)
            {
                fragByMoof[f.Moof.Offset] = f;
            }

                long delta = 0; // how far the first fragment moved because the sidx changed size
            long oldSidxBytes = 0;
            foreach (BoxHeader s in oldSidx)
            {
                oldSidxBytes += s.Size;
            }

            if (writeSidx)
            {
                delta = sidxSize - oldSidxBytes;
            }

            int idx = 0;
            while (idx < boxes.Count)
            {
                BoxHeader b = boxes[idx];
                if (b.Offset >= dataEnd)
                {
                    break;
                }

                if (b.Type == Sidx && writeSidx)
                {
                    if (!sidxPlaced)
                    {
                        sidxPlaced = true;
                        output.Write(BuildSidx(frags, sidxTrack, timescale));
                    }

                    idx++;
                    continue;
                }

                if (b.Type == Mfra)
                {
                    idx++;
                    continue;
                }

                if (b.Type == Moof)
                {
                    if (!fragByMoof.TryGetValue(b.Offset, out Fragment? f))
                    {
                        idx++;
                        continue;
                    }

                    f.OutOffset = output.Position;
                    byte[] moofBytes = f.MoofBytes!;
                    foreach (FragTraf t in f.Trafs)
                    {
                        if (t.BaseOffsetAt >= 0 && delta != 0)
                        {
                            long old = (long)BinaryPrimitives.ReadUInt64BigEndian(moofBytes.AsSpan(t.BaseOffsetAt));
                            BinaryPrimitives.WriteUInt64BigEndian(moofBytes.AsSpan(t.BaseOffsetAt), (ulong)(old + delta));
                        }
                    }

                    output.Write(moofBytes);
                    BoxHeader mdat = boxes[idx + 1];
                    output.Copy(input, mdat.Offset, mdat.Size);
                    idx += 2;
                    continue;
                }

                if (b.Type == Moov)
                {
                    outMoov = output.Position;
                }

                output.Copy(input, b.Offset, b.Truncated ? input.Length - b.Offset : b.Size);
                idx++;
            }

            if (mehdAt >= 0 && !mehdOk && outMoov >= 0)
            {
                output.Patch(outMoov + (mehdAt - moov.Offset), Number(fragDuration, mehdWidth));
                report.Add(RepairAction.Fixed, "mehd", -1, mehdAt, mehdWidth, $"Fragment duration {mehdValue} set to {fragDuration}.", RepairConfidence.High);
            }

            if (writeSidx)
            {
                report.Add(RepairAction.Rebuilt, "sidx", (int)sidxTrack, oldSidx[0].Offset, oldSidxBytes, $"Segment index of {frags.Count} fragments built from the fragment boxes.", RepairConfidence.High);
            }

            byte[] mfra = BuildMfra(frags, trackIds);
            output.Write(mfra);
            report.Add(RepairAction.Rebuilt, "mfra", -1, oldMfra?.Offset ?? input.Length, oldMfra?.Size ?? 0,
                $"Random access index with one entry per fragment for {trackIds.Count} track(s) built from the fragment boxes{(oldMfra is null ? "; the file had none." : ".")}", RepairConfidence.High);
            if (tail > 0)
            {
                report.Add(RepairAction.Dropped, "garbage", -1, input.Length - tail, tail, "Bytes after the last box do not form a box.", RepairConfidence.High);
            }

            AddFragmentTracks(report, trackIds, frags, 0);
            output.Flush();
            ok = true;
            return report.Build(RepairStatus.Repaired, output.Position);
        }
        finally
        {
            if (!ok)
            {
                File.Delete(outputPath);
            }
        }
    }

    private static void AddFragmentTracks(RepairReportBuilder report, SortedSet<uint> trackIds, List<Fragment> frags, long dropped)
    {
        foreach (uint id in trackIds)
        {
            long n = 0;
            foreach (Fragment f in frags)
            {
                foreach (FragTraf t in f.Trafs)
                {
                    n += t.TrackId == id ? 1 : 0;
                }
            }

            report.Tracks.Add(new RepairTrackReport((int)id, string.Empty, n, dropped, RepairConfidence.High, [new ByteRange(frags[0].Moof.Offset, frags[^1].End - frags[0].Moof.Offset)]));
        }
    }

    private static TrackInfo? ParseTrakHeaderOnly(byte[] m, int at, int hdr, int size)
    {
        var t = new TrackInfo();
        foreach ((uint type, int a, int h, int s) in Children(m, at + hdr, at + size))
        {
            if (type == Tkhd)
            {
                int v = m[a + h];
                t.Id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(a + h + 4 + (v == 1 ? 16 : 8)));
            }
            else if (type == Mdia)
            {
                foreach ((uint mt, int ma, int mh, int _) in Children(m, a + h, a + s))
                {
                    if (mt == Mdhd)
                    {
                        int v = m[ma + mh];
                        t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ma + mh + 4 + (v == 1 ? 16 : 8)));
                    }
                    else if (mt == Hdlr)
                    {
                        t.Handler = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ma + mh + 8));
                    }
                }
            }
        }

        return t.Id == 0 ? null : t;
    }

    // ISO/IEC 14496-12 §8.8.7 tfhd, §8.8.12 tfdt, §8.8.8 trun.
    private static void ParseMoof(Fragment f, Dictionary<uint, uint> timescale, Dictionary<uint, uint> trexDuration, Dictionary<uint, long> nextTime)
    {
        byte[] b = f.MoofBytes!;
        int number = 0;
        foreach ((uint type, int at, int hdr, int size) in Children(b, f.Moof.HeaderSize, b.Length))
        {
            if (type != Traf)
            {
                continue;
            }

            var t = new FragTraf { Number = ++number, Sync = true };
            uint defaultDuration = 0;
            bool haveTime = false;
            bool firstRun = true;
            foreach ((uint ct, int ca, int ch, int cs) in Children(b, at + hdr, at + size))
            {
                int body = ca + ch;
                if (ct == Tfhd && cs >= ch + 8)
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body)) & 0xFFFFFF;
                    t.TrackId = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 4));
                    int p = body + 8;
                    if ((flags & 0x1) != 0)
                    {
                        t.BaseOffsetAt = p;
                        p += 8;
                    }

                    if ((flags & 0x2) != 0)
                    {
                        p += 4;
                    }

                    defaultDuration = (flags & 0x8) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)) : trexDuration.GetValueOrDefault(t.TrackId);
                }
                else if (ct == Tfdt && cs >= ch + 8)
                {
                    int v = b[body];
                    t.Time = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(body + 4)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 4));
                    haveTime = true;
                }
                else if (ct == Trun && cs >= ch + 8)
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body)) & 0xFFFFFF;
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 4));
                    int p = body + 8;
                    if ((flags & 0x1) != 0)
                    {
                        p += 4;
                    }

                    uint firstFlags = 0;
                    bool haveFirstFlags = false;
                    if ((flags & 0x4) != 0)
                    {
                        firstFlags = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
                        haveFirstFlags = true;
                        p += 4;
                    }

                    int per = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                    long entries = per == 0 ? 0 : Math.Min(n, (at + size - p) / per);
                    long dur = 0;
                    if ((flags & 0x100) != 0)
                    {
                        for (long s = 0; s < entries; s++)
                        {
                            dur += BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + (int)(s * per)));
                        }
                    }
                    else
                    {
                        dur = (long)n * defaultDuration;
                    }

                    t.Duration += dur;
                    if (firstRun)
                    {
                        firstRun = false;
                        if (haveFirstFlags)
                        {
                            t.Sync = (firstFlags & 0x10000) == 0;
                        }
                        else if ((flags & 0x400) != 0 && entries > 0)
                        {
                            int off = p + ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0);
                            t.Sync = (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off)) & 0x10000) == 0;
                        }
                    }
                }
            }

            if (!haveTime)
            {
                t.Time = nextTime.GetValueOrDefault(t.TrackId);
            }

            nextTime[t.TrackId] = t.Time + t.Duration;
            _ = timescale;
            f.Trafs.Add(t);
        }
    }

    // §8.8.10 mfra / §8.8.11 mfro / tfra: version 1 with one-byte traf, trun and sample numbers.
    private static byte[] BuildMfra(List<Fragment> frags, SortedSet<uint> trackIds)
    {
        var body = new List<byte>();
        foreach (uint id in trackIds)
        {
            var entries = new List<byte>();
            uint count = 0;
            foreach (Fragment f in frags)
            {
                foreach (FragTraf t in f.Trafs)
                {
                    if (t.TrackId != id)
                    {
                        continue;
                    }

                    byte[] e = new byte[19];
                    BinaryPrimitives.WriteUInt64BigEndian(e, (ulong)t.Time);
                    BinaryPrimitives.WriteUInt64BigEndian(e.AsSpan(8), (ulong)f.OutOffset);
                    e[16] = (byte)Math.Min(255, t.Number);
                    e[17] = 1;
                    e[18] = 1;
                    entries.AddRange(e);
                    count++;
                }
            }

            byte[] tfra = new byte[8 + 4 + 4 + 4 + 4 + entries.Count];
            BinaryPrimitives.WriteUInt32BigEndian(tfra, (uint)tfra.Length);
            BinaryPrimitives.WriteUInt32BigEndian(tfra.AsSpan(4), 0x74667261);
            tfra[8] = 1; // version
            BinaryPrimitives.WriteUInt32BigEndian(tfra.AsSpan(12), id);
            BinaryPrimitives.WriteUInt32BigEndian(tfra.AsSpan(20), count);
            entries.CopyTo(tfra, 24);
            body.AddRange(tfra);
        }

        byte[] mfra = new byte[8 + body.Count + 16];
        BinaryPrimitives.WriteUInt32BigEndian(mfra, (uint)mfra.Length);
        BinaryPrimitives.WriteUInt32BigEndian(mfra.AsSpan(4), Mfra);
        body.CopyTo(mfra, 8);
        int mfro = 8 + body.Count;
        BinaryPrimitives.WriteUInt32BigEndian(mfra.AsSpan(mfro), 16);
        BinaryPrimitives.WriteUInt32BigEndian(mfra.AsSpan(mfro + 4), 0x6D66726F);
        BinaryPrimitives.WriteUInt32BigEndian(mfra.AsSpan(mfro + 12), (uint)mfra.Length);
        return mfra;
    }

    // §8.16.3 sidx, version 1: references to media (type 0), each one fragment, starting with a stream access point when its first sample is sync.
    private static byte[] BuildSidx(List<Fragment> frags, uint track, Dictionary<uint, uint> timescale)
    {
        byte[] b = new byte[40 + (12 * frags.Count)];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), Sidx);
        b[8] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), track);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), timescale.GetValueOrDefault(track, 1u));
        long ept = 0;
        foreach (FragTraf t in frags[0].Trafs)
        {
            if (t.TrackId == track)
            {
                ept = t.Time;
            }
        }

        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(20), (ulong)ept);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(28), 0); // first_offset: the first fragment follows directly
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(38), (ushort)Math.Min(frags.Count, ushort.MaxValue));
        for (int i = 0; i < frags.Count && i < ushort.MaxValue; i++)
        {
            long size = (i + 1 < frags.Count ? frags[i + 1].Moof.Offset : frags[i].End) - frags[i].Moof.Offset;
            long dur = 0;
            bool sync = false;
            foreach (FragTraf t in frags[i].Trafs)
            {
                if (t.TrackId == track)
                {
                    dur += t.Duration;
                    sync = t.Sync;
                }
            }

            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(40 + (i * 12)), (uint)size & 0x7FFFFFFF);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(44 + (i * 12)), (uint)dur);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(48 + (i * 12)), sync ? 0x90000000u : 0u);
        }

        return b;
    }

    private static bool MfraMatches(RepairInput input, BoxHeader mfra, List<Fragment> frags)
    {
        if (mfra.Truncated || mfra.Size > 64 * 1024 * 1024)
        {
            return false;
        }

        byte[] b = new byte[mfra.Size];
        input.Read(mfra.Offset, b);
        var moofs = new HashSet<long>();
        foreach (Fragment f in frags)
        {
            moofs.Add(f.Moof.Offset);
        }

        bool any = false;
        foreach ((uint type, int at, int hdr, int size) in Children(b, 8, b.Length))
        {
            if (type != 0x74667261 || size < hdr + 12)
            {
                continue;
            }

            int v = b[at + hdr];
            uint lens = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + hdr + 8));
            int tn = (int)((lens >> 4) & 3) + 1, rn = (int)((lens >> 2) & 3) + 1, sn = (int)(lens & 3) + 1;
            uint n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + hdr + 12));
            int w = v == 1 ? 8 : 4;
            int entry = (2 * w) + tn + rn + sn;
            for (uint k = 0; k < n && at + hdr + 16 + ((k + 1) * entry) <= at + size; k++)
            {
                int p = at + hdr + 16 + (int)(k * entry);
                long moofOffset = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + w)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + w));
                if (!moofs.Contains(moofOffset))
                {
                    return false;
                }

                any = true;
            }
        }

        return any;
    }

    private static bool SidxMatches(RepairInput input, List<BoxHeader> sidx, List<Fragment> frags, uint track)
    {
        if (sidx.Count != 1 || sidx[0].Truncated || sidx[0].Size > 64 * 1024 * 1024)
        {
            return false;
        }

        byte[] b = new byte[sidx[0].Size];
        input.Read(sidx[0].Offset, b);
        byte[] expect = BuildSidx(frags, track, new Dictionary<uint, uint> { [track] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)) });
        if (b.Length != expect.Length)
        {
            return false;
        }

        // compare references (sizes and durations); the earliest presentation time may carry a composition offset
        for (int i = 40; i + 8 <= b.Length; i += 12)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i)) != BinaryPrimitives.ReadUInt32BigEndian(expect.AsSpan(i)) ||
                BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 4)) != BinaryPrimitives.ReadUInt32BigEndian(expect.AsSpan(i + 4)))
            {
                return false;
            }
        }

        return true;
    }
}

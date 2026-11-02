using System.Collections;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// MPEG-2 transport stream clean-up (ITU-T H.222.0 §2.4.3). One pass reads only packet headers, adaptation fields and PES headers and decides, per
/// PES packet, whether it arrived whole: a PES packet with a continuity gap, a transport error, or fewer payload bytes than its length field states
/// is dropped completely, as are the stray packets before the first PES start of a stream. A second pass writes the packets that are kept,
/// unchanged except for the 4-bit continuity counter of the header, which is renumbered so the output has none of the gaps the dropped packets left.
/// Payload bytes are never rewritten. Section streams (PAT, PMT, SDT and the like) are never dropped. PCR jumps are reported, not changed.
/// </summary>
internal static class TsRepair
{
    private const int MaxReportedRanges = 40;

    private sealed class PidState
    {
        public int LastCc = -1;
        public int Kind; // 0 unknown, 1 PES, 2 sections
        public List<int> Orphans = [];   // packets before the first start of a PES
        public List<int> Current = [];   // packets of the PES in progress
        public bool CurrentBad;
        public long CurrentPayload;
        public long CurrentExpected;      // total PES bytes stated by its header, 0 when unbounded
        public bool HasPesStart;
        public long LastPcr = -1;
        public int OutCc = -1;
    }

    public static RepairReport Run(RepairInput input, Stream destination, MediaLimits limits, CancellationToken ct)
    {
        var report = new RepairReportBuilder("mpegts", input.Length);
        if (!FindLayout(input, out int packetSize, out int prefix, out long first))
        {
            report.Warn("No run of transport stream packets (sync byte 0x47 every 188, 192 or 204 bytes) was found.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        long total = (input.Length - first) / packetSize;
        limits.CheckSampleTable(total, first);
        if (total > int.MaxValue - 1)
        {
            report.Warn("The file holds more packets than one repair pass numbers.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        var drop = new BitArray((int)total + 1);
        var pids = new PidState?[0x2000];
        long gapBytes = 0;
        var syncLoss = new List<(long Start, long End)>();
        long dupCount = 0;
        long ccGaps = 0;
        long pcrJumps = 0;
        var pcrNotes = new List<string>();
        long leading = first;
        int index = 0;
        int lastPid = -1;
        long pos = first;
        // Bytes where the packet grid was lost are not packets; marks record the running total of such bytes so a packet number maps back to its offset.
        var gapMarks = new List<(int Index, long Cumulative)>();
        while (pos + packetSize <= input.Length)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> p = input.Peek(pos, packetSize);
            if (p.Length < packetSize || p[prefix] != 0x47)
            {
                // resynchronise: the next offset from which three packets in a row start with a sync byte
                long next = pos + 1;
                while (next + (3L * packetSize) <= input.Length && !IsSync(input, next + prefix) && !Sync3(input, next, packetSize, prefix))
                {
                    next++;
                }

                if (next + (3L * packetSize) > input.Length)
                {
                    next = input.Length;
                }

                if (index > 0 && lastPid >= 0)
                {
                    // the packet before the lost grid may hold bytes that are not its own (inserted data shifts what follows)
                    drop[index - 1] = true;
                    if (pids[lastPid] is { Kind: 1 } before)
                    {
                        before.CurrentBad = true;
                    }
                }

                syncLoss.Add((pos, next));
                gapBytes += next - pos;
                gapMarks.Add((index, gapBytes));
                pos = next;
                continue;
            }

            limits.CheckSampleTable(index + 1, pos);
            int pid = ((p[prefix + 1] & 0x1F) << 8) | p[prefix + 2];
            lastPid = pid == 0x1FFF ? -1 : pid;
            bool tei = (p[prefix + 1] & 0x80) != 0;
            bool pusi = (p[prefix + 1] & 0x40) != 0;
            int afc = (p[prefix + 3] >> 4) & 3;
            int cc = p[prefix + 3] & 0xF;
            if (pid != 0x1FFF)
            {
                PidState st = pids[pid] ??= new PidState();
                int payloadAt = prefix + 4;
                bool hasAf = (afc & 2) != 0;
                bool hasPayload = (afc & 1) != 0;
                bool discontinuity = false;
                if (hasAf)
                {
                    int afLen = p[payloadAt];
                    if (afLen > 0 && payloadAt + 1 + afLen <= packetSize)
                    {
                        byte flags = p[payloadAt + 1];
                        discontinuity = (flags & 0x80) != 0;
                        if ((flags & 0x10) != 0 && afLen >= 7)
                        {
                            // H.222.0 §2.4.3.5: PCR_base 33 bits, 6 reserved, PCR_extension 9 bits
                            long b = ((long)p[payloadAt + 2] << 25) | ((long)p[payloadAt + 3] << 17) | ((long)p[payloadAt + 4] << 9) | ((long)p[payloadAt + 5] << 1) | (long)(p[payloadAt + 6] >> 7);
                            long e = ((p[payloadAt + 6] & 1) << 8) | p[payloadAt + 7];
                            long pcr = (b * 300) + e;
                            if (st.LastPcr >= 0 && !discontinuity)
                            {
                                long d = pcr - st.LastPcr;
                                if (d < 0 || d > 27_000_000)
                                {
                                    pcrJumps++;
                                    if (pcrNotes.Count < 10)
                                    {
                                        pcrNotes.Add($"PCR on PID {pid} moves by {d / 27_000.0:0.###} ms at offset {pos} without a discontinuity flag.");
                                    }
                                }
                            }

                            st.LastPcr = pcr;
                        }
                    }

                    payloadAt += 1 + afLen;
                }

                bool gap = false;
                if (hasPayload)
                {
                    if (st.LastCc >= 0 && !discontinuity)
                    {
                        if (cc == st.LastCc)
                        {
                            // H.222.0 §2.4.3.3: one duplicate packet is allowed; it carries the same data and is dropped here
                            drop[index] = true;
                            dupCount++;
                            index++;
                            pos += packetSize;
                            continue;
                        }

                        gap = cc != ((st.LastCc + 1) & 0xF);
                    }

                    st.LastCc = cc;
                }

                if (gap)
                {
                    ccGaps++;
                }

                bool start = pusi && hasPayload && payloadAt + 3 <= packetSize;
                if (start && st.Kind == 0)
                {
                    st.Kind = p[payloadAt] == 0 && p[payloadAt + 1] == 0 && p[payloadAt + 2] == 1 ? 1 : 2;
                    if (st.Kind == 1)
                    {
                        foreach (int o in st.Orphans)
                        {
                            drop[o] = true;
                        }
                    }

                    st.Orphans = [];
                }

                if (st.Kind == 2)
                {
                    // sections are kept whole; their packets are only renumbered
                }
                else if (st.Kind == 0)
                {
                    st.Orphans.Add(index);
                }
                else
                {
                    if (start)
                    {
                        FinishPes(st, drop, final: false);
                        st.CurrentPayload = 0;
                        st.CurrentBad = false;
                        st.HasPesStart = true;
                        int plen = (p[payloadAt + 4] << 8) | p[payloadAt + 5];
                        st.CurrentExpected = plen == 0 ? 0 : plen + 6L;
                    }
                    else if (!st.HasPesStart)
                    {
                        st.Orphans.Add(index);
                    }

                    if (st.HasPesStart)
                    {
                        st.Current.Add(index);
                        st.CurrentBad |= gap || tei;
                        if (hasPayload)
                        {
                            st.CurrentPayload += Math.Max(0, packetSize - payloadAt);
                        }
                    }
                }
            }

            index++;
            pos += packetSize;
        }

        long trailing = input.Length - pos;
        if (trailing >= prefix + 3 && !(syncLoss.Count > 0 && syncLoss[^1].End == input.Length))
        {
            // a packet cut in the middle: whatever PES it belonged to is incomplete, even when that PES states no length
            ReadOnlySpan<byte> t = input.Peek(pos, prefix + 3);
            if (t.Length == prefix + 3 && t[prefix] == 0x47 && pids[((t[prefix + 1] & 0x1F) << 8) | t[prefix + 2]] is { Kind: 1 } cutPid)
            {
                cutPid.CurrentBad = true;
            }
        }

        foreach (PidState? st in pids)
        {
            if (st is not null && st.Kind == 1)
            {
                FinishPes(st, drop, final: true);
                foreach (int o in st.Orphans)
                {
                    drop[o] = true;
                }
            }
            else if (st is not null && st.Kind == 0)
            {
                // a PID that never starts anything: left alone
            }
        }

        // ---- summarise
        int dropped = 0;
        long dropStart = -1;
        long dropEnd = -1;
        int ranges = 0;
        int mark = 0;
        long cumulative = 0;
        for (int i = 0; i < index; i++)
        {
            while (mark < gapMarks.Count && gapMarks[mark].Index <= i)
            {
                cumulative = gapMarks[mark++].Cumulative;
            }

            long at = first + (i * (long)packetSize) + cumulative;
            if (drop[i])
            {
                dropped++;
                if (dropStart < 0)
                {
                    dropStart = at;
                }

                dropEnd = at + packetSize;
            }

            bool flush = dropStart >= 0 && (!drop[i] || i == index - 1);
            if (flush)
            {
                if (ranges++ < MaxReportedRanges)
                {
                    report.Add(RepairAction.Dropped, "PES", -1, dropStart, dropEnd - dropStart, "Packets of PES packets that did not arrive whole (continuity gap, transport error, cut payload, or no start) are left out.", RepairConfidence.High);
                }

                dropStart = -1;
            }
        }

        if (ranges > MaxReportedRanges)
        {
            report.Add(RepairAction.Dropped, "PES", -1, 0, 0, $"{ranges - MaxReportedRanges} more dropped ranges are not listed.", RepairConfidence.High);
        }

        if (leading > 0)
        {
            report.Add(RepairAction.Dropped, "garbage", -1, 0, leading, "Bytes before the first packet are left out.", RepairConfidence.High);
        }

        foreach ((long s, long e) in syncLoss)
        {
            report.Add(RepairAction.Dropped, "garbage", -1, s, e - s, "Bytes where the packet grid was lost are left out.", RepairConfidence.High);
        }

        if (trailing > 0 && !(syncLoss.Count > 0 && syncLoss[^1].End == input.Length))
        {
            report.Add(RepairAction.Dropped, "garbage", -1, pos, trailing, "A partial packet at the end of the file is left out.", RepairConfidence.High);
        }

        if (dupCount > 0)
        {
            report.Add(RepairAction.Dropped, "packet", -1, 0, dupCount * packetSize, $"{dupCount} duplicate packets (same continuity counter) are left out.", RepairConfidence.High);
        }

        // continuity renumbering is needed when packets were dropped or a gap was counted that stays (a flagged discontinuity needs none)
        bool changed = dropped > 0 || leading > 0 || gapBytes > 0 || trailing > 0;
        foreach (string n in pcrNotes)
        {
            report.Add(RepairAction.Kept, "PCR", -1, 0, 0, n + " Reported, not changed.", RepairConfidence.High);
        }

        if (pcrJumps > pcrNotes.Count)
        {
            report.Add(RepairAction.Kept, "PCR", -1, 0, 0, $"{pcrJumps} PCR jumps in total. Reported, not changed.", RepairConfidence.High);
        }

        report.Tracks.Add(new RepairTrackReport(0, "mpegts", index - dropped, dropped, changed ? RepairConfidence.High : RepairConfidence.High, [new ByteRange(first, pos - first)]));
        if (ccGaps > 0)
        {
            report.Add(RepairAction.Fixed, "continuity_counter", -1, 0, 0, $"{ccGaps} continuity gaps found; the counters of the packets that stay are renumbered so none remain.", RepairConfidence.High);
            changed = true;
        }

        if (!changed)
        {
            using (RepairOutput copy = new(destination, ct))
            {
                copy.Copy(input, 0, input.Length);
            }

            return report.Build(RepairStatus.NothingToRepair, input.Length);
        }

        // ---- write
        {
            using var output = new RepairOutput(destination, ct);
            byte[] buffer = new byte[packetSize];
            int wmark = 0;
            long wcum = 0;
            for (int i = 0; i < index; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (wmark < gapMarks.Count && gapMarks[wmark].Index <= i)
                {
                    wcum = gapMarks[wmark++].Cumulative;
                }

                if (drop[i])
                {
                    continue;
                }

                input.Read(first + (i * (long)packetSize) + wcum, buffer);
                int pid = ((buffer[prefix + 1] & 0x1F) << 8) | buffer[prefix + 2];
                if (pid != 0x1FFF)
                {
                    PidState st = pids[pid]!;
                    int afc = (buffer[prefix + 3] >> 4) & 3;
                    int cc = buffer[prefix + 3] & 0xF;
                    int disc = (afc & 2) != 0 && buffer[prefix + 4] > 0 && (buffer[prefix + 5] & 0x80) != 0 ? 1 : 0;
                    if (st.OutCc < 0 || disc == 1)
                    {
                        st.OutCc = cc;
                    }
                    else if ((afc & 1) != 0)
                    {
                        st.OutCc = (st.OutCc + 1) & 0xF;
                    }

                    // H.222.0 §2.4.3.3: the counter does not advance on packets without payload; this edits the header's low nibble only
                    buffer[prefix + 3] = (byte)((buffer[prefix + 3] & 0xF0) | st.OutCc);
                }

                output.Write(buffer);
            }

            output.Flush();
            if (output.Position == 0)
            {
                report.Warn("No packet of the input is intact enough to keep.");
                return report.Build(RepairStatus.Unrepairable, 0);
            }

            return report.Build(RepairStatus.Repaired, output.Position);
        }
    }

    private static void FinishPes(PidState st, BitArray drop, bool final)
    {
        if (st.Current.Count == 0)
        {
            return;
        }

        bool bad = st.CurrentBad;
        if (!bad && st.CurrentExpected > 0 && st.CurrentPayload < st.CurrentExpected)
        {
            bad = true; // fewer payload bytes than the PES header states: cut off
        }

        if (bad)
        {
            foreach (int i in st.Current)
            {
                drop[i] = true;
            }
        }

        _ = final;
        st.Current = [];
    }

    public static bool LooksLikeTransportStream(IMediaSource source, CancellationToken ct)
    {
        using var reader = new RepairInput(source, ct);
        return FindLayout(reader, out _, out _, out _);
    }

    private static bool IsSync(RepairInput input, long at)
    {
        ReadOnlySpan<byte> s = input.Peek(at, 1);
        return s.Length == 1 && s[0] == 0x47;
    }

    private static bool Sync3(RepairInput input, long at, int size, int prefix)
    {
        for (int k = 1; k <= 2; k++)
        {
            ReadOnlySpan<byte> s = input.Peek(at + (k * (long)size) + prefix, 1);
            if (s.Length < 1 || s[0] != 0x47)
            {
                return false;
            }
        }

        return true;
    }

    // Packet size 188, M2TS 192 (a 4-byte time stamp before each packet) or 204 (16 bytes of error correction): the size at which sync bytes repeat.
    private static bool FindLayout(RepairInput input, out int packetSize, out int prefix, out long first)
    {
        packetSize = 0;
        prefix = 0;
        first = 0;
        foreach (int size in new[] { 188, 192, 204 })
        {
            int pre = size == 192 ? 4 : 0;
            for (long start = 0; start < Math.Min(input.Length, 2048); start++)
            {
                ReadOnlySpan<byte> s = input.Peek(start + pre, 1);
                if (s.Length < 1 || s[0] != 0x47)
                {
                    continue;
                }

                bool ok = true;
                int checks = 0;
                for (int k = 1; k <= 5 && start + (k * (long)size) + pre < input.Length; k++)
                {
                    ReadOnlySpan<byte> t = input.Peek(start + (k * (long)size) + pre, 1);
                    ok &= t.Length == 1 && t[0] == 0x47;
                    checks++;
                }

                if (ok && checks >= 3)
                {
                    packetSize = size;
                    prefix = pre;
                    first = start;
                    return true;
                }
            }
        }

        return false;
    }
}

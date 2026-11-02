using System.Buffers.Binary;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// Matroska/WebM index repair (RFC 9559). One front-to-back scan reads only element headers and the first bytes of each block, then the
/// file is written again with a fresh SeekHead, the Info duration set from the blocks, every Cluster given a known size and a Cues element
/// built from the keyframes. Cluster payloads are copied unchanged. The scan copes with an unknown-size Segment or Cluster (a cut live
/// recording), a size that runs past the end of the file, a final Cluster cut in the middle of a block, and garbage between Clusters.
/// </summary>
internal static class MatroskaRepair
{
    private const long MaxMetaElement = 16L * 1024 * 1024;

    private sealed class Element
    {
        public uint Id;
        public long Offset;
        public int HeaderSize;
        public long PayloadSize;
        public bool Declared; // the input stated this size correctly
        public ClusterScan? Cluster;
    }

    private sealed class CueCandidate
    {
        public long Time;
        public long Track;
        public long RelativePosition;
    }

    private sealed class ClusterScan
    {
        public long Timestamp;
        public long ContentStart;
        public long KeptEnd;
        public bool Rewrite; // header must be written again (unknown or wrong size, or content dropped)
        public bool HasCrc;
        public long PositionOffset = -1;
        public long PositionValue;
        public int PositionWidth;
        public List<CueCandidate> Cues = [];
        public long OutputPosition; // relative to the Segment data, set by the plan
        public long OutputHeaderSize;
    }

    private sealed class TrackInfo
    {
        public long Number;
        public long Type;
        public long DefaultDurationNs;
        public string Codec = string.Empty;
        public long Packets;
    }

    public static RepairReport Run(RepairInput input, Stream destination, MediaLimits limits, CancellationToken ct)
    {
        var report = new RepairReportBuilder("matroska", input.Length);
        if (!TryHeader(input.Peek(0, 16), out uint firstId, out int ebmlHdr, out long ebmlSize) || firstId != MatroskaIds.Ebml || ebmlSize < 0 || ebmlHdr + ebmlSize > input.Length)
        {
            report.Warn("The file does not start with a complete EBML header.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        long ebmlEnd = ebmlHdr + ebmlSize;
        if (!TryHeader(input.Peek(ebmlEnd, 16), out uint segId, out int segHdr, out long segDeclared) || segId != MatroskaIds.Segment)
        {
            report.Warn("No Segment element follows the EBML header.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        long segData = ebmlEnd + segHdr;
        long segEnd = input.Length;
        if (segDeclared < 0)
        {
            report.Add(RepairAction.Fixed, "Segment", -1, ebmlEnd, segHdr, "The Segment has an unknown size (a recording that was cut or never finished); it now states its real size.", RepairConfidence.High);
        }
        else if (segData + segDeclared > input.Length)
        {
            report.Add(RepairAction.Fixed, "Segment", -1, ebmlEnd, segHdr, $"The Segment declares {segDeclared} bytes but only {input.Length - segData} are present; it now states its real size.", RepairConfidence.High);
        }
        else if (segData + segDeclared < input.Length)
        {
            report.Add(RepairAction.Fixed, "Segment", -1, ebmlEnd, segHdr, $"The Segment declares {segDeclared} bytes but {input.Length - segData} follow its header; the elements after its declared end are kept.", RepairConfidence.Medium);
        }

        // ---- scan the level-1 elements
        var elements = new List<Element>();
        Element? info = null, tracksEl = null;
        var seekHeads = new List<Element>();
        var cuesEls = new List<Element>();
        long pos = segData;
        long dropped = 0;
        long count = 0;
        List<TrackInfo> tracks = [];
        bool tracksSeen = false;
        long scale = 1_000_000;
        long maxEnd = 0;
        bool anyBlockWithoutDuration = false;
        bool structuralChange = segDeclared < 0 || segData + segDeclared != input.Length;
        long clusterCount = 0;
        long lastClusterEnd = segData;

        while (pos < segEnd)
        {
            ct.ThrowIfCancellationRequested();
            limits.CheckElementCount(++count, pos);
            if (!TryHeader(input.Peek(pos, 16), out uint id, out int hdr, out long size) || !IsLevel1(id) || (size < 0 && id != MatroskaIds.Cluster))
            {
                long next = Resync(input, pos + 1, segEnd);
                report.Add(RepairAction.Dropped, "garbage", -1, pos, next - pos, next >= segEnd
                    ? "Bytes at the end of the file do not form a complete element (the recording was cut)."
                    : "Bytes between elements are not a valid element; skipped up to the next Cluster.", RepairConfidence.High);
                dropped += next - pos;
                structuralChange = true;
                pos = next;
                continue;
            }

            var el = new Element { Id = id, Offset = pos, HeaderSize = hdr, PayloadSize = size, Declared = size >= 0 };
            if (id == MatroskaIds.Cluster)
            {
                clusterCount++;
                ClusterScan c = ScanCluster(input, pos, hdr, size, tracks, tracksSeen, scale, ref maxEnd, ref anyBlockWithoutDuration, report, limits, ct, out long next);
                el.Cluster = c;
                if (c.KeptEnd < next)
                {
                    long cut = next - c.KeptEnd;
                    report.Add(RepairAction.Dropped, "Cluster", -1, c.KeptEnd, cut, "The last block of the Cluster is cut off; the partial data is left out.", RepairConfidence.High);
                    dropped += cut;
                    structuralChange = true;
                }

                if (c.Rewrite)
                {
                    structuralChange = true;
                    if (size < 0)
                    {
                        report.Add(RepairAction.Fixed, "Cluster", -1, pos, hdr, "The Cluster has an unknown size; it now states its size.", RepairConfidence.High);
                    }
                    else if (c.KeptEnd == next && pos + hdr + size != c.KeptEnd)
                    {
                        report.Add(RepairAction.Fixed, "Cluster", -1, pos, hdr, "The Cluster's size did not match its content; corrected.", RepairConfidence.High);
                    }
                }

                el.PayloadSize = c.KeptEnd - c.ContentStart;
                lastClusterEnd = c.KeptEnd;
                elements.Add(el);
                pos = next;
                continue;
            }

            if (pos + hdr + size > input.Length)
            {
                // A truncated non-Cluster element: nothing after it can be trusted.
                report.Add(RepairAction.Dropped, el.Id == MatroskaIds.Cues ? "Cues" : (MatroskaIds.NameOf(id) ?? "element"), -1, pos, input.Length - pos, "The element is cut off at the end of the file; it is left out" + (id == MatroskaIds.Cues ? " and the Cues are rebuilt." : "."), RepairConfidence.High);
                dropped += input.Length - pos;
                structuralChange = true;
                if (id == MatroskaIds.Tracks && tracksEl is null)
                {
                    report.Warn("The Tracks element is cut off: the file cannot be described. A reference file from the same recorder would be needed.");
                    return report.Build(RepairStatus.Unrepairable, 0);
                }

                pos = input.Length;
                break;
            }

            switch (id)
            {
                case MatroskaIds.SeekHead:
                    seekHeads.Add(el);
                    break;
                case MatroskaIds.Cues:
                    cuesEls.Add(el);
                    break;
                case MatroskaIds.Info when info is null:
                    info = el;
                    break;
                case MatroskaIds.Tracks when tracksEl is null:
                    tracksEl = el;
                    tracksSeen = true;
                    if (size > MaxMetaElement || size > limits.MaxAllocationBytes)
                    {
                        report.Warn("The Tracks element is larger than the allocation limit.");
                        return report.Build(RepairStatus.Unrepairable, 0);
                    }

                    ParseTracks(input, el, tracks, limits);
                    break;
                default:
                    break;
            }

            if (id == MatroskaIds.Info)
            {
                scale = ReadTimestampScale(input, el);
            }

            elements.Add(el);
            pos += hdr + size;
        }

        if (tracksEl is null)
        {
            report.Warn("The file has no Tracks element; without a reference file nothing can be described.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        if (clusterCount == 0)
        {
            report.Warn("The file holds no Cluster: there is no media to index.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        // ---- decide whether anything needs doing
        var clusterOffsets = new HashSet<long>();
        var l1ById = new Dictionary<long, uint>();
        foreach (Element e in elements)
        {
            l1ById[e.Offset - segData] = e.Id;
            if (e.Id == MatroskaIds.Cluster)
            {
                clusterOffsets.Add(e.Offset - segData);
            }
        }

        double? duration = info is null ? null : ReadDuration(input, info);
        long wantDuration = maxEnd;
        string? durationProblem = null;
        long tolerance = Math.Max(100_000_000 / Math.Max(1, scale), 1);
        foreach (TrackInfo t in tracks)
        {
            tolerance = Math.Max(tolerance, 2 * (t.DefaultDurationNs / Math.Max(1, scale)));
        }

        if (info is null)
        {
            durationProblem = "The file has no Info element; one is written with the duration.";
        }
        else if (duration is null or <= 0)
        {
            durationProblem = "The Info element states no duration; it is set from the last block.";
        }
        else if (Math.Abs(duration.Value - wantDuration) > tolerance)
        {
            durationProblem = $"The stated duration ({duration.Value:0} ticks) disagrees with the last block ({wantDuration} ticks); set from the blocks.";
        }

        bool cuesOk = cuesEls.Count == 1 && CuesValid(input, cuesEls[0], tracks, clusterOffsets, segData, limits);
        bool seekOk = seekHeads.Count >= 1 && SeekHeadsValid(input, seekHeads, l1ById, segData, limits);
        bool healthy = !structuralChange && !report.Changed && durationProblem is null && cuesOk && seekOk && dropped == 0;
        bool anyRewrite = false;
        foreach (Element e in elements)
        {
            anyRewrite |= e.Cluster is { Rewrite: true };
        }

        healthy &= !anyRewrite;
        foreach (TrackInfo t in tracks)
        {
            report.Tracks.Add(new RepairTrackReport((int)t.Number, t.Codec, t.Packets, 0, RepairConfidence.High, [new ByteRange(segData, lastClusterEnd - segData)]));
        }

        if (healthy)
        {
            report.Add(RepairAction.Kept, "Cues", -1, cuesEls[0].Offset, cuesEls[0].HeaderSize + cuesEls[0].PayloadSize, "Cues point at real Clusters and tracks.", RepairConfidence.High);
            report.Add(RepairAction.Kept, "SeekHead", -1, seekHeads[0].Offset, seekHeads[0].HeaderSize + seekHeads[0].PayloadSize, "Every entry points at the element it names.", RepairConfidence.High);
            report.Add(RepairAction.Kept, "Info", -1, info!.Offset, info.HeaderSize + info.PayloadSize, "The duration agrees with the last block.", RepairConfidence.High);
            using (RepairOutput copy = new(destination, ct))
            {
                copy.Copy(input, 0, input.Length);
            }

            return report.Build(RepairStatus.NothingToRepair, input.Length);
        }

        if (durationProblem is not null)
        {
            report.Add(info is null ? RepairAction.Rebuilt : RepairAction.Fixed, "Info", -1, info?.Offset ?? segData, info is null ? 0 : info.HeaderSize + info.PayloadSize, durationProblem, anyBlockWithoutDuration ? RepairConfidence.Medium : RepairConfidence.High);
        }

        if (seekHeads.Count == 0)
        {
            report.Add(RepairAction.Rebuilt, "SeekHead", -1, segData, 0, "The file has no SeekHead; one is written.", RepairConfidence.High);
        }
        else if (!seekOk)
        {
            report.Add(RepairAction.Rebuilt, "SeekHead", -1, seekHeads[0].Offset, seekHeads[0].HeaderSize + seekHeads[0].PayloadSize, "SeekHead entries pointed at the wrong positions or past the end of the file; rebuilt.", RepairConfidence.High);
        }
        else
        {
            report.Add(RepairAction.Rebuilt, "SeekHead", -1, seekHeads[0].Offset, seekHeads[0].HeaderSize + seekHeads[0].PayloadSize, "Rewritten because the elements moved.", RepairConfidence.High);
        }

        // ---- plan the output
        byte[] newInfo = BuildInfo(input, info, wantDuration);
        var pieces = new List<(Element? El, byte[]? Bytes)>();
        long cursor = 0; // relative to the Segment data, after the SeekHead
        List<(uint Id, long OutPos)> seekEntries = [];
        long firstClusterOut = -1;
        long cuesOut = -1;
        // SeekHead size does not depend on positions: entries have fixed-width positions.
        List<uint> entryIds = [];
        foreach (Element e in elements)
        {
            if (e == info || e.Id is MatroskaIds.Tracks or MatroskaIds.Chapters or MatroskaIds.Tags or MatroskaIds.Attachments)
            {
                entryIds.Add(e.Id);
            }
        }

        if (info is null)
        {
            entryIds.Insert(0, MatroskaIds.Info);
        }

        entryIds.Add(MatroskaIds.Cluster);
        entryIds.Add(MatroskaIds.Cues);
        int seekHeadSize = SeekHeadBytes(entryIds, new long[entryIds.Count]).Length;
        cursor = seekHeadSize;
                if (info is null)
        {
            seekEntries.Add((MatroskaIds.Info, cursor));
            pieces.Add((null, newInfo));
            cursor += newInfo.Length;
        }

        foreach (Element e in elements)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Id is MatroskaIds.SeekHead or MatroskaIds.Cues or MatroskaIds.Crc32)
            {
                continue;
            }

            if (e == info)
            {
                seekEntries.Add((MatroskaIds.Info, cursor));
                pieces.Add((null, newInfo));
                cursor += newInfo.Length;
                continue;
            }

            if (e.Id == MatroskaIds.Cluster)
            {
                ClusterScan c = e.Cluster!;
                if (firstClusterOut < 0)
                {
                    firstClusterOut = cursor;
                }

                c.OutputPosition = cursor;
                if (!c.Rewrite && c.HasCrc && c.PositionOffset >= 0 && c.PositionValue != cursor)
                {
                    c.Rewrite = true; // the Position will change, which the CRC-32 would no longer cover
                }

                c.OutputHeaderSize = c.Rewrite ? EbmlWriter.IdLength(MatroskaIds.Cluster) + 8 : e.HeaderSize;
                pieces.Add((e, null));
                cursor += c.OutputHeaderSize + e.PayloadSize + (c.HasCrc && c.Rewrite ? -6 : 0);
                // A Position child that no longer matches is patched in place (same width); a CRC-32 child no longer matches and is left out.
                continue;
            }

            if (e.Id is MatroskaIds.Tracks or MatroskaIds.Chapters or MatroskaIds.Tags or MatroskaIds.Attachments)
            {
                seekEntries.Add((e.Id, cursor));
            }

            pieces.Add((e, null));
            cursor += e.HeaderSize + e.PayloadSize;
        }

        seekEntries.Add((MatroskaIds.Cluster, firstClusterOut));
        cuesOut = cursor;
        seekEntries.Add((MatroskaIds.Cues, cuesOut));
        byte[] cues = BuildCues(elements, tracks, limits, out long cueCount);
        cursor += cues.Length;
        long segmentSize = cursor;

        // SeekHead entries in file order
        seekEntries.Sort((a, b) => a.OutPos.CompareTo(b.OutPos));
        byte[] seekHead = SeekHeadBytes(seekEntries.ConvertAll(e => e.Id), seekEntries.ConvertAll(e => e.OutPos).ToArray());
        if (seekHead.Length != seekHeadSize)
        {
            throw new InvalidOperationException("The SeekHead size changed while planning.");
        }

        report.Add(RepairAction.Rebuilt, "Cues", -1, cuesEls.Count > 0 ? cuesEls[0].Offset : lastClusterEnd, cuesEls.Count > 0 ? cuesEls[0].HeaderSize + cuesEls[0].PayloadSize : 0,
            $"{cueCount} cue points built from the keyframes of {(tracks.Exists(t => t.Type == 1) ? "the first video track" : "the audio tracks")}" + (cuesEls.Count > 0 && !cuesOk ? "; the old Cues were invalid." : "."), RepairConfidence.High);

        // ---- write
        {
            using var output = new RepairOutput(destination, ct);
            output.Copy(input, 0, ebmlEnd);
            Span<byte> head = stackalloc byte[12]; // Segment ID (4) + 8-byte size
            EbmlWriter.WriteId(head, MatroskaIds.Segment);
            EbmlWriter.WriteSize(head[4..], segmentSize, 8);
            output.Write(head);
            output.Write(seekHead);
            foreach ((Element? e, byte[]? bytes) in pieces)
            {
                if (bytes is not null)
                {
                    output.Write(bytes);
                }
                else if (e!.Id == MatroskaIds.Cluster)
                {
                    WriteCluster(input, output, e);
                }
                else
                {
                    output.Copy(input, e.Offset, e.HeaderSize + e.PayloadSize);
                }
            }

            output.Write(cues);
            output.Flush();
            RepairStatus status = RepairStatus.Repaired;
            return report.Build(status, output.Position);
        }
    }

    // ---------------------------------------------------------------- scanning

    private static bool IsLevel1(uint id) => id is MatroskaIds.SeekHead or MatroskaIds.Info or MatroskaIds.Tracks or MatroskaIds.Cluster or MatroskaIds.Cues
        or MatroskaIds.Tags or MatroskaIds.Chapters or MatroskaIds.Attachments or MatroskaIds.Void or MatroskaIds.Crc32;

    private static bool IsClusterChild(uint id) => id is MatroskaIds.Timestamp or MatroskaIds.Position or MatroskaIds.PrevSize or MatroskaIds.SimpleBlock
        or MatroskaIds.BlockGroup or MatroskaIds.EncryptedBlock or MatroskaIds.SilentTracks or MatroskaIds.Void or MatroskaIds.Crc32;

    private static bool TryHeader(ReadOnlySpan<byte> s, out uint id, out int hdr, out long size)
    {
        hdr = 0;
        size = 0;
        if (!Ebml.TryReadId(s, out id, out int il) || !Ebml.TryReadSize(s[il..], out size, out int sl))
        {
            return false;
        }

        hdr = il + sl;
        return true;
    }

    // Finds the next offset at or after `from` where a Cluster header starts, or `end`.
    private static long Resync(RepairInput input, long from, long end)
    {
        long p = from;
        while (p < end)
        {
            ReadOnlySpan<byte> s = input.Peek(p, 16384);
            if (s.Length == 0)
            {
                break;
            }

            int i = 0;
            while (i < s.Length)
            {
                int k = s[i..].IndexOf((byte)0x1F);
                if (k < 0)
                {
                    break;
                }

                i += k;
                if (i + 4 <= s.Length && s[i + 1] == 0x43 && s[i + 2] == 0xB6 && s[i + 3] == 0x75)
                {
                    long at = p + i;
                    if (TryHeader(input.Peek(at, 16), out uint id, out _, out long size) && id == MatroskaIds.Cluster && (size < 0 || at + size <= input.Length))
                    {
                        return at;
                    }
                }

                i++;
            }

            p += Math.Max(1, s.Length - 3);
        }

        return end;
    }

    private static ClusterScan ScanCluster(RepairInput input, long offset, int headerSize, long declared, List<TrackInfo> tracks, bool tracksSeen, long scale,
        ref long maxEnd, ref bool anyBlockWithoutDuration, RepairReportBuilder report, MediaLimits limits, CancellationToken ct, out long next)
    {
        var c = new ClusterScan { ContentStart = offset + headerSize };
        long limit = declared >= 0 ? Math.Min(c.ContentStart + declared, input.Length) : input.Length;
        long cp = c.ContentStart;
        bool truncatedChild = false;
        var blocks = new List<(long Track, long Rel, bool Key, long Dur, long Pos)>();
        while (cp < limit)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> s = input.Peek(cp, 16);
            if (!TryHeader(s, out uint id, out int hdr, out long size))
            {
                // A header cut by the end of the file is a partial element; anything else is garbage and ends the Cluster.
                truncatedChild = input.Length - cp < 12;
                break;
            }

            if (size < 0 || !IsClusterChild(id))
            {
                break; // the next level-1 element, or garbage: the Cluster ends here
            }

            if (cp + hdr + size > limit)
            {
                truncatedChild = true;
                break;
            }

            switch (id)
            {
                case MatroskaIds.Timestamp:
                    {
                        ReadOnlySpan<byte> p = input.Peek(cp + hdr, (int)Math.Min(size, 8));
                        if (Ebml.TryReadUnsigned(p, out ulong ts))
                        {
                            c.Timestamp = (long)ts;
                        }

                        break;
                    }

                case MatroskaIds.Position:
                    {
                        ReadOnlySpan<byte> p = input.Peek(cp + hdr, (int)Math.Min(size, 8));
                        if (Ebml.TryReadUnsigned(p, out ulong pv))
                        {
                            c.PositionOffset = cp + hdr;
                            c.PositionWidth = (int)size;
                            c.PositionValue = (long)pv;
                        }

                        break;
                    }

                case MatroskaIds.Crc32:
                    c.HasCrc = cp == c.ContentStart && size == 4; // RFC 8794 §11.3: the CRC-32 is the first child
                    break;
                case MatroskaIds.SimpleBlock:
                    if (TryBlockHeader(input.Peek(cp + hdr, 16), out long trk, out int rel, out byte flags))
                    {
                        blocks.Add((trk, rel, (flags & 0x80) != 0, 0, cp - c.ContentStart));
                    }

                    break;
                case MatroskaIds.BlockGroup:
                    ScanBlockGroup(input, cp, hdr, size, c.ContentStart, blocks);
                    break;
                default:
                    break;
            }

            cp += hdr + size;
        }

        c.KeptEnd = cp;
        next = truncatedChild ? limit : cp;

        // the Cluster is written again when its stated size does not describe what is kept
        c.Rewrite = declared < 0 || c.ContentStart + declared != c.KeptEnd;

        // register blocks
        foreach ((long track, long rel, bool key, long dur, long pos) in blocks)
        {
            TrackInfo? t = tracks.Find(x => x.Number == track);
            if (t is null && !tracksSeen)
            {
                t = new TrackInfo { Number = track };
                tracks.Add(t);
            }

            if (t is not null)
            {
                t.Packets++;
            }

            long start = c.Timestamp + rel;
            long length = dur;
            if (length == 0 && t is not null && t.DefaultDurationNs > 0)
            {
                length = Math.Max(1, t.DefaultDurationNs / Math.Max(1, scale));
            }

            if (dur == 0)
            {
                anyBlockWithoutDuration = true;
            }

            maxEnd = Math.Max(maxEnd, start + length);
            if (key && t is not null && (t.Type == 1 || (t.Type == 2 && !tracks.Exists(x => x.Type == 1))))
            {
                // one cue per keyframe of video; for a file without video, one cue per Cluster per audio track
                if (t.Type == 1 || !c.Cues.Exists(q => q.Track == track))
                {
                    c.Cues.Add(new CueCandidate { Time = start, Track = track, RelativePosition = pos });
                }
            }
        }

        _ = report;
        _ = limits;
        return c;
    }

    private static void ScanBlockGroup(RepairInput input, long groupOffset, int groupHeader, long groupSize, long clusterContent, List<(long Track, long Rel, bool Key, long Dur, long Pos)> blocks)
    {
        long p = groupOffset + groupHeader;
        long end = p + groupSize;
        long trk = 0;
        int rel = 0;
        bool have = false;
        bool reference = false;
        long dur = 0;
        while (p < end)
        {
            ReadOnlySpan<byte> s = input.Peek(p, 24);
            if (!TryHeader(s, out uint id, out int hdr, out long size) || size < 0 || p + hdr + size > end)
            {
                break;
            }

            if (id == MatroskaIds.Block && TryBlockHeader(s[hdr..], out trk, out rel, out _))
            {
                have = true;
            }
            else if (id == MatroskaIds.ReferenceBlock)
            {
                reference = true;
            }
            else if (id == MatroskaIds.BlockDuration && Ebml.TryReadUnsigned(input.Peek(p + hdr, (int)Math.Min(size, 8)), out ulong d))
            {
                dur = (long)d;
            }

            p += hdr + size;
        }

        if (have)
        {
            blocks.Add((trk, rel, !reference, dur, groupOffset - clusterContent));
        }
    }

    // Block header (RFC 9559 §10.1): track number as a vint, a signed 16-bit time relative to the Cluster, one flags byte.
    private static bool TryBlockHeader(ReadOnlySpan<byte> s, out long track, out int rel, out byte flags)
    {
        track = 0;
        rel = 0;
        flags = 0;
        if (!Ebml.TryReadVint(s, out ulong t, out int l) || s.Length < l + 3)
        {
            return false;
        }

        track = (long)t;
        rel = BinaryPrimitives.ReadInt16BigEndian(s[l..]);
        flags = s[l + 2];
        return true;
    }

    private static void ParseTracks(RepairInput input, Element tracks, List<TrackInfo> list, MediaLimits limits)
    {
        byte[] payload = new byte[tracks.PayloadSize];
        limits.CheckAllocation(payload.Length, tracks.Offset);
        input.Read(tracks.Offset + tracks.HeaderSize, payload);
        int p = 0;
        while (p < payload.Length)
        {
            if (!TryHeader(payload.AsSpan(p), out uint id, out int hdr, out long size) || size < 0 || p + hdr + size > payload.Length)
            {
                break;
            }

            if (id == MatroskaIds.TrackEntry)
            {
                var t = new TrackInfo();
                int q = p + hdr;
                int e = q + (int)size;
                while (q < e)
                {
                    if (!TryHeader(payload.AsSpan(q, e - q), out uint cid, out int ch, out long cs) || cs < 0 || q + ch + cs > e)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> v = payload.AsSpan(q + ch, (int)cs);
                    if (cid == MatroskaIds.TrackNumber && Ebml.TryReadUnsigned(v, out ulong n))
                    {
                        t.Number = (long)n;
                    }
                    else if (cid == MatroskaIds.TrackType && Ebml.TryReadUnsigned(v, out ulong ty))
                    {
                        t.Type = (long)ty;
                    }
                    else if (cid == MatroskaIds.DefaultDuration && Ebml.TryReadUnsigned(v, out ulong dd))
                    {
                        t.DefaultDurationNs = (long)dd;
                    }
                    else if (cid == MatroskaIds.CodecIdElement)
                    {
                        t.Codec = System.Text.Encoding.ASCII.GetString(v);
                    }

                    q += ch + (int)cs;
                }

                limits.CheckTracks(list.Count + 1, tracks.Offset);
                list.Add(t);
            }

            p += hdr + (int)size;
        }
    }

    private static long ReadTimestampScale(RepairInput input, Element info)
    {
        byte[] payload = ReadPayload(input, info);
        int p = 0;
        while (p < payload.Length && TryHeader(payload.AsSpan(p), out uint id, out int hdr, out long size) && size >= 0 && p + hdr + size <= payload.Length)
        {
            if (id == MatroskaIds.TimestampScale && Ebml.TryReadUnsigned(payload.AsSpan(p + hdr, (int)size), out ulong v) && v > 0)
            {
                return (long)v;
            }

            p += hdr + (int)size;
        }

        return 1_000_000;
    }

    private static double? ReadDuration(RepairInput input, Element info)
    {
        byte[] payload = ReadPayload(input, info);
        int p = 0;
        while (p < payload.Length && TryHeader(payload.AsSpan(p), out uint id, out int hdr, out long size) && size >= 0 && p + hdr + size <= payload.Length)
        {
            if (id == MatroskaIds.Duration)
            {
                ReadOnlySpan<byte> v = payload.AsSpan(p + hdr, (int)size);
                if (size == 4)
                {
                    return BinaryPrimitives.ReadSingleBigEndian(v);
                }

                if (size == 8)
                {
                    return BinaryPrimitives.ReadDoubleBigEndian(v);
                }
            }

            p += hdr + (int)size;
        }

        return null;
    }

    private static byte[] ReadPayload(RepairInput input, Element e)
    {
        byte[] b = new byte[Math.Min(e.PayloadSize, MaxMetaElement)];
        input.Read(e.Offset + e.HeaderSize, b);
        return b;
    }

    // ---------------------------------------------------------------- validity of existing indexes

    private static bool CuesValid(RepairInput input, Element cues, List<TrackInfo> tracks, HashSet<long> clusters, long segData, MediaLimits limits)
    {
        if (cues.PayloadSize > MaxMetaElement || cues.PayloadSize > limits.MaxAllocationBytes)
        {
            return false;
        }

        byte[] p = ReadPayload(input, cues);
        int n = 0;
        int i = 0;
        while (i < p.Length)
        {
            if (!TryHeader(p.AsSpan(i), out uint id, out int h, out long s) || s < 0 || i + h + s > p.Length)
            {
                return false;
            }

            if (id is MatroskaIds.Crc32 or MatroskaIds.Void)
            {
                i += h + (int)s;
                continue;
            }

            if (id != MatroskaIds.CuePoint)
            {
                return false;
            }

            bool good = false;
            int q = i + h;
            int e = q + (int)s;
            while (q < e && TryHeader(p.AsSpan(q, e - q), out uint cid, out int ch, out long cs) && cs >= 0 && q + ch + cs <= e)
            {
                if (cid == MatroskaIds.CueTrackPositions)
                {
                    long track = -1, cluster = -1;
                    int r = q + ch;
                    int re = r + (int)cs;
                    while (r < re && TryHeader(p.AsSpan(r, re - r), out uint gid, out int gh, out long gs) && gs >= 0 && r + gh + gs <= re)
                    {
                        if (gid == MatroskaIds.CueTrack && Ebml.TryReadUnsigned(p.AsSpan(r + gh, (int)gs), out ulong tv))
                        {
                            track = (long)tv;
                        }
                        else if (gid == MatroskaIds.CueClusterPosition && Ebml.TryReadUnsigned(p.AsSpan(r + gh, (int)gs), out ulong cv))
                        {
                            cluster = (long)cv;
                        }

                        r += gh + (int)gs;
                    }

                    good = tracks.Exists(t => t.Number == track) && clusters.Contains(cluster);
                }

                q += ch + (int)cs;
            }

            if (!good)
            {
                return false;
            }

            n++;
            i += h + (int)s;
        }

        _ = segData;
        return n > 0;
    }

    private static bool SeekHeadsValid(RepairInput input, List<Element> heads, Dictionary<long, uint> l1, long segData, MediaLimits limits)
    {
        bool any = false;
        foreach (Element sh in heads)
        {
            if (sh.PayloadSize > limits.MaxAllocationBytes || sh.PayloadSize > MaxMetaElement)
            {
                return false;
            }

            byte[] p = ReadPayload(input, sh);
            int i = 0;
            while (i < p.Length)
            {
                if (!TryHeader(p.AsSpan(i), out uint id, out int h, out long s) || s < 0 || i + h + s > p.Length)
                {
                    return false;
                }

                if (id == MatroskaIds.Seek)
                {
                    uint seekId = 0;
                    long position = -1;
                    int q = i + h;
                    int e = q + (int)s;
                    while (q < e && TryHeader(p.AsSpan(q, e - q), out uint cid, out int ch, out long cs) && cs >= 0 && q + ch + cs <= e)
                    {
                        ReadOnlySpan<byte> v = p.AsSpan(q + ch, (int)cs);
                        if (cid == MatroskaIds.SeekId)
                        {
                            foreach (byte b in v)
                            {
                                seekId = (seekId << 8) | b;
                            }
                        }
                        else if (cid == MatroskaIds.SeekPosition && Ebml.TryReadUnsigned(v, out ulong pv))
                        {
                            position = (long)pv;
                        }

                        q += ch + (int)cs;
                    }

                    // An entry for another SeekHead, Void or Cues-less file is fine as long as it resolves.
                    if (!l1.TryGetValue(position, out uint actual) || actual != seekId)
                    {
                        return false;
                    }

                    any = true;
                }

                i += h + (int)s;
            }
        }

        _ = segData;
        return any;
    }

    // ---------------------------------------------------------------- building

    private static byte[] BuildInfo(RepairInput input, Element? info, long durationTicks)
    {
        var body = new List<byte>();
        bool wrote = false;
        byte[] duration = DurationElement(durationTicks);
        if (info is not null)
        {
            byte[] payload = ReadPayload(input, info);
            int i = 0;
            while (i < payload.Length && TryHeader(payload.AsSpan(i), out uint id, out int h, out long s) && s >= 0 && i + h + s <= payload.Length)
            {
                int total = h + (int)s;
                if (id == MatroskaIds.Duration)
                {
                    body.AddRange(duration);
                    wrote = true;
                }
                else if (id != MatroskaIds.Crc32)
                {
                    body.AddRange(payload.AsSpan(i, total).ToArray());
                }

                i += total;
            }
        }
        else
        {
            body.AddRange(EbmlWriter.UnsignedElement(MatroskaIds.TimestampScale, 1_000_000));
        }

        if (!wrote)
        {
            body.AddRange(duration);
        }

        return EbmlWriter.Element(MatroskaIds.Info, body.ToArray());
    }

    private static byte[] DurationElement(long ticks)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(payload, ticks);
        return EbmlWriter.Element(MatroskaIds.Duration, payload);
    }

    private static byte[] SeekHeadBytes(List<uint> ids, long[] positions)
    {
        var body = new List<byte>();
        for (int i = 0; i < ids.Count; i++)
        {
            byte[] idBytes = new byte[4];
            int il = EbmlWriter.WriteId(idBytes, ids[i]);
            byte[] pos = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(pos, positions[i]);
            byte[] entry = [.. EbmlWriter.Element(MatroskaIds.SeekId, idBytes.AsSpan(0, il)), .. EbmlWriter.Element(MatroskaIds.SeekPosition, pos)];
            body.AddRange(EbmlWriter.Element(MatroskaIds.Seek, entry));
        }

        return EbmlWriter.Element(MatroskaIds.SeekHead, body.ToArray());
    }

    private static byte[] BuildCues(List<Element> elements, List<TrackInfo> tracks, MediaLimits limits, out long count)
    {
        var body = new List<byte>();
        count = 0;
        foreach (Element e in elements)
        {
            if (e.Cluster is not { } c)
            {
                continue;
            }

            foreach (CueCandidate q in c.Cues)
            {
                limits.CheckCues((int)Math.Min(int.MaxValue, count + 1), e.Offset);
                byte[] positions = EbmlWriter.Element(MatroskaIds.CueTrackPositions, [
                    .. EbmlWriter.UnsignedElement(MatroskaIds.CueTrack, (ulong)q.Track),
                    .. EbmlWriter.UnsignedElement(MatroskaIds.CueClusterPosition, (ulong)c.OutputPosition),
                    .. EbmlWriter.UnsignedElement(MatroskaIds.CueRelativePosition, (ulong)(q.RelativePosition - (c.Rewrite && c.HasCrc ? 6 : 0)))]);
                body.AddRange(EbmlWriter.Element(MatroskaIds.CuePoint, [.. EbmlWriter.UnsignedElement(MatroskaIds.CueTime, (ulong)Math.Max(0, q.Time)), .. positions]));
                count++;
            }
        }

        _ = tracks;
        return EbmlWriter.Element(MatroskaIds.Cues, body.ToArray());
    }

    private static void WriteCluster(RepairInput input, RepairOutput output, Element e)
    {
        ClusterScan c = e.Cluster!;
        if (!c.Rewrite)
        {
            // Untouched Cluster: header and payload as they were, except a Position that no longer matches (same width, patched).
            WriteBody(input, output, e, c, e.Offset, e.HeaderSize);
            return;
        }

        Span<byte> head = stackalloc byte[12]; // Cluster ID (4) + 8-byte size
        EbmlWriter.WriteId(head, MatroskaIds.Cluster);
        long payload = e.PayloadSize + (c.HasCrc ? -6 : 0);
        EbmlWriter.WriteSize(head[4..], payload, 8);
        output.Write(head);
        long start = c.ContentStart;
        if (c.HasCrc)
        {
            // A CRC-32 covers the old content; it is dropped because the content changed. It is the first child (RFC 8794 §11.3).
            ReadOnlySpan<byte> s = input.Peek(start, 8);
            if (s.Length >= 6 && s[0] == 0xBF && s[1] == 0x84)
            {
                start += 6;
            }
        }

        CopyWithPosition(input, output, c, start, c.KeptEnd);
    }

    private static void WriteBody(RepairInput input, RepairOutput output, Element e, ClusterScan c, long headerOffset, int headerSize)
    {
        output.Copy(input, headerOffset, headerSize);
        CopyWithPosition(input, output, c, c.ContentStart, c.KeptEnd);
    }

    private static void CopyWithPosition(RepairInput input, RepairOutput output, ClusterScan c, long from, long to)
    {
        if (c.PositionOffset >= from && c.PositionOffset + c.PositionWidth <= to && c.PositionValue != c.OutputPosition)
        {
            output.Copy(input, from, c.PositionOffset - from);
            byte[] v = new byte[c.PositionWidth];
            ulong value = (ulong)c.OutputPosition;
            for (int i = v.Length - 1; i >= 0; i--)
            {
                v[i] = (byte)value;
                value >>= 8;
            }

            output.Write(v);
            output.Copy(input, c.PositionOffset + c.PositionWidth, to - (c.PositionOffset + c.PositionWidth));
        }
        else
        {
            output.Copy(input, from, to - from);
        }
    }
}

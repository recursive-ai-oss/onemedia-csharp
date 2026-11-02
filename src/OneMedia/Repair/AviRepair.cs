using System.Buffers.Binary;
using RecursiveAI.OneMedia.Avi;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// AVI/OpenDML index repair (Microsoft AVI RIFF reference; OpenDML AVI File Format Extensions 1.02). One scan walks the chunks of every
/// <c>movi</c> list by their own headers, never trusting the sizes of the enclosing RIFF and LIST, and drops a chunk cut off at the end or
/// bytes that are not a chunk. The file is then written again with the data chunks copied unchanged, sizes of RIFF and LIST fixed, frame
/// counts in <c>avih</c>, <c>strh</c> and <c>dmlh</c> set from the chunks, a fresh <c>idx1</c> and, for OpenDML files, fresh standard
/// indexes (<c>ix##</c>) with the super indexes (<c>indx</c>) that point at them.
/// </summary>
internal static class AviRepair
{
    private const uint FccRiff = 0x46464952; // "RIFF"
    private const uint FccList = 0x5453494C; // "LIST"
    private const uint FccJunk = 0x4B4E554A; // "JUNK"
    private const uint FccIdx1 = 0x31786469; // "idx1"
    private const uint FccMovi = 0x69766F6D; // "movi"
    private const uint FccHdrl = 0x6C726468; // "hdrl"
    private const uint FccStrl = 0x6C727473; // "strl"
    private const uint FccOdml = 0x6C6D646F; // "odml"
    private const uint FccRec = 0x20636572; // "rec "
    private const uint FccAvih = 0x68697661; // "avih"
    private const uint FccStrh = 0x68727473; // "strh"
    private const uint FccStrf = 0x66727473; // "strf"
    private const uint FccIndx = 0x78646E69; // "indx"
    private const uint FccDmlh = 0x686C6D64; // "dmlh"
    private const int MaxEntriesPerIx = 8192;

    private struct Rec
    {
        public long Offset;
        public uint Id;
        public uint Size;
        public long Total; // bytes the record takes in the file, header and padding included
        public int Stream; // >= 0 data chunk; -1 raw piece copied unchanged; -2 an old ix chunk (dropped, rebuilt)
        public bool Key;
    }

    private sealed class Item
    {
        public long Offset;
        public long Length;
        public bool IsHdrl;
        public bool IsMovi;
    }

    private sealed class Movi
    {
        public long ListOffset;
        public long DeclaredSize;
        public long End; // where the last record ends
        public List<Rec> Recs = [];
    }

    private sealed class Riff
    {
        public long Offset;
        public long DeclaredSize;
        public uint Form;
        public List<Item> Items = [];
        public Movi? Movi;
        public int MoviItemIndex = -1;
        public Item? OldIdx1;
    }

    private sealed class StreamState
    {
        public int Index;
        public AviStreamHeader Header;
        public KeyframeGuess Guess;
        public long Chunks;
        public long Bytes;
        public uint OldLength;
        public bool HasIndx;
        public string Codec = string.Empty;
    }

    public static RepairReport Run(RepairInput input, Stream destination, MediaLimits limits, CancellationToken ct)
    {
        var report = new RepairReportBuilder("avi", input.Length);
        ReadOnlySpan<byte> head = input.Peek(0, 16);
        if (head.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(head) != FccRiff || BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != 0x20495641)
        {
            report.Warn("The file does not start with a RIFF 'AVI ' header.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        // ---- scan
        var riffs = new List<Riff>();
        var streams = new List<StreamState>();
        Riff? current = null;
        long pos = 0;
        long dropped = 0;
        long count = 0;
        byte[]? hdrl = null;
        long hdrlOffset = 0;
        bool structureChanged = false;
        byte[] keyBuffer = new byte[AviKeyframes.HeadBytes];
        bool sawGuess = false;

        while (pos < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            limits.CheckElementCount(++count, pos);
            ReadOnlySpan<byte> h = input.Peek(pos, 12);
            if (h.Length < 8)
            {
                Drop(report, "garbage", pos, input.Length - pos, "A few bytes at the end of the file do not form a chunk header.");
                dropped += input.Length - pos;
                structureChanged = true;
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (id == FccRiff && h.Length >= 12)
            {
                current = new Riff { Offset = pos, DeclaredSize = size, Form = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) };
                riffs.Add(current);
                if (riffs.Count > 1 && current.Form != 0x58495641)
                {
                    report.Warn($"A second RIFF at {pos} is not an 'AVIX' extension; it is treated as one.");
                }

                pos += 12;
                continue;
            }

            if (current is null)
            {
                break;
            }

            long total = 8L + size + (size & 1);
            if (id == FccList && h.Length >= 12)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
                if (type == FccMovi)
                {
                    var m = new Movi { ListOffset = pos, DeclaredSize = size };
                    long end = ScanMovi(input, m, pos + 12, size, streams, keyBuffer, report, limits, ct, ref dropped, ref structureChanged, ref sawGuess);
                    if (current.Movi is null)
                    {
                        current.Movi = m;
                        current.MoviItemIndex = current.Items.Count;
                        current.Items.Add(new Item { Offset = pos, Length = end - pos, IsMovi = true });
                    }
                    else
                    {
                        report.Warn($"A second movi list at {pos} was left out.");
                        report.Add(RepairAction.Dropped, "movi", -1, pos, end - pos, "A second movi list in one RIFF.", RepairConfidence.Medium);
                        dropped += end - pos;
                    }

                    pos = end;
                    continue;
                }

                if (pos + total > input.Length && !(type == FccHdrl))
                {
                    Drop(report, "LIST", pos, input.Length - pos, "A list is cut off at the end of the file; it is left out.");
                    dropped += input.Length - pos;
                    structureChanged = true;
                    break;
                }

                if (type == FccHdrl)
                {
                    if (pos + total > input.Length || hdrl is not null)
                    {
                        report.Warn("The header list (hdrl) is cut off or repeated; without it the streams cannot be described.");
                        if (hdrl is null)
                        {
                            return report.Build(RepairStatus.Unrepairable, 0);
                        }
                    }
                    else
                    {
                        limits.CheckAllocation(total, pos);
                        hdrl = new byte[total];
                        input.Read(pos, hdrl);
                        hdrlOffset = pos;
                        current.Items.Add(new Item { Offset = pos, Length = total, IsHdrl = true });
                        ParseHeaders(hdrl, streams, limits);
                    }

                    pos += total;
                    continue;
                }

                current.Items.Add(new Item { Offset = pos, Length = total });
                pos += total;
                continue;
            }

            if (id == FccIdx1)
            {
                if (pos + 8 + size > input.Length)
                {
                    Drop(report, "idx1", pos, input.Length - pos, "The index is cut off at the end of the file; it is rebuilt.");
                    dropped += input.Length - pos;
                    structureChanged = true;
                    break;
                }

                current.OldIdx1 = new Item { Offset = pos, Length = total };
                pos += total;
                continue;
            }

            if (pos + 8 + size > input.Length)
            {
                Drop(report, "chunk", pos, input.Length - pos, "A chunk is cut off at the end of the file; it is left out.");
                dropped += input.Length - pos;
                structureChanged = true;
                break;
            }

            current.Items.Add(new Item { Offset = pos, Length = Math.Min(total, input.Length - pos) });
            pos += total;
        }

        if (hdrl is null || streams.Count == 0)
        {
            report.Warn("The file has no usable header list (hdrl) with stream headers.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        if (riffs[0].Movi is null || riffs[0].Movi!.Recs.Count == 0)
        {
            report.Warn("The file holds no stream chunks to index.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        // ---- counts
        bool openDml = riffs.Count > 1 || streams.Exists(s => s.HasIndx) || HasOdml(hdrl);
        Rec[][] recs = new Rec[riffs.Count][];
        for (int r = 0; r < riffs.Count; r++)
        {
            recs[r] = riffs[r].Movi is { } mv ? [.. mv.Recs] : [];
        }

        long firstRiffVideo = 0;
        int videoStream = streams.FindIndex(s => s.Header.Type == AviStreamHeader.Vids);
        foreach (Rec c in recs[0])
        {
            if (c.Stream >= 0 && c.Stream == (videoStream < 0 ? 0 : videoStream))
            {
                firstRiffVideo++;
            }
        }

        foreach (StreamState s in streams)
        {
            report.Tracks.Add(new RepairTrackReport(s.Index, s.Codec, s.Chunks, 0, s.Guess == KeyframeGuess.All ? RepairConfidence.High : RepairConfidence.Medium,
                [new ByteRange(riffs[0].Movi!.ListOffset, riffs[^1].Movi?.End - riffs[0].Movi!.ListOffset ?? 0)]));
        }

        // ---- is the file already healthy?
        bool sizesOk = SizesConsistent(input, riffs, dropped);
        bool headerOk = HeaderCountsOk(hdrl, streams, firstRiffVideo, videoStream, openDml);
        bool idxOk = riffs[0].OldIdx1 is not null && Idx1Matches(input, riffs[0], riffs[0].OldIdx1!);
        bool odmlOk = !openDml || OdmlMatches(input, streams, recs, riffs, limits);
        if (!structureChanged && sizesOk && headerOk && idxOk && odmlOk && dropped == 0)
        {
            report.Add(RepairAction.Kept, "idx1", -1, riffs[0].OldIdx1!.Offset, riffs[0].OldIdx1!.Length, "Every index entry points at a chunk of the same size.", RepairConfidence.High);
            using (RepairOutput copy = new(destination, ct))
            {
                copy.Copy(input, 0, input.Length);
            }

            return report.Build(RepairStatus.NothingToRepair, input.Length);
        }

        if (!sizesOk || structureChanged)
        {
            report.Add(RepairAction.Fixed, "RIFF", -1, 0, 12, "RIFF and LIST sizes are set from the chunks that are present.", RepairConfidence.High);
        }

        if (!headerOk)
        {
            report.Add(RepairAction.Fixed, "avih/strh", -1, hdrlOffset, hdrl.Length, "Frame counts in the main header, stream headers and OpenDML header are set from the chunks.", RepairConfidence.High);
        }

        // ---- plan the new header list
        var superEntries = new List<(long Pos, uint Size, uint Duration)>[streams.Count];
        for (int i = 0; i < streams.Count; i++)
        {
            superEntries[i] = [];
        }

        int[] ixCounts = new int[streams.Count];
        if (openDml)
        {
            for (int r = 0; r < riffs.Count; r++)
            {
                var perStream = new long[streams.Count];
                foreach (Rec c in recs[r])
                {
                    if (c.Stream >= 0 && c.Stream < streams.Count)
                    {
                        perStream[c.Stream]++;
                    }
                }

                for (int i = 0; i < streams.Count; i++)
                {
                    ixCounts[i] += (int)((perStream[i] + MaxEntriesPerIx - 1) / MaxEntriesPerIx);
                }
            }
        }

        byte[] newHdrl = BuildHeaderList(hdrl, streams, firstRiffVideo, videoStream, openDml, ixCounts, riffs.Count, out long[] indxOffsets);

        // ---- write
        {
            using var output = new RepairOutput(destination, ct);
            long idx1Count = 0;
            long firstHdrlOut = 0;
            List<(uint Id, uint Flags, uint Offset, uint Size)>? pendingIdx1 = null;
            for (int r = 0; r < riffs.Count; r++)
            {
                Riff riff = riffs[r];
                long riffStart = output.Position;
                byte[] rh = new byte[12]; // RIFF header: id, size, form type
                BinaryPrimitives.WriteUInt32LittleEndian(rh, FccRiff);
                BinaryPrimitives.WriteUInt32LittleEndian(rh.AsSpan(4), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(rh.AsSpan(8), r == 0 ? 0x20495641u : 0x58495641u);
                output.Write(rh);
                long hdrlOut = -1;
                var after = new List<Item>();
                for (int k = 0; k < riff.Items.Count; k++)
                {
                    Item it = riff.Items[k];
                    if (it.IsHdrl)
                    {
                        hdrlOut = output.Position;
                        output.Write(newHdrl);
                    }
                    else if (it.IsMovi)
                    {
                        WriteMovi(input, output, riff, streams, recs[r], r == 0, openDml, superEntries, out List<(uint Id, uint Flags, uint Offset, uint Size)>? built, ct);
                        if (built is not null)
                        {
                            pendingIdx1 = built;
                            idx1Count = built.Count;
                        }
                    }
                    else if (riff.MoviItemIndex >= 0 && k > riff.MoviItemIndex)
                    {
                        after.Add(it);
                    }
                    else
                    {
                        output.Copy(input, it.Offset, it.Length);
                    }
                }

                foreach (Item it in after)
                {
                    output.Copy(input, it.Offset, it.Length);
                }

                if (r == 0)
                {
                    WriteIdx1(output, pendingIdx1!);
                }

                PatchUInt32(output, riffStart + 4, (uint)(output.Position - riffStart - 8));
                _ = hdrlOut;
                if (r == 0)
                {
                    firstHdrlOut = hdrlOut;
                }
            }

            // super indexes
            if (openDml)
            {
                for (int i = 0; i < streams.Count; i++)
                {
                    PatchSuperIndex(output, firstHdrlOut + indxOffsets[i], streams[i], superEntries[i]);
                }
            }

            output.Flush();
            report.Add(RepairAction.Rebuilt, "idx1", -1, riffs[0].OldIdx1?.Offset ?? input.Length, riffs[0].OldIdx1?.Length ?? 0,
                $"{idx1Count} entries built from the chunks of the first movi list" + (sawGuess ? "; key frames of the video are judged from the picture headers." : "; every chunk is a key frame."), sawGuess ? RepairConfidence.Medium : RepairConfidence.High);
            if (openDml)
            {
                int total = 0;
                foreach (var l in superEntries)
                {
                    total += l.Count;
                }

                report.Add(RepairAction.Rebuilt, "indx/ix##", -1, 0, 0, $"{total} standard index chunks built and pointed at by the super indexes.", RepairConfidence.High);
            }
            return report.Build(RepairStatus.Repaired, output.Position);
        }
    }

    // ---------------------------------------------------------------- scan helpers

    private static void Drop(RepairReportBuilder report, string what, long offset, long length, string detail) =>
        report.Add(RepairAction.Dropped, what, -1, offset, length, detail, RepairConfidence.High);

    private static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';

    private static bool IsLetter(byte c) => (c | 0x20) is >= (byte)'a' and <= (byte)'z';

    // A stream chunk is two decimal digits and two letters ("00dc"); a standard index chunk is "ix" and two digits (OpenDML 1.02).
    private static bool IsStreamChunkId(uint id)
    {
        byte a = (byte)id, b = (byte)(id >> 8), c = (byte)(id >> 16), d = (byte)(id >> 24);
        return (IsDigit(a) && IsDigit(b) && IsLetter(c) && IsLetter(d)) || (a == (byte)'i' && b == (byte)'x' && IsDigit(c) && IsDigit(d));
    }

    private static long ScanMovi(RepairInput input, Movi m, long start, uint declared, List<StreamState> streams, byte[] keyBuffer, RepairReportBuilder report, MediaLimits limits,
        CancellationToken ct, ref long dropped, ref bool structureChanged, ref bool sawGuess)
    {
        long declaredEnd = m.ListOffset + 8 + (long)declared;
        if (declaredEnd > input.Length || declared < 4)
        {
            structureChanged = true;
            declaredEnd = -1;
        }

        long pos = start;
        long count = 0;
        while (pos < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> h = input.Peek(pos, 12);
            if (declaredEnd >= 0 && pos >= declaredEnd)
            {
                // The stated end is only believed when something other than a chunk follows it: a stated size that is too small is a damage.
                if (h.Length < 8 || !IsStreamChunkId(BinaryPrimitives.ReadUInt32LittleEndian(h)))
                {
                    break;
                }

                structureChanged = true;
                declaredEnd = -1;
            }

            if (h.Length < 8)
            {
                Drop(report, "garbage", pos, input.Length - pos, "A few bytes at the end of the file do not form a chunk header.");
                dropped += input.Length - pos;
                structureChanged = true;
                pos = input.Length;
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (id == FccRiff || id == FccIdx1)
            {
                break;
            }

            if (id == FccList)
            {
                if (h.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) == FccRec)
                {
                    m.Recs.Add(new Rec { Offset = pos, Id = id, Size = 12, Total = 12, Stream = -1 });
                    pos += 12;
                    continue;
                }

                break; // another list: the movi list ended before its stated size
            }

            bool valid = IsStreamChunkId(id) || id == FccJunk;
            long total = 8L + size + (size & 1);
            if (valid && pos + 8 + size <= input.Length)
            {
                limits.CheckSampleTable(++count, pos);
                int stream = -1;
                bool isIx = (byte)id == (byte)'i' && (byte)(id >> 8) == (byte)'x';
                if (!isIx && id != FccJunk)
                {
                    stream = (byte)(id) - '0';
                    stream = (stream * 10) + (byte)(id >> 8) - '0';
                }

                if (isIx)
                {
                    m.Recs.Add(new Rec { Offset = pos, Id = id, Size = size, Total = Math.Min(total, input.Length - pos), Stream = -2 });
                }
                else if (stream >= 0 && stream < streams.Count)
                {
                    StreamState s = streams[stream];
                    bool key = true;
                    if (s.Guess != KeyframeGuess.All && size > 0 && s.Header.Type == AviStreamHeader.Vids)
                    {
                        int n = input.Read(pos + 8, keyBuffer.AsSpan(0, (int)Math.Min(size, keyBuffer.Length)));
                        key = AviKeyframes.IsKey(s.Guess, keyBuffer.AsSpan(0, n));
                        sawGuess = true;
                    }

                    s.Chunks++;
                    s.Bytes += size;
                    m.Recs.Add(new Rec { Offset = pos, Id = id, Size = size, Total = Math.Min(total, input.Length - pos), Stream = stream, Key = key });
                }
                else
                {
                    // a chunk of a stream the header list does not describe: copied, not indexed
                    m.Recs.Add(new Rec { Offset = pos, Id = id, Size = size, Total = Math.Min(total, input.Length - pos), Stream = -1 });
                }

                pos += total;
                continue;
            }

            structureChanged = true;
            if (valid && pos + 8 + size > input.Length)
            {
                Drop(report, "chunk", pos, input.Length - pos, "The last chunk is cut off; the partial data is left out.");
                dropped += input.Length - pos;
                pos = input.Length;
                break;
            }

            long next = FindChunk(input, pos + 1, declaredEnd);
            Drop(report, "garbage", pos, next - pos, next >= input.Length ? "Bytes at the end of the movi list are not chunks." : "Bytes between chunks are not a chunk; skipped up to the next chunk.");
            dropped += next - pos;
            pos = next;
        }

        m.End = pos;
        if (declaredEnd >= 0 && pos != declaredEnd)
        {
            structureChanged = true;
        }

        return pos;
    }

    // Finds the next offset that starts a chain of two plausible chunks, or a list/idx1/RIFF header, or the end of the file.
    private static long FindChunk(RepairInput input, long from, long declaredEnd)
    {
        long p = from;
        while (p + 8 <= input.Length)
        {
            ReadOnlySpan<byte> h = input.Peek(p, 8);
            if (h.Length < 8)
            {
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (id is FccIdx1 or FccRiff)
            {
                return p;
            }

            if (IsStreamChunkId(id) && p + 8 + size <= input.Length)
            {
                long q = p + 8 + size + (size & 1);
                if (q >= input.Length)
                {
                    return p;
                }

                ReadOnlySpan<byte> n = input.Peek(q, 8);
                if (n.Length >= 8)
                {
                    uint nid = BinaryPrimitives.ReadUInt32LittleEndian(n);
                    if (IsStreamChunkId(nid) || nid is FccIdx1 or FccRiff or FccList or FccJunk)
                    {
                        return p;
                    }
                }
            }

            p++; // damage can leave a chunk at any offset, so every byte is a candidate
        }

        _ = declaredEnd;
        return input.Length;
    }

    private static bool HasOdml(byte[] hdrl)
    {
        int p = 12;
        while (p + 12 <= hdrl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 4));
            if (id == FccList && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8)) == FccOdml)
            {
                return true;
            }

            p += (int)Math.Min(8L + size + (size & 1), hdrl.Length - p);
        }

        return false;
    }

    private static void ParseHeaders(byte[] hdrl, List<StreamState> streams, MediaLimits limits)
    {
        int p = 12;
        while (p + 8 <= hdrl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 4));
            int total = (int)Math.Min(8L + size + (size & 1), hdrl.Length - p);
            if (id == FccList && p + 12 <= hdrl.Length && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8)) == FccStrl)
            {
                limits.CheckTracks(streams.Count + 1, p);
                var s = new StreamState { Index = streams.Count };
                CodecId codec = CodecId.Unknown;
                int q = p + 12;
                int e = p + total;
                bool haveHeader = false;
                while (q + 8 <= e)
                {
                    uint cid = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(q));
                    uint cs = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(q + 4));
                    int ct = (int)Math.Min(8L + cs + (cs & 1), e - q);
                    if (cid == FccStrh && cs >= AviStreamHeader.Size)
                    {
                        s.Header = AviStreamHeader.Parse(hdrl.AsSpan(q + 8, (int)cs));
                        s.OldLength = s.Header.Length;
                        haveHeader = true;
                    }
                    else if (cid == FccStrf && haveHeader && s.Header.Type == AviStreamHeader.Vids && BitmapInfoHeader.TryParse(hdrl.AsSpan(q + 8, (int)Math.Min(cs, hdrl.Length - q - 8)), out BitmapInfoHeader bmp))
                    {
                        codec = bmp.Compression is 0 or 3 or 1 or 2 ? CodecId.Unknown : AviCodecMap.Video(bmp.CompressionText);
                        if (codec.IsUnknown)
                        {
                            codec = AviCodecMap.Video(s.Header.HandlerText);
                        }
                    }
                    else if (cid == FccIndx)
                    {
                        s.HasIndx = true;
                    }

                    q += ct;
                }

                s.Codec = codec.IsUnknown ? string.Empty : codec.Name;
                s.Guess = AviKeyframes.For(codec);
                streams.Add(s);
            }

            p += total;
        }
    }

    // ---------------------------------------------------------------- health checks

    private static bool SizesConsistent(RepairInput input, List<Riff> riffs, long dropped)
    {
        for (int r = 0; r < riffs.Count; r++)
        {
            long end = r + 1 < riffs.Count ? riffs[r + 1].Offset : input.Length;
            if (riffs[r].Offset + 8 + riffs[r].DeclaredSize != end)
            {
                return false;
            }

            if (riffs[r].Movi is { } m)
            {
                if (m.ListOffset + 8 + m.DeclaredSize != m.End)
                {
                    return false;
                }
            }
        }

        return dropped == 0;
    }

    private static bool HeaderCountsOk(byte[] hdrl, List<StreamState> streams, long firstRiffVideo, int videoStream, bool openDml)
    {
        int p = 12;
        while (p + 8 <= hdrl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 4));
            if (id == FccAvih && size >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8 + 16)) != firstRiffVideo)
            {
                return false;
            }

            p += (int)Math.Min(8L + size + (size & 1), hdrl.Length - p);
        }

        foreach (StreamState s in streams)
        {
            if (s.OldLength != StreamLength(s))
            {
                return false;
            }
        }

        _ = videoStream;
        _ = openDml;
        return true;
    }

    private static uint StreamLength(StreamState s) => s.Header.SampleSize > 0 ? (uint)(s.Bytes / s.Header.SampleSize) : (uint)s.Chunks;

    private static bool Idx1Matches(RepairInput input, Riff riff, Item idx1)
    {
        Movi m = riff.Movi!;
        long entries = (idx1.Length - 8) / 16;
        long expected = 0;
        foreach (Rec c in m.Recs)
        {
            if (c.Stream >= 0)
            {
                expected++;
            }
        }

        if (entries != expected)
        {
            return false;
        }

        long baseRel = m.ListOffset + 8; // offsets count from the 'movi' tag
        long i = 0;
        byte[] e = new byte[16];
        bool? relative = null;
        foreach (Rec c in m.Recs)
        {
            if (c.Stream < 0)
            {
                continue;
            }

            input.Read(idx1.Offset + 8 + (i * 16), e);
            i++;
            uint cid = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8));
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(12));
            if (cid != c.Id || sz != c.Size)
            {
                return false;
            }

            // offsets are counted from the 'movi' tag by most writers and from the start of the file by some
            bool rel = off == c.Offset - baseRel;
            bool abs = off == c.Offset;
            if (!rel && !abs)
            {
                return false;
            }

            relative ??= rel;
            if (relative != rel && !(rel && abs))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OdmlMatches(RepairInput input, List<StreamState> streams, Rec[][] recs, List<Riff> riffs, MediaLimits limits)
    {
        // every stream with a super index: the sum of the entries it points at equals the stream's chunk count, and each ix entry names a chunk of that size
        var chunkLookup = new List<(long Offset, uint Size, int Stream)>();
        foreach (Rec[] rr in recs)
        {
            foreach (Rec c in rr)
            {
                if (c.Stream >= 0)
                {
                    chunkLookup.Add((c.Offset, c.Size, c.Stream));
                }
            }
        }

        // find indx chunks in the first RIFF's hdrl by re-reading it
        Item? hdrlItem = riffs[0].Items.Find(x => x.IsHdrl);
        if (hdrlItem is null)
        {
            return false;
        }

        byte[] hdrl = new byte[hdrlItem.Length];
        input.Read(hdrlItem.Offset, hdrl);
        long[] counted = new long[streams.Count];
        int stream = -1;
        int p = 12;
        while (p + 8 <= hdrl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 4));
            int total = (int)Math.Min(8L + size + (size & 1), hdrl.Length - p);
            if (id == FccList && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8)) == FccStrl)
            {
                stream++;
                int q = p + 12;
                while (q + 8 <= p + total)
                {
                    uint cid = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(q));
                    uint cs = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(q + 4));
                    if (cid == FccIndx && stream < streams.Count)
                    {
                        uint inUse = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(q + 8 + 4));
                        if (hdrl[q + 8 + 3] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(hdrl.AsSpan(q + 8)) != 4 || inUse > (cs - 24) / 16)
                        {
                            return false;
                        }

                        for (uint k = 0; k < inUse; k++)
                        {
                            long entry = q + 8 + 24 + (k * 16);
                            long ixPos = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdrl.AsSpan((int)entry));
                            byte[] ix = new byte[32];
                            if (input.Read(ixPos, ix) < 32 || ix[0] != (byte)'i' || ix[1] != (byte)'x' || ix[11] != 1)
                            {
                                return false;
                            }

                            uint n = BinaryPrimitives.ReadUInt32LittleEndian(ix.AsSpan(12));
                            long baseOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(ix.AsSpan(20));
                            limits.CheckSampleTable(n, ixPos);
                            byte[] ent = new byte[8];
                            for (uint j = 0; j < n; j++)
                            {
                                if (input.Read(ixPos + 32 + (j * 8), ent) < 8)
                                {
                                    return false;
                                }

                                long header = baseOff + BinaryPrimitives.ReadUInt32LittleEndian(ent) - 8;
                                uint sz = BinaryPrimitives.ReadUInt32LittleEndian(ent.AsSpan(4)) & 0x7FFFFFFF;
                                int at = chunkLookup.BinarySearch((header, 0, 0), Comparer<(long Offset, uint Size, int Stream)>.Create((a, b) => a.Offset.CompareTo(b.Offset)));
                                if (at < 0 || chunkLookup[at].Size != sz || chunkLookup[at].Stream != stream)
                                {
                                    return false;
                                }
                            }

                            counted[stream] += n;
                        }
                    }

                    q += (int)Math.Min(8L + cs + (cs & 1), hdrl.Length - q);
                }
            }

            p += total;
        }

        for (int i = 0; i < streams.Count; i++)
        {
            if (counted[i] != streams[i].Chunks)
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- building

    private static byte[] BuildHeaderList(byte[] hdrl, List<StreamState> streams, long firstRiffVideo, int videoStream, bool openDml, int[] ixCounts, int riffCount, out long[] indxOffsets)
    {
        var body = new List<byte>(hdrl.Length + 1024);
        indxOffsets = new long[streams.Count];
        int stream = -1;
        bool hasOdml = HasOdml(hdrl);
        int p = 12;
        while (p + 8 <= hdrl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 4));
            int total = (int)Math.Min(8L + size + (size & 1), hdrl.Length - p);
            byte[] chunk = hdrl.AsSpan(p, total).ToArray();
            if (id == FccAvih && size >= 20)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8 + 16), (uint)firstRiffVideo);
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8 + 12), BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(8 + 12)) | AviMainHeader.HasIndex);
                body.AddRange(chunk);
            }
            else if (id == FccList && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8)) == FccStrl)
            {
                stream++;
                int before = body.Count;
                body.AddRange(BuildStrl(chunk, stream < streams.Count ? streams[stream] : null, openDml, stream < ixCounts.Length ? ixCounts[stream] : 0, out long indxAt));
                if (stream < indxOffsets.Length)
                {
                    indxOffsets[stream] = before + indxAt;
                }
            }
            else if (id == FccList && BinaryPrimitives.ReadUInt32LittleEndian(hdrl.AsSpan(p + 8)) == FccOdml)
            {
                PatchDmlh(chunk, streams, videoStream);
                body.AddRange(chunk);
            }
            else
            {
                body.AddRange(chunk);
            }

            p += total;
        }

        if (openDml && !hasOdml)
        {
            byte[] dmlh = new byte[8 + 248];
            BinaryPrimitives.WriteUInt32LittleEndian(dmlh, FccDmlh);
            BinaryPrimitives.WriteUInt32LittleEndian(dmlh.AsSpan(4), 248);
            byte[] odml = new byte[12 + dmlh.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(odml, FccList);
            BinaryPrimitives.WriteUInt32LittleEndian(odml.AsSpan(4), (uint)(4 + dmlh.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(odml.AsSpan(8), FccOdml);
            dmlh.CopyTo(odml, 12);
            PatchDmlh(odml, streams, videoStream);
            body.AddRange(odml);
        }

        _ = riffCount;
        byte[] result = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(result, FccList);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(4 + body.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), FccHdrl);
        body.CopyTo(result, 12);
        // indxOffsets were measured inside `body`; make them relative to the start of the hdrl list
        for (int i = 0; i < indxOffsets.Length; i++)
        {
            indxOffsets[i] += 12;
        }

        return result;
    }

    private static void PatchDmlh(byte[] odml, List<StreamState> streams, int videoStream)
    {
        long total = 0;
        int v = videoStream < 0 ? 0 : videoStream;
        if (v < streams.Count)
        {
            total = streams[v].Chunks;
        }

        int p = 12;
        while (p + 8 <= odml.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(odml.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(odml.AsSpan(p + 4));
            if (id == FccDmlh && size >= 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(odml.AsSpan(p + 8), (uint)total);
            }

            p += (int)Math.Min(8L + size + (size & 1), odml.Length - p);
        }
    }

    private static byte[] BuildStrl(byte[] strl, StreamState? s, bool openDml, int ixCount, out long indxAt)
    {
        var body = new List<byte>(strl.Length + 256);
        indxAt = -1;
        bool wroteIndx = false;
        int p = 12;
        while (p + 8 <= strl.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(strl.AsSpan(p));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(strl.AsSpan(p + 4));
            int total = (int)Math.Min(8L + size + (size & 1), strl.Length - p);
            if (id == FccIndx)
            {
                if (openDml)
                {
                    indxAt = body.Count;
                    body.AddRange(EmptySuperIndex(Math.Max(1, ixCount)));
                    wroteIndx = true;
                }

                p += total;
                continue;
            }

            byte[] chunk = strl.AsSpan(p, total).ToArray();
            if (id == FccStrh && s is not null && size >= 36)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8 + 32), StreamLength(s));
            }

            body.AddRange(chunk);
            p += total;
        }

        if (openDml && !wroteIndx)
        {
            indxAt = body.Count;
            body.AddRange(EmptySuperIndex(Math.Max(1, ixCount)));
        }

        byte[] result = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(result, FccList);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(4 + body.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), FccStrl);
        body.CopyTo(result, 12);
        if (indxAt >= 0)
        {
            indxAt += 12;
        }

        return result;
    }

    private static byte[] EmptySuperIndex(int capacity)
    {
        byte[] b = new byte[8 + 24 + (16 * capacity)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, FccIndx);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(24 + (16 * capacity)));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 4); // wLongsPerEntry
        return b;
    }

    private static void PatchUInt32(RepairOutput output, long at, uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        output.Patch(at, b);
    }

    private static void PatchSuperIndex(RepairOutput output, long indxPos, StreamState s, List<(long Pos, uint Size, uint Duration)> entries)
    {
        // AVISUPERINDEX (OpenDML 1.02): wLongsPerEntry 4, bIndexSubType 0, bIndexType 0, nEntriesInUse, dwChunkId, 3 reserved, then {qwOffset, dwSize, dwDuration}
        byte[] b = new byte[24 + (16 * entries.Count)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), ChunkIdFor(s));
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24 + (i * 16)), (ulong)entries[i].Pos);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + (i * 16) + 8), entries[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + (i * 16) + 12), entries[i].Duration);
        }

        output.Patch(indxPos + 8, b);
    }

    private static uint ChunkIdFor(StreamState s)
    {
        char t = s.Header.Type == AviStreamHeader.Vids ? 'd' : 'w';
        char u = s.Header.Type == AviStreamHeader.Vids ? 'c' : 'b';
        if (s.Header.Type == AviStreamHeader.Txts)
        {
            t = 't';
            u = 'x';
        }

        return (uint)('0' + (s.Index / 10 % 10)) | ((uint)('0' + (s.Index % 10)) << 8) | ((uint)t << 16) | ((uint)u << 24);
    }

    private static void WriteMovi(RepairInput input, RepairOutput output, Riff riff, List<StreamState> streams, Rec[] recs, bool first, bool openDml,
        List<(long Pos, uint Size, uint Duration)>[] superEntries, out List<(uint Id, uint Flags, uint Offset, uint Size)>? idx1Out, CancellationToken ct)
    {
        idx1Out = null;
        long listStart = output.Position;
        Span<byte> lh = stackalloc byte[12]; // LIST header: id, size, 'movi'
        BinaryPrimitives.WriteUInt32LittleEndian(lh, FccList);
        BinaryPrimitives.WriteUInt32LittleEndian(lh[8..], FccMovi);
        output.Write(lh);
        long moviTag = listStart + 8;
        var idx1 = new List<(uint Id, uint Flags, uint Offset, uint Size)>();
        var perStream = new List<(long Header, uint Size, bool Key)>[streams.Count];
        for (int i = 0; i < perStream.Length; i++)
        {
            perStream[i] = [];
        }

        int k = 0;
        while (k < recs.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (recs[k].Stream == -2)
            {
                k++;
                continue;
            }

            // copy a run of records that are adjacent in the input
            int j = k;
            long runStart = recs[k].Offset;
            long runEnd = runStart;
            long outRun = output.Position;
            while (j < recs.Length && recs[j].Stream != -2 && recs[j].Offset == runEnd)
            {
                Rec c = recs[j];
                if (c.Stream >= 0)
                {
                    long outHeader = outRun + (c.Offset - runStart);
                    if (first)
                    {
                        idx1.Add((c.Id, c.Key ? 0x10u : 0u, (uint)(outHeader - moviTag), c.Size));
                    }

                    if (c.Stream < perStream.Length)
                    {
                        perStream[c.Stream].Add((outHeader, c.Size, c.Key));
                    }
                }

                runEnd = c.Offset + c.Total;
                j++;
            }

            output.Copy(input, runStart, runEnd - runStart);
            k = j;
        }

        if (openDml)
        {
            for (int s = 0; s < streams.Count; s++)
            {
                List<(long Header, uint Size, bool Key)> list = perStream[s];
                int at = 0;
                while (at < list.Count)
                {
                    int n = Math.Min(MaxEntriesPerIx, list.Count - at);
                    long baseOffset = list[at].Header;
                    // dwOffset is 32 bits: stop the chunk where the span would overflow
                    int fit = 1;
                    while (fit < n && list[at + fit].Header + 8 - baseOffset < 0x7FFFFFFF)
                    {
                        fit++;
                    }

                    n = fit;
                    long ixPos = output.Position;
                    byte[] ix = new byte[8 + 24 + (8 * n)];
                    ix[0] = (byte)'i';
                    ix[1] = (byte)'x';
                    ix[2] = (byte)('0' + (s / 10 % 10));
                    ix[3] = (byte)('0' + (s % 10));
                    BinaryPrimitives.WriteUInt32LittleEndian(ix.AsSpan(4), (uint)(24 + (8 * n)));
                    BinaryPrimitives.WriteUInt16LittleEndian(ix.AsSpan(8), 2);
                    ix[11] = 1; // AVI_INDEX_OF_CHUNKS
                    BinaryPrimitives.WriteUInt32LittleEndian(ix.AsSpan(12), (uint)n);
                    BinaryPrimitives.WriteUInt32LittleEndian(ix.AsSpan(16), ChunkIdFor(streams[s]));
                    BinaryPrimitives.WriteUInt64LittleEndian(ix.AsSpan(20), (ulong)baseOffset);
                    long bytes = 0;
                    for (int e = 0; e < n; e++)
                    {
                        (long header, uint size, bool key) = list[at + e];
                        BinaryPrimitives.WriteUInt32LittleEndian(ix.AsSpan(32 + (e * 8)), (uint)(header + 8 - baseOffset));
                        BinaryPrimitives.WriteUInt32LittleEndian(ix.AsSpan(32 + (e * 8) + 4), size | (key ? 0u : 0x80000000u));
                        bytes += size;
                    }

                    output.Write(ix);
                    uint duration = streams[s].Header.SampleSize > 0 ? (uint)(bytes / streams[s].Header.SampleSize) : (uint)n;
                    superEntries[s].Add((ixPos, (uint)ix.Length, duration));
                    at += n;
                }
            }
        }

        PatchUInt32(output, listStart + 4, (uint)(output.Position - listStart - 8));
        if (first)
        {
            idx1Out = idx1;
        }
    }

    private static void WriteIdx1(RepairOutput output, List<(uint Id, uint Flags, uint Offset, uint Size)> entries)
    {
        byte[] b = new byte[8 + (16 * entries.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, FccIdx1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(16 * entries.Count));
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + (i * 16)), entries[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + (i * 16) + 4), entries[i].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + (i * 16) + 8), entries[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + (i * 16) + 12), entries[i].Size);
        }

        output.Write(b);
    }
}

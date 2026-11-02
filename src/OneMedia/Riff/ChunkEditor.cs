using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Edits the chunk tree of a RIFF, RF64, Wave64 or IFF (AIFF) file and plans the new file. Every edit replaces, removes or adds whole
/// chunks; untouched chunks (above all the sample data) are copied from the original by range.
/// </summary>
/// <remarks>
/// <para>Where the edit fits the padding beside it (a chunk of exactly the old slot size, a smaller chunk followed by a <c>JUNK</c> chunk
/// that makes up the difference, or a larger chunk that uses up a following padding chunk) nothing else changes, so
/// <see cref="EditPlan.IsSameLayout"/> is true and <see cref="SafeWriter.SaveInPlace"/> can apply it. Otherwise the chunks after it move
/// and the enclosing <c>LIST</c>/<c>RIFF</c>/<c>FORM</c> sizes are recomputed; an RF64 file's <c>ds64</c> riffSize is updated. A plain
/// WAVE file whose edit would push it past the 32-bit RIFF size is promoted to RF64: the id becomes <c>RF64</c>, the size field
/// 0xFFFFFFFF, and a <c>ds64</c> chunk is added (using a leading <c>JUNK</c> chunk when one is big enough, as EBU Tech 3306 §3 allows).</para>
/// <para>Replacing the <c>data</c> chunk of an RF64 file is refused, because its size lives in <c>ds64</c> and the sample count with it.</para>
/// </remarks>
public sealed class ChunkEditor
{
    private readonly ChunkFile _file;
    private readonly IMediaSource _source;
    private readonly MediaLimits _limits;
    private readonly List<Request> _requests = [];
    private readonly HashSet<long> _padAdded = [];
    private Dictionary<ChunkNode, ChunkNode>? _parents;

    /// <summary>Creates an editor over a chunk file.</summary>
    /// <param name="file">The original file's chunk tree.</param>
    /// <param name="source">The source the tree was read from; it must stay open while plans are built and written.</param>
    /// <param name="limits">Allocation ceilings for the small chunks the editor reads (<c>ds64</c>); <see cref="MediaLimits.Default"/> when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ChunkEditor(ChunkFile file, IMediaSource source, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(source);
        _file = file;
        _source = source;
        _limits = limits ?? MediaLimits.Default;
    }

    /// <summary>
    /// Diagnostic: re-emit the header of every container chunk from its children instead of copying it. Untouched files must still come
    /// out byte-identical. Off by default.
    /// </summary>
    public bool RewriteHeaders { get; set; }

    /// <summary>
    /// Bytes of padding chunk (<c>JUNK</c>, or <c>junk</c> in Wave64) to leave after a chunk that has to grow, or after a chunk added where there was
    /// no padding to take it, so that a later edit fits in place. Rounded down to the layout's alignment (2, or 8 in Wave64); a value below a
    /// chunk header is treated as 0. Default 0 (no padding). An edit that fits the existing layout, and a file that is not edited, never gain padding.
    /// </summary>
    public long GrowthPadding { get; set; }

    /// <summary>Replaces a chunk with a new chunk.</summary>
    /// <param name="node">The chunk to replace.</param>
    /// <param name="chunk">The complete new chunk slot as built by <see cref="ChunkBuilder"/> (header, payload, pad).</param>
    /// <exception cref="ArgumentException"><paramref name="chunk"/> is not exactly one chunk slot of this file's layout, <paramref name="node"/> overlaps another edit, or it is the data chunk of an RF64 file.</exception>
    public void Replace(ChunkNode node, ReadOnlyMemory<byte> chunk)
    {
        ArgumentNullException.ThrowIfNull(node);
        CheckChunk(chunk);
        if (_file.Layout == ChunkLayout.Rf64 && node.Id == "data")
        {
            throw new ArgumentException("The data chunk of an RF64 file cannot be replaced: its size is held in ds64.", nameof(node));
        }

        Add(new Request(RequestKind.Replace, node, chunk, false));
    }

    /// <summary>Removes a chunk.</summary>
    /// <param name="node">The chunk to remove.</param>
    /// <param name="reclaimSpace">False (default): a padding chunk of the same slot size takes its place and nothing moves. True: the bytes are cut out and everything after moves up.</param>
    /// <exception cref="ArgumentException"><paramref name="node"/> overlaps another edit.</exception>
    public void Remove(ChunkNode node, bool reclaimSpace = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        Add(new Request(RequestKind.Remove, node, default, reclaimSpace));
    }

    /// <summary>Adds a chunk at the end of a container's children (the root or a <c>LIST</c>).</summary>
    /// <param name="parent">The container, or null for the root chunk.</param>
    /// <param name="chunk">The complete new chunk slot.</param>
    /// <exception cref="ArgumentException"><paramref name="chunk"/> is not exactly one chunk slot, or <paramref name="parent"/> is not a container.</exception>
    public void AppendChild(ChunkNode? parent, ReadOnlyMemory<byte> chunk)
    {
        CheckChunk(chunk);
        if (parent is not null && !parent.IsContainer)
        {
            throw new ArgumentException("The parent is not a container chunk.", nameof(parent));
        }

        Add(new Request(RequestKind.Append, parent ?? _file.Root, chunk, false));
    }

    private void CheckChunk(ReadOnlyMemory<byte> chunk)
    {
        if (!ChunkBuilder.IsSingleChunk(_file.Layout, chunk.Span))
        {
            throw new ArgumentException("The new content must be exactly one chunk slot (header, payload and pad byte) of this file's layout.", nameof(chunk));
        }
    }

    private Dictionary<ChunkNode, ChunkNode> Parents()
    {
        if (_parents is not null)
        {
            return _parents;
        }

        Dictionary<ChunkNode, ChunkNode> map = new(NodeComparer<ChunkNode>.Instance);
        Stack<ChunkNode> stack = new();
        stack.Push(_file.Root);
        while (stack.Count > 0)
        {
            ChunkNode p = stack.Pop();
            foreach (ChunkNode c in p.Chunks)
            {
                map[c] = p;
                if (c.IsContainer)
                {
                    stack.Push(c);
                }
            }
        }

        return _parents = map;
    }

    private void Add(Request request)
    {
        if (request.Kind != RequestKind.Append)
        {
            foreach (Request other in _requests)
            {
                if (other.Kind != RequestKind.Append && (other.Node.Offset == request.Node.Offset || IsAncestor(other.Node, request.Node) || IsAncestor(request.Node, other.Node)))
                {
                    throw new ArgumentException("Two edits overlap: a chunk and one of its descendants (or the same chunk) cannot both be edited.");
                }
            }
        }

        // A chunk appended inside something that is replaced or removed would vanish with it; refuse instead of losing it silently.
        foreach (Request other in _requests)
        {
            (ChunkNode replaced, ChunkNode appendedTo) = request.Kind == RequestKind.Append ? (other.Node, request.Node) : (request.Node, other.Node);
            bool pair = request.Kind == RequestKind.Append ? other.Kind != RequestKind.Append : other.Kind == RequestKind.Append;
            if (pair && (replaced.Offset == appendedTo.Offset || IsAncestor(replaced, appendedTo)))
            {
                throw new ArgumentException("A chunk cannot be added inside a chunk that is replaced or removed by another edit.");
            }
        }

        _requests.Add(request);
    }

    private bool IsAncestor(ChunkNode ancestor, ChunkNode node)
    {
        Dictionary<ChunkNode, ChunkNode> parents = Parents();
        while (parents.TryGetValue(node, out ChunkNode? p))
        {
            if (p.Offset == ancestor.Offset)
            {
                return true;
            }

            node = p;
        }

        return false;
    }

    // A padding chunk whose size was inferred (a damaged file) may hide real chunks inside the bytes it claims; it is not padding to reuse.
    private bool IsPadding(ChunkNode n) => !n.IsSizeInferred && _file.Layout switch
    {
        ChunkLayout.Wave64 => n.ChunkGuid == ChunkBuilder.Wave64Junk,
        ChunkLayout.Iff => n.Id is "JUNK" or "FLLR" or "PAD ",
        _ => n.Id is "JUNK" or "PAD ",
    };

    // A last chunk may lack its pad byte at the end of the file; the slot then ends where the file does.
    private long Slot(ChunkNode n) => NextOf(n) - n.Offset;

    private long NextOf(ChunkNode n) => Math.Min(n.NextOffset, _source.Length);

    // A chunk that had to grow leaves a padding chunk behind it, so that a later edit can be saved in place. Returns the slot added.
    private long AddGrowthPadding(PieceList into)
    {
        long align = _file.Layout == ChunkLayout.Wave64 ? 8 : 2;
        long slot = GrowthPadding / align * align;
        if (slot < ChunkBuilder.HeaderSize(_file.Layout))
        {
            return 0;
        }

        PaddingPieces.AddChunkPadding(into, _file.Layout, slot);
        return slot;
    }

    private bool Fits(long remainder) => remainder == 0 || remainder >= ChunkBuilder.HeaderSize(_file.Layout);

    private ChunkNode? NextSibling(ChunkNode n)
    {
        IReadOnlyList<ChunkNode> sibs = (Parents().TryGetValue(n, out ChunkNode? p) ? p : _file.Root).Chunks;
        for (int i = 0; i < sibs.Count - 1; i++)
        {
            if (sibs[i].Offset == n.Offset)
            {
                return sibs[i + 1];
            }
        }

        return null;
    }

    /// <summary>Plans the new file. May be called again after more edits; each call plans all edits made so far.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="UnsupportedMediaException">A size would exceed what the layout can state, or the file cannot be promoted to RF64 safely.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public EditPlan Build(CancellationToken cancellationToken = default)
    {
        ChunkNode root = _file.Root;
        _padAdded.Clear();
        Rewriter rw = new(_file, _source, Parents(), RewriteHeaders, cancellationToken);
        ShiftMap shift = new();
        HashSet<ChunkNode> consumed = new(NodeComparer<ChunkNode>.Instance);
        long used = 0;
        long available = 0;
        long required = 0;
        foreach (Request r in _requests)
        {
            switch (r.Kind)
            {
                case RequestKind.Replace:
                    PlanReplace(rw, shift, consumed, r, ref used, ref available, ref required);
                    break;
                case RequestKind.Remove:
                    PlanRemove(rw, shift, r);
                    break;
                default:
                    PlanAppend(rw, shift, consumed, r, ref used, ref available, ref required);
                    break;
            }
        }

        long rootPayloadEnd = root.Offset + root.HeaderSize + root.PayloadSize;
        // An RF64 file states 0xFFFFFFFF in its RIFF header, which reads as an open-ended size; its real size in ds64 still has to follow the edit.
        if ((!root.IsSizeInferred || root.StatedEndPastFile || _file.Layout == ChunkLayout.Rf64) && !shift.IsEmpty)
        {
            AdjustLargeSizes(rw, shift, consumed, rootPayloadEnd, cancellationToken);
        }

        if (RewriteHeaders)
        {
            rw.MarkAllContainers();
        }

        PieceList list = new();
        list.AddCopy(0, root.Offset);
        if (rw.IsRootTouched)
        {
            list.Add(rw.RewriteRoot());
        }
        else
        {
            list.AddCopy(root.Offset, NextOf(root) - root.Offset);
        }

        list.AddCopy(NextOf(root), _source.Length - NextOf(root));
        return list.ToPlan(_source.Length, used, available, required);
    }

    private void PlanReplace(Rewriter rw, ShiftMap shift, HashSet<ChunkNode> consumed, Request r, ref long used, ref long available, ref long required)
    {
        ChunkNode node = r.Node;
        ReadOnlyMemory<byte> bytes = r.Chunk;
        long n = Slot(node);
        long m = bytes.Length;
        string name = $"replace '{node.Id}'";
        PieceList repl = new();
        ChunkNode last = node;
        if (m == n)
        {
            repl.AddData(bytes);
        }
        else if (m < n && Fits(n - m))
        {
            repl.AddData(bytes);
            PaddingPieces.AddChunkPadding(repl, _file.Layout, n - m);
            name += " (remainder kept as padding chunk)";
        }
        else
        {
            ChunkNode? next = NextSibling(node);
            if (m > n && next is not null && IsPadding(next) && !consumed.Contains(next) && Slot(next) is long f && f >= m - n && Fits(f - (m - n)))
            {
                consumed.Add(next);
                last = next;
                repl.AddData(bytes);
                if (f - (m - n) > 0)
                {
                    PaddingPieces.AddChunkPadding(repl, _file.Layout, f - (m - n));
                }

                used += m - n;
                available += f;
                name += $" (uses {m - n} byte(s) of the following padding chunk)";
            }
            else
            {
                repl.AddData(bytes);
                long free = next is not null && IsPadding(next) && !consumed.Contains(next) ? Slot(next) : 0;
                available += free;
                // A shrink that leaves a gap too small for a chunk header needs the rest of a header, or the exact old size.
                required += m > n ? Math.Max(1, m - n - free) : ChunkBuilder.HeaderSize(_file.Layout) - (n - m);
                long pad = m > n ? AddGrowthPadding(repl) : 0;
                shift.Add(NextOf(node), m - n + pad);
            }
        }

        rw.AddSlot(node, last, repl, name);
    }

    private void PlanRemove(Rewriter rw, ShiftMap shift, Request r)
    {
        ChunkNode node = r.Node;
        long n = Slot(node);
        PieceList repl = new();
        if (r.Reclaim)
        {
            shift.Add(NextOf(node), -n);
        }
        else
        {
            PaddingPieces.AddChunkPadding(repl, _file.Layout, n);
        }

        rw.AddSlot(node, node, repl, r.Reclaim ? $"remove '{node.Id}'" : $"clear '{node.Id}' to a padding chunk");
    }

    private void PlanAppend(Rewriter rw, ShiftMap shift, HashSet<ChunkNode> consumed, Request r, ref long used, ref long available, ref long required)
    {
        ChunkNode parent = r.Node;
        ReadOnlyMemory<byte> bytes = r.Chunk;
        long m = bytes.Length;
        IReadOnlyList<ChunkNode> kids = parent.Chunks;
        if (kids.Count > 0 && kids[^1].IsSizeInferred)
        {
            // A chunk that runs to the end of the file (an unbounded or cut-off size) would swallow whatever is added after it.
            throw new UnsupportedMediaException($"The last chunk '{kids[^1].Id}' has no stated end, so nothing can be added after it.") { Offset = kids[^1].Offset };
        }

        ChunkNode? padding = null;
        for (int i = kids.Count - 1; i >= 0; i--)
        {
            if (IsPadding(kids[i]) && !consumed.Contains(kids[i]))
            {
                padding = kids[i];
                break;
            }
        }

        if (padding is not null && Slot(padding) is long f && f >= m && Fits(f - m))
        {
            consumed.Add(padding);
            PieceList repl = new();
            repl.AddData(bytes);
            if (f - m > 0)
            {
                PaddingPieces.AddChunkPadding(repl, _file.Layout, f - m);
            }

            used += m;
            available += f;
            rw.AddSlot(padding, padding, repl, "insert in a padding chunk");
            return;
        }

        long free = padding is null ? 0 : Slot(padding);
        available += free;
        required += Math.Max(1, m - free);
        PieceList added = new();
        if (kids.Count > 0)
        {
            // A last chunk of odd length may lack its pad byte at the end of the file; the new chunk must start on an even offset.
            ChunkNode lastKid = kids[^1];
            long payloadEnd = lastKid.Offset + lastKid.HeaderSize + lastKid.PayloadSize;
            long missing = lastKid.NextOffset - Math.Max(payloadEnd, Math.Min(lastKid.NextOffset, _source.Length));
            // Only the first chunk added after it carries the pad byte; the ones after that start on an even offset already.
            if (missing > 0 && _padAdded.Add(lastKid.Offset))
            {
                added.AddData(new byte[missing]);
            }
        }

        added.AddData(bytes);
        long appendedPad = AddGrowthPadding(added);
        shift.Add(kids.Count > 0 ? NextOf(kids[^1]) : parent.Offset + parent.HeaderSize, m + appendedPad);
        rw.AddAppendChunk(parent, added);
    }

    // EBU Tech 3306 §3: an RF64 file keeps the real RIFF size in ds64 (the 32-bit field is 0xFFFFFFFF); a RIFF file that outgrows 32 bits is promoted.
    private void AdjustLargeSizes(Rewriter rw, ShiftMap shift, HashSet<ChunkNode> consumed, long rootPayloadEnd, CancellationToken ct)
    {
        ChunkNode root = _file.Root;
        long delta = shift.Total;
        if (_file.Layout == ChunkLayout.Rf64)
        {
            ChunkNode? ds64 = root.Find("ds64");
            if (ds64 is null || rw.HasEdit(ds64) || consumed.Contains(ds64) || ds64.PayloadSize < 28)
            {
                return;
            }

            _limits.CheckAllocation(Slot(ds64), ds64.Offset);
            byte[] bytes = new byte[Slot(ds64)];
            _source.ReadExactly(ds64.Offset, bytes, ct);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)(rootPayloadEnd + delta - 8));
            PieceList l = new();
            l.AddData(bytes);
            rw.AddSlot(ds64, ds64, l, "update riffSize in 'ds64'");
            return;
        }

        if (_file.Layout != ChunkLayout.Riff || root.FormType != "WAVE" || rootPayloadEnd + delta - 8 < 0xFFFF_FFFFL)
        {
            return;
        }

        IReadOnlyList<ChunkNode> kids = root.Chunks;
        if (kids.Count == 0 || rw.HasEdit(kids[0]) || consumed.Contains(kids[0]))
        {
            throw new UnsupportedMediaException("The edit makes the file too large for RIFF and the first chunk is also being edited, so the file cannot be promoted to RF64.");
        }

        ChunkNode first = kids[0];
        bool absorb = IsPadding(first) && Slot(first) >= 36 && Fits(Slot(first) - 36);
        long riffSize = rootPayloadEnd + delta + (absorb ? 0 : 36) - 8;
        ChunkNode? data = root.Find("data");
        ChunkNode? fmt = root.Find("fmt ");
        long dataSize = data?.PayloadSize ?? 0;
        long samples = 0;
        if (fmt is not null && fmt.PayloadSize >= 14)
        {
            Span<byte> fmtHead = new byte[14];
            _source.ReadExactly(fmt.Offset + fmt.HeaderSize, fmtHead, ct);
            int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmtHead[12..]);
            samples = blockAlign > 0 ? dataSize / blockAlign : 0;
        }

        byte[] newDs64 = new byte[36];
        Encoding.ASCII.GetBytes("ds64", newDs64);
        BinaryPrimitives.WriteUInt32LittleEndian(newDs64.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt64LittleEndian(newDs64.AsSpan(8), (ulong)riffSize);
        BinaryPrimitives.WriteUInt64LittleEndian(newDs64.AsSpan(16), (ulong)dataSize);
        BinaryPrimitives.WriteUInt64LittleEndian(newDs64.AsSpan(24), (ulong)samples);
        PieceList repl = new();
        repl.AddData(newDs64);
        if (absorb)
        {
            if (Slot(first) > 36)
            {
                repl.AddData(ChunkBuilder.CreatePadding(ChunkLayout.Riff, Slot(first) - 36));
            }
        }
        else
        {
            repl.AddCopy(first.Offset, Slot(first));
        }

        rw.AddSlot(first, first, repl, "promote to RF64: add 'ds64'");
        rw.Promote = true;
    }

    private enum RequestKind
    {
        Replace,
        Remove,
        Append,
    }

    private sealed record Request(RequestKind Kind, ChunkNode Node, ReadOnlyMemory<byte> Chunk, bool Reclaim);

    private sealed class Rewriter : TreeRewriter<ChunkNode>
    {
        private readonly ChunkFile _file;
        private readonly Dictionary<ChunkNode, ChunkNode> _parents;
        private readonly bool _regenerate;

        public Rewriter(ChunkFile file, IMediaSource source, Dictionary<ChunkNode, ChunkNode> parents, bool regenerate, CancellationToken ct)
            : base(source, ct)
        {
            _file = file;
            _parents = parents;
            _regenerate = regenerate;
        }

        public bool Promote { get; set; }

        public bool IsRootTouched => IsTouched(_file.Root) || _regenerate;

        public void AddSlot(ChunkNode first, ChunkNode last, PieceList replacement, string description) =>
            AddEdit(new SlotEdit<ChunkNode>(first, last, replacement, description));

        public void AddAppendChunk(ChunkNode parent, PieceList content) => AddAppend(parent, content);

        public new bool HasEdit(ChunkNode n) => base.HasEdit(n);

        public PieceList RewriteRoot()
        {
            ForceTouched(_file.Root);
            return RewriteNode(_file.Root);
        }

        public void MarkAllContainers()
        {
            Stack<ChunkNode> stack = new();
            stack.Push(_file.Root);
            while (stack.Count > 0)
            {
                ChunkNode n = stack.Pop();
                ForceTouched(n);
                foreach (ChunkNode c in n.Chunks)
                {
                    if (c.IsContainer)
                    {
                        stack.Push(c);
                    }
                }
            }
        }

        private int Prefix => _file.Layout == ChunkLayout.Wave64 ? 16 : 4;

        protected override ChunkNode? ParentOf(ChunkNode node) => _parents.GetValueOrDefault(node);

        protected override IReadOnlyList<ChunkNode> ChildrenOf(ChunkNode? node) => node is null ? [] : node.Chunks;

        protected override long ChildStart(ChunkNode? node) => node is null ? 0 : node.Offset + node.HeaderSize + Prefix;

        protected override long ChildEnd(ChunkNode? node) => node is null ? Source.Length : node.Offset + node.HeaderSize + node.PayloadSize;

        protected override long SlotEnd(ChunkNode node) => Math.Min(node.NextOffset, Source.Length);

        protected override void EmitHeader(ChunkNode node, long areaLength, PieceList into)
        {
            long region = node.HeaderSize + Prefix;
            long newPayload = Prefix + areaLength;
            bool root = node.Offset == _file.Root.Offset;
            // A size that overshoots the end of the file (some writers state the file length instead of the length minus the header) is
            // replaced by the real one once content moves; an unbounded size (all ones, or zero) keeps meaning 'to the end'.
            if ((node.IsSizeInferred && !(node.StatedEndPastFile && newPayload != node.PayloadSize)) || (!_regenerate && !(root && Promote) && newPayload == node.PayloadSize))
            {
                into.AddCopy(node.Offset, region);
                return;
            }

            byte[] head = new byte[region];
            Source.ReadExactly(node.Offset, head, CancellationToken);
            if (_file.Layout == ChunkLayout.Wave64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(16), (ulong)(24 + newPayload));
            }
            else if (root && Promote)
            {
                Encoding.ASCII.GetBytes("RF64", head);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 0xFFFF_FFFF);
            }
            else if (_file.Layout == ChunkLayout.Rf64 && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) == 0xFFFF_FFFF && (root || (newPayload == node.PayloadSize && node.PayloadSize <= Source.Length - node.Offset)))
            {
                // The real size is in ds64; the 32-bit field stays 0xFFFFFFFF (for a nested chunk only while its size is unchanged and inside the file).
            }
            else
            {
                if (newPayload >= 0xFFFF_FFFFL)
                {
                    throw new UnsupportedMediaException($"The chunk '{node.Id}' would be {newPayload} bytes, more than a 32-bit chunk size can state.") { Offset = node.Offset };
                }

                if (_file.Layout == ChunkLayout.Iff)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)newPayload);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)newPayload);
                }
            }

            into.AddData(head);
        }

        protected override void EmitTrailer(ChunkNode node, long areaLength, PieceList into)
        {
            if (node.IsSizeInferred || node.NextOffset > Source.Length)
            {
                return; // an unbounded chunk has no pad, and a file may end without its last pad byte
            }

            long payload = Prefix + areaLength;
            long pad = _file.Layout == ChunkLayout.Wave64 ? (-(24 + payload)) & 7 : payload & 1;
            long payloadEnd = node.Offset + node.HeaderSize + node.PayloadSize;
            long originalPad = Math.Min(node.NextOffset, Source.Length) - payloadEnd;
            if (pad > 0 && pad == originalPad)
            {
                into.AddCopy(payloadEnd, pad); // keep whatever pad bytes the writer of the file used
            }
            else if (pad > 0)
            {
                into.AddData(new byte[pad]);
            }
        }
    }
}

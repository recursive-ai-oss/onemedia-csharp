using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Edits the box tree of an ISO BMFF / QuickTime file and plans the new file. Every edit is a replacement, a removal or an insertion of
/// whole boxes; untouched boxes (above all <c>mdat</c>) are copied from the original by range.
/// </summary>
/// <remarks>
/// <para>Where the edit fits the padding beside it (an exactly equal box, a smaller box followed by a <c>free</c> box that makes up the
/// difference, or a larger box that uses up a following <c>free</c>/<c>skip</c> box) the plan changes nothing else, so
/// <see cref="EditPlan.IsSameLayout"/> is true and <see cref="SafeWriter.SaveInPlace"/> can apply it. Where it does not fit, the plan moves
/// the boxes after it, re-emits the enclosing boxes with new sizes, and moves every stored offset that points past the change:
/// <c>stco</c>/<c>co64</c> chunk offsets (promoting <c>stco</c> to <c>co64</c> when a moved offset no longer fits 32 bits), absolute
/// <c>base_data_offset</c> values in <c>tfhd</c>, <c>moof_offset</c> values in <c>tfra</c>, and <c>first_offset</c> in <c>sidx</c>.</para>
/// <para>Not adjusted: item locations (<c>iloc</c>) of HEIF-style files, and any proprietary box that stores absolute file offsets. Such
/// boxes are copied as they are.</para>
/// </remarks>
public sealed class BoxEditor
{
    private readonly BoxTree _tree;
    private readonly IMediaSource _source;
    private readonly MediaLimits _limits;
    private readonly List<Request> _requests = [];

    /// <summary>Creates an editor over a tree.</summary>
    /// <param name="tree">The tree of the original file.</param>
    /// <param name="source">The source the tree was read from; it must stay open while plans are built and written.</param>
    /// <param name="limits">Allocation ceilings for the small boxes the editor reads (sidx, tfra, tfhd); <see cref="MediaLimits.Default"/> when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public BoxEditor(BoxTree tree, IMediaSource source, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(source);
        _tree = tree;
        _source = source;
        _limits = limits ?? MediaLimits.Default;
    }

    /// <summary>
    /// Diagnostic: re-emit the header of every container box from its children instead of copying it. Untouched files must still come out
    /// byte-identical, which shows the writer mirrors the reader. Off by default.
    /// </summary>
    public bool RewriteHeaders { get; set; }

    /// <summary>
    /// Bytes of <c>free</c> box to leave after a box that has to grow, or after a box added where there was no padding to take it, so that a
    /// later edit fits in place. Default 0 (no padding). Values below 8 (the smallest box) are treated as 0. An edit that fits the existing
    /// layout, and a file that is not edited, never gain padding.
    /// </summary>
    public long GrowthPadding { get; set; }

    /// <summary>Replaces a box with a new box.</summary>
    /// <param name="node">The box to replace.</param>
    /// <param name="box">The complete new box (header included); its size field must match its length.</param>
    /// <exception cref="ArgumentException"><paramref name="box"/> is not exactly one well-formed box, or <paramref name="node"/> overlaps another edit.</exception>
    public void Replace(BoxNode node, ReadOnlyMemory<byte> box)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!BoxBuilder.IsSingleBox(box.Span, out _))
        {
            throw new ArgumentException("The new content must be exactly one box whose size field matches its length.", nameof(box));
        }

        Add(new Request(RequestKind.Replace, node, box, false));
    }

    /// <summary>Removes a box.</summary>
    /// <param name="node">The box to remove.</param>
    /// <param name="reclaimSpace">
    /// False (default): the box becomes a <c>free</c> box of the same size, so nothing moves and the edit can be saved in place. True: the
    /// bytes are cut out and everything after moves up.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="node"/> overlaps another edit.</exception>
    public void Remove(BoxNode node, bool reclaimSpace = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        Add(new Request(RequestKind.Remove, node, default, reclaimSpace));
    }

    /// <summary>Adds a box at the end of a container's children, or at the end of the file.</summary>
    /// <param name="parent">The container (for example <c>moov</c> or <c>udta</c>), or null for the top level.</param>
    /// <param name="box">The complete new box.</param>
    /// <exception cref="ArgumentException"><paramref name="box"/> is not one well-formed box, or <paramref name="parent"/> cannot hold boxes.</exception>
    public void AppendChild(BoxNode? parent, ReadOnlyMemory<byte> box)
    {
        if (!BoxBuilder.IsSingleBox(box.Span, out _))
        {
            throw new ArgumentException("The new content must be exactly one box whose size field matches its length.", nameof(box));
        }

        if (parent is not null && !BoxReader.IsContainer(parent.Type, parent.Parent?.Type ?? 0))
        {
            throw new ArgumentException($"A '{parent.TypeName}' box does not hold child boxes.", nameof(parent));
        }

        Add(new Request(RequestKind.Append, parent, box, false));
    }

    private void Add(Request request)
    {
        if (request.Node is { } node)
        {
            foreach (Request other in _requests)
            {
                if (other.Node is { } o && request.Kind != RequestKind.Append && other.Kind != RequestKind.Append
                    && (o.Offset == node.Offset || IsAncestor(o, node) || IsAncestor(node, o)))
                {
                    throw new ArgumentException("Two edits overlap: a box and one of its descendants (or the same box) cannot both be edited.");
                }
            }
        }

        // A box appended inside something that is replaced or removed would vanish with it; refuse instead of losing it silently.
        foreach (Request other in _requests)
        {
            (BoxNode? replaced, BoxNode? appendedTo) = request.Kind == RequestKind.Append ? (other.Kind != RequestKind.Append ? other.Node : null, request.Node)
                : (request.Node, other.Kind == RequestKind.Append ? other.Node : null);
            if (replaced is not null && appendedTo is not null && (replaced.Offset == appendedTo.Offset || IsAncestor(replaced, appendedTo)))
            {
                throw new ArgumentException("A box cannot be added inside a box that is replaced or removed by another edit.");
            }
        }

        _requests.Add(request);
    }

    private static bool IsAncestor(BoxNode ancestor, BoxNode node)
    {
        for (BoxNode? p = node.Parent; p is not null; p = p.Parent)
        {
            if (p.Offset == ancestor.Offset)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Plans the new file. May be called again after more edits; each call plans all edits made so far.</summary>
    /// <param name="cancellationToken">Cancels the planning (which reads table headers and scans offset tables).</param>
    /// <returns>The plan.</returns>
    /// <exception cref="UnsupportedMediaException">A stored offset cannot be moved (a damaged offset table, or a 32-bit field that overflows).</exception>
    /// <exception cref="MediaLimitExceededException">A box that must be read is larger than the allocation limit.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public EditPlan Build(CancellationToken cancellationToken = default)
    {
        // The plan decides how far things move (a rewritten header can grow, a table can be promoted), and how far they move decides what the
        // stored offsets must say. Plan, read the movement off the plan, plan again with the offsets fixed, until the two agree.
        ShiftMap map = new();
        HashSet<long> promoted = [];
        for (int round = 0; ; round++)
        {
            EditPlan plan = BuildRound(map, promoted, cancellationToken);
            ShiftMap next = ShiftMap.FromChanges(plan.Changes);
            if (next.Signature() == map.Signature())
            {
                return plan;
            }

            if (round >= 8)
            {
                throw new UnsupportedMediaException("The stored offsets did not settle after the edit.");
            }

            map = next;
        }
    }

    private EditPlan BuildRound(ShiftMap map, HashSet<long> promoted, CancellationToken cancellationToken)
    {
        Rewriter rw = new(_tree, _source, _limits, RewriteHeaders, cancellationToken);
        HashSet<BoxNode> consumed = new(NodeComparer<BoxNode>.Instance);
        long used = 0;
        long available = 0;
        long required = 0;
        foreach (Request r in _requests)
        {
            switch (r.Kind)
            {
                case RequestKind.Replace:
                    PlanReplace(rw, consumed, r, ref used, ref available, ref required);
                    break;
                case RequestKind.Remove:
                    PlanRemove(rw, r);
                    break;
                default:
                    PlanAppend(rw, consumed, r, ref used, ref available, ref required);
                    break;
            }
        }

        if (!map.IsEmpty)
        {
            AddOffsetFixups(rw, map, promoted, cancellationToken);
        }

        if (RewriteHeaders)
        {
            rw.MarkAllContainers();
        }

        return rw.Rewrite().ToPlan(_tree.Length, used, available, required);
    }

    // A free box whose size was inferred or cut down to fit (a damaged file) may hide real boxes inside the bytes it claims; it is not padding to reuse.
    private static bool IsPadding(BoxNode n) => n.Type is BoxTypes.Free or BoxTypes.Skip && !n.IsSizeInferred && !n.Header.IsClamped;

    private static long TotalSize(BoxNode n) => n.Header.End - n.Offset;

    private IReadOnlyList<BoxNode> SiblingsOf(BoxNode n) => n.Parent is null ? _tree.TopLevel : n.Parent.Children;

    private BoxNode? NextSibling(BoxNode n)
    {
        IReadOnlyList<BoxNode> sibs = SiblingsOf(n);
        for (int i = 0; i < sibs.Count - 1; i++)
        {
            if (sibs[i].Offset == n.Offset)
            {
                return sibs[i + 1];
            }
        }

        return null;
    }

    // A box that had to grow leaves a little free space behind it, so that a later edit of the same box can be saved in place.
    private void AddGrowthPadding(PieceList into)
    {
        if (GrowthPadding >= BoxBuilder.MinimumSize)
        {
            PaddingPieces.AddFree(into, GrowthPadding);
        }
    }

    private void PlanReplace(Rewriter rw, HashSet<BoxNode> consumed, Request r, ref long used, ref long available, ref long required)
    {
        BoxNode node = r.Node!;
        ReadOnlyMemory<byte> box = r.Box;
        long n = TotalSize(node);
        long m = box.Length;
        string name = $"replace '{node.TypeName}'";
        PieceList repl = new();
        BoxNode last = node;
        if (m == n)
        {
            repl.AddData(box);
        }
        else if (m < n && n - m >= BoxBuilder.MinimumSize)
        {
            repl.AddData(box);
            PaddingPieces.AddFree(repl, n - m);
            name += " (remainder kept as free box)";
        }
        else
        {
            BoxNode? next = NextSibling(node);
            if (m > n && next is not null && IsPadding(next) && !consumed.Contains(next) && TotalSize(next) is long f && (m - n == f || f - (m - n) >= BoxBuilder.MinimumSize))
            {
                consumed.Add(next);
                last = next;
                repl.AddData(box);
                if (f - (m - n) > 0)
                {
                    PaddingPieces.AddFree(repl, f - (m - n));
                }

                used += m - n;
                available += f;
                name += $" (uses {m - n} byte(s) of the following '{next.TypeName}' box)";
            }
            else
            {
                // Does not fit: the box takes the size it needs and everything after it moves.
                repl.AddData(box);
                if (m > n)
                {
                    AddGrowthPadding(repl);
                }

                long free = next is not null && IsPadding(next) && !consumed.Contains(next) ? TotalSize(next) : 0;
                available += free;
                // A shrink that leaves a gap too small for a box header needs the rest of a header, or the exact old size.
                required += m > n ? Math.Max(1, m - n - free) : BoxBuilder.MinimumSize - (n - m);
            }
        }

        rw.AddSlot(node, last, repl, name);
    }

    private static void PlanRemove(Rewriter rw, Request r)
    {
        BoxNode node = r.Node!;
        long n = TotalSize(node);
        PieceList repl = new();
        if (!r.Reclaim)
        {
            PaddingPieces.AddFree(repl, n);
        }

        rw.AddSlot(node, node, repl, r.Reclaim ? $"remove '{node.TypeName}'" : $"clear '{node.TypeName}' to a free box");
    }

    private void PlanAppend(Rewriter rw, HashSet<BoxNode> consumed, Request r, ref long used, ref long available, ref long required)
    {
        BoxNode? parent = r.Node;
        ReadOnlyMemory<byte> box = r.Box;
        long m = box.Length;
        IReadOnlyList<BoxNode> kids = parent is null ? _tree.TopLevel : parent.Children;
        if (kids.Count > 0 && (kids[^1].IsSizeInferred || kids[^1].Header.IsClamped))
        {
            // A box that runs to the end of the file (size 0, or a size cut back to fit) would swallow whatever is added after it.
            throw new UnsupportedMediaException($"The last box '{kids[^1].TypeName}' has no stated end, so nothing can be added after it.") { Offset = kids[^1].Offset };
        }

        BoxNode? padding = null;
        for (int i = kids.Count - 1; i >= 0; i--)
        {
            if (IsPadding(kids[i]) && !consumed.Contains(kids[i]))
            {
                padding = kids[i];
                break;
            }
        }

        if (padding is not null && TotalSize(padding) is long f && (m == f || f - m >= BoxBuilder.MinimumSize))
        {
            consumed.Add(padding);
            PieceList repl = new();
            repl.AddData(box);
            if (f - m > 0)
            {
                PaddingPieces.AddFree(repl, f - m);
            }

            used += m;
            available += f;
            rw.AddSlot(padding, padding, repl, $"insert in the '{padding.TypeName}' box");
            return;
        }

        long free = padding is null ? 0 : TotalSize(padding);
        available += free;
        required += Math.Max(1, m - free);
        PieceList added = new();
        added.AddData(box);
        AddGrowthPadding(added);
        rw.AddAppendBox(parent, added);
    }

    // ---- stored offsets ----

    private void AddOffsetFixups(Rewriter rw, ShiftMap shift, HashSet<long> promoted, CancellationToken ct)
    {
        List<OffsetTable> tables = [];
        bool hasFragments = false;
        foreach (BoxNode top in _tree.TopLevel)
        {
            if (top.Type == BoxTypes.Moov)
            {
                CollectTables(top, tables);
            }
            else if (top.Type is BoxTypes.Moof or BoxTypes.Sidx or BoxTypes.Mfra)
            {
                hasFragments = true;
            }
        }

        foreach (OffsetTable t in tables)
        {
            ct.ThrowIfCancellationRequested();
            // Promoting a stco to co64 makes the movie box larger, which moves the media further; the next round sees that. A table once
            // promoted stays promoted, so the rounds cannot flip back and forth.
            (long max, bool any) = Scan(t, shift, ct);
            if (!t.Is64 && max > uint.MaxValue)
            {
                promoted.Add(t.Box.Offset);
            }

            bool promote = promoted.Contains(t.Box.Offset);
            if (!any && !promote)
            {
                continue;
            }

            bool to64 = t.Is64 || promote;
            long payload = 8 + (t.Count * (to64 ? 8 : 4));
            long total = payload + 8;
            byte[] head = new byte[16];
            int h = BoxBuilder.WriteHeader(head, to64 ? BoxTypes.Co64 : BoxTypes.Stco, total);
            if (h != 8)
            {
                throw new UnsupportedMediaException("A chunk offset table is too large for a 32-bit box size.") { Offset = t.Box.Offset };
            }

            _source.ReadExactly(t.Box.Header.PayloadOffset, head.AsSpan(8, 4), ct); // version and flags
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), (uint)t.Count);
            PieceList repl = new();
            repl.AddGenerated(total, new ChunkOffsetGenerator(t, to64, shift.Clone(), head));
            rw.AddSlot(t.Box, t.Box, repl, promote && !t.Is64 ? $"move chunk offsets, '{t.Box.TypeName}' becomes 'co64'" : $"move chunk offsets in '{t.Box.TypeName}'");
        }

        if (hasFragments)
        {
            AddFragmentFixups(rw, shift, ct);
        }
    }

    // BoxNode.Find builds new node objects on each call; the editor must hold the tree's own cached nodes.
    private static BoxNode? ChildOf(BoxNode? parent, uint type)
    {
        if (parent is null)
        {
            return null;
        }

        foreach (BoxNode c in parent.Children)
        {
            if (c.Type == type)
            {
                return c;
            }
        }

        return null;
    }

    private void CollectTables(BoxNode moov, List<OffsetTable> into)
    {
        byte[] headBuffer = new byte[8];
        foreach (BoxNode trak in moov.Children)
        {
            if (trak.Type != BoxTypes.Trak)
            {
                continue;
            }

            BoxNode? stbl = ChildOf(ChildOf(ChildOf(trak, BoxTypes.Mdia), BoxTypes.Minf), BoxTypes.Stbl);
            if (stbl is null)
            {
                continue;
            }

            foreach (BoxNode child in stbl.Children)
            {
                if (child.Type is BoxTypes.Stco or BoxTypes.Co64)
                {
                    bool is64 = child.Type == BoxTypes.Co64;
                    Span<byte> head = headBuffer; // version/flags and entry_count of the table
                    if (child.PayloadSize < 8 || _source.Read(child.Header.PayloadOffset, head) < 8)
                    {
                        throw new UnsupportedMediaException($"The '{child.TypeName}' box is too short to hold its entry count, so the media cannot be moved safely.") { Offset = child.Offset };
                    }

                    long count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
                    long width = is64 ? 8 : 4;
                    if (child.PayloadSize - 8 < count * width)
                    {
                        throw new UnsupportedMediaException($"The '{child.TypeName}' box declares {count} entries but holds fewer, so the media cannot be moved safely.") { Offset = child.Offset };
                    }

                    into.Add(new OffsetTable(child, is64, count));
                }
            }
        }
    }

    private (long Max, bool Any) Scan(OffsetTable t, ShiftMap shift, CancellationToken ct)
    {
        long max = 0;
        bool any = false;
        byte[] buffer = new byte[64 * 1024];
        int w = t.Is64 ? 8 : 4;
        long remaining = t.Count;
        long at = t.Box.Header.PayloadOffset + 8;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int entries = (int)Math.Min(remaining, buffer.Length / w);
            _source.ReadExactly(at, buffer.AsSpan(0, entries * w), ct);
            for (int i = 0; i < entries; i++)
            {
                long v = t.Is64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(i * 8)) : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 4));
                long mapped = shift.Map(v);
                if (mapped != v)
                {
                    any = true;
                }

                max = Math.Max(max, mapped);
            }

            at += entries * w;
            remaining -= entries;
        }

        return (max, any);
    }

    private void AddFragmentFixups(Rewriter rw, ShiftMap shift, CancellationToken ct)
    {
        foreach (BoxNode top in _tree.TopLevel)
        {
            ct.ThrowIfCancellationRequested();
            switch (top.Type)
            {
                case BoxTypes.Moof:
                    foreach (BoxNode traf in top.Children)
                    {
                        if (traf.Type != BoxTypes.Traf)
                        {
                            continue;
                        }

                        BoxNode? tfhd = ChildOf(traf, BoxTypes.Tfhd);
                        if (tfhd is not null)
                        {
                            PatchTfhd(rw, shift, traf, tfhd);
                        }
                    }

                    break;
                case BoxTypes.Sidx:
                    PatchSidx(rw, shift, top);
                    break;
                case BoxTypes.Mfra:
                    foreach (BoxNode tfra in top.Children)
                    {
                        if (tfra.Type == BoxTypes.Tfra)
                        {
                            PatchTfra(rw, shift, tfra);
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private byte[] ReadBox(BoxNode box)
    {
        _limits.CheckAllocation(box.Header.End - box.Offset, box.Offset);
        byte[] bytes = new byte[box.Header.End - box.Offset];
        _source.ReadExactly(box.Offset, bytes);
        return bytes;
    }

    // ISO/IEC 14496-12 §8.8.7: with flag 0x000001 the track fragment header carries an absolute base_data_offset after track_ID. The data
    // a fragment points at is at base_data_offset plus the first run's data_offset (§8.8.8), and files exist (observed) whose base is 0 and
    // whose runs hold absolute offsets; so the base moves by however far that data moved.
    private void PatchTfhd(Rewriter rw, ShiftMap shift, BoxNode traf, BoxNode tfhd)
    {
        if (tfhd.PayloadSize < 16)
        {
            return;
        }

        byte[] box = ReadBox(tfhd);
        int p = tfhd.HeaderSize;
        if ((box[p + 3] & 1) == 0)
        {
            return;
        }

        long old = (long)BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(p + 8));
        long dataPosition = old;
        BoxNode? trun = ChildOf(traf, BoxTypes.Trun);
        if (trun is not null && trun.PayloadSize >= 12)
        {
            byte[] run = ReadBox(trun);
            if ((run[trun.HeaderSize + 3] & 1) != 0)
            {
                dataPosition += BinaryPrimitives.ReadInt32BigEndian(run.AsSpan(trun.HeaderSize + 8));
            }
        }

        long mapped = old + shift.Shift(dataPosition);
        if (mapped == old)
        {
            return;
        }

        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(p + 8), (ulong)mapped);
        PieceList repl = new();
        repl.AddData(box);
        rw.AddSlot(tfhd, tfhd, repl, "move base_data_offset in 'tfhd'");
    }

    // §8.16.3: first_offset is the distance from the end of the sidx box to the first referenced box.
    private void PatchSidx(Rewriter rw, ShiftMap shift, BoxNode sidx)
    {
        if (sidx.PayloadSize < 24)
        {
            return;
        }

        byte[] box = ReadBox(sidx);
        int p = sidx.HeaderSize;
        byte version = box[p];
        int at = p + (version == 0 ? 16 : 20);
        if (at + (version == 0 ? 4 : 8) > box.Length)
        {
            return;
        }

        long first = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(at)) : (long)BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(at));
        long anchor = sidx.Header.End + first;
        long newFirst = first + shift.Shift(anchor) - shift.Shift(sidx.Offset);
        if (newFirst == first)
        {
            return;
        }

        if (version == 0)
        {
            if (newFirst is < 0 or > uint.MaxValue)
            {
                throw new UnsupportedMediaException("A 'sidx' first_offset no longer fits 32 bits after the edit.") { Offset = sidx.Offset };
            }

            BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(at), (uint)newFirst);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(at), (ulong)newFirst);
        }

        PieceList repl = new();
        repl.AddData(box);
        rw.AddSlot(sidx, sidx, repl, "move first_offset in 'sidx'");
    }

    // §8.8.10: tfra entries hold the absolute file offset of each movie fragment (moof_offset).
    private void PatchTfra(Rewriter rw, ShiftMap shift, BoxNode tfra)
    {
        if (tfra.PayloadSize < 16)
        {
            return;
        }

        byte[] box = ReadBox(tfra);
        int p = tfra.HeaderSize;
        byte version = box[p];
        uint lengths = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 8));
        int trafBytes = (int)((lengths >> 4) & 3) + 1;
        int trunBytes = (int)((lengths >> 2) & 3) + 1;
        int sampleBytes = (int)(lengths & 3) + 1;
        long entries = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 12));
        int field = version == 1 ? 8 : 4;
        int entrySize = (2 * field) + trafBytes + trunBytes + sampleBytes;
        if (box.Length - (p + 16) < entries * entrySize)
        {
            return;
        }

        bool changed = false;
        for (long i = 0; i < entries; i++)
        {
            int e = p + 16 + (int)(i * entrySize) + field; // moof_offset follows the time field
            long old = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(e)) : BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(e));
            long mapped = shift.Map(old);
            if (mapped == old)
            {
                continue;
            }

            changed = true;
            if (version == 1)
            {
                BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(e), (ulong)mapped);
            }
            else if (mapped is < 0 or > uint.MaxValue)
            {
                throw new UnsupportedMediaException("A 'tfra' moof_offset no longer fits 32 bits after the edit.") { Offset = tfra.Offset };
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(e), (uint)mapped);
            }
        }

        if (changed)
        {
            PieceList repl = new();
            repl.AddData(box);
            rw.AddSlot(tfra, tfra, repl, "move moof_offset values in 'tfra'");
        }
    }

    private enum RequestKind
    {
        Replace,
        Remove,
        Append,
    }

    private sealed record Request(RequestKind Kind, BoxNode? Node, ReadOnlyMemory<byte> Box, bool Reclaim);

    private sealed class OffsetTable(BoxNode box, bool is64, long count)
    {
        public BoxNode Box { get; } = box;

        public bool Is64 { get; } = is64;

        public long Count { get; } = count;

        public bool Promote { get; set; }
    }

    /// <summary>Writes a chunk offset box with every offset mapped, streaming the entries.</summary>
    private sealed class ChunkOffsetGenerator(OffsetTable table, bool to64, ShiftMap shift, byte[] header) : IPieceGenerator
    {
        public void Write(Stream output, IMediaSource source, CancellationToken cancellationToken)
        {
            output.Write(header, 0, 16);
            int inW = table.Is64 ? 8 : 4;
            int outW = to64 ? 8 : 4;
            int perBlock = 8192;
            byte[] input = new byte[perBlock * inW];
            byte[] result = new byte[perBlock * outW];
            long remaining = table.Count;
            long at = table.Box.Header.PayloadOffset + 8;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(remaining, perBlock);
                source.ReadExactly(at, input.AsSpan(0, n * inW), cancellationToken);
                for (int i = 0; i < n; i++)
                {
                    long v = table.Is64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(input.AsSpan(i * 8)) : BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(i * 4));
                    long mapped = shift.Map(v);
                    if (to64)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(i * 8), (ulong)mapped);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), checked((uint)mapped));
                    }
                }

                output.Write(result, 0, n * outW);
                at += n * inW;
                remaining -= n;
            }
        }
    }

    private sealed class Rewriter : TreeRewriter<BoxNode>
    {
        private readonly BoxTree _tree;
        private readonly bool _regenerate;

        public Rewriter(BoxTree tree, IMediaSource source, MediaLimits limits, bool regenerate, CancellationToken ct)
            : base(source, ct)
        {
            _tree = tree;
            _regenerate = regenerate;
            _ = limits;
        }

        public void AddSlot(BoxNode first, BoxNode last, PieceList replacement, string description) =>
            AddEdit(new SlotEdit<BoxNode>(first, last, replacement, description));

        public void AddAppendBox(BoxNode? parent, PieceList content) => AddAppend(parent, content);

        public PieceList Rewrite() => RewriteArea(null);

        public void MarkAllContainers()
        {
            Stack<BoxNode> stack = new(_tree.TopLevel);
            while (stack.Count > 0)
            {
                BoxNode n = stack.Pop();
                if (BoxReader.IsContainer(n.Type, n.Parent?.Type ?? 0) && n.Children.Count > 0)
                {
                    ForceTouched(n);
                    foreach (BoxNode c in n.Children)
                    {
                        stack.Push(c);
                    }
                }
            }
        }

        protected override BoxNode? ParentOf(BoxNode node) => node.Parent;

        protected override IReadOnlyList<BoxNode> ChildrenOf(BoxNode? node) => node is null ? _tree.TopLevel : node.Children;

        protected override long ChildStart(BoxNode? node) => node is null ? 0 : node.ChildrenOffset;

        protected override long ChildEnd(BoxNode? node) => node is null ? _tree.Length : node.Header.End;

        protected override long SlotEnd(BoxNode node) => node.Header.End;

        protected override void EmitHeader(BoxNode node, long areaLength, PieceList into)
        {
            long headerRegion = node.ChildrenOffset - node.Offset;
            long newTotal = headerRegion + areaLength;
            long oldTotal = node.Header.End - node.Offset;
            if (node.IsSizeInferred || (newTotal == oldTotal && (!_regenerate || node.Header.IsClamped)))
            {
                // A size of 0 ("to the end of the file") stays as it is, and so does a size that was cut back to fit (a damaged file);
                // an unchanged size needs no new header.
                into.AddCopy(node.Offset, headerRegion);
                return;
            }

            bool wasLarge = node.Header.HeaderSize == (node.Type == BoxTypes.Uuid ? 32 : 16);
            bool large = wasLarge || newTotal > uint.MaxValue;
            if (large && !wasLarge)
            {
                newTotal += 8; // the 64-bit size field makes the header 8 bytes longer
            }

            int prefix = (int)(headerRegion - node.HeaderSize);
            byte[] head = new byte[BoxBuilder.HeaderSize(node.Type, large) + prefix];
            int h = BoxBuilder.WriteHeader(head, node.Type, newTotal, node.Header.UserType, large);
            if (prefix > 0)
            {
                Source.ReadExactly(node.Header.PayloadOffset, head.AsSpan(h, prefix), CancellationToken);
            }

            into.AddData(head);
        }
    }
}

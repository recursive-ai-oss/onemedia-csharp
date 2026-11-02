using System.Linq;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Edits the element tree of a Matroska or WebM file and plans the new file. Every edit replaces, removes or adds whole elements;
/// untouched elements (above all Clusters) are copied from the original by range.
/// </summary>
/// <remarks>
/// <para>Where the edit fits the padding beside it (an element of exactly the old size, a smaller element followed by a Void element
/// that makes up the difference, or a larger element that uses up a following Void) nothing else changes, so
/// <see cref="EditPlan.IsSameLayout"/> is true and <see cref="SafeWriter.SaveInPlace"/> can apply it. Where it does not fit, the plan
/// moves the elements after it, re-emits the enclosing elements with new sizes (keeping the width of each size field when the new size
/// still fits it), and keeps every Segment-relative position correct: <c>SeekPosition</c> in SeekHead entries, <c>CueClusterPosition</c>,
/// <c>CueCodecState</c> and <c>CueRefCluster</c> in Cues, and the Cluster <c>Position</c> element. A master element that has a CRC-32
/// child gets it recomputed whenever its content changes.</para>
/// <para>An element of unknown size keeps its unknown size. Positions in vendor elements are not adjusted.</para>
/// </remarks>
public sealed class EbmlEditor
{
    private const int MaxCuePointBytes = 1 << 20;

    private readonly EbmlDocument _document;
    private readonly IMediaSource _source;
    private readonly MediaLimits _limits;
    private readonly List<Request> _requests = [];

    /// <summary>Creates an editor over a document.</summary>
    /// <param name="document">The tree of the original file.</param>
    /// <param name="source">The source the tree was read from; it must stay open while plans are built and written.</param>
    /// <param name="limits">Allocation ceilings for the small elements the editor reads (SeekHead, one CuePoint); <see cref="MediaLimits.Default"/> when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public EbmlEditor(EbmlDocument document, IMediaSource source, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        _document = document;
        _source = source;
        _limits = limits ?? MediaLimits.Default;
    }

    /// <summary>
    /// Diagnostic: re-emit the header of every master element (Clusters excluded, whose blocks would all have to be parsed) and recompute every CRC-32 instead of copying them.
    /// Untouched files with canonical sizes must still come out byte-identical. Off by default.
    /// </summary>
    public bool RewriteHeaders { get; set; }

    /// <summary>Replaces an element with a new element.</summary>
    /// <param name="element">The element to replace.</param>
    /// <param name="newElement">The complete new element (ID, size and payload).</param>
    /// <exception cref="ArgumentException"><paramref name="newElement"/> is not exactly one element, or <paramref name="element"/> overlaps another edit.</exception>
    public void Replace(EbmlElement element, ReadOnlyMemory<byte> newElement)
    {
        ArgumentNullException.ThrowIfNull(element);
        CheckElement(newElement);
        Add(new Request(RequestKind.Replace, element, newElement, false));
    }

    /// <summary>Removes an element.</summary>
    /// <param name="element">The element to remove.</param>
    /// <param name="reclaimSpace">False (default): a Void element of the same size takes its place and nothing moves. True: the bytes are cut out and everything after moves up.</param>
    /// <exception cref="ArgumentException"><paramref name="element"/> overlaps another edit.</exception>
    public void Remove(EbmlElement element, bool reclaimSpace = false)
    {
        ArgumentNullException.ThrowIfNull(element);
        Add(new Request(RequestKind.Remove, element, default, reclaimSpace));
    }

    /// <summary>Adds an element at the end of a master element's children, or at the end of the file.</summary>
    /// <param name="parent">The master element (for example the Segment), or null for the top level.</param>
    /// <param name="newElement">The complete new element.</param>
    /// <exception cref="ArgumentException"><paramref name="newElement"/> is not exactly one element, or <paramref name="parent"/> is not a master element.</exception>
    public void AppendChild(EbmlElement? parent, ReadOnlyMemory<byte> newElement)
    {
        CheckElement(newElement);
        if (parent is not null && !parent.IsMaster)
        {
            throw new ArgumentException("The parent is not a master element.", nameof(parent));
        }

        Add(new Request(RequestKind.Append, parent, newElement, false));
    }

    /// <summary>
    /// Replaces an element with a run of complete elements, for inserting new elements beside an existing one (the first of the run is
    /// normally the element itself, copied). Growth is absorbed by a Void element that follows, as for <see cref="Replace"/>.
    /// </summary>
    /// <param name="element">The element to replace.</param>
    /// <param name="elements">One or more complete elements, back to back.</param>
    /// <exception cref="ArgumentException"><paramref name="elements"/> is not a run of whole elements, or <paramref name="element"/> overlaps another edit.</exception>
    public void ReplaceWithRun(EbmlElement element, ReadOnlyMemory<byte> elements)
    {
        ArgumentNullException.ThrowIfNull(element);
        ReadOnlySpan<byte> rest = elements.Span;
        while (!rest.IsEmpty)
        {
            if (!Ebml.TryReadId(rest, out _, out int idLen) || !Ebml.TryReadSize(rest[idLen..], out long size, out int sizeLen) || size < 0 || idLen + sizeLen + size > rest.Length)
            {
                throw new ArgumentException("The new content must be whole elements whose size fields match their lengths.", nameof(elements));
            }

            rest = rest[(idLen + sizeLen + (int)size)..];
        }

        Add(new Request(RequestKind.Replace, element, elements, false));
    }

    private static void CheckElement(ReadOnlyMemory<byte> element)
    {
        if (!EbmlWriter.IsSingleElement(element.Span, out _))
        {
            throw new ArgumentException("The new content must be exactly one element whose size field matches its length.", nameof(element));
        }
    }

    private void Add(Request request)
    {
        if (request.Element is { } node && request.Kind != RequestKind.Append)
        {
            foreach (Request other in _requests)
            {
                if (other.Element is { } o && other.Kind != RequestKind.Append && (o.Offset == node.Offset || IsAncestor(o, node) || IsAncestor(node, o)))
                {
                    throw new ArgumentException("Two edits overlap: an element and one of its descendants (or the same element) cannot both be edited.");
                }
            }
        }

        // An element appended inside something that is replaced or removed would vanish with it; refuse instead of losing it silently.
        foreach (Request other in _requests)
        {
            (EbmlElement? replaced, EbmlElement? appendedTo) = request.Kind == RequestKind.Append ? (other.Kind != RequestKind.Append ? other.Element : null, request.Element)
                : (request.Element, other.Kind == RequestKind.Append ? other.Element : null);
            if (replaced is not null && appendedTo is not null && (replaced.Offset == appendedTo.Offset || IsAncestor(replaced, appendedTo)))
            {
                throw new ArgumentException("An element cannot be added inside an element that is replaced or removed by another edit.");
            }
        }

        _requests.Add(request);
    }

    private static bool IsAncestor(EbmlElement ancestor, EbmlElement node)
    {
        for (EbmlElement? p = node.Parent; p is not null; p = p.Parent)
        {
            if (p.Offset == ancestor.Offset)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Plans the new file. May be called again after more edits; each call plans all edits made so far.</summary>
    /// <param name="cancellationToken">Cancels the planning, which reads the SeekHead and scans the Cues.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="UnsupportedMediaException">A CuePoint is larger than 1 MiB, so its positions cannot be moved.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public EditPlan Build(CancellationToken cancellationToken = default)
    {
        // How far elements move decides what the stored positions must say, and the positions' own widths can change how far elements
        // move. Plan, read the movement off the plan, plan again with positions fixed, until the two agree.
        ShiftMap map = new();
        for (int round = 0; ; round++)
        {
            EditPlan plan = BuildRound(map, cancellationToken);
            ShiftMap next = ShiftMap.FromChanges(plan.Changes);
            if (next.Signature() == map.Signature())
            {
                return plan;
            }

            if (round >= 8)
            {
                throw new UnsupportedMediaException("The positions in the Segment did not settle after the edit.");
            }

            map = next;
        }
    }

    private EditPlan BuildRound(ShiftMap map, CancellationToken cancellationToken)
    {
        Rewriter rw = new(_document, _source, RewriteHeaders, cancellationToken);
        HashSet<EbmlElement> consumed = new(NodeComparer<EbmlElement>.Instance);
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
            AddPositionFixups(rw, map, cancellationToken);
        }

        if (RewriteHeaders)
        {
            rw.MarkAllMasters();
        }

        return rw.Rewrite().ToPlan(_source.Length, used, available, required);
    }

    // A Void whose stated size was cut down to fit (a damaged file) may hide real elements inside the bytes it claims; it is not padding to reuse.
    // A Void that a SeekHead entry points into hides an element the reader reaches by that entry, so it is no padding either.
    private bool IsPadding(EbmlElement e) => e.Id == MatroskaIds.Void && !e.IsSizeInferred && !SeekHeadHides(e);

    private List<long>? _seekTargets;
    private bool _seekHeadDamaged;

    // A SeekHead with a damaged entry cannot say where everything is, so no Void is trusted to be empty then.
    private bool SeekHeadHides(EbmlElement voidElement)
    {
        List<long> targets = SeekTargets();
        return _seekHeadDamaged || targets.Any(t => t >= voidElement.Offset && t < voidElement.Range.End);
    }

    private List<long> SeekTargets()
    {
        if (_seekTargets is not null)
        {
            return _seekTargets;
        }

        List<long> found = [];
        foreach (EbmlElement seg in _document.Elements.Where(x => x.Id == MatroskaIds.Segment))
        {
            long segStart = seg.Offset + seg.HeaderSize;
            foreach (EbmlElement head in seg.ChildElements.Where(x => x.Id == MatroskaIds.SeekHead))
            {
                _seekHeadDamaged |= HasInferredSize(head);
                bool changed = false;
                // Rebuilding with an identity map reads every position in the SeekHead and changes nothing.
                EbmlPositions.Rebuild(ReadElement(head), rel =>
                {
                    found.Add(segStart + rel);
                    return rel;
                }, ref changed);
            }
        }

        return _seekTargets = found;
    }

    private static long TotalSize(EbmlElement e) => e.HeaderSize + e.PayloadSize;

    private IReadOnlyList<EbmlElement> SiblingsOf(EbmlElement e) => e.Parent is null ? _document.Elements : e.Parent.ChildElements;

    private EbmlElement? NextSibling(EbmlElement e)
    {
        IReadOnlyList<EbmlElement> sibs = SiblingsOf(e);
        for (int i = 0; i < sibs.Count - 1; i++)
        {
            if (sibs[i].Offset == e.Offset)
            {
                return sibs[i + 1];
            }
        }

        return null;
    }

    private static bool Fits(long remainder) => remainder == 0 || remainder >= 2;

    private void PlanReplace(Rewriter rw, HashSet<EbmlElement> consumed, Request r, ref long used, ref long available, ref long required)
    {
        EbmlElement node = r.Element!;
        ReadOnlyMemory<byte> bytes = r.Bytes;
        long n = TotalSize(node);
        long m = bytes.Length;
        string name = $"replace {node.Name ?? $"0x{node.Id:X}"}";
        PieceList repl = new();
        EbmlElement last = node;
        if (m == n)
        {
            repl.AddData(bytes);
        }
        else if (m < n && Fits(n - m))
        {
            repl.AddData(bytes);
            repl.AddData(EbmlWriter.Void(n - m));
            name += " (remainder kept as Void)";
        }
        else
        {
            EbmlElement? next = NextSibling(node);
            if (m > n && next is not null && IsPadding(next) && !consumed.Contains(next) && TotalSize(next) is long f && Fits(f - (m - n)) && f >= m - n)
            {
                consumed.Add(next);
                last = next;
                repl.AddData(bytes);
                if (f - (m - n) > 0)
                {
                    repl.AddData(EbmlWriter.Void(f - (m - n)));
                }

                used += m - n;
                available += f;
                name += $" (uses {m - n} byte(s) of the following Void)";
            }
            else
            {
                repl.AddData(bytes);
                long free = next is not null && IsPadding(next) && !consumed.Contains(next) ? TotalSize(next) : 0;
                available += free;
                required += m > n ? Math.Max(1, m - n - free) : 1;
            }
        }

        rw.AddSlot(node, last, repl, name);
    }

    private static void PlanRemove(Rewriter rw, Request r)
    {
        EbmlElement node = r.Element!;
        long n = TotalSize(node);
        PieceList repl = new();
        string name = node.Name ?? $"0x{node.Id:X}";
        if (!r.Reclaim)
        {
            repl.AddData(EbmlWriter.Void(n));
        }

        rw.AddSlot(node, node, repl, r.Reclaim ? $"remove {name}" : $"clear {name} to a Void element");
    }

    private void PlanAppend(Rewriter rw, HashSet<EbmlElement> consumed, Request r, ref long used, ref long available, ref long required)
    {
        EbmlElement? parent = r.Element;
        ReadOnlyMemory<byte> bytes = r.Bytes;
        long m = bytes.Length;
        IReadOnlyList<EbmlElement> kids = parent is null ? _document.Elements : parent.ChildElements;
        EbmlElement? padding = null;
        for (int i = kids.Count - 1; i >= 0; i--)
        {
            if (IsPadding(kids[i]) && !consumed.Contains(kids[i]))
            {
                padding = kids[i];
                break;
            }
        }

        if (padding is not null && TotalSize(padding) is long f && f >= m && Fits(f - m))
        {
            consumed.Add(padding);
            PieceList repl = new();
            repl.AddData(bytes);
            if (f - m > 0)
            {
                repl.AddData(EbmlWriter.Void(f - m));
            }

            used += m;
            available += f;
            rw.AddSlot(padding, padding, repl, "insert in a Void element");
            return;
        }

        long free = padding is null ? 0 : TotalSize(padding);
        available += free;
        required += Math.Max(1, m - free);
        PieceList added = new();
        added.AddData(bytes);
        rw.AddAppendElement(parent, added);
    }

    // ---- Segment-relative positions ----

    private sealed record Patch(EbmlElement Target, long NewLength, Func<PieceList> Make);

    private void AddPositionFixups(Rewriter rw, ShiftMap shift, CancellationToken ct)
    {
        foreach (EbmlElement seg in _document.Elements)
        {
            if (seg.Id != MatroskaIds.Segment)
            {
                continue;
            }

            long segStart = seg.Offset + seg.HeaderSize;
            if (!shift.HasChangeIn(segStart, seg.Range.End))
            {
                continue;
            }

            foreach (Patch p in ComputePatches(rw, seg, segStart, shift, ct))
            {
                rw.AddSlot(p.Target, p.Target, p.Make(), $"move positions in {p.Target.Name ?? $"0x{p.Target.Id:X}"}");
            }
        }
    }

    private List<Patch> ComputePatches(Rewriter rw, EbmlElement seg, long segStart, ShiftMap shift, CancellationToken ct)
    {
        long baseShift = shift.Shift(segStart);
        Func<long, long> mapRel = rel => rel + shift.Shift(segStart + rel) - baseShift;
        List<Patch> patches = [];
        foreach (EbmlElement child in seg.ChildElements)
        {
            ct.ThrowIfCancellationRequested();
            if (rw.HasEdit(child))
            {
                continue;
            }

            switch (child.Id)
            {
                case MatroskaIds.SeekHead:
                    {
                        byte[] original = ReadElement(child);
                        bool changed = false;
                        byte[] rebuilt = EbmlPositions.Rebuild(original, mapRel, ref changed);
                        if (changed)
                        {
                            // Re-encoding positions inside entries whose sizes were cut back would scramble them.
                            if (HasInferredSize(child))
                            {
                                throw new UnsupportedMediaException("The SeekHead is damaged (an entry's size runs past its parent), so its positions cannot be moved safely.");
                            }

                            patches.Add(new Patch(child, rebuilt.Length, () => Data(rebuilt)));
                        }

                        break;
                    }

                case MatroskaIds.Cues:
                    {
                        CuesGenerator gen = new(child, mapRel, _limits);
                        (long length, bool changed) = gen.Measure(_source, ct);
                        if (changed)
                        {
                            patches.Add(new Patch(child, length, () =>
                            {
                                PieceList l = new();
                                l.AddGenerated(length, gen);
                                return l;
                            }));
                        }

                        break;
                    }

                case MatroskaIds.Cluster:
                    {
                        Patch? p = ClusterPositionPatch(child, mapRel);
                        if (p is not null)
                        {
                            patches.Add(p);
                        }

                        break;
                    }

                default:
                    break;
            }
        }

        return patches;
    }

    private static bool HasInferredSize(EbmlElement e) => e.IsSizeInferred || e.ChildElements.Any(HasInferredSize);

    private static PieceList Data(byte[] bytes)
    {
        PieceList l = new();
        l.AddData(bytes);
        return l;
    }

    private byte[] ReadElement(EbmlElement e)
    {
        long size = TotalSize(e);
        _limits.CheckAllocation(size, e.Offset);
        byte[] bytes = new byte[size];
        _source.ReadExactly(e.Offset, bytes);
        return bytes;
    }

    // RFC 9559 §12.2.6: a Cluster may carry its own Segment-relative Position, a few elements into its payload.
    private Patch? ClusterPositionPatch(EbmlElement cluster, Func<long, long> mapRel)
    {
        Span<byte> head = new byte[64];
        int n = _source.Read(cluster.Offset + cluster.HeaderSize, head);
        int at = 0;
        for (int step = 0; step < 6 && at < n; step++)
        {
            // The header buffer may end inside an element header (a cut-off or hostile file): then there is nothing to patch.
            if (!Ebml.TryReadId(head[at..n], out uint id, out int idLen) || at + idLen >= n || !Ebml.TryReadSize(head[(at + idLen)..n], out long size, out int sizeLen) || size < 0)
            {
                return null;
            }

            if (id is MatroskaIds.SimpleBlock or MatroskaIds.BlockGroup)
            {
                return null;
            }

            if (id == MatroskaIds.Position && size <= 8 && at + idLen + sizeLen + size <= n)
            {
                Ebml.TryReadUnsigned(head.Slice(at + idLen + sizeLen, (int)size), out ulong old);
                long mapped = mapRel((long)old);
                if (mapped == (long)old)
                {
                    return null;
                }

                long absolute = cluster.Offset + cluster.HeaderSize + at;
                EbmlElement? target = null;
                foreach (EbmlElement c in cluster.ChildElements)
                {
                    if (c.Offset == absolute)
                    {
                        target = c;
                        break;
                    }
                }

                if (target is null)
                {
                    return null;
                }

                byte[] rebuilt = EbmlWriter.UnsignedElement(MatroskaIds.Position, (ulong)mapped, (int)size);
                return new Patch(target, rebuilt.Length, () => Data(rebuilt));
            }

            if (size > n)
            {
                return null; // longer than what was read: the Position, if any, is not before it
            }

            at += idLen + sizeLen + (int)size;
        }

        return null;
    }

    private enum RequestKind
    {
        Replace,
        Remove,
        Append,
    }

    private sealed record Request(RequestKind Kind, EbmlElement? Element, ReadOnlyMemory<byte> Bytes, bool Reclaim);

    private sealed class Rewriter : TreeRewriter<EbmlElement>
    {
        private readonly EbmlDocument _document;
        private readonly bool _regenerate;

        public Rewriter(EbmlDocument document, IMediaSource source, bool regenerate, CancellationToken ct)
            : base(source, ct)
        {
            _document = document;
            _regenerate = regenerate;
        }

        public void AddSlot(EbmlElement first, EbmlElement last, PieceList replacement, string description) =>
            AddEdit(new SlotEdit<EbmlElement>(first, last, replacement, description));

        public void AddAppendElement(EbmlElement? parent, PieceList content) => AddAppend(parent, content);

        public new bool HasEdit(EbmlElement e) => base.HasEdit(e);

        public PieceList Rewrite() => RewriteArea(null);

        public void MarkAllMasters()
        {
            Stack<EbmlElement> stack = new(_document.Elements);
            while (stack.Count > 0)
            {
                EbmlElement e = stack.Pop();
                if (e.IsMaster && e.Id != MatroskaIds.Cluster)
                {
                    ForceTouched(e);
                    foreach (EbmlElement c in e.ChildElements)
                    {
                        stack.Push(c);
                    }
                }
            }
        }

        protected override EbmlElement? ParentOf(EbmlElement node) => node.Parent;

        protected override IReadOnlyList<EbmlElement> ChildrenOf(EbmlElement? node) => node is null ? _document.Elements : node.ChildElements;

        protected override long ChildStart(EbmlElement? node) => node is null ? 0 : node.Offset + node.HeaderSize;

        protected override long ChildEnd(EbmlElement? node) => node is null ? Source.Length : node.Range.End;

        protected override long SlotEnd(EbmlElement node) => node.Range.End;

        protected override void EmitHeader(EbmlElement node, long areaLength, PieceList into)
        {
            int idLen = EbmlWriter.IdLength(node.Id);
            int oldWidth = node.HeaderSize - idLen;
            if (node.IsSizeInferred)
            {
                // An unknown size stays unknown. A size cut back to fit the file (a truncated file) is kept as stated plus the growth, so the
                // stated end still lies past the edited content and nothing falls outside the element.
                if (areaLength != node.PayloadSize && StatedSize(node, idLen, oldWidth) is long stated && stated + (areaLength - node.PayloadSize) >= areaLength)
                {
                    long grown = stated + (areaLength - node.PayloadSize);
                    if (grown >= (1L << 56) - 1)
                    {
                        // The element already claims almost the largest size EBML can state; there is no room to state more.
                        throw new UnsupportedMediaException("An element states a size too close to the EBML maximum to grow; the file is not edited.") { Offset = node.Offset };
                    }

                    int w = oldWidth is >= 1 and <= 8 && grown < (1L << (7 * oldWidth)) - 1 ? oldWidth : EbmlWriter.SizeLength(grown);
                    byte[] h = new byte[idLen + w];
                    EbmlWriter.WriteId(h, node.Id);
                    EbmlWriter.WriteSize(h.AsSpan(idLen), grown, w);
                    into.AddData(h);
                    return;
                }

                into.AddCopy(node.Offset, node.HeaderSize);
                return;
            }

            if (areaLength == node.PayloadSize && !_regenerate)
            {
                // An unchanged size needs no new header.
                into.AddCopy(node.Offset, node.HeaderSize);
                return;
            }

            int width = oldWidth is >= 1 and <= 8 && areaLength < (1L << (7 * oldWidth)) - 1 ? oldWidth : EbmlWriter.SizeLength(areaLength);
            byte[] head = new byte[idLen + width];
            EbmlWriter.WriteId(head, node.Id);
            EbmlWriter.WriteSize(head.AsSpan(idLen), areaLength, width);
            into.AddData(head);
        }

        // The size the file states for an element whose size was cut back; null when the field is the unknown-size marker or unreadable.
        private long? StatedSize(EbmlElement node, int idLen, int sizeWidth)
        {
            Span<byte> field = stackalloc byte[Ebml.MaxSizeLength]; // a size field is at most 8 bytes
            if (sizeWidth is < 1 or > Ebml.MaxSizeLength || Source.Read(node.Offset + idLen, field[..sizeWidth]) != sizeWidth || !Ebml.TryReadSize(field[..sizeWidth], out long size, out _) || size < 0)
            {
                return null;
            }

            return size;
        }

        protected override bool TryGetLeading(EbmlElement node, out long leadingEnd)
        {
            leadingEnd = 0;
            if (!node.IsMaster)
            {
                return false;
            }

            IReadOnlyList<EbmlElement> kids = node.ChildElements;
            if (kids.Count > 0 && kids[0].Id == MatroskaIds.Crc32 && kids[0].PayloadSize == 4)
            {
                leadingEnd = kids[0].Range.End;
                return true;
            }

            return false;
        }

        protected override void AssembleArea(EbmlElement node, long leadingEnd, PieceList rest, PieceList into)
        {
            // RFC 8794 §11.3.1: the CRC-32 covers everything in the parent after the CRC-32 element, stored little-endian.
            EbmlElement crc = node.ChildElements[0];
            uint value = rest.Crc32(Source, CancellationToken);
            byte[] le = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(le, value);
            into.AddCopy(crc.Offset, crc.HeaderSize);
            into.AddData(le);
            into.Add(rest);
        }
    }
}

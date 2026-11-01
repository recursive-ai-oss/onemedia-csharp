namespace RecursiveAI.OneMedia;

/// <summary>
/// Treats two node objects as the same node when they sit at the same offset. Lazy trees can hand out several objects for one node (a
/// search that does not cache its results), and an edit must find its node whichever object the caller holds.
/// </summary>
internal sealed class NodeComparer<TNode> : IEqualityComparer<TNode>
    where TNode : class, IStructureNode
{
    public static readonly NodeComparer<TNode> Instance = new();

    public bool Equals(TNode? x, TNode? y) => x is not null && y is not null && x.Offset == y.Offset;

    public int GetHashCode(TNode obj) => obj.Offset.GetHashCode();
}

/// <summary>A replacement of one node, or of a run of neighbouring nodes, by new content.</summary>
internal sealed class SlotEdit<TNode>(TNode first, TNode last, PieceList replacement, string description)
    where TNode : class, IStructureNode
{
    /// <summary>First node of the replaced run.</summary>
    public TNode First { get; } = first;

    /// <summary>Last node of the replaced run (the same as <see cref="First"/> for a single node).</summary>
    public TNode Last { get; } = last;

    /// <summary>What goes in their place; may be empty (a removal).</summary>
    public PieceList Replacement { get; } = replacement;

    /// <summary>Words for the report.</summary>
    public string Description { get; } = description;
}

/// <summary>
/// Positions in the original file mapped to positions in the new one. Each size change is recorded with the original offset where it
/// happens; a position at or after that offset moves by the change. Used to fix every stored offset (chunk offsets, seek positions).
/// </summary>
internal sealed class ShiftMap
{
    private readonly List<(long Position, long Delta)> _items = [];
    private long[] _positions = [];
    private long[] _cumulative = [];
    private bool _dirty = true;

    /// <summary>The sum of all changes: the difference between the new file's length and the old one's.</summary>
    public long Total
    {
        get
        {
            long sum = 0;
            foreach ((long _, long delta) in _items)
            {
                sum += delta;
            }

            return sum;
        }
    }

    public bool IsEmpty
    {
        get
        {
            foreach ((long _, long delta) in _items)
            {
                if (delta != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void Add(long position, long delta)
    {
        if (delta != 0)
        {
            _items.Add((position, delta));
            _dirty = true;
        }
    }

    /// <summary>True when some change was made at a position after <paramref name="start"/> and at or before <paramref name="end"/>.</summary>
    public bool HasChangeIn(long start, long end)
    {
        foreach ((long position, long delta) in _items)
        {
            if (delta != 0 && position > start && position <= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A text form of the recorded changes, to tell whether two maps are the same.</summary>
    public string Signature()
    {
        Prepare();
        return string.Join(';', _items.ConvertAll(static i => $"{i.Position}:{i.Delta}"));
    }

    /// <summary>
    /// Reads the movement off a finished plan: each change moves everything that follows its source range by the difference between the new
    /// content's length and the old range's length. An insertion is a change with an empty source range at the insertion point.
    /// </summary>
    public static ShiftMap FromChanges(IReadOnlyList<WriteChange> changes)
    {
        ShiftMap map = new();
        foreach (WriteChange c in changes)
        {
            if (!c.Source.IsNone)
            {
                map.Add(c.Source.End, c.Output.Length - c.Source.Length);
            }
        }

        return map;
    }

    public ShiftMap Clone()
    {
        ShiftMap copy = new();
        copy._items.AddRange(_items);
        return copy;
    }

    /// <summary>How far a position moves: the sum of the changes made at or before it.</summary>
    public long Shift(long position)
    {
        Prepare();
        int lo = 0;
        int hi = _positions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_positions[mid] <= position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo == 0 ? 0 : _cumulative[lo - 1];
    }

    public long Map(long position) => position + Shift(position);

    private void Prepare()
    {
        if (!_dirty)
        {
            return;
        }

        _items.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        _positions = new long[_items.Count];
        _cumulative = new long[_items.Count];
        long sum = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            sum += _items[i].Delta;
            _positions[i] = _items[i].Position;
            _cumulative[i] = sum;
        }

        _dirty = false;
    }
}

/// <summary>
/// Walks a structure tree and produces the pieces of the new file: untouched nodes become copied ranges, nodes with an edit below them
/// are re-emitted with a recomputed header around their rewritten children, and the edits themselves put new content in place. The
/// format-specific parts (what a header looks like, where children start) are the abstract members.
/// </summary>
internal abstract class TreeRewriter<TNode>
    where TNode : class, IStructureNode
{
    private readonly Dictionary<TNode, SlotEdit<TNode>> _edits = new(NodeComparer<TNode>.Instance);
    private readonly Dictionary<TNode, List<PieceList>> _appends = new(NodeComparer<TNode>.Instance);
    private readonly List<PieceList> _rootAppends = [];
    private readonly HashSet<TNode> _touched = new(NodeComparer<TNode>.Instance);

    protected TreeRewriter(IMediaSource source, CancellationToken cancellationToken)
    {
        Source = source;
        CancellationToken = cancellationToken;
    }

    protected IMediaSource Source { get; }

    protected CancellationToken CancellationToken { get; }

    /// <summary>Nodes in the tree whose children are rewritten even without an edit (diagnostic full rewrite).</summary>
    protected bool IsTouched(TNode node) => _touched.Contains(node);

    protected abstract TNode? ParentOf(TNode node);

    protected abstract IReadOnlyList<TNode> ChildrenOf(TNode? node);

    protected abstract long ChildStart(TNode? node);

    protected abstract long ChildEnd(TNode? node);

    protected abstract long SlotEnd(TNode node);

    /// <summary>Emits the bytes from the node's first byte up to where its children start, with the size recomputed for <paramref name="areaLength"/>.</summary>
    protected abstract void EmitHeader(TNode node, long areaLength, PieceList into);

    /// <summary>When the node starts with an element the format maintains itself (an EBML CRC-32), returns where that element ends.</summary>
    protected virtual bool TryGetLeading(TNode node, out long leadingEnd)
    {
        leadingEnd = 0;
        return false;
    }

    /// <summary>Emits bytes that follow a rewritten container's children (alignment padding), given the new children area length.</summary>
    protected virtual void EmitTrailer(TNode node, long areaLength, PieceList into) { }

    /// <summary>Puts the leading element back in front of the rewritten rest (recomputing it).</summary>
    protected virtual void AssembleArea(TNode node, long leadingEnd, PieceList rest, PieceList into) => into.Add(rest);

    protected void AddEdit(SlotEdit<TNode> edit)
    {
        if (_edits.ContainsKey(edit.First))
        {
            throw new ArgumentException("The node is already being edited.");
        }

        _edits[edit.First] = edit;
        MarkTouched(edit.First);
    }

    protected void AddAppend(TNode? parent, PieceList content)
    {
        if (parent is null)
        {
            _rootAppends.Add(content);
            return;
        }

        if (!_appends.TryGetValue(parent, out List<PieceList>? list))
        {
            _appends[parent] = list = [];
        }

        list.Add(content);
        _touched.Add(parent);
        MarkTouched(parent);
    }

    protected bool HasEdit(TNode node) => _edits.ContainsKey(node);

    protected void MarkTouched(TNode node)
    {
        TNode? p = ParentOf(node);
        while (p is not null && _touched.Add(p))
        {
            p = ParentOf(p);
        }
    }

    protected void ForceTouched(TNode node) => _touched.Add(node);

    /// <summary>Rewrites the children area of a node (or of the whole file when <paramref name="node"/> is null).</summary>
    protected PieceList RewriteArea(TNode? node)
    {
        CancellationToken.ThrowIfCancellationRequested();
        PieceList area = new();
        long start = ChildStart(node);
        long end = ChildEnd(node);
        long cursor = start;
        long leadingEnd = 0;
        bool leading = node is not null && TryGetLeading(node, out leadingEnd);
        if (leading)
        {
            cursor = leadingEnd;
        }

        IReadOnlyList<TNode> kids = ChildrenOf(node);
        foreach (TNode child in kids)
        {
            if (child.Offset < cursor)
            {
                continue; // inside a run an earlier edit replaced, or the leading element
            }

            if (_edits.TryGetValue(child, out SlotEdit<TNode>? edit))
            {
                area.AddCopy(cursor, child.Offset - cursor);
                long outStart = area.Length;
                area.Add(edit.Replacement);
                long replacedEnd = SlotEnd(edit.Last);
                area.NoteChange(edit.Description, new ByteRange(child.Offset, replacedEnd - child.Offset), outStart);
                cursor = replacedEnd;
            }
            else if (_touched.Contains(child))
            {
                area.AddCopy(cursor, child.Offset - cursor);
                area.Add(RewriteNode(child));
                cursor = SlotEnd(child);
            }
        }

        List<PieceList>? appends = node is null ? (_rootAppends.Count > 0 ? _rootAppends : null) : _appends.GetValueOrDefault(node);
        if (appends is not null)
        {
            long at = kids.Count > 0 ? Math.Max(cursor, SlotEnd(kids[^1])) : cursor;
            if (kids.Count == 0 && leading)
            {
                at = leadingEnd;
            }

            area.AddCopy(cursor, at - cursor);
            cursor = at;
            foreach (PieceList a in appends)
            {
                long outStart = area.Length;
                area.Add(a);
                area.NoteChange("insert", new ByteRange(at, 0), outStart);
            }
        }

        area.AddCopy(cursor, end - cursor);
        if (leading && node is not null)
        {
            PieceList assembled = new();
            AssembleArea(node, leadingEnd, area, assembled);
            return assembled;
        }

        return area;
    }

    /// <summary>Rewrites one container node: its header, recomputed, around its rewritten children.</summary>
    protected PieceList RewriteNode(TNode node)
    {
        PieceList area = RewriteArea(node);
        PieceList result = new();
        long region = ChildStart(node) - node.Offset;
        EmitHeader(node, area.Length, result);
        if (result.Length != region)
        {
            // A wider size field makes the header longer: everything after it moves, and stored offsets must know.
            result.NoteChange("header size changed", new ByteRange(node.Offset, region), 0);
        }

        result.Add(area);
        EmitTrailer(node, area.Length, result);
        return result;
    }
}

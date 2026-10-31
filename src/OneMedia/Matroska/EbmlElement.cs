namespace RecursiveAI.OneMedia.Matroska;

/// <summary>The result of checking an element's <c>CRC-32</c> child (RFC 8794 §11.3.1).</summary>
public enum EbmlCrcStatus
{
    /// <summary>The element has no CRC-32 child.</summary>
    None,

    /// <summary>The stored CRC-32 matches the element's content.</summary>
    Valid,

    /// <summary>The stored CRC-32 differs from the content's; a defect is also logged.</summary>
    Mismatch,
}

/// <summary>Shared state of one element tree: the source, limits, the defect log and a small read cache.</summary>
internal sealed class EbmlTreeContext(IMediaSource source, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
{
    private const int CacheSize = 16 * 1024;
    private readonly byte[] _cache = new byte[CacheSize];
    private long _cacheStart = -1;
    private int _cacheLength;
    private long _elementCount;

    public IMediaSource Source { get; } = source;

    public MediaLimits Limits { get; } = limits;

    public DefectLog Defects { get; } = defects;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public long Length { get; } = source.Length;

    public void CountElement(long offset) => Limits.CheckElementCount(++_elementCount, offset);

    /// <summary>Reads at an offset through a one-chunk cache so that walking many small headers costs few source reads.</summary>
    public int ReadAt(long offset, Span<byte> destination)
    {
        if (destination.Length >= CacheSize)
        {
            return Source.Read(offset, destination, CancellationToken);
        }

        if (_cacheStart < 0 || offset < _cacheStart || offset + destination.Length > _cacheStart + _cacheLength)
        {
            _cacheLength = Source.Read(offset, _cache, CancellationToken);
            _cacheStart = offset;
        }

        long available = _cacheStart + _cacheLength - offset;
        int take = (int)Math.Clamp(available, 0, destination.Length);
        if (take > 0)
        {
            _cache.AsSpan((int)(offset - _cacheStart), take).CopyTo(destination);
        }

        return take;
    }
}

/// <summary>
/// One EBML element in a lazily built tree. The node holds only its position and size; children are read from the source the first
/// time they are asked for, and unknown elements stay as ranges. Damage (sizes past the parent or the file, invalid headers) is
/// logged to the document's defects and the tree stays walkable.
/// </summary>
public sealed class EbmlElement : IStructureNode
{
    private readonly EbmlTreeContext _context;
    private readonly int _depth;
    private List<EbmlElement>? _children;
    private long _payloadSize;
    private bool _inferred;

    internal EbmlElement(EbmlTreeContext context, EbmlElement? parent, uint id, long offset, int headerSize, long payloadSize, bool inferred, int depth)
    {
        _context = context;
        Parent = parent;
        Id = id;
        Offset = offset;
        HeaderSize = headerSize;
        _payloadSize = payloadSize;
        _inferred = inferred;
        _depth = depth;
    }

    /// <summary>The element ID with its length-marker bits, for example <c>0x1F43B675</c> for a Cluster.</summary>
    public uint Id { get; }

    /// <summary>The element's name in the specification, or null for an unknown ID.</summary>
    public string? Name => MatroskaIds.NameOf(Id);

    /// <summary>The element that contains this one, or null at the top level.</summary>
    public EbmlElement? Parent { get; }

    /// <summary>True when the ID names a master element, one that contains other elements.</summary>
    public bool IsMaster => MatroskaIds.IsMaster(Id);

    /// <inheritdoc />
    public ulong TypeKey => Id;

    /// <inheritdoc />
    public long Offset { get; }

    /// <inheritdoc />
    public int HeaderSize { get; }

    /// <summary>
    /// The payload size in bytes. For an element of unknown size this finds the end by reading the element's children, so it can
    /// cost a walk over the element.
    /// </summary>
    public long PayloadSize
    {
        get
        {
            if (_payloadSize < 0)
            {
                EnsureChildren();
            }

            return _payloadSize;
        }
    }

    /// <summary>True when the size was not stored in the file: an unknown size resolved from the children, or a size cut down to fit its parent or the file.</summary>
    public bool IsSizeInferred
    {
        get
        {
            if (_payloadSize < 0)
            {
                EnsureChildren();
            }

            return _inferred;
        }
    }

    /// <summary>The element's whole extent: header and payload.</summary>
    public ByteRange Range => new(Offset, HeaderSize + PayloadSize);

    /// <summary>The element's payload extent.</summary>
    public ByteRange Payload => new(Offset + HeaderSize, PayloadSize);

    /// <summary>The child elements in file order; empty for anything that is not a master element.</summary>
    public IReadOnlyList<IStructureNode> Children
    {
        get
        {
            EnsureChildren();
            return _children!;
        }
    }

    /// <summary>The child elements in file order, typed.</summary>
    public IReadOnlyList<EbmlElement> ChildElements
    {
        get
        {
            EnsureChildren();
            return _children!;
        }
    }

    /// <summary>Reads payload bytes.</summary>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="payloadOffset">Where in the payload to start.</param>
    /// <returns>The number of bytes read; fewer than requested at the end of the payload or the data.</returns>
    public int ReadPayload(Span<byte> destination, long payloadOffset = 0)
    {
        long size = PayloadSize;
        if (payloadOffset < 0 || payloadOffset >= size)
        {
            return 0;
        }

        int count = (int)Math.Min(destination.Length, size - payloadOffset);
        return _context.Source.Read(Offset + HeaderSize + payloadOffset, destination[..count], _context.CancellationToken);
    }

    /// <summary>
    /// Checks the CRC-32 child (RFC 8794 §11.3.1): the CRC-32 of every byte of the element's payload after the CRC-32 element. A mismatch is
    /// logged as a defect, never thrown. This reads the whole payload, in small chunks.
    /// </summary>
    /// <returns>Whether the element has a CRC-32 and whether it matches.</returns>
    public EbmlCrcStatus VerifyCrc()
    {
        Span<byte> head = new byte[6];
        if (PayloadSize < 6 || ReadPayload(head) != 6 || head[0] != MatroskaIds.Crc32 || head[1] != 0x84)
        {
            return EbmlCrcStatus.None;
        }

        uint stored = (uint)(head[2] | (head[3] << 8) | (head[4] << 16) | (head[5] << 24));
        uint state = EbmlCrc32.Initial;
        byte[] chunk = new byte[16 * 1024];
        long position = 6;
        long size = PayloadSize;
        while (position < size)
        {
            int n = ReadPayload(chunk.AsSpan(0, (int)Math.Min(chunk.Length, size - position)), position);
            if (n <= 0)
            {
                break;
            }

            state = EbmlCrc32.Update(state, chunk.AsSpan(0, n));
            position += n;
        }

        uint actual = EbmlCrc32.Finish(state);
        if (position < size)
        {
            return EbmlCrcStatus.None;
        }

        if (actual == stored)
        {
            return EbmlCrcStatus.Valid;
        }

        _context.Defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, Offset, $"The CRC-32 of the {Name ?? "element"} at {Offset} is {stored:X8} but its content hashes to {actual:X8}.");
        return EbmlCrcStatus.Mismatch;
    }

    /// <summary>Returns a short description for debugging.</summary>
    /// <returns>The name, offset and size.</returns>
    public override string ToString() => $"{Name ?? $"0x{Id:X}"} @{Offset} +{HeaderSize} size {(_payloadSize < 0 ? "unknown" : _payloadSize)}";

    private void EnsureChildren()
    {
        if (_children is not null)
        {
            return;
        }

        if (!IsMaster)
        {
            _children = [];
            return;
        }

        _context.Limits.CheckDepth(_depth + 1, Offset);
        long start = Offset + HeaderSize;
        bool unknown = _payloadSize < 0;
        long end = unknown ? long.MaxValue : start + _payloadSize;
        _children = ParseChildren(_context, this, start, end, unknown ? MatroskaIds.LevelOf(Id) : -1, _depth + 1, out long consumedEnd);
        if (unknown)
        {
            _payloadSize = consumedEnd - start;
            _inferred = true;
        }
    }

    /// <summary>
    /// Looks for the next position in [<paramref name="from"/>, <paramref name="limit"/>) where a known Segment child (level 1) starts with a
    /// sound size. Reads in 4 KiB windows that overlap by one header, so a header is never split.
    /// </summary>
    private static bool TryFindNextTopLevel(EbmlTreeContext context, long from, long limit, out long found)
    {
        const int Window = 4096;
        Span<byte> buffer = new byte[Window];
        long position = from;
        while (position < limit)
        {
            int got = context.ReadAt(position, buffer[..(int)Math.Min(Window, limit - position)]);
            if (got <= 0)
            {
                break;
            }

            bool more = got == Window && position + got < limit;
            int last = more ? got - (Ebml.MaxIdLength + Ebml.MaxSizeLength) : got;
            for (int i = 0; i < last; i++)
            {
                ReadOnlySpan<byte> window = buffer[i..got];
                if (Ebml.TryReadId(window, out uint id, out int idLength)
                    && MatroskaIds.LevelOf(id) == 1
                    && Ebml.TryReadSize(window[idLength..], out long size, out int sizeLength)
                    && (size < 0 || position + i + idLength + sizeLength + size <= limit))
                {
                    found = position + i;
                    return true;
                }
            }

            if (!more)
            {
                break;
            }

            position += last;
        }

        found = 0;
        return false;
    }

    /// <summary>
    /// Reads child headers from <paramref name="start"/> to <paramref name="end"/>. For an unknown-size parent (<paramref name="terminatorLevel"/> ≥ 0)
    /// the run ends at the first element whose level is that of the parent or higher (RFC 8794 §6.2).
    /// </summary>
    internal static List<EbmlElement> ParseChildren(EbmlTreeContext context, EbmlElement? parent, long start, long end, int terminatorLevel, int depth, out long consumedEnd)
    {
        List<EbmlElement> children = [];
        long length = context.Length;
        long limit = end == long.MaxValue ? (length >= 0 ? length : long.MaxValue) : Math.Min(end, length >= 0 ? Math.Max(length, start) : end);
        long position = start;
        Span<byte> header = new byte[Ebml.MaxIdLength + Ebml.MaxSizeLength];
        while (position < limit)
        {
            int got = context.ReadAt(position, header[..(int)Math.Min(header.Length, limit - position)]);
            if (got == 0)
            {
                break;
            }

            ReadOnlySpan<byte> window = header[..got];
            if (!Ebml.TryReadId(window, out uint id, out int idLength) || !Ebml.TryReadSize(window[idLength..], out long size, out int sizeLength))
            {
                if (parent is { Id: MatroskaIds.Segment } && TryFindNextTopLevel(context, position + 1, limit, out long resumeAt))
                {
                    // Damage inside a Segment: carry on at the next Segment child that has a sound header, as the demuxer does.
                    context.Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Error, position, $"{resumeAt - position} bytes are not a valid element; the next one starts at {resumeAt}.");
                    position = resumeAt;
                    continue;
                }

                context.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, position, "An element header is not a valid EBML ID and size; the rest of the parent is not parsed.");
                break;
            }

            if (terminatorLevel >= 0)
            {
                int level = MatroskaIds.LevelOf(id);
                if (level is >= 0 && level <= terminatorLevel)
                {
                    break;
                }
            }

            context.CountElement(position);
            int headerSize = idLength + sizeLength;
            long payloadStart = position + headerSize;
            bool inferred = false;
            EbmlElement child;
            if (size < 0)
            {
                if (id is MatroskaIds.Segment or MatroskaIds.Cluster)
                {
                    // Unknown-size Segment: runs to the end of the data. Unknown-size Cluster: found by walking its children.
                    if (id == MatroskaIds.Segment)
                    {
                        child = new EbmlElement(context, parent, id, position, headerSize, Math.Max(0, limit == long.MaxValue ? 0 : limit - payloadStart), true, depth);
                    }
                    else
                    {
                        child = new EbmlElement(context, parent, id, position, headerSize, -1, true, depth);
                    }
                }
                else
                {
                    context.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, position, $"An element of unknown size is not a Segment or Cluster; it is cut at the end of its parent.");
                    child = new EbmlElement(context, parent, id, position, headerSize, Math.Max(0, limit - payloadStart), true, depth);
                }
            }
            else
            {
                long childEnd = payloadStart + size;
                if (childEnd > limit)
                {
                    bool pastFile = length >= 0 && childEnd > length;
                    context.Defects.Add(
                        pastFile ? DefectKind.SizeExceedsFile : DefectKind.SizeExceedsParent,
                        DefectSeverity.Error,
                        position,
                        pastFile ? $"An element claims {size} bytes but the file ends first; it is cut at the end of the file." : $"An element claims {size} bytes but only {limit - payloadStart} remain in its parent; it is cut at the end of the parent.");
                    size = Math.Max(0, limit - payloadStart);
                    inferred = true;
                }

                child = new EbmlElement(context, parent, id, position, headerSize, size, inferred, depth);
            }

            children.Add(child);
            position = payloadStart + child.PayloadSize;
        }

        consumedEnd = position;
        return children;
    }
}

/// <summary>
/// An EBML file's top-level elements as a lazy tree: the EBML header and the Segment(s). Nothing below the top level is read until
/// asked for, and no payload is read at all.
/// </summary>
public sealed class EbmlDocument
{
    private EbmlDocument(IReadOnlyList<EbmlElement> elements, DefectLog defects)
    {
        Elements = elements;
        Defects = defects;
    }

    /// <summary>The top-level elements in file order.</summary>
    public IReadOnlyList<EbmlElement> Elements { get; }

    /// <summary>The defects found while walking the tree; it grows as more of the tree is read.</summary>
    public DefectLog Defects { get; }

    /// <summary>The EBML header element, or null when the file has none first.</summary>
    public EbmlElement? Header => Elements.Count > 0 && Elements[0].Id == MatroskaIds.Ebml ? Elements[0] : null;

    /// <summary>The first Segment element, or null.</summary>
    public EbmlElement? Segment
    {
        get
        {
            foreach (EbmlElement e in Elements)
            {
                if (e.Id == MatroskaIds.Segment)
                {
                    return e;
                }
            }

            return null;
        }
    }

    /// <summary>Reads the top level of the file. Later reads (children, payloads) go through <paramref name="source"/> too, so keep it open.</summary>
    /// <param name="source">The source; it must stay open as long as the tree is used.</param>
    /// <param name="limits">Limits for depth and element count; <see cref="MediaLimits.Default"/> when null.</param>
    /// <param name="defects">The log to fill; a new one when null.</param>
    /// <param name="cancellationToken">Cancels this and later lazy reads of the tree.</param>
    /// <returns>The document.</returns>
    /// <exception cref="MediaFormatException">The data does not start with an EBML header.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static EbmlDocument Open(IMediaSource source, MediaLimits? limits = null, DefectLog? defects = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        EbmlTreeContext context = new(source, limits, defects, cancellationToken);
        Span<byte> first = new byte[4];
        if (source.Read(0, first, cancellationToken) < 4 || first[0] != 0x1A || first[1] != 0x45 || first[2] != 0xDF || first[3] != 0xA3)
        {
            throw new MediaFormatException("The data does not start with an EBML header.") { Offset = 0, Kind = DefectKind.InvalidValue };
        }

        List<EbmlElement> top = EbmlElement.ParseChildren(context, null, 0, context.Length, -1, 1, out _);
        return new EbmlDocument(top, defects);
    }
}

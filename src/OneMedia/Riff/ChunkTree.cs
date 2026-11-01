using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>Which of the chunk-file layouts a file uses. They differ in byte order, the width of the size field and padding.</summary>
public enum ChunkLayout
{
    /// <summary>RIFF: a four-character code and a 32-bit little-endian size; chunks are padded to an even length.</summary>
    Riff = 0,

    /// <summary>RF64 / BW64 (EBU Tech 3306, ITU-R BS.2088): RIFF whose 32-bit sizes may be 0xFFFFFFFF, with the real sizes in the <c>ds64</c> chunk.</summary>
    Rf64,

    /// <summary>Sony Wave64: 16-byte GUID ids and 64-bit little-endian sizes that include the 24-byte header; chunks are padded to 8 bytes.</summary>
    Wave64,

    /// <summary>IFF (AIFF, AIFF-C): a four-character code and a 32-bit big-endian size; chunks are padded to an even length.</summary>
    Iff,
}

/// <summary>
/// One chunk of a RIFF, RF64, Wave64 or IFF file. The node holds positions only; the payload is read from the source by whoever needs it.
/// Chunks a reader does not know are kept as ordinary nodes, so the whole file is accounted for.
/// </summary>
public sealed class ChunkNode : IStructureNode
{
    private readonly ChunkContext _context;
    private readonly int _depth;
    private List<ChunkNode>? _children;

    internal ChunkNode(ChunkContext context, ulong typeKey, Guid guid, long offset, int headerSize, long payloadSize, bool inferred, long nextOffset, bool container, string? formType, int depth)
    {
        _context = context;
        TypeKey = typeKey;
        ChunkGuid = guid;
        Offset = offset;
        HeaderSize = headerSize;
        PayloadSize = payloadSize;
        IsSizeInferred = inferred;
        NextOffset = nextOffset;
        IsContainer = container;
        FormType = formType;
        _depth = depth;
    }

    /// <inheritdoc />
    public ulong TypeKey { get; }

    /// <summary>The chunk id as text (the first four bytes of the GUID for Wave64): <c>fmt </c>, <c>data</c>, <c>LIST</c>.</summary>
    public string Id => FourCc(TypeKey);

    /// <summary>The 16-byte chunk GUID of a Wave64 chunk, as stored (Windows byte order); <see cref="Guid.Empty"/> in the other layouts.</summary>
    public Guid ChunkGuid { get; }

    /// <inheritdoc />
    public long Offset { get; }

    /// <inheritdoc />
    public int HeaderSize { get; }

    /// <inheritdoc />
    public long PayloadSize { get; }

    /// <summary>The whole chunk, header and payload (not the padding byte).</summary>
    public ByteRange Range => new(Offset, HeaderSize + PayloadSize);

    /// <summary>The chunk's data.</summary>
    public ByteRange Payload => new(Offset + HeaderSize, PayloadSize);

    /// <inheritdoc />
    public bool IsSizeInferred { get; }

    /// <summary>Offset of the next sibling: the end of the payload plus its padding.</summary>
    public long NextOffset { get; }

    /// <summary>True for chunks that hold other chunks after a form type: <c>RIFF</c>, <c>LIST</c>, <c>FORM</c> and the Wave64 <c>riff</c> and <c>list</c>.</summary>
    public bool IsContainer { get; }

    /// <summary>The form or list type of a container (<c>WAVE</c>, <c>INFO</c>, <c>AIFF</c>), or <c>null</c> for other chunks.</summary>
    public string? FormType { get; }

    /// <summary>The chunks inside a container, in file order, parsed on first access; empty for other chunks.</summary>
    public IReadOnlyList<ChunkNode> Chunks => _children ??= ParseChildren();

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children => Chunks;

    internal static string FourCc(ulong key) => string.Create(4, key, static (span, k) =>
    {
        for (int i = 0; i < 4; i++)
        {
            span[i] = (char)((k >> (8 * (3 - i))) & 0xFF);
        }
    });

    /// <summary>Packs a four-character code into the number <see cref="TypeKey"/> uses.</summary>
    /// <param name="id">Exactly four ASCII characters.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not four characters long.</exception>
    public static ulong Key(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 4)
        {
            throw new ArgumentException("A chunk id is four characters.", nameof(id));
        }

        return ((ulong)(byte)id[0] << 24) | ((ulong)(byte)id[1] << 16) | ((ulong)(byte)id[2] << 8) | (byte)id[3];
    }

    /// <summary>Returns the first chunk directly inside this container with the given id (and form type, when given), or <c>null</c>.</summary>
    /// <param name="id">The four-character id.</param>
    /// <param name="formType">For containers, the required form or list type.</param>
    /// <returns>The chunk, or <c>null</c>.</returns>
    public ChunkNode? Find(string id, string? formType = null)
    {
        foreach (ChunkNode c in Chunks)
        {
            if (c.Id == id && (formType is null || c.FormType == formType))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Reads the chunk's payload, up to <paramref name="max"/> bytes.</summary>
    /// <param name="max">Largest number of bytes to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The data; shorter than the payload when the source ends first (a <see cref="DefectKind.Truncated"/> defect is recorded) or <paramref name="max"/> applies.</returns>
    /// <exception cref="MediaLimitExceededException">The size exceeds the allocation limit.</exception>
    public byte[] ReadData(int max, CancellationToken cancellationToken = default)
    {
        long n = Math.Min(Math.Max(PayloadSize, 0), max);
        if (n <= 0)
        {
            return [];
        }

        _context.Limits.CheckAllocation(n, Offset);
        byte[] data = new byte[n];
        int got = _context.Source.Read(Offset + HeaderSize, data, cancellationToken);
        if (got < n)
        {
            _context.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Offset + HeaderSize + got, $"The data of the '{Id}' chunk ends early.", "riff.truncated-data");
            Array.Resize(ref data, got);
        }

        return data;
    }

    private List<ChunkNode> ParseChildren()
    {
        List<ChunkNode> list = [];
        if (!IsContainer)
        {
            return list;
        }

        long pos = Offset + HeaderSize + (_context.Layout == ChunkLayout.Wave64 ? 16 : 4);
        long end = Offset + HeaderSize + PayloadSize;
        _context.Limits.CheckDepth(_depth + 1, pos);
        while (ChunkWalker.TryReadNext(_context, ref pos, end, _depth + 1, out ChunkNode? node))
        {
            list.Add(node!);
        }

        return list;
    }
}

/// <summary>What the chunk walkers share: the source, the limits, the defect log and the RF64 size table.</summary>
internal sealed class ChunkContext(IMediaSource source, MediaLimits limits, DefectLog defects, ChunkLayout layout, long length)
{
    public IMediaSource Source { get; } = source;

    public MediaLimits Limits { get; } = limits;

    public DefectLog Defects { get; } = defects;

    public ChunkLayout Layout { get; } = layout;

    /// <summary>Length of the source, or <see cref="long.MaxValue"/> when it cannot say.</summary>
    public long Length { get; } = length;

    public long Count;

    // EBU Tech 3306 §3: the 64-bit sizes of the data chunk and of other oversized chunks, read from ds64 when it is met.
    public long Ds64DataSize = -1;

    public Dictionary<uint, long>? Ds64Table;
}

/// <summary>Reads chunk headers one after another; the same code builds the lazy tree and serves readers that must stop early.</summary>
internal static class ChunkWalker
{
    private static readonly Guid Wave64Riff = new("66666972-912E-11CF-A5D6-28DB04C10000");
    private static readonly Guid Wave64Wave = new("65766177-ACF3-11D3-8CD1-00C04F8EDB8A");

    /// <summary>
    /// Reads the chunk header at <paramref name="pos"/>, checks it against <paramref name="end"/> and moves <paramref name="pos"/> to the next
    /// sibling. A size that reaches past <paramref name="end"/> is cut back and reported; a header that does not fit ends the list.
    /// </summary>
    public static bool TryReadNext(ChunkContext c, ref long pos, long end, int depth, out ChunkNode? node)
    {
        node = null;
        bool w64 = c.Layout == ChunkLayout.Wave64;
        int header = w64 ? 24 : 8;
        if (pos >= end)
        {
            return false;
        }

        if (end - pos < header)
        {
            c.Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, pos, $"{end - pos} byte(s) after the last chunk are too few for a chunk header.", "riff.trailing");
            pos = end;
            return false;
        }

        c.Limits.CheckElementCount(++c.Count, pos);
        Span<byte> h = stackalloc byte[24]; // one chunk header: 8 bytes, or 24 for Wave64
        if (c.Source.Read(pos, h[..header]) < header)
        {
            c.Defects.Add(DefectKind.Truncated, DefectSeverity.Error, pos, "The data ends inside a chunk header.", "riff.truncated-header");
            pos = end;
            return false;
        }

        long start = pos;
        ulong key = BinaryPrimitives.ReadUInt32BigEndian(h);
        Guid guid = w64 ? new Guid(h[..16]) : Guid.Empty;
        long size;
        bool unbounded = false;
        if (w64)
        {
            // Wave64: the size includes the 24-byte header (Sony's Wave64 description).
            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(h[16..]);
            if (total < 24)
            {
                c.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, pos, $"A Wave64 chunk states a size of {total}, less than its own header.", "riff.w64-size");
                pos = end;
                return false;
            }

            size = total - 24 > long.MaxValue ? long.MaxValue : (long)(total - 24);
        }
        else if (c.Layout == ChunkLayout.Iff)
        {
            size = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        }
        else
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            size = raw;
            if (raw == 0xFFFF_FFFF)
            {
                if (c.Layout == ChunkLayout.Rf64 && Ds64Size(c, key) is { } big)
                {
                    size = big;
                }
                else
                {
                    // A size of all ones is how streaming writers say "until the end".
                    unbounded = true;
                    size = long.MaxValue;
                    if (depth > 1 || c.Layout != ChunkLayout.Rf64)
                    c.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Info, pos, $"The chunk '{ChunkNode.FourCc(key)}' states the size 0xFFFFFFFF; it is taken to run to the end of its parent.", "riff.unbounded-size");
                }
            }
        }

        if (depth == 1 && !w64 && size < 4)
        {
            // A streaming writer that did not know the length leaves the outer size at zero: the file runs to its end.
            unbounded = true;
            size = long.MaxValue;
            c.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Info, pos, "The outer chunk states no usable size; it is taken to run to the end of the file.", "riff.unbounded-size");
        }

        long payloadStart = pos + header;
        long available = end - payloadStart;
        bool inferred = unbounded;
        long payload = size;
        if (size > available && !unbounded && depth == 2 && c.Length > 0 && payloadStart + size <= c.Length)
        {
            // The outer size is too small (some writers leave out the last bytes) but this chunk fits the file: its own size is believed.
            c.Defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, pos,
                $"The chunk '{ChunkNode.FourCc(key)}' states {size} bytes and ends after the outer chunk does; the outer size is too small and the chunk is read in full.", "riff.size-exceeds-parent");
            available = size;
        }

        if (size > available)
        {
            if (!unbounded)
            {
                bool file = end >= c.Length;
                c.Defects.Add(file ? DefectKind.SizeExceedsFile : DefectKind.SizeExceedsParent, DefectSeverity.Warning, pos,
                    $"The chunk '{ChunkNode.FourCc(key)}' states {size} bytes but only {available} remain; it is cut short.", file ? "riff.size-exceeds-file" : "riff.size-exceeds-parent");
            }

            payload = available;
            inferred = true;
        }

        long next;
        if (w64)
        {
            next = start + ((header + payload + 7) & ~7L);
        }
        else
        {
            next = payloadStart + payload + (payload & 1);
        }

        if (inferred)
        {
            next = end;
        }

        bool container = IsContainer(c.Layout, key, guid);
        string? formType = null;
        if (container)
        {
            int prefix = w64 ? 16 : 4;
            if (payload < prefix)
            {
                container = false;
            }
            else if (w64)
            {
                Span<byte> g = stackalloc byte[16]; // the form GUID that follows a Wave64 riff or list header
                if (c.Source.Read(payloadStart, g) == 16)
                {
                    formType = ChunkNode.FourCc(BinaryPrimitives.ReadUInt32BigEndian(g));
                    if (key == ChunkNode.Key("riff") && new Guid(g) != Wave64Wave)
                    {
                        formType = "?" + formType;
                    }
                }
            }
            else
            {
                Span<byte> f = stackalloc byte[4]; // the form type that follows a RIFF, LIST or FORM header
                if (c.Source.Read(payloadStart, f) == 4)
                {
                    formType = Encoding.Latin1.GetString(f);
                }
            }
        }

        node = new ChunkNode(c, key, guid, start, header, payload, inferred, next, container, formType, depth);
        pos = next;
        if (c.Layout == ChunkLayout.Rf64 && key == ChunkNode.Key("ds64"))
        {
            ReadDs64(c, node);
        }

        return true;
    }

    private static bool IsContainer(ChunkLayout layout, ulong key, Guid guid)
    {
        _ = guid;
        return layout switch
        {
            ChunkLayout.Wave64 => key == ChunkNode.Key("riff") || key == ChunkNode.Key("list"),
            ChunkLayout.Iff => key == ChunkNode.Key("FORM") || key == ChunkNode.Key("LIST") || key == ChunkNode.Key("CAT "),
            _ => key == ChunkNode.Key("RIFF") || key == ChunkNode.Key("LIST") || key == ChunkNode.Key("RF64") || key == ChunkNode.Key("BW64"),
        };
    }

    private static long? Ds64Size(ChunkContext c, ulong key)
    {
        if (key == ChunkNode.Key("data") && c.Ds64DataSize >= 0)
        {
            return c.Ds64DataSize;
        }

        return c.Ds64Table is not null && c.Ds64Table.TryGetValue((uint)key, out long v) ? v : null;
    }

    // EBU Tech 3306 §3.1: riffSize, dataSize and sampleCount (64 bits each), a table length and the table of (chunk id, 64-bit size).
    private static void ReadDs64(ChunkContext c, ChunkNode node)
    {
        if (node.PayloadSize < 28)
        {
            c.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, node.Offset, "The ds64 chunk is shorter than its fixed fields.", "riff.ds64-short");
            return;
        }

        int length = (int)Math.Min(node.PayloadSize, 28 + (4096L * 12));
        byte[] b = new byte[length];
        if (c.Source.Read(node.Offset + node.HeaderSize, b) < 28)
        {
            return;
        }

        ulong data = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8));
        c.Ds64DataSize = data > long.MaxValue ? long.MaxValue : (long)data;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24));
        c.Ds64Table = [];
        for (int i = 0; i < count && 28 + ((i + 1) * 12) <= b.Length; i++)
        {
            int at = 28 + (i * 12);
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at + 4));
            c.Ds64Table[BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at))] = v > long.MaxValue ? long.MaxValue : (long)v;
        }
    }

    /// <summary>Recognises the layout from the first bytes; <c>null</c> when the data is none of the chunk-file layouts.</summary>
    public static ChunkLayout? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 12)
        {
            if (head.StartsWith("RIFF"u8))
            {
                return ChunkLayout.Riff;
            }

            if (head.StartsWith("RF64"u8) || head.StartsWith("BW64"u8))
            {
                return ChunkLayout.Rf64;
            }

            if (head.StartsWith("FORM"u8))
            {
                return ChunkLayout.Iff;
            }
        }

        return head.Length >= 40 && new Guid(head[..16]) == Wave64Riff ? ChunkLayout.Wave64 : null;
    }
}

/// <summary>
/// The outermost chunk of a RIFF, RF64, Wave64 or IFF file with its layout. Opening reads the first bytes only; the chunk tree below it
/// is parsed on demand, header by header.
/// </summary>
public sealed class ChunkFile
{
    private ChunkFile(ChunkContext context, ChunkNode root)
    {
        Context = context;
        Root = root;
    }

    internal ChunkContext Context { get; }

    /// <summary>The outermost chunk (<c>RIFF</c>, <c>RF64</c>, <c>BW64</c>, <c>FORM</c> or the Wave64 <c>riff</c>).</summary>
    public ChunkNode Root { get; }

    /// <summary>The byte order, size width and padding rule.</summary>
    public ChunkLayout Layout => Context.Layout;

    /// <summary>The form type: <c>WAVE</c>, <c>AIFF</c>, <c>AIFC</c>, <c>AVI </c>…</summary>
    public string FormType => Root.FormType ?? string.Empty;

    /// <summary>The chunks directly below the root, in file order.</summary>
    public IReadOnlyList<ChunkNode> Chunks => Root.Chunks;

    /// <summary>Opens the outermost chunk of a source.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="limits">Ceilings for depth and chunk count.</param>
    /// <param name="defects">Receives the deviations found, with their offsets.</param>
    /// <returns>The file, or <c>null</c> when the data is not a chunk file.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkFile? TryOpen(IMediaSource source, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        Span<byte> head = stackalloc byte[40]; // the root header and form type (12 bytes), or the Wave64 header and form GUID (40)
        int n = source.Read(0, head);
        if (ChunkWalker.Detect(head[..n]) is not { } layout)
        {
            return null;
        }

        long length = source.Length >= 0 ? source.Length : long.MaxValue;
        ChunkContext context = new(source, limits, defects, layout, length);
        long pos = 0;
        long end = length;
        if (!ChunkWalker.TryReadNext(context, ref pos, end, 1, out ChunkNode? root) || root is null || !root.IsContainer)
        {
            return null;
        }

        return new ChunkFile(context, root);
    }

    /// <summary>
    /// Reads the next chunk at <paramref name="position"/> below the root and moves <paramref name="position"/> past it. Readers that must stop
    /// early (a forward-only stream ends its header walk at the sample data) use this instead of <see cref="Chunks"/>.
    /// </summary>
    /// <param name="position">Where the next chunk header starts; start with <see cref="FirstChunkOffset"/>.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns>False at the end of the root.</returns>
    public bool TryReadNext(ref long position, out ChunkNode? chunk) =>
        ChunkWalker.TryReadNext(Context, ref position, Root.Offset + Root.HeaderSize + Root.PayloadSize, 2, out chunk);

    /// <summary>Offset of the first chunk below the root.</summary>
    public long FirstChunkOffset => Root.Offset + Root.HeaderSize + (Layout == ChunkLayout.Wave64 ? 16 : 4);
}

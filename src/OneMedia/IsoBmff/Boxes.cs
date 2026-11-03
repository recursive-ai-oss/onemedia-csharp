using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Four-character codes of the boxes this layer knows by name, and helpers to convert them.</summary>
public static class BoxTypes
{
    /// <summary>Packs four Latin-1 characters into a big-endian code.</summary>
    /// <param name="code">A four-character string.</param>
    /// <returns>The code, as it reads when the four bytes are taken big-endian.</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> is not four characters.</exception>
    public static uint Make(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 4)
        {
            throw new ArgumentException("A box type is four characters.", nameof(code));
        }

        return ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];
    }

    /// <summary>Renders a code as text, one Latin-1 character per byte (so <c>0xA9 'n' 'a' 'm'</c> reads as <c>©nam</c>).</summary>
    /// <param name="type">The code.</param>
    /// <returns>Four characters.</returns>
    public static string ToText(uint type) =>
        string.Create(4, type, static (span, t) =>
        {
            span[0] = (char)(t >> 24);
            span[1] = (char)((t >> 16) & 0xFF);
            span[2] = (char)((t >> 8) & 0xFF);
            span[3] = (char)(t & 0xFF);
        });

    /// <summary>File type box.</summary>
    public const uint Ftyp = 0x66747970;
    /// <summary>Segment type box.</summary>
    public const uint Styp = 0x73747970;
    /// <summary>Movie box.</summary>
    public const uint Moov = 0x6D6F6F76;
    /// <summary>Movie header box.</summary>
    public const uint Mvhd = 0x6D766864;
    /// <summary>Track box.</summary>
    public const uint Trak = 0x7472616B;
    /// <summary>Track header box.</summary>
    public const uint Tkhd = 0x746B6864;
    /// <summary>Edit box.</summary>
    public const uint Edts = 0x65647473;
    /// <summary>Edit list box.</summary>
    public const uint Elst = 0x656C7374;
    /// <summary>Media box.</summary>
    public const uint Mdia = 0x6D646961;
    /// <summary>Media header box.</summary>
    public const uint Mdhd = 0x6D646864;
    /// <summary>Handler reference box.</summary>
    public const uint Hdlr = 0x68646C72;
    /// <summary>Media information box.</summary>
    public const uint Minf = 0x6D696E66;
    /// <summary>Sample table box.</summary>
    public const uint Stbl = 0x7374626C;
    /// <summary>Sample description box.</summary>
    public const uint Stsd = 0x73747364;
    /// <summary>Decoding time to sample box.</summary>
    public const uint Stts = 0x73747473;
    /// <summary>Composition time to sample box.</summary>
    public const uint Ctts = 0x63747473;
    /// <summary>Sync sample box.</summary>
    public const uint Stss = 0x73747373;
    /// <summary>Sample size box.</summary>
    public const uint Stsz = 0x7374737A;
    /// <summary>Compact sample size box.</summary>
    public const uint Stz2 = 0x73747A32;
    /// <summary>Sample to chunk box.</summary>
    public const uint Stsc = 0x73747363;
    /// <summary>Chunk offset box (32-bit).</summary>
    public const uint Stco = 0x7374636F;
    /// <summary>Chunk offset box (64-bit).</summary>
    public const uint Co64 = 0x636F3634;
    /// <summary>Independent and disposable samples box.</summary>
    public const uint Sdtp = 0x73647470;
    /// <summary>Sample group description box.</summary>
    public const uint Sgpd = 0x73677064;
    /// <summary>Sample to group box.</summary>
    public const uint Sbgp = 0x73626770;
    /// <summary>Movie extends box.</summary>
    public const uint Mvex = 0x6D766578;
    /// <summary>Movie extends header box.</summary>
    public const uint Mehd = 0x6D656864;
    /// <summary>Track extends box.</summary>
    public const uint Trex = 0x74726578;
    /// <summary>Movie fragment box.</summary>
    public const uint Moof = 0x6D6F6F66;
    /// <summary>Movie fragment header box.</summary>
    public const uint Mfhd = 0x6D666864;
    /// <summary>Track fragment box.</summary>
    public const uint Traf = 0x74726166;
    /// <summary>Track fragment header box.</summary>
    public const uint Tfhd = 0x74666864;
    /// <summary>Track fragment decode time box.</summary>
    public const uint Tfdt = 0x74666474;
    /// <summary>Track fragment run box.</summary>
    public const uint Trun = 0x7472756E;
    /// <summary>Movie fragment random access box.</summary>
    public const uint Mfra = 0x6D667261;
    /// <summary>Track fragment random access box.</summary>
    public const uint Tfra = 0x74667261;
    /// <summary>Movie fragment random access offset box.</summary>
    public const uint Mfro = 0x6D66726F;
    /// <summary>Segment index box.</summary>
    public const uint Sidx = 0x73696478;
    /// <summary>Event message box.</summary>
    public const uint Emsg = 0x656D7367;
    /// <summary>Media data box.</summary>
    public const uint Mdat = 0x6D646174;
    /// <summary>Free space box.</summary>
    public const uint Free = 0x66726565;
    /// <summary>Free space box (skip).</summary>
    public const uint Skip = 0x736B6970;
    /// <summary>Vendor extension box with a 16-byte user type.</summary>
    public const uint Uuid = 0x75756964;
    /// <summary>User data box.</summary>
    public const uint Udta = 0x75647461;
    /// <summary>Metadata box.</summary>
    public const uint Meta = 0x6D657461;
    /// <summary>iTunes metadata item list.</summary>
    public const uint Ilst = 0x696C7374;
    /// <summary>Data information box.</summary>
    public const uint Dinf = 0x64696E66;
    /// <summary>Data reference box.</summary>
    public const uint Dref = 0x64726566;

    /// <summary>QuickTime track aperture mode dimensions (<c>tapt</c>): holds <c>clef</c>, <c>prof</c> and <c>enof</c>.</summary>
    public static readonly uint Tapt = Make("tapt");

    /// <summary>QuickTime generic media information (<c>gmhd</c>): holds <c>gmin</c> and the media-specific boxes.</summary>
    public static readonly uint Gmhd = Make("gmhd");

    /// <summary>Hint track information (<c>hinf</c>) and hint track user data (<c>hnti</c>, which holds <c>sdp </c>).</summary>
    public static readonly uint Hinf = Make("hinf");

    /// <summary>See <see cref="Hinf"/>.</summary>
    public static readonly uint Hnti = Make("hnti");
    /// <summary>Track reference box.</summary>
    public const uint Tref = 0x74726566;
    /// <summary>Protection scheme information box.</summary>
    public const uint Sinf = 0x73696E66;
    /// <summary>Scheme information box.</summary>
    public const uint Schi = 0x73636869;
    /// <summary>Pixel aspect ratio box.</summary>
    public const uint Pasp = 0x70617370;
    /// <summary><c>btrt</c>: BitRateBox of a sample entry (ISO/IEC 14496-12 §8.5.2).</summary>
    public const uint Btrt = 0x62747274;
    /// <summary>Elementary stream descriptor box.</summary>
    public const uint Esds = 0x65736473;
    /// <summary>Wave atom of QuickTime audio sample entries.</summary>
    public const uint Wave = 0x77617665;
}

/// <summary>
/// The header of one box: where it starts, how long the header is, and how many payload bytes follow.
/// ISO/IEC 14496-12 §4.2.
/// </summary>
/// <param name="Type">The box type as a big-endian four-character code.</param>
/// <param name="Offset">Absolute offset of the first header byte.</param>
/// <param name="HeaderSize">8, 16 (64-bit size), 24 (<c>uuid</c>) or 32 (<c>uuid</c> with a 64-bit size).</param>
/// <param name="PayloadSize">Bytes after the header, clamped to the enclosing range when the declared size overran it.</param>
/// <param name="IsSizeInferred">True when the size field was 0 ("to the end of the enclosing range").</param>
/// <param name="DeclaredSize">The size as written (0 for the to-end form); differs from <c>HeaderSize + PayloadSize</c> when clamped.</param>
/// <param name="UserType">For <c>uuid</c> boxes, the 16-byte extended type, else <c>null</c>.</param>
public readonly record struct BoxHeader(uint Type, long Offset, int HeaderSize, long PayloadSize, bool IsSizeInferred, ulong DeclaredSize, Guid? UserType = null)
{
    /// <summary>Absolute offset one past the last byte of the box.</summary>
    public long End => Offset + HeaderSize + PayloadSize;

    /// <summary>Absolute offset of the first payload byte.</summary>
    public long PayloadOffset => Offset + HeaderSize;

    /// <summary>True when the declared size reached past the enclosing range and was clamped to it.</summary>
    public bool IsClamped => !IsSizeInferred && DeclaredSize != (ulong)(HeaderSize + PayloadSize);
}

/// <summary>Reads box headers (ISO/IEC 14496-12 §4.2) from bytes or a source, reporting deviations as defects.</summary>
public static class BoxReader
{
    /// <summary>The most bytes a header can need: size, type, 64-bit size and a <c>uuid</c> user type.</summary>
    public const int MaxHeaderBytes = 32;

    /// <summary>
    /// Reads the header of the box that starts at <paramref name="offset"/> and must end by <paramref name="rangeEnd"/>.
    /// </summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="offset">Where the box starts.</param>
    /// <param name="rangeEnd">End of the enclosing range (the parent's end, or the file length at top level).</param>
    /// <param name="isTopLevel">True at file level, where an overrunning size is a truncated file rather than a bad nesting.</param>
    /// <param name="defects">Receives what is wrong; may be <c>null</c> to ignore.</param>
    /// <param name="header">The header, when the result is true.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when no valid box starts here (the range is exhausted or the size field is impossible); the cause is in <paramref name="defects"/>.</returns>
    public static bool TryRead(IMediaSource source, long offset, long rangeEnd, bool isTopLevel, DefectLog? defects, out BoxHeader header, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Span<byte> buffer = stackalloc byte[MaxHeaderBytes]; // fixed small buffer: a header is at most 32 bytes
        long available = Math.Min(rangeEnd - offset, MaxHeaderBytes);
        if (available <= 0)
        {
            header = default;
            return false;
        }

        int n = source.Read(offset, buffer[..(int)available], cancellationToken);
        return TryParse(buffer[..n], offset, rangeEnd, isTopLevel, defects, out header);
    }

    /// <summary>Parses a header from bytes already read; the same rules as <see cref="TryRead"/>.</summary>
    /// <param name="bytes">The bytes at <paramref name="offset"/>, up to <see cref="MaxHeaderBytes"/> (fewer if the range ends).</param>
    /// <param name="offset">Absolute offset of <c>bytes[0]</c>.</param>
    /// <param name="rangeEnd">End of the enclosing range.</param>
    /// <param name="isTopLevel">True at file level.</param>
    /// <param name="defects">Receives what is wrong; may be <c>null</c>.</param>
    /// <param name="header">The header, when the result is true.</param>
    /// <returns>False when no valid box starts here.</returns>
    public static bool TryParse(ReadOnlySpan<byte> bytes, long offset, long rangeEnd, bool isTopLevel, DefectLog? defects, out BoxHeader header)
    {
        header = default;
        long remaining = rangeEnd - offset;
        if (remaining <= 0)
        {
            return false;
        }

        if (bytes.Length < 8 || remaining < 8)
        {
            // QuickTime ends a user-data list with a 32-bit zero; some writers pad with a few zero bytes.
            bool zeros = bytes.Length >= Math.Min(remaining, 4) && !bytes[..(int)Math.Min(bytes.Length, remaining)].ContainsAnyExcept((byte)0);
            defects?.Add(zeros ? DefectKind.UnaccountedBytes : DefectKind.Truncated, zeros ? DefectSeverity.Info : DefectSeverity.Warning, offset,
                zeros ? "Zero padding or a QuickTime 32-bit terminator ends the box list." : "Fewer than 8 bytes remain, too few for a box header.");
            return false;
        }

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        int headerSize = 8;
        ulong declared = size32;
        bool inferred = false;
        if (size32 == 1)
        {
            if (bytes.Length < 16 || remaining < 16)
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The 64-bit box size is cut off.");
                return false;
            }

            declared = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            // §4.2: size 0 means the box extends to the end of the file (here: of the enclosing range).
            inferred = true;
        }

        Guid? userType = null;
        if (type == BoxTypes.Uuid)
        {
            if (bytes.Length < headerSize + 16 || remaining < headerSize + 16)
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The uuid box's user type is cut off.");
                return false;
            }

            userType = new Guid(bytes.Slice(headerSize, 16), bigEndian: true);
            headerSize += 16;
        }

        long total;
        if (inferred)
        {
            total = remaining;
        }
        else
        {
            if (declared < (ulong)headerSize)
            {
                defects?.Add(DefectKind.InvalidSize, DefectSeverity.Error, offset,
                    $"Box '{BoxTypes.ToText(type)}' declares size {declared}, less than its {headerSize}-byte header.", "isobmff.size-below-header");
                return false;
            }

            if (declared > (ulong)long.MaxValue)
            {
                defects?.Add(DefectKind.InvalidSize, DefectSeverity.Error, offset, $"Box '{BoxTypes.ToText(type)}' declares an impossible 64-bit size.", "isobmff.size-huge");
                return false;
            }

            total = (long)declared;
            if (total > remaining)
            {
                defects?.Add(isTopLevel ? DefectKind.SizeExceedsFile : DefectKind.SizeExceedsParent, DefectSeverity.Error, offset,
                    $"Box '{BoxTypes.ToText(type)}' declares {declared} bytes but only {remaining} remain.", isTopLevel ? "isobmff.truncated" : "isobmff.size-exceeds-parent");
                total = remaining;
            }
        }

        header = new BoxHeader(type, offset, headerSize, total - headerSize, inferred, declared, userType);
        return true;
    }

    /// <summary>
    /// Tells whether <paramref name="type"/>, found inside a box of type <paramref name="parent"/>, holds child boxes.
    /// The set is the containers of ISO/IEC 14496-12 plus the QuickTime and iTunes ones this layer walks.
    /// </summary>
    /// <param name="type">The box type.</param>
    /// <param name="parent">The parent's type, or 0 at top level.</param>
    /// <returns>True when the payload is a list of boxes (after any fixed prefix, see <see cref="PayloadPrefix"/>).</returns>
    public static bool IsContainer(uint type, uint parent) => type switch
    {
        BoxTypes.Moov or BoxTypes.Trak or BoxTypes.Mdia or BoxTypes.Minf or BoxTypes.Stbl or BoxTypes.Edts or BoxTypes.Dinf
            or BoxTypes.Mvex or BoxTypes.Moof or BoxTypes.Traf or BoxTypes.Mfra or BoxTypes.Udta or BoxTypes.Tref or BoxTypes.Sinf
            or BoxTypes.Schi or BoxTypes.Meta or BoxTypes.Ilst or BoxTypes.Stsd or BoxTypes.Dref => true,
        _ => parent == BoxTypes.Ilst // each iTunes item box holds mean/name/data children
            || type == BoxTypes.Tapt || type == BoxTypes.Gmhd || type == BoxTypes.Hinf || type == BoxTypes.Hnti,
    };

    /// <summary>
    /// Bytes between the box header and its first child: 4 for a full box with children after the version and flags (<c>meta</c> in
    /// ISO files), 8 for <c>stsd</c>/<c>dref</c> (version, flags and an entry count), else 0. <c>meta</c> in a QuickTime file has no full-box
    /// header; the caller passes what it saw.
    /// </summary>
    /// <param name="type">The box type.</param>
    /// <param name="quickTimeMeta">For <c>meta</c>: true when the box has no version/flags field.</param>
    /// <returns>The prefix length.</returns>
    public static int PayloadPrefix(uint type, bool quickTimeMeta = false) => type switch
    {
        BoxTypes.Stsd or BoxTypes.Dref => 8,
        BoxTypes.Meta => quickTimeMeta ? 0 : 4,
        _ => 0,
    };

    /// <summary>Decides whether a <c>meta</c> payload is QuickTime style (starts with a <c>hdlr</c> box) rather than ISO style (version and flags first).</summary>
    /// <param name="payloadHead">At least the first 8 payload bytes.</param>
    /// <returns>True for QuickTime style.</returns>
    public static bool IsQuickTimeMeta(ReadOnlySpan<byte> payloadHead) =>
        payloadHead.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(payloadHead[4..]) == BoxTypes.Hdlr;

    internal static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}

/// <summary>
/// One box in a lazily built tree. Holds the header and, once asked, its child boxes as further nodes; it never holds payload bytes.
/// Not thread-safe.
/// </summary>
public sealed class BoxNode : IStructureNode
{
    private readonly BoxContext _context;
    private readonly int _depth;
    private List<BoxNode>? _children;
    private readonly int _prefix;

    internal BoxNode(BoxContext context, BoxHeader header, BoxNode? parent, int depth, int prefix)
    {
        _context = context;
        Header = header;
        Parent = parent;
        _depth = depth;
        _prefix = prefix;
    }

    /// <summary>The parsed header.</summary>
    public BoxHeader Header { get; }

    /// <summary>The enclosing box, or <c>null</c> for a top-level box.</summary>
    public BoxNode? Parent { get; }

    /// <summary>The box type as text, e.g. <c>moov</c>.</summary>
    public string TypeName => BoxTypes.ToText(Header.Type);

    /// <summary>The box type as a big-endian code.</summary>
    public uint Type => Header.Type;

    /// <inheritdoc />
    public ulong TypeKey => Header.Type;

    /// <inheritdoc />
    public long Offset => Header.Offset;

    /// <inheritdoc />
    public int HeaderSize => Header.HeaderSize;

    /// <inheritdoc />
    public long PayloadSize => Header.PayloadSize;

    /// <inheritdoc />
    public bool IsSizeInferred => Header.IsSizeInferred;

    /// <summary>Where the child boxes start (after the header and any version/flags/count prefix).</summary>
    public long ChildrenOffset => Header.PayloadOffset + _prefix;

    /// <summary>
    /// The child boxes, read from the source on first use. Empty for leaf boxes. A damaged list ends where it breaks, with the cause
    /// recorded in the defect log; exceeding a limit ends it with a <see cref="DefectKind.LimitReached"/> defect.
    /// </summary>
    public IReadOnlyList<BoxNode> Children => _children ??= ReadChildren();

    IReadOnlyList<IStructureNode> IStructureNode.Children => Children;

    /// <summary>Finds the first child of a type.</summary>
    /// <param name="type">The type to look for.</param>
    /// <returns>The child, or <c>null</c>.</returns>
    public BoxNode? Find(uint type)
    {
        foreach (BoxNode child in EnumerateChildren())
        {
            if (child.Type == type)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Reads the payload into a new array, after checking the allocation limit.</summary>
    /// <param name="maxBytes">Reads at most this many payload bytes.</param>
    /// <returns>The bytes; shorter than the payload when the source ends early (a defect is recorded).</returns>
    /// <exception cref="MediaLimitExceededException">The payload is larger than the allocation limit.</exception>
    public byte[] ReadPayload(long maxBytes = long.MaxValue)
    {
        long count = Math.Min(PayloadSize, maxBytes);
        _context.Limits.CheckAllocation(count, Offset);
        byte[] bytes = new byte[count];
        int n = _context.Source.Read(Header.PayloadOffset, bytes);
        if (n < count)
        {
            _context.Defects.Add(DefectKind.Truncated, DefectSeverity.Error, Header.PayloadOffset + n, $"The source ends inside box '{TypeName}'.");
            Array.Resize(ref bytes, n);
        }

        return bytes;
    }

    private List<BoxNode> ReadChildren()
    {
        List<BoxNode> list = [];
        long pos = ChildrenOffset;
        while (TryReadNextChild(ref pos, out BoxNode? child))
        {
            list.Add(child);
        }

        return list;
    }

    /// <summary>
    /// Walks the child boxes without keeping them, so a box with millions of children costs no memory. Use it for one pass over a container;
    /// <see cref="Children"/> caches the list.
    /// </summary>
    /// <returns>The children in file order, with the same defect reporting as <see cref="Children"/>.</returns>
    public IEnumerable<BoxNode> EnumerateChildren()
    {
        if (_children is not null)
        {
            foreach (BoxNode cached in _children)
            {
                yield return cached;
            }

            yield break;
        }

        long pos = ChildrenOffset;
        while (TryReadNextChild(ref pos, out BoxNode? child))
        {
            yield return child;
        }
    }

    private bool TryReadNextChild(ref long pos, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BoxNode? child)
    {
        child = null;
        if (!BoxReader.IsContainer(Header.Type, Parent?.Header.Type ?? 0) || pos >= Header.End)
        {
            return false;
        }

        try
        {
            if (pos == ChildrenOffset)
            {
                _context.Limits.CheckDepth(_depth + 1, Offset);
            }

            if (!BoxReader.TryRead(_context.Source, pos, Header.End, isTopLevel: false, _context.Defects, out BoxHeader h))
            {
                return false;
            }

            _context.Limits.CheckElementCount(++_context.Count, h.Offset);
            child = new BoxNode(_context, h, this, _depth + 1, ChildPrefix(h));
            pos = h.End;
            return true;
        }
        catch (MediaLimitExceededException e)
        {
            _context.Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "isobmff.limit");
            return false;
        }
    }

    private int ChildPrefix(BoxHeader child) => ComputePrefix(_context, child);

    private static int ComputePrefix(BoxContext context, BoxHeader child)
    {
        if (child.Type == BoxTypes.Meta)
        {
            Span<byte> head = stackalloc byte[8]; // a meta payload's first 8 bytes tell the ISO and QuickTime layouts apart
            int n = context.Source.Read(child.PayloadOffset, head);
            return BoxReader.PayloadPrefix(child.Type, quickTimeMeta: n == 8 && BoxReader.IsQuickTimeMeta(head));
        }

        return BoxReader.PayloadPrefix(child.Type);
    }

    internal static BoxNode CreateRoot(BoxContext context, long length) =>
        new(context, new BoxHeader(0, 0, 0, length, false, (ulong)length), null, 0, 0);

    internal BoxNode CreateTopLevel(BoxHeader h) => new(_context, h, null, 1, ComputePrefix(_context, h));
}

internal sealed class BoxContext(IMediaSource source, MediaLimits limits, DefectLog defects)
{
    public IMediaSource Source { get; } = source;

    public MediaLimits Limits { get; } = limits;

    public DefectLog Defects { get; } = defects;

    public long Count;
}

/// <summary>
/// The top level of a file as a lazily read list of boxes. Reading the list touches one 16-byte header per top-level box, so a file with
/// <c>moov</c> at the end costs a few header reads, not a scan of <c>mdat</c>.
/// </summary>
public sealed class BoxTree
{
    private readonly BoxContext _context;
    private readonly BoxNode _root;
    private List<BoxNode>? _topLevel;

    /// <summary>Creates a tree over a source.</summary>
    /// <param name="source">The source; it must stay open while the tree is used.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <param name="defects">Receives defects; a new log when <c>null</c>.</param>
    public BoxTree(IMediaSource source, MediaLimits? limits = null, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= MediaLimits.Default;
        Defects = defects ?? new DefectLog(limits);
        _context = new BoxContext(source, limits, Defects);
        Length = source.Length;
        _root = BoxNode.CreateRoot(_context, Length);
    }

    /// <summary>The defects found while reading boxes.</summary>
    public DefectLog Defects { get; }

    /// <summary>Length of the source in bytes.</summary>
    public long Length { get; }

    /// <summary>The whole file as one node whose children are the top-level boxes.</summary>
    public IStructureNode Root => _root;

    /// <summary>The top-level boxes, read on first use.</summary>
    public IReadOnlyList<BoxNode> TopLevel => _topLevel ??= ReadTopLevel();

    private List<BoxNode> ReadTopLevel()
    {
        List<BoxNode> list = [];
        long pos = 0;
        try
        {
            while (pos < Length && BoxReader.TryRead(_context.Source, pos, Length, isTopLevel: true, Defects, out BoxHeader h))
            {
                _context.Limits.CheckElementCount(++_context.Count, h.Offset);
                list.Add(_root.CreateTopLevel(h));
                pos = h.End;
            }
        }
        catch (MediaLimitExceededException e)
        {
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "isobmff.limit");
        }

        return list;
    }

    internal BoxNode CreateNode(BoxHeader header) => _root.CreateTopLevel(header);

    /// <summary>Finds the first top-level box of a type.</summary>
    /// <param name="type">The type to look for.</param>
    /// <returns>The box, or <c>null</c>.</returns>
    public BoxNode? Find(uint type)
    {
        foreach (BoxNode node in TopLevel)
        {
            if (node.Type == type)
            {
                return node;
            }
        }

        return null;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// One chunk of a RIFF file: a data chunk (<c>avih</c>, <c>strf</c>, <c>00dc</c>, <c>JUNK</c>…) or a list (<c>RIFF</c>, <c>LIST</c>).
/// The node holds the chunk's position and size, never its content; a list's children are read from the source the first time
/// <see cref="Children"/> is used, so a multi-gigabyte <c>movi</c> list costs nothing until someone asks for its chunks.
/// </summary>
/// <remarks>
/// Microsoft's RIFF file reference: a chunk is a four-character code, a little-endian 32-bit size that does not count the 8-byte
/// header, and the data, padded with one byte to an even length. <c>RIFF</c> and <c>LIST</c> chunks begin their data with a
/// four-character form or list type.
/// </remarks>
public sealed class RiffChunk : IStructureNode
{
    private const int MaxChildrenPerList = 1 << 22;

    private readonly IMediaSource _source;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly int _depth;
    private IReadOnlyList<IStructureNode>? _children;

    private RiffChunk(IMediaSource source, MediaLimits limits, DefectLog defects, int depth, long offset, uint id, string? listType, long declaredSize, long payloadSize, bool inferred, long parentEnd)
    {
        _source = source;
        _limits = limits;
        _defects = defects;
        _depth = depth;
        Offset = offset;
        Id = id;
        ListType = listType;
        DeclaredSize = declaredSize;
        PayloadSize = payloadSize;
        IsSizeInferred = inferred;
        ParentEnd = parentEnd;
    }

    /// <summary>The chunk identifier as the four bytes read little-endian, which is also <see cref="TypeKey"/>.</summary>
    public uint Id { get; }

    /// <summary>The four-character code as text (Latin-1), for example <c>"strh"</c>.</summary>
    public string FourCc => FourCcToString(Id);

    /// <summary>The form or list type (<c>"AVI "</c>, <c>"hdrl"</c>, <c>"movi"</c>) for a <c>RIFF</c> or <c>LIST</c> chunk; otherwise <c>null</c>.</summary>
    public string? ListType { get; }

    /// <summary>True for <c>RIFF</c> and <c>LIST</c> chunks, which contain other chunks.</summary>
    public bool IsList => ListType is not null;

    /// <summary>The size field as stored, which may be wrong (zero or 0xFFFFFFFF in captures, or beyond the end of the file).</summary>
    public long DeclaredSize { get; }

    /// <summary>The offset one past the data of the parent list (or the end of the source for a top-level chunk); children never extend past it.</summary>
    public long ParentEnd { get; }

    /// <inheritdoc />
    public ulong TypeKey => Id;

    /// <inheritdoc />
    public long Offset { get; }

    /// <inheritdoc />
    /// <remarks>8 for a data chunk; 12 for a list, whose list type is counted as header so that the payload is the child chunks.</remarks>
    public int HeaderSize => IsList ? 12 : 8;

    /// <summary>The whole chunk from its header to the end of its data (the pad byte is not included).</summary>
    public ByteRange Range => new(Offset, HeaderSize + PayloadSize);

    /// <inheritdoc />
    /// <remarks>The data size after clamping to the parent and the source; for a list this excludes the four-byte list type.</remarks>
    public long PayloadSize { get; }

    /// <inheritdoc />
    /// <remarks>True when the stored size was unusable and the chunk was taken to run to the end of its parent.</remarks>
    public bool IsSizeInferred { get; }

    /// <summary>The offset of the next sibling: the data end rounded up to an even offset (RIFF pads odd-sized chunks).</summary>
    public long NextOffset => Offset + HeaderSize + PayloadSize + ((PayloadSize + (IsList ? 4 : 0)) & 1);

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children => _children ??= IsList ? ReadChildren() : [];

    /// <summary>Formats a chunk identifier for messages.</summary>
    public static string FourCcToString(uint id)
    {
        Span<char> c = stackalloc char[4]; // four characters, fixed
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(id >> (8 * i));
            c[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(c);
    }

    /// <summary>Packs a four-character code into the little-endian value found in <see cref="Id"/>.</summary>
    /// <param name="code">Exactly four ASCII characters.</param>
    public static uint Make(ReadOnlySpan<char> code) => code.Length == 4 ? (uint)(code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24)) : throw new ArgumentException("A four-character code has four characters.", nameof(code));

    /// <summary>Well-known identifiers.</summary>
    public static class Ids
    {
        /// <summary><c>RIFF</c>.</summary>
        public static readonly uint Riff = Make("RIFF");

        /// <summary><c>LIST</c>.</summary>
        public static readonly uint List = Make("LIST");

        /// <summary><c>JUNK</c>, padding.</summary>
        public static readonly uint Junk = Make("JUNK");
    }

    /// <summary>
    /// Reads the chunk header at <paramref name="offset"/>. Returns <c>null</c> when fewer than eight bytes remain before
    /// <paramref name="end"/>. A size that runs past <paramref name="end"/> is clamped and reported as a defect.
    /// </summary>
    /// <param name="source">The file.</param>
    /// <param name="offset">Where the chunk header starts.</param>
    /// <param name="end">The end of the enclosing list's data, or the length of the source at top level.</param>
    /// <param name="limits">Limits for nesting depth.</param>
    /// <param name="defects">Receives deviations.</param>
    /// <param name="depth">Nesting depth of this chunk (1 at top level).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The chunk, or <c>null</c> at the end of the data.</returns>
    public static RiffChunk? Read(IMediaSource source, long offset, long end, MediaLimits limits, DefectLog defects, int depth = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        limits.CheckDepth(depth, offset);
        if (end - offset < 8)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[12]; // chunk header plus list type, fixed
        int wanted = (int)Math.Min(12, end - offset);
        int got = source.Read(offset, header[..wanted], cancellationToken);
        if (got < 8)
        {
            if (got > 0)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "The file ends inside a chunk header.", "riff.truncated-header");
            }

            return null;
        }

        uint id = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        bool list = (id == Ids.Riff || id == Ids.List) && size >= 4;
        string? listType = null;
        if (list)
        {
            if (got < 12)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "The file ends inside a list header.", "riff.truncated-header");
                return null;
            }

            listType = Encoding.Latin1.GetString(header.Slice(8, 4));
        }

        long available = end - (offset + 8);
        long dataSize = size;
        bool inferred = false;
        if (dataSize > available || (id == Ids.Riff && size == 0xFFFFFFFF))
        {
            // Captures and interrupted writers leave 0 or 0xFFFFFFFF here; either way the data runs to the end of what is there.
            inferred = size == 0xFFFFFFFF || size == 0;
            defects.Add(
                DefectKind.SizeExceedsParent,
                inferred ? DefectSeverity.Info : DefectSeverity.Warning,
                offset,
                $"The {FourCcToString(id)} chunk states {size} bytes but only {available} remain; it is taken to end there.",
                "riff.size-past-end");
            dataSize = available;
        }
        else if (size == 0 && id == Ids.Riff)
        {
            inferred = true;
            dataSize = available;
        }

        long payload = list ? dataSize - 4 : dataSize;
        return new RiffChunk(source, limits, defects, depth, offset, id, listType, size, payload, inferred, end);
    }

    /// <summary>Returns the first child with the given identifier (and list type, when given), or <c>null</c>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="listType">For lists, the required list type.</param>
    public RiffChunk? Find(uint id, string? listType = null)
    {
        foreach (IStructureNode n in Children)
        {
            if (n is RiffChunk c && c.Id == id && (listType is null || c.ListType == listType))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Reads the chunk's data, up to <paramref name="max"/> bytes.</summary>
    /// <param name="max">Largest number of bytes to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The data; shorter than the chunk when the source ends or <paramref name="max"/> applies.</returns>
    public byte[] ReadData(int max, CancellationToken cancellationToken = default)
    {
        long n = Math.Min(Math.Max(PayloadSize, 0), max);
        if (n <= 0)
        {
            return [];
        }

        _limits.CheckAllocation(n, Offset);
        byte[] data = new byte[n];
        int got = _source.Read(Offset + HeaderSize, data, cancellationToken);
        if (got < n)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, Offset + HeaderSize + got, $"The data of {FourCc} ends early.", "riff.truncated-data");
            Array.Resize(ref data, got);
        }

        return data;
    }

    private List<IStructureNode> ReadChildren()
    {
        List<IStructureNode> children = [];
        long pos = Offset + HeaderSize;
        long end = pos + PayloadSize;
        while (pos < end && children.Count < MaxChildrenPerList)
        {
            RiffChunk? child = Read(_source, pos, end, _limits, _defects, _depth + 1);
            if (child is null)
            {
                if (end - pos > 0 && end - pos < 8)
                {
                    _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, pos, $"{end - pos} stray bytes at the end of the {FourCc} list.", "riff.trailing-bytes");
                }

                break;
            }

            _limits.CheckElementCount(children.Count + 1, pos);
            children.Add(child);
            if (child.NextOffset <= pos)
            {
                break;
            }

            pos = child.NextOffset;
        }

        return children;
    }
}

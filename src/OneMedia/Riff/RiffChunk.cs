using System.Buffers.Binary;
using System.Linq;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// One chunk of an AVI file, seen the way the AVI reader wants it: a four-character code as a little-endian number, a list type, and a
/// payload that for a list excludes the four-byte list type. The chunk tree itself is the shared <see cref="ChunkNode"/> walker that WAVE
/// and AIFF use, so a size that is wrong, past the end or all ones is handled and reported in one place.
/// </summary>
/// <remarks>
/// Microsoft's RIFF file reference: a chunk is a four-character code, a little-endian 32-bit size that does not count the 8-byte
/// header, and the data, padded with one byte to an even length. <c>RIFF</c> and <c>LIST</c> chunks begin their data with a
/// four-character form or list type.
/// </remarks>
public sealed class RiffChunk : IStructureNode
{
    private readonly ChunkNode _node;
    private IReadOnlyList<IStructureNode>? _children;

    private RiffChunk(ChunkNode node)
    {
        _node = node;
        Id = BinaryPrimitives.ReverseEndianness((uint)node.TypeKey);
    }

    /// <summary>The chunk identifier as the four bytes read little-endian, which is also <see cref="TypeKey"/>.</summary>
    public uint Id { get; }

    /// <summary>The four-character code as text (Latin-1), for example <c>"strh"</c>.</summary>
    public string FourCc => _node.Id;

    /// <summary>The form or list type (<c>"AVI "</c>, <c>"hdrl"</c>, <c>"movi"</c>) for a <c>RIFF</c> or <c>LIST</c> chunk; otherwise <c>null</c>.</summary>
    public string? ListType => _node.IsContainer ? _node.FormType : null;

    /// <summary>True for <c>RIFF</c> and <c>LIST</c> chunks, which contain other chunks.</summary>
    public bool IsList => _node.IsContainer;

    /// <inheritdoc />
    public ulong TypeKey => Id;

    /// <inheritdoc />
    public long Offset => _node.Offset;

    /// <inheritdoc />
    /// <remarks>8 for a data chunk; 12 for a list, whose list type is counted as header so that the payload is the child chunks.</remarks>
    public int HeaderSize => IsList ? 12 : 8;

    /// <summary>The whole chunk from its header to the end of its data (the pad byte is not included).</summary>
    public ByteRange Range => _node.Range;

    /// <inheritdoc />
    /// <remarks>The data size after clamping to the parent and the source; for a list this excludes the four-byte list type.</remarks>
    public long PayloadSize => IsList ? _node.PayloadSize - 4 : _node.PayloadSize;

    /// <inheritdoc />
    /// <remarks>True when the stored size was unusable and the chunk was taken to run to the end of its parent.</remarks>
    public bool IsSizeInferred => _node.IsSizeInferred;

    /// <summary>The offset of the next sibling: the data end rounded up to an even offset (RIFF pads odd-sized chunks).</summary>
    public long NextOffset => _node.NextOffset;

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children => _children ??= [.. _node.Chunks.Select(c => new RiffChunk(c))];

    /// <summary>The chunk in the shared chunk-file tree, for code that reads RIFF, WAVE and AVI the same way.</summary>
    public ChunkNode Node => _node;

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
    /// <param name="limits">Limits for nesting depth and chunk count.</param>
    /// <param name="defects">Receives deviations.</param>
    /// <param name="depth">Nesting depth of this chunk (1 at top level).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The chunk, or <c>null</c> at the end of the data.</returns>
    public static RiffChunk? Read(IMediaSource source, long offset, long end, MediaLimits limits, DefectLog defects, int depth = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        cancellationToken.ThrowIfCancellationRequested();
        limits.CheckDepth(depth, offset);
        ChunkContext context = new(source, limits, defects, ChunkLayout.Riff, source.Length >= 0 ? source.Length : long.MaxValue);
        long pos = offset;
        return ChunkWalker.TryReadNext(context, ref pos, end, depth, out ChunkNode? node) && node is not null ? new RiffChunk(node) : null;
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
    public byte[] ReadData(int max, CancellationToken cancellationToken = default) => _node.ReadData(max, cancellationToken);
}

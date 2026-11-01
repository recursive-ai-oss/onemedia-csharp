using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>One entry of an AVI index, whichever index it came from.</summary>
/// <param name="Offset">Absolute file offset of the chunk header the entry describes.</param>
/// <param name="Size">Size of the chunk data as the index states it.</param>
/// <param name="IsKeyframe">Whether the index marks the chunk as a key frame.</param>
/// <param name="Stream">Stream number from the chunk identifier (idx1), or the stream whose index this is.</param>
internal readonly record struct AviIndexEntry(long Offset, uint Size, bool IsKeyframe, int Stream);

/// <summary>Forward cursor over the entries of an index, in file order.</summary>
internal abstract class AviIndexCursor
{
    private AviIndexEntry _current;
    private bool _has;

    /// <summary>
    /// Moves to the first entry whose chunk header is at or after <paramref name="offset"/>; false when none is left. Entries passed over
    /// that state a size of 0 are counted per stream in <paramref name="emptyBefore"/>: writers list dropped frames in the index without
    /// writing a chunk for them, and those frames still take their place on the stream's clock.
    /// </summary>
    public bool Seek(long offset, out AviIndexEntry entry, int[] emptyBefore)
    {
        while (true)
        {
            if (!_has && !TryReadNext(out _current))
            {
                entry = default;
                return false;
            }

            _has = true;
            if (_current.Offset >= offset)
            {
                entry = _current;
                return true;
            }

            if (_current.Size == 0 && (uint)_current.Stream < (uint)emptyBefore.Length)
            {
                emptyBefore[_current.Stream]++;
                HasEmpty = true;
            }

            _has = false;
        }
    }

    /// <summary>True when a <see cref="Seek"/> since the last <see cref="ClearEmpty"/> passed over entries of size 0.</summary>
    public bool HasEmpty { get; private set; }

    /// <summary>Forgets the empty entries counted so far.</summary>
    public void ClearEmpty() => HasEmpty = false;

    /// <summary>Moves past the entry the last <see cref="Seek"/> returned, once its chunk has been read.</summary>
    public void Consume() => _has = false;

    /// <summary>Starts over at the first entry.</summary>
    public void Reset()
    {
        _has = false;
        ResetCore();
    }

    /// <summary>Reads the next entry of the underlying index.</summary>
    protected abstract bool TryReadNext(out AviIndexEntry entry);

    /// <summary>Rewinds the underlying index.</summary>
    protected abstract void ResetCore();
}

/// <summary>
/// Reads the legacy <c>idx1</c> index (Microsoft AVI RIFF file reference, <c>AVIOLDINDEX</c>): 16-byte entries of chunk
/// identifier, flags, offset and size. The offset is relative to the <c>movi</c> list type in conforming files; some writers
/// store absolute file offsets, so the base is decided by checking where the first entries point.
/// </summary>
internal sealed class Idx1Cursor : AviIndexCursor
{
    private const int BlockEntries = 1024;
    private const uint KeyframeFlag = 0x10; // AVIIF_KEYFRAME

    private readonly IMediaSource _source;
    private readonly long _dataOffset;
    private readonly long _count;
    private readonly long _base;
    private readonly int _streamFilter;
    private readonly byte[] _block = new byte[BlockEntries * 16];
    private long _next;
    private long _blockFirst = -1;
    private int _blockLength;

    private Idx1Cursor(IMediaSource source, long dataOffset, long count, long baseOffset, int streamFilter)
    {
        _source = source;
        _dataOffset = dataOffset;
        _count = count;
        _base = baseOffset;
        _streamFilter = streamFilter;
    }

    /// <summary>Number of entries in the index.</summary>
    public long Count => _count;

    /// <summary>The offset base that was chosen: added to each entry's offset to get a chunk header position.</summary>
    public long Base => _base;

    /// <summary>Opens the index; null when its offsets match no known base (it is then not used).</summary>
    /// <param name="source">The file.</param>
    /// <param name="data">The <c>idx1</c> chunk data.</param>
    /// <param name="moviListOffset">Offset of the <c>LIST</c> header of the first <c>movi</c> list.</param>
    /// <param name="defects">Receives a defect when the offsets cannot be matched.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Idx1Cursor? Open(IMediaSource source, ByteRange data, long moviListOffset, DefectLog defects, CancellationToken cancellationToken)
    {
        long count = data.Length / 16;
        if (count == 0)
        {
            return null;
        }

        // The conforming base is the 'movi' list type, 8 bytes into the LIST chunk; absolute offsets and offsets from the LIST header also occur.
        ReadOnlySpan<long> candidates = [moviListOffset + 8, 0, moviListOffset];
        Span<byte> probe = stackalloc byte[16 * 4]; // first four entries, fixed
        int got = source.Read(data.Offset, probe[..(int)Math.Min(probe.Length, count * 16)], cancellationToken);
        int entries = got / 16;
        Span<byte> id = stackalloc byte[4]; // chunk identifier check, fixed
        foreach (long b in candidates)
        {
            bool ok = entries > 0;
            for (int i = 0; i < entries && ok; i++)
            {
                long pos = b + BinaryPrimitives.ReadUInt32LittleEndian(probe[((i * 16) + 8)..]);
                // An entry named 'rec ' points at a LIST header: the interleave group that holds the chunks listed after it.
                ReadOnlySpan<byte> named = probe.Slice(i * 16, 4);
                ok = source.Read(pos, id, cancellationToken) == 4 && (id.SequenceEqual(named) || (named.SequenceEqual("rec "u8) && id.SequenceEqual("LIST"u8)));
            }

            if (ok)
            {
                return new Idx1Cursor(source, data.Offset, count, b, -1);
            }
        }

        defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, data.Offset, "The idx1 entries do not point at the chunks they name; the index is not used.", "avi.idx1-offsets");
        return null;
    }

    /// <summary>A second cursor over the same index that returns only the entries of one stream.</summary>
    /// <param name="stream">The stream number.</param>
    public Idx1Cursor ForStream(int stream) => new(_source, _dataOffset, _count, _base, stream);

    /// <inheritdoc />
    protected override void ResetCore()
    {
        _next = 0;
        _blockFirst = -1;
        _blockLength = 0;
    }

    /// <inheritdoc />
    protected override bool TryReadNext(out AviIndexEntry entry)
    {
        while (_next < _count)
        {
            long i = _next++;
            if (i < _blockFirst || i >= _blockFirst + _blockLength)
            {
                long n = Math.Min(BlockEntries, _count - i);
                int got = _source.Read(_dataOffset + (i * 16), _block.AsSpan(0, (int)(n * 16)));
                _blockFirst = i;
                _blockLength = got / 16;
                if (_blockLength == 0)
                {
                    _next = _count;
                    break;
                }
            }

            ReadOnlySpan<byte> e = _block.AsSpan((int)(i - _blockFirst) * 16, 16);
            if (!AviChunkId.TryStreamNumber(BinaryPrimitives.ReadUInt32LittleEndian(e), out int stream, out _) || (_streamFilter >= 0 && stream != _streamFilter))
            {
                continue;
            }

            entry = new AviIndexEntry(_base + BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), (BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) & KeyframeFlag) != 0, stream);
            return true;
        }

        entry = default;
        return false;
    }
}

/// <summary>
/// Reads one stream's OpenDML index: the super index (<c>indx</c>) points at standard index chunks (<c>ix##</c>), each of which lists
/// chunks relative to a 64-bit base (OpenDML AVI File Format Extensions 1.02, "AVI Standard Index Chunk"). Field indexes, which
/// add a second offset per entry, are read by their first field's offset.
/// </summary>
internal sealed class OdmlCursor : AviIndexCursor
{
    private const int BlockEntries = 512;

    private readonly IMediaSource _source;
    private readonly IReadOnlyList<SuperIndexEntry> _super;
    private readonly int _stream;
    private readonly DefectLog _defects;
    private readonly byte[] _block = new byte[BlockEntries * 12];
    private int _superIndex;
    private long _baseOffset;
    private long _entriesOffset;
    private int _entrySize;
    private long _entryCount;
    private long _entryNext;
    private bool _inChunk;
    private long _blockFirst = -1;
    private int _blockLength;

    public OdmlCursor(IMediaSource source, IReadOnlyList<SuperIndexEntry> super, int stream, DefectLog defects)
    {
        _source = source;
        _super = super;
        _stream = stream;
        _defects = defects;
    }

    /// <inheritdoc />
    protected override void ResetCore()
    {
        _superIndex = 0;
        _inChunk = false;
        _blockFirst = -1;
        _blockLength = 0;
    }

    /// <inheritdoc />
    protected override bool TryReadNext(out AviIndexEntry entry)
    {
        while (true)
        {
            if (!_inChunk && !OpenNextChunk())
            {
                entry = default;
                return false;
            }

            if (_entryNext >= _entryCount)
            {
                _inChunk = false;
                continue;
            }

            long i = _entryNext++;
            if (i < _blockFirst || i >= _blockFirst + _blockLength)
            {
                long n = Math.Min(BlockEntries, _entryCount - i);
                int got = _source.Read(_entriesOffset + (i * _entrySize), _block.AsSpan(0, (int)(n * _entrySize)));
                _blockFirst = i;
                _blockLength = got / _entrySize;
                if (_blockLength == 0)
                {
                    _inChunk = false;
                    continue;
                }
            }

            ReadOnlySpan<byte> e = _block.AsSpan((int)(i - _blockFirst) * _entrySize, _entrySize);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            // The entry points at the chunk's data; the chunk header is the 8 bytes before it. Bit 31 of the size is set for a non-key frame.
            entry = new AviIndexEntry(_baseOffset + offset - 8, size & 0x7FFFFFFF, (size & 0x80000000) == 0, _stream);
            return true;
        }
    }

    private bool OpenNextChunk()
    {
        Span<byte> head = stackalloc byte[32]; // chunk header plus the 24-byte standard index header, fixed
        while (_superIndex < _super.Count)
        {
            SuperIndexEntry s = _super[_superIndex++];
            if (_source.Read(s.Offset, head) < 32 || (head[0] != (byte)'i' || head[1] != (byte)'x'))
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, s.Offset, $"The super index of stream {_stream} points at something other than an ix chunk.", "avi.ix-missing");
                continue;
            }

            ushort longs = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
            byte type = head[11];
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            if (type != 1 || longs is not (2 or 3))
            {
                _defects.Add(DefectKind.Unsupported, DefectSeverity.Info, s.Offset, $"An ix chunk of stream {_stream} has type {type} and {longs} longs per entry; it is skipped.", "avi.ix-type");
                continue;
            }

            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            long fit = Math.Max(0, (chunkSize - 24L) / (longs * 4L));
            if (count > fit)
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, s.Offset, $"An ix chunk states {count} entries but has room for {fit}.", "avi.ix-entry-count");
                count = (uint)fit;
            }

            _baseOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(head[20..]);
            _entriesOffset = s.Offset + 32;
            _entrySize = longs * 4;
            _entryCount = count;
            _entryNext = 0;
            _blockFirst = -1;
            _blockLength = 0;
            _inChunk = true;
            return true;
        }

        return false;
    }
}

/// <summary>Chunk identifiers inside <c>movi</c>: two digits of stream number then two characters of type.</summary>
internal static class AviChunkId
{
    /// <summary>
    /// Splits an identifier such as <c>01wb</c> into the stream number and the two type characters (<c>wb</c> as a little-endian pair).
    /// The number is decimal, as the index chunks of files written by common muxers use it.
    /// </summary>
    public static bool TryStreamNumber(uint id, out int stream, out ushort type)
    {
        byte d0 = (byte)id;
        byte d1 = (byte)(id >> 8);
        if (d0 is >= (byte)'0' and <= (byte)'9' && d1 is >= (byte)'0' and <= (byte)'9')
        {
            stream = ((d0 - '0') * 10) + (d1 - '0');
            type = (ushort)(id >> 16);
            return true;
        }

        stream = -1;
        type = 0;
        return false;
    }

    public static readonly ushort Dc = Pair("dc");
    public static readonly ushort Db = Pair("db");
    public static readonly ushort Wb = Pair("wb");
    public static readonly ushort Tx = Pair("tx");
    public static readonly ushort Pc = Pair("pc");

    private static ushort Pair(string s) => (ushort)(s[0] | (s[1] << 8));
}

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Which sample-table list a deferred table box is written from.</summary>
internal enum TableKind
{
    /// <summary>Pairs of (count, value) as stored: <c>stts</c> and <c>ctts</c>.</summary>
    Pairs,

    /// <summary>One 32-bit value per entry: <c>stsz</c> sizes and <c>stss</c> sample numbers.</summary>
    Words,

    /// <summary>Sample sizes narrowed to 8 or 16 bits for <c>stz2</c>.</summary>
    Narrow8,

    /// <summary>Sample sizes narrowed to 16 bits for <c>stz2</c>.</summary>
    Narrow16,

    /// <summary>Triples for <c>stsc</c>: first chunk, samples per chunk, sample description index.</summary>
    Triples,

    /// <summary>Chunk offsets written as 32-bit values after adding the base.</summary>
    Offsets32,

    /// <summary>Chunk offsets written as 64-bit values after adding the base.</summary>
    Offsets64,

    /// <summary>One byte per sample for <c>sdtp</c>.</summary>
    Bytes,
}

/// <summary>
/// The entries of one sample-table box, written straight from the muxer's paged lists when the movie box is emitted, so the box
/// never exists as one array. Its length is known up front, which is what lets the enclosing boxes state their sizes.
/// </summary>
internal sealed class DeferredTable
{
    private readonly TableKind _kind;
    private readonly Paged<uint>? _words;
    private readonly Paged<ulong>? _offsets;
    private readonly Paged<byte>? _bytes;
    private readonly long _count;
    private readonly long _add;

    public DeferredTable(TableKind kind, Paged<uint> words, long count)
    {
        _kind = kind;
        _words = words;
        _count = count;
        Length = kind switch
        {
            TableKind.Pairs => count * 8,
            TableKind.Triples => count * 12,
            TableKind.Narrow8 => count,
            TableKind.Narrow16 => count * 2,
            _ => count * 4,
        };
    }

    public DeferredTable(TableKind kind, Paged<ulong> offsets, long count, long add)
    {
        _kind = kind;
        _offsets = offsets;
        _count = count;
        _add = add;
        Length = kind == TableKind.Offsets64 ? count * 8 : count * 4;
    }

    public DeferredTable(Paged<byte> bytes, long count)
    {
        _kind = TableKind.Bytes;
        _bytes = bytes;
        _count = count;
        Length = count;
    }

    public long Length { get; }

    /// <summary>Writes the entries. <paramref name="stage"/> is scratch space of at least 16 KiB.</summary>
    public void WriteTo(OutputBuffer output, byte[] stage, CancellationToken cancellationToken)
    {
        long done = 0;
        int pageCount = _words?.PageCount ?? _offsets?.PageCount ?? _bytes!.PageCount;
        // Pair and triple tables are streams of words; an entry may straddle two pages, so only the word count matters.
        long wordsLeft = _kind switch { TableKind.Pairs => _count * 2, TableKind.Triples => _count * 3, _ => _count };
        for (int p = 0; p < pageCount && (_words is not null ? wordsLeft > 0 : done < _count); p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_words is not null)
            {
                ReadOnlySpan<uint> page = _words.Page(p);
                int n = (int)Math.Min(page.Length, wordsLeft);
                WriteWords(output, stage, page[..n]);
                wordsLeft -= n;
            }
            else if (_offsets is not null)
            {
                ReadOnlySpan<ulong> page = _offsets.Page(p);
                int n = (int)Math.Min(page.Length, _count - done);
                WriteOffsets(output, stage, page[..n]);
                done += n;
            }
            else
            {
                ReadOnlySpan<byte> page = _bytes!.Page(p);
                int n = (int)Math.Min(page.Length, _count - done);
                output.Write(page[..n]);
                done += n;
            }
        }
    }

    private void WriteWords(OutputBuffer output, byte[] stage, ReadOnlySpan<uint> words)
    {
        int width = _kind switch { TableKind.Narrow8 => 1, TableKind.Narrow16 => 2, _ => 4 };
        int perChunk = stage.Length / 4;
        while (!words.IsEmpty)
        {
            int n = Math.Min(words.Length, perChunk);
            for (int i = 0; i < n; i++)
            {
                switch (width)
                {
                    case 1:
                        stage[i] = (byte)words[i];
                        break;
                    case 2:
                        BinaryPrimitives.WriteUInt16BigEndian(stage.AsSpan(i * 2), (ushort)words[i]);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32BigEndian(stage.AsSpan(i * 4), words[i]);
                        break;
                }
            }

            output.Write(stage.AsSpan(0, n * width));
            words = words[n..];
        }
    }

    private void WriteOffsets(OutputBuffer output, byte[] stage, ReadOnlySpan<ulong> offsets)
    {
        int width = _kind == TableKind.Offsets64 ? 8 : 4;
        int perChunk = stage.Length / 8;
        while (!offsets.IsEmpty)
        {
            int n = Math.Min(offsets.Length, perChunk);
            for (int i = 0; i < n; i++)
            {
                ulong value = offsets[i] + (ulong)_add;
                if (width == 8)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(stage.AsSpan(i * 8), value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(stage.AsSpan(i * 4), (uint)value);
                }
            }

            output.Write(stage.AsSpan(0, n * width));
            offsets = offsets[n..];
        }
    }
}

/// <summary>
/// Builds a tree of boxes in memory, back-patching each size when the box ends. Large sample tables are not copied in: they are
/// registered as deferred entries whose length counts toward the enclosing boxes and which are streamed out in place by
/// <see cref="WriteTo"/>.
/// </summary>
internal sealed class BoxStack
{
    private byte[] _buffer;
    private int _length;
    private long _deferredTotal;
    private readonly List<(int At, DeferredTable Table)> _deferred = [];
    private readonly Stack<(int HeaderAt, long LogicalStart)> _open = new();

    public BoxStack(int capacity = 512)
    {
        _buffer = new byte[Math.Max(capacity, 64)];
    }

    /// <summary>Bytes the tree occupies once the deferred tables are written in.</summary>
    public long Length => _length + _deferredTotal;

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void U8(int value) => Reserve(1)[0] = (byte)value;

    public void U16(int value) => BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), (ushort)value);

    public void U32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);

    public void I32(int value) => BinaryPrimitives.WriteInt32BigEndian(Reserve(4), value);

    public void U64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);

    public void Zeros(int count) => Reserve(count).Clear();

    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    /// <summary>Writes a four-character code (Latin-1).</summary>
    public void Code(string code)
    {
        Span<byte> span = Reserve(4);
        for (int i = 0; i < 4; i++)
        {
            span[i] = (byte)code[i];
        }
    }

    /// <summary>Opens a box: its size is patched by <see cref="End"/>.</summary>
    public void Begin(string type)
    {
        _open.Push((_length, Length));
        Reserve(4).Clear();
        Code(type);
    }

    /// <summary>Opens a full box (version and 24 flag bits follow the header).</summary>
    public void BeginFull(string type, int version, uint flags)
    {
        Begin(type);
        U32(((uint)version << 24) | (flags & 0xFF_FFFF));
    }

    /// <summary>Closes the innermost open box.</summary>
    /// <exception cref="InvalidOperationException">No box is open, or the box is larger than a 32-bit size can state.</exception>
    public void End()
    {
        (int headerAt, long start) = _open.Pop();
        long size = Length - start;
        if (size > uint.MaxValue)
        {
            throw new InvalidOperationException("A box does not fit a 32-bit size.");
        }

        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(headerAt), (uint)size);
    }

    /// <summary>Puts a table's entries at the current position.</summary>
    public void Defer(DeferredTable table)
    {
        _deferred.Add((_length, table));
        _deferredTotal += table.Length;
    }

    /// <summary>Offset of the next byte in the in-memory part, for <see cref="PatchU32"/>.</summary>
    public int Mark => _length;

    /// <summary>Overwrites four bytes already written, at an offset taken from <see cref="Mark"/>.</summary>
    public void PatchU32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(at), value);

    /// <summary>Empties the tree so its buffer can be reused.</summary>
    public void Clear()
    {
        _length = 0;
        _deferredTotal = 0;
        _deferred.Clear();
        _open.Clear();
    }

    /// <summary>The bytes written so far. Valid only when nothing was deferred.</summary>
    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    /// <summary>The tree as bytes. Valid only when nothing was deferred.</summary>
    public byte[] ToArray()
    {
        if (_deferred.Count != 0)
        {
            throw new InvalidOperationException("Deferred tables cannot be copied into an array.");
        }

        return _buffer.AsSpan(0, _length).ToArray();
    }

    /// <summary>Writes the tree, with every deferred table in place.</summary>
    public void WriteTo(OutputBuffer output, CancellationToken cancellationToken)
    {
        byte[] stage = ArrayPool<byte>.Shared.Rent(1 << 14);
        try
        {
            int at = 0;
            foreach ((int position, DeferredTable table) in _deferred)
            {
                output.Write(_buffer.AsSpan(at, position - at));
                table.WriteTo(output, stage, cancellationToken);
                at = position;
            }

            output.Write(_buffer.AsSpan(at, _length - at));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stage);
        }
    }

    /// <summary>Writes a Latin-1 or UTF-8 string without a terminator.</summary>
    public void Utf8(string text)
    {
        int n = Encoding.UTF8.GetByteCount(text);
        Encoding.UTF8.GetBytes(text, Reserve(n));
    }
}

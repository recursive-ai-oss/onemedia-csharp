using System.Buffers;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// A growing list kept in fixed-size pages rented from the shared array pool, so it never copies what it holds when it grows and a
/// second run in the same process finds its pages already pooled. The sample tables of a muxer are the only state that grows with the
/// length of the file; each is one of these.
/// </summary>
internal sealed class Paged<T> : IDisposable where T : unmanaged
{
    public const int PageShift = 14;
    public const int PageSize = 1 << PageShift;
    private const int Mask = PageSize - 1;
    private T[]?[] _pages = new T[]?[8];
    private long _count;

    public long Count => _count;

    public int PageCount => (int)((_count + Mask) >> PageShift);

    public void Add(T value)
    {
        int slot = (int)(_count & Mask);
        if (slot == 0)
        {
            int page = (int)(_count >> PageShift);
            if (page == _pages.Length)
            {
                Array.Resize(ref _pages, page * 2);
            }

            _pages[page] = ArrayPool<T>.Shared.Rent(PageSize);
        }

        _pages[(int)(_count >> PageShift)]![slot] = value;
        _count++;
    }

    public T this[long index]
    {
        get => _pages[(int)(index >> PageShift)]![(int)(index & Mask)];
        set => _pages[(int)(index >> PageShift)]![(int)(index & Mask)] = value;
    }

    /// <summary>The entries of one page (the last page is shorter).</summary>
    public ReadOnlySpan<T> Page(int page)
    {
        long start = (long)page << PageShift;
        int length = (int)Math.Min(PageSize, _count - start);
        return _pages[page]!.AsSpan(0, length);
    }

    public void Dispose()
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is { } p)
            {
                ArrayPool<T>.Shared.Return(p);
                _pages[i] = null;
            }
        }

        _count = 0;
    }
}

/// <summary>
/// A buffered writer onto the destination stream that counts what it has written, so the muxer never asks the stream for its position
/// (the stream may not be seekable) and small packets do not become small writes.
/// </summary>
internal sealed class OutputBuffer : IDisposable
{
    private const int Capacity = 1 << 16;
    private readonly Stream _stream;
    private readonly long _origin;
    private byte[]? _buffer;
    private int _used;

    public OutputBuffer(Stream stream)
    {
        _stream = stream;
        _origin = stream.CanSeek ? stream.Position : 0;
        _buffer = ArrayPool<byte>.Shared.Rent(Capacity);
    }

    /// <summary>Bytes written through this buffer so far, counting from where the stream was when the buffer was made.</summary>
    public long Position { get; private set; }

    public bool CanSeek => _stream.CanSeek;

    public void Write(ReadOnlySpan<byte> data)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(OutputBuffer));
        Position += data.Length;
        if (data.Length >= buffer.Length)
        {
            FlushBuffer();
            _stream.Write(data);
            return;
        }

        if (data.Length > buffer.Length - _used)
        {
            FlushBuffer();
        }

        data.CopyTo(buffer.AsSpan(_used));
        _used += data.Length;
    }

    public void WriteZeros(long count)
    {
        Span<byte> zeros = stackalloc byte[256]; // fixed small buffer
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            Write(zeros[..n]);
            count -= n;
        }
    }

    /// <summary>Overwrites bytes already written. The stream must be seekable.</summary>
    public void Patch(long position, ReadOnlySpan<byte> data)
    {
        FlushBuffer();
        long current = _stream.Position;
        _stream.Position = _origin + position;
        _stream.Write(data);
        _stream.Position = current;
    }

    public void Flush(bool flushStream)
    {
        FlushBuffer();
        if (flushStream)
        {
            _stream.Flush();
        }
    }

    private void FlushBuffer()
    {
        if (_used > 0)
        {
            _stream.Write(_buffer!, 0, _used);
            _used = 0;
        }
    }

    public void Dispose()
    {
        if (_buffer is { } b)
        {
            ArrayPool<byte>.Shared.Return(b);
            _buffer = null;
        }
    }
}

/// <summary>
/// Holds payload that has to be written after something that is only known at the end (a movie box in front of its media data). Up to
/// a budget it stays in pooled memory blocks; past it the blocks go to a stream made by the factory (a temporary file by default),
/// so memory stays flat however long the file is.
/// </summary>
internal sealed class SpoolBuffer : IDisposable
{
    private const int BlockSize = 1 << 18;
    private readonly long _memoryBudget;
    private readonly Func<Stream> _factory;
    private readonly List<byte[]> _blocks;
    private byte[]? _current;
    private int _currentLength;
    private Stream? _file;

    public SpoolBuffer(long memoryBudget, Func<Stream> factory)
    {
        _memoryBudget = memoryBudget;
        _factory = factory;
        // Room for every block the budget allows (at most 4096 references), so keeping a block never grows the list.
        _blocks = new List<byte[]>((int)Math.Min((memoryBudget / BlockSize) + 1, 4096));
    }

    public long Length { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        Length += data.Length;
        while (!data.IsEmpty)
        {
            if (_current is null || _currentLength == BlockSize)
            {
                NextBlock();
            }

            int n = Math.Min(data.Length, BlockSize - _currentLength);
            data[..n].CopyTo(_current.AsSpan(_currentLength));
            _currentLength += n;
            data = data[n..];
        }
    }

    private void NextBlock()
    {
        if (_file is null)
        {
            if ((_blocks.Count + 1L) * BlockSize <= _memoryBudget || _current is null)
            {
                _current = ArrayPool<byte>.Shared.Rent(BlockSize);
                _blocks.Add(_current);
                _currentLength = 0;
                return;
            }

            _file = _factory();
            foreach (byte[] b in _blocks)
            {
                _file.Write(b, 0, BlockSize);
                if (b != _current)
                {
                    ArrayPool<byte>.Shared.Return(b);
                }
            }

            _blocks.Clear();
            _currentLength = 0;
            return;
        }

        _file.Write(_current!, 0, _currentLength);
        _currentLength = 0;
    }

    public void CopyTo(OutputBuffer destination, CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            return;
        }

        if (_file is null)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(_blocks[i].AsSpan(0, i == _blocks.Count - 1 ? _currentLength : BlockSize));
            }

            return;
        }

        _file.Write(_current, 0, _currentLength);
        _currentLength = 0;
        _file.Flush();
        _file.Position = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = _file.Read(_current, 0, BlockSize);
            if (n <= 0)
            {
                break;
            }

            destination.Write(_current.AsSpan(0, n));
        }
    }

    public void Dispose()
    {
        foreach (byte[] b in _blocks)
        {
            ArrayPool<byte>.Shared.Return(b);
        }

        if (_file is not null && _current is not null && !_blocks.Contains(_current))
        {
            ArrayPool<byte>.Shared.Return(_current);
        }

        _blocks.Clear();
        _current = null;
        _file?.Dispose();
        _file = null;
    }
}

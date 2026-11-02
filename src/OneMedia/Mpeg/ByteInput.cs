using System.Buffers;

namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>
/// A buffered front-to-back reader over a seekable or forward-only source, used by the stream formats that have no index
/// (transport streams, program streams, elementary streams). It keeps one rented window, so memory does not grow with the file.
/// </summary>
internal sealed class ByteInput : IDisposable
{
    private const int WindowSize = 256 * 1024;

    private readonly IMediaSource? _random;
    private readonly IForwardMediaSource? _forward;
    private readonly bool _leaveOpen;
    private byte[]? _buffer;
    private int _start;
    private int _end;
    private long _bufferOffset; // file offset of _buffer[0]
    private bool _atEnd;

    // Front cache: the bytes read from _cacheStart on, kept while a probe may rewind and read the same range again.
    private byte[]? _cache;
    private int _cacheLength;
    private int _cacheLimit;
    private long _cacheStart;

    public ByteInput(IMediaSource source, bool leaveOpen)
    {
        _random = source;
        _leaveOpen = leaveOpen;
        _buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        Length = source.Length;
    }

    public ByteInput(IForwardMediaSource source, bool leaveOpen)
    {
        _forward = source;
        _leaveOpen = leaveOpen;
        _buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        _bufferOffset = source.Position;
        Length = -1;
    }

    /// <summary>Total length of the source, or -1 when it is forward-only.</summary>
    public long Length { get; }

    public bool CanSeek => _random is not null;

    /// <summary>File offset of the next unread byte.</summary>
    public long Position => _bufferOffset + _start;

    /// <summary>The source, when random access is available.</summary>
    public IMediaSource? Random => _random;

    /// <summary>Returns up to <paramref name="count"/> bytes without consuming them; fewer only at the end of the data.</summary>
    public ReadOnlySpan<byte> Peek(int count)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(ByteInput));
        if (count > WindowSize / 2)
        {
            count = WindowSize / 2;
        }

        if (_end - _start < count && !_atEnd)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(buffer, _start, buffer, 0, _end - _start);
                _bufferOffset += _start;
                _end -= _start;
                _start = 0;
            }

            while (_end < count && !_atEnd)
            {
                int n = _random is not null
                    ? ReadRandom(_bufferOffset + _end, buffer.AsSpan(_end))
                    : _forward!.Read(buffer.AsSpan(_end));
                if (n <= 0)
                {
                    _atEnd = true;
                }
                else
                {
                    _end += n;
                }
            }
        }

        return buffer.AsSpan(_start, Math.Min(count, _end - _start));
    }

    /// <summary>
    /// Starts keeping the bytes read from the current position on, up to <paramref name="limit"/> bytes, so that a later <see cref="Seek"/>
    /// back into that range is answered from memory and no byte is requested from the source twice. Random-access inputs only.
    /// The memory (at most <paramref name="limit"/> bytes, grown as the data arrives) is returned by <see cref="EndCache"/>.
    /// </summary>
    public void BeginCache(int limit)
    {
        EndCache();
        if (_random is null)
        {
            return;
        }

        _cacheStart = Position;
        _cacheLimit = limit;
        _cacheLength = 0;
        _cache = ArrayPool<byte>.Shared.Rent(WindowSize);
        // Whatever the window already holds from the current position belongs to the cache.
        int held = _end - _start;
        if (held > 0)
        {
            Append(_buffer!.AsSpan(_start, held));
        }
    }

    /// <summary>Drops the front cache and returns its memory.</summary>
    public void EndCache()
    {
        if (_cache is { } c)
        {
            _cache = null;
            _cacheLength = 0;
            ArrayPool<byte>.Shared.Return(c);
        }
    }

    private int ReadRandom(long offset, Span<byte> destination)
    {
        if (_cache is not null && offset >= _cacheStart && offset < _cacheStart + _cacheLength)
        {
            int at = (int)(offset - _cacheStart);
            int n = Math.Min(destination.Length, _cacheLength - at);
            _cache.AsSpan(at, n).CopyTo(destination);
            return n;
        }

        int read = _random!.Read(offset, destination);
        if (_cache is not null && read > 0 && offset == _cacheStart + _cacheLength)
        {
            Append(destination[..read]);
        }

        return read;
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        int n = Math.Min(bytes.Length, _cacheLimit - _cacheLength);
        if (n <= 0)
        {
            return;
        }

        byte[] cache = _cache!;
        if (_cacheLength + n > cache.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Min(_cacheLimit, Math.Max(_cacheLength + n, cache.Length * 2)));
            cache.AsSpan(0, _cacheLength).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(cache);
            _cache = cache = bigger;
        }

        bytes[..n].CopyTo(cache.AsSpan(_cacheLength));
        _cacheLength += n;
    }

    public void Advance(int count)
    {
        _start += Math.Min(count, _end - _start);
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length bytes and consumes them.</summary>
    public int Read(Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            ReadOnlySpan<byte> got = Peek(Math.Min(destination.Length - done, WindowSize / 2));
            if (got.IsEmpty)
            {
                break;
            }

            got.CopyTo(destination[done..]);
            Advance(got.Length);
            done += got.Length;
        }

        return done;
    }

    /// <summary>Moves to an absolute offset. Forward-only inputs can only move forward.</summary>
    public bool Seek(long offset)
    {
        if (offset >= _bufferOffset && offset <= _bufferOffset + _end)
        {
            _start = (int)(offset - _bufferOffset);
            return true;
        }

        if (_random is not null)
        {
            _bufferOffset = offset;
            _start = _end = 0;
            _atEnd = false;
            return true;
        }

        if (offset < Position)
        {
            return false;
        }

        // Forward-only: read and discard up to the target.
        _bufferOffset += _end;
        _start = _end = 0;
        byte[] scratch = _buffer!;
        while (_bufferOffset < offset)
        {
            int n = _forward!.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, offset - _bufferOffset)));
            if (n <= 0)
            {
                _atEnd = true;
                return false;
            }

            _bufferOffset += n;
        }

        return true;
    }

    public void Dispose()
    {
        EndCache();
        if (_buffer is { } b)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(b);
        }

        if (!_leaveOpen)
        {
            _random?.Dispose();
            _forward?.Dispose();
        }
    }
}

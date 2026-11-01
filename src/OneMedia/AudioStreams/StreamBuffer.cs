namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>
/// Buffered, forward-moving access to an audio stream, over either a seekable source or a forward-only one. Callers look ahead with
/// <see cref="Peek"/>, then <see cref="Consume"/> what they used; spans stay valid until the next call that fills the buffer. A seekable
/// source can also be repositioned and given an end (the start of trailing tags) beyond which nothing is read.
/// </summary>
internal sealed class StreamBuffer : IDisposable
{
    private const int Capacity = 256 * 1024;
    private const int FirstFill = 16 * 1024;
    private const int Fill = 64 * 1024;

    private readonly IMediaSource? _media;
    private readonly IForwardMediaSource? _forward;
    private readonly IDisposable? _owned;
    // Rented rather than allocated: opening a file only to read its tags would otherwise allocate (and zero) 256 KiB each time.
    private byte[] _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Capacity);
    private int _start;
    private int _end;
    private long _startOffset;
    private bool _ended;
    private bool _first = true;

    public StreamBuffer(IMediaSource source, IDisposable? owned, long position = 0)
    {
        _media = source;
        _owned = owned;
        _startOffset = position;
        Limit = long.MaxValue;
    }

    public StreamBuffer(IForwardMediaSource source, IDisposable? owned)
    {
        _forward = source;
        _owned = owned;
        _startOffset = source.Position;
        Limit = long.MaxValue;
    }

    /// <summary>Absolute offset of the next unread byte.</summary>
    public long Position => _startOffset;

    public bool CanSeek => _media is not null;

    /// <summary>Length of the source in bytes; -1 for a forward-only one.</summary>
    public long Length => _media?.Length ?? -1;

    /// <summary>Nothing at or past this offset is read; the buffer behaves as if the source ended there.</summary>
    public long Limit { get; set; }

    public CancellationToken Cancellation { get; set; }

    /// <summary>Looks at up to <paramref name="count"/> bytes from the current position; fewer come back only at the end of the data.</summary>
    public ReadOnlySpan<byte> Peek(int count)
    {
        if (_end - _start < count && !_ended)
        {
            Refill(count);
        }

        int available = _end - _start;
        long room = Limit - _startOffset;
        int n = (int)Math.Min(Math.Min(count, available), Math.Max(room, 0));
        return _buffer.AsSpan(_start, n);
    }

    public void Consume(int count)
    {
        _start += count;
        _startOffset += count;
    }

    /// <summary>Copies the next bytes out and consumes them; false when the data ends first.</summary>
    public bool TryTake(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            ReadOnlySpan<byte> chunk = Peek(Math.Min(destination.Length, 64 * 1024));
            if (chunk.IsEmpty)
            {
                return false;
            }

            chunk.CopyTo(destination);
            destination = destination[chunk.Length..];
            Consume(chunk.Length);
        }

        return true;
    }

    /// <summary>Moves forward by <paramref name="count"/> bytes without keeping them; returns how many bytes were actually skipped.</summary>
    public long Skip(long count)
    {
        long skipped = 0;
        while (skipped < count)
        {
            if (_end - _start > 0)
            {
                int n = (int)Math.Min(count - skipped, _end - _start);
                Consume(n);
                skipped += n;
                continue;
            }

            if (_media is not null)
            {
                long target = Math.Min(_startOffset + (count - skipped), Math.Min(Limit, _media.Length));
                long moved = Math.Max(0, target - _startOffset);
                _startOffset += moved;
                skipped += moved;
                _start = _end = 0;
                _ended = false;
                break;
            }

            ReadOnlySpan<byte> chunk = Peek((int)Math.Min(count - skipped, 64 * 1024));
            if (chunk.IsEmpty)
            {
                break;
            }

            Consume(chunk.Length);
            skipped += chunk.Length;
        }

        return skipped;
    }

    public void Seek(long offset)
    {
        if (_media is null)
        {
            throw new NotSupportedException("The source cannot seek.");
        }

        if (offset >= _startOffset && offset <= _startOffset + (_end - _start))
        {
            Consume((int)(offset - _startOffset));
            return;
        }

        _start = _end = 0;
        _startOffset = offset;
        _ended = false;
    }

    private void Refill(int need)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        need = Math.Min(need, Capacity);
        while (_end < need && !_ended)
        {
            int want = Math.Min(Capacity - _end, _first ? FirstFill : Fill);
            long streamPosition = _startOffset + _end;
            if (streamPosition >= Limit)
            {
                _ended = true;
                break;
            }

            want = (int)Math.Min(want, Limit - streamPosition);
            int n = _media is not null ? _media.Read(streamPosition, _buffer.AsSpan(_end, want), Cancellation) : _forward!.Read(_buffer.AsSpan(_end, want), Cancellation);
            _first = false;
            if (n <= 0)
            {
                _ended = true;
                break;
            }

            _end += n;
        }
    }

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length != 0)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        _owned?.Dispose();
    }
}

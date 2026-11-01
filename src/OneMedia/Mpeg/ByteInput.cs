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
                    ? _random.Read(_bufferOffset + _end, buffer.AsSpan(_end))
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

using System.Buffers;

namespace RecursiveAI.OneMedia.Input;

/// <summary>
/// A buffered front-to-back reader over a seekable source, an asynchronous ranged source or a forward-only one, with a position in the
/// underlying data. Container readers whose structure is a plain sequence (Ogg pages, FLAC frames, FLV tags) read through it so that one code
/// path serves every kind of source: the methods return <see cref="ValueTask"/>s that are complete on return for a source that never waits.
/// It never holds more than one window of the data.
/// </summary>
internal sealed class SequentialInput : IDisposable
{
    private const int WindowSize = 64 * 1024;

    private readonly IMediaSource? _sync;
    private readonly IAsyncMediaSource? _async;
    private readonly IForwardMediaSource? _forward;
    private byte[]? _window;
    private int _start;       // first unread byte in the window
    private int _end;         // end of valid bytes in the window
    private long _windowPos;  // source offset of window[_start]
    private bool _atEnd;

    public SequentialInput(IMediaSource source)
    {
        _sync = source;
    }

    public SequentialInput(IAsyncMediaSource source)
    {
        _async = source;
    }

    public SequentialInput(IForwardMediaSource source)
    {
        _forward = source;
        _windowPos = source.Position;
    }

    /// <summary>True when <see cref="Seek"/> and <see cref="ReadAtAsync"/> may be used.</summary>
    public bool CanSeek => _forward is null;

    /// <summary>The total length when the source is seekable; -1 for a forward-only source.</summary>
    public long Length => _sync?.Length ?? _async?.Length ?? -1;

    /// <summary>The source offset of the next byte to be read.</summary>
    public long Position => _windowPos;

    /// <summary>Makes the first bytes of the source, already read by the caller, available without reading them again. Call before any read.</summary>
    public void Seed(ReadOnlySpan<byte> head)
    {
        EnsureWindow();
        int n = Math.Min(head.Length, _window!.Length);
        head[..n].CopyTo(_window);
        _start = 0;
        _end = n;
        _windowPos = 0;
    }

    private void EnsureWindow() => _window ??= ArrayPool<byte>.Shared.Rent(WindowSize);

    /// <summary>Moves to <paramref name="offset"/> in a seekable source.</summary>
    public void Seek(long offset)
    {
        if (_forward is not null)
        {
            throw new NotSupportedException("The source cannot seek.");
        }

        long windowStart = _windowPos - _start;
        if (_window is not null && offset >= windowStart && offset <= windowStart + _end)
        {
            _start = (int)(offset - windowStart);
            _windowPos = offset;
            return;
        }

        _start = 0;
        _end = 0;
        _windowPos = offset;
        _atEnd = false;
    }

    /// <summary>Reads from an absolute offset without touching the window or the position. Seekable sources only.</summary>
    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct)
    {
        if (_sync is not null)
        {
            return new ValueTask<int>(_sync.Read(offset, destination.Span, ct));
        }

        if (_async is not null)
        {
            return _async.ReadAsync(offset, destination, ct);
        }

        throw new NotSupportedException("The source cannot seek.");
    }

    private ValueTask<int> SourceReadAsync(Memory<byte> destination, long at, CancellationToken ct)
    {
        if (_sync is not null)
        {
            return new ValueTask<int>(_sync.Read(at, destination.Span, ct));
        }

        if (_async is not null)
        {
            return _async.ReadAsync(at, destination, ct);
        }

        return new ValueTask<int>(_forward!.Read(destination.Span, ct));
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        EnsureWindow();
        if (_start > 0)
        {
            int keep = _end - _start;
            Buffer.BlockCopy(_window!, _start, _window!, 0, keep);
            _start = 0;
            _end = keep;
        }

        if (_end == _window!.Length || _atEnd)
        {
            return false;
        }

        int n = await SourceReadAsync(_window.AsMemory(_end), _windowPos + _end, ct).ConfigureAwait(false);
        if (n <= 0)
        {
            _atEnd = true;
            return false;
        }

        _end += n;
        return true;
    }

    /// <summary>Returns up to <paramref name="count"/> upcoming bytes (at most the window size) without consuming them; fewer only at the end of the data. Valid until the next read.</summary>
    public async ValueTask<ReadOnlyMemory<byte>> PeekAsync(int count, CancellationToken ct = default)
    {
        EnsureWindow();
        count = Math.Min(count, WindowSize);
        while (_end - _start < count && await FillAsync(ct).ConfigureAwait(false))
        {
        }

        return _window.AsMemory(_start, Math.Min(count, _end - _start));
    }

    /// <summary>Consumes bytes previously returned by <see cref="PeekAsync"/>.</summary>
    public void Advance(int count)
    {
        _start += count;
        _windowPos += count;
    }

    /// <summary>Reads up to <paramref name="destination"/>'s length; returns fewer bytes only at the end of the data.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        int total = 0;
        while (!destination.IsEmpty)
        {
            int have = _end - _start;
            if (have == 0)
            {
                if (destination.Length >= WindowSize)
                {
                    // Large reads go straight to the source.
                    int direct = await SourceReadAsync(destination, _windowPos, ct).ConfigureAwait(false);
                    if (direct <= 0)
                    {
                        break;
                    }

                    _windowPos += direct;
                    total += direct;
                    destination = destination[direct..];
                    continue;
                }

                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            int n = Math.Min(have, destination.Length);
            _window.AsSpan(_start, n).CopyTo(destination.Span);
            _start += n;
            _windowPos += n;
            total += n;
            destination = destination[n..];
        }

        return total;
    }

    /// <summary>Skips bytes. On a seekable source this is free; on a forward-only source the bytes are read and dropped. Returns the number skipped.</summary>
    public async ValueTask<long> SkipAsync(long count, CancellationToken ct = default)
    {
        if (_forward is null)
        {
            long room = Math.Max(0, Length - _windowPos);
            long n = Math.Min(count, room);
            Seek(_windowPos + n);
            return n;
        }

        long skipped = 0;
        while (skipped < count)
        {
            int have = _end - _start;
            if (have == 0)
            {
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            int n = (int)Math.Min(have, count - skipped);
            _start += n;
            _windowPos += n;
            skipped += n;
        }

        return skipped;
    }

    /// <summary>Synchronous <see cref="ReadAsync"/> for readers that are written synchronously; waits when the source does.</summary>
    public int Read(Span<byte> destination, CancellationToken ct = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            int n = EngineHost.Wait(ReadAsync(scratch.AsMemory(0, destination.Length), ct));
            scratch.AsSpan(0, n).CopyTo(destination);
            return n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Synchronous <see cref="SkipAsync"/>.</summary>
    public long Skip(long count, CancellationToken ct = default) => EngineHost.Wait(SkipAsync(count, ct));

    public void Dispose()
    {
        if (_window is not null)
        {
            ArrayPool<byte>.Shared.Return(_window);
            _window = null;
        }
    }
}

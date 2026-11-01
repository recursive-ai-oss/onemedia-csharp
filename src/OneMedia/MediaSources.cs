using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneMedia;

/// <summary>A source over a block of memory. Reads never copy more than they return; the memory is not copied on construction.</summary>
public sealed class MemoryMediaSource : IMediaSource, IAsyncMediaSource
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>Wraps <paramref name="data"/>. The caller must not modify it while the source is in use.</summary>
    /// <param name="data">The bytes.</param>
    public MemoryMediaSource(ReadOnlyMemory<byte> data) => _data = data;

    /// <inheritdoc />
    public long Length => _data.Length;

    /// <inheritdoc />
    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        if (offset >= _data.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, _data.Length - offset);
        _data.Span.Slice((int)offset, n).CopyTo(destination);
        return n;
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new ValueTask<int>(Read(offset, destination.Span, cancellationToken));
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// A source over a file, using positional reads, so reads at different offsets need no seeking and may run concurrently.
/// The file is opened shared for reading and writing so a file that is still being recorded can be read; <see cref="Length"/>
/// reflects its current size.
/// </summary>
public sealed class FileMediaSource : IMediaSource, IAsyncMediaSource
{
    private readonly SafeFileHandle _handle;
    private long _length = -1; // remembered size, or -1 when it must be read

    private FileMediaSource(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The path the source was opened from, as given to <see cref="Open"/>. Readers use it to find files stored beside the media.</summary>
    public string Path { get; }

    /// <summary>Opens <paramref name="path"/> for reading.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The source; the caller disposes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="IOException">The file cannot be opened (including <see cref="FileNotFoundException"/>).</exception>
    public static FileMediaSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileMediaSource(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess | FileOptions.Asynchronous), path);
    }

    /// <summary>
    /// The file's current size. It is read from the file system once and kept until a read comes back short or asks for bytes at or past
    /// that size, which is when a file that is still being written may have grown; the next call then reads it again. A reader that
    /// asks for the length often therefore costs one system call per growth check instead of one per call.
    /// </summary>
    public long Length
    {
        get
        {
            long known = Volatile.Read(ref _length);
            if (known >= 0)
            {
                return known;
            }

            known = RandomAccess.GetLength(_handle);
            Volatile.Write(ref _length, known);
            return known;
        }
    }

    // Forgets the remembered length when a read may have reached the end of the data it describes.
    private void NoteRead(long offset, int requested, int returned)
    {
        long known = Volatile.Read(ref _length);
        if (known >= 0 && (returned < requested || offset + requested >= known))
        {
            Volatile.Write(ref _length, -1);
        }
    }

    /// <inheritdoc />
    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        NoteRead(offset, destination.Length, total);
        return total;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int total = 0;
        while (total < destination.Length)
        {
            int n = await RandomAccess.ReadAsync(_handle, destination[total..], offset + total, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        NoteRead(offset, destination.Length, total);
        return total;
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

/// <summary>
/// A source over a seekable <see cref="Stream"/>. Reads are serialised, because a stream has one position; for files prefer
/// <see cref="FileMediaSource"/>, which does not.
/// </summary>
public sealed class StreamMediaSource : IMediaSource, IAsyncMediaSource
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Wraps a stream that can read and seek.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="leaveOpen">Leave the stream open when the source is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot read or cannot seek; use <see cref="StreamForwardSource"/> for those.</exception>
    public StreamMediaSource(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public long Length
    {
        get
        {
            _gate.Wait();
            try
            {
                return _stream.Length;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _gate.Wait(cancellationToken);
        try
        {
            _stream.Position = offset;
            return _stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            return await _stream.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        _gate.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

/// <summary>
/// A source whose bytes come from a caller-supplied ranged read: an HTTP range request, a cloud-storage read, a browser
/// <c>Blob</c> slice. Every call to <see cref="ReadAsync"/> makes one call to the delegate (more only if the delegate returns
/// short before the end), so range requests stay countable and the caller decides about caching.
/// </summary>
public sealed class RangedMediaSource : IAsyncMediaSource
{
    private readonly Func<long, Memory<byte>, CancellationToken, ValueTask<int>> _readRange;
    private readonly Func<ValueTask>? _dispose;

    /// <summary>Creates a ranged source.</summary>
    /// <param name="length">Total length in bytes, or -1 if unknown.</param>
    /// <param name="readRange">
    /// Reads up to the buffer's length starting at the offset and returns the count. Returning fewer bytes than asked is
    /// allowed (the source asks again for the rest); 0 means the end.
    /// </param>
    /// <param name="dispose">Optional clean-up run on <see cref="DisposeAsync"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="readRange"/> is null.</exception>
    public RangedMediaSource(long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readRange, Func<ValueTask>? dispose = null)
    {
        ArgumentNullException.ThrowIfNull(readRange);
        Length = length;
        _readRange = readRange;
        _dispose = dispose;
    }

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        if (Length >= 0 && offset >= Length)
        {
            return 0;
        }

        if (Length >= 0 && destination.Length > Length - offset)
        {
            destination = destination[..(int)(Length - offset)];
        }

        int total = 0;
        while (total < destination.Length)
        {
            int n = await _readRange(offset + total, destination[total..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dispose?.Invoke() ?? default;
}

/// <summary>A forward-only source over any readable <see cref="Stream"/>, seekable or not.</summary>
public sealed class StreamForwardSource : IForwardMediaSource
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    /// <summary>Wraps a readable stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="leaveOpen">Leave the stream open when the source is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot read.</exception>
    public StreamForwardSource(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public long Position { get; private set; }

    /// <inheritdoc />
    public int Read(Span<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n = _stream.Read(destination);
        Position += n;
        return n;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        int n = await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        Position += n;
        return n;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _leaveOpen ? default : _stream.DisposeAsync();
}

/// <summary>
/// Wraps a random-access source and reports every read to an <see cref="IMediaReadObserver"/>, so a harness can count the bytes and
/// range requests a probe or a metadata read costs. The wrapper adds no buffering.
/// </summary>
public sealed class ObservedMediaSource : IMediaSource, IAsyncMediaSource
{
    private readonly IMediaSource? _sync;
    private readonly IAsyncMediaSource? _async;
    private readonly IMediaReadObserver _observer;

    /// <summary>Wraps a source that supports synchronous reads, asynchronous reads, or both.</summary>
    /// <param name="sync">The synchronous side, or null.</param>
    /// <param name="async">The asynchronous side, or null. Pass the same object twice for a source that has both.</param>
    /// <param name="observer">Receives one call per read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null, or both sources are.</exception>
    public ObservedMediaSource(IMediaSource? sync, IAsyncMediaSource? @async, IMediaReadObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (sync is null && @async is null)
        {
            throw new ArgumentNullException(nameof(sync), "At least one source is required.");
        }

        _sync = sync;
        _async = @async;
        _observer = observer;
    }

    /// <inheritdoc />
    public long Length => _sync?.Length ?? _async!.Length;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped source has no synchronous side.</exception>
    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (_sync is null)
        {
            throw new NotSupportedException("The wrapped source reads asynchronously only.");
        }

        int n = _sync.Read(offset, destination, cancellationToken);
        _observer.OnRead(offset, destination.Length, n);
        return n;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped source has no asynchronous side.</exception>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (_async is null)
        {
            throw new NotSupportedException("The wrapped source reads synchronously only.");
        }

        int n = await _async.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        _observer.OnRead(offset, destination.Length, n);
        return n;
    }

    /// <inheritdoc />
    public void Dispose() => _sync?.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _async is not null ? _async.DisposeAsync() : DisposeSync();

    private ValueTask DisposeSync()
    {
        _sync?.Dispose();
        return default;
    }
}

/// <summary>Wraps a forward-only source and reports every read to an <see cref="IMediaReadObserver"/>.</summary>
public sealed class ObservedForwardSource : IForwardMediaSource
{
    private readonly IForwardMediaSource _inner;
    private readonly IMediaReadObserver _observer;

    /// <summary>Wraps a source.</summary>
    /// <param name="inner">The source to observe; disposed with the wrapper.</param>
    /// <param name="observer">Receives one call per read, with the position the read started at.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ObservedForwardSource(IForwardMediaSource inner, IMediaReadObserver observer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(observer);
        _inner = inner;
        _observer = observer;
    }

    /// <inheritdoc />
    public long Position => _inner.Position;

    /// <inheritdoc />
    public int Read(Span<byte> destination, CancellationToken cancellationToken = default)
    {
        long start = _inner.Position;
        int n = _inner.Read(destination, cancellationToken);
        _observer.OnRead(start, destination.Length, n);
        return n;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        long start = _inner.Position;
        int n = await _inner.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        _observer.OnRead(start, destination.Length, n);
        return n;
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

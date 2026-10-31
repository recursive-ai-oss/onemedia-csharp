using System.Buffers;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Serves sample payload reads from a read-ahead window, so a file of many small samples costs a few large reads instead of one read
/// per sample. The window starts small and doubles while reads continue where the previous one ended (samples of one chunk are
/// stored back to back); a jump elsewhere, such as over the chunks of a switched-off track, drops it back to the small size, so
/// reading one stream of an interleaved file does not fetch much of the others. Samples larger than half the largest window
/// bypass it. The window is rented from the shared pool on first use and returned by <see cref="Release"/>, so opening and
/// reading allocate nothing of their own.
/// </summary>
internal sealed class PayloadReadAhead
{
    private const int MinWindow = 4 * 1024;
    private const int MaxWindow = 64 * 1024;

    // Bytes skipped between two reads that still count as "continuing": the window simply reads through the gap.
    private const int ContinueGap = 4 * 1024;

    private byte[]? _window;
    private long _start = -1;
    private int _count;
    private int _size = MinWindow;
    private long _lastEnd = -1;
    private bool _released;

    /// <summary>Reads like <see cref="IMediaSource.Read"/>: fills <paramref name="destination"/> from <paramref name="offset"/> and returns how many bytes were available.</summary>
    /// <param name="source">The file.</param>
    /// <param name="offset">Where the sample starts.</param>
    /// <param name="destination">Receives the sample.</param>
    /// <param name="cancellationToken">Observed by the underlying read.</param>
    public int Read(IMediaSource source, long offset, Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length > MaxWindow / 2 || _released)
        {
            return source.Read(offset, destination, cancellationToken);
        }

        if (destination.Length == 0)
        {
            return 0;
        }

        byte[] window = _window ??= ArrayPool<byte>.Shared.Rent(MaxWindow);
        if (_start < 0 || offset < _start || offset + destination.Length > _start + _count)
        {
            bool continuing = _lastEnd >= 0 && offset >= _lastEnd && offset - _lastEnd <= ContinueGap;
            _size = continuing ? Math.Min(_size * 2, MaxWindow) : MinWindow;
            _count = source.Read(offset, window.AsSpan(0, Math.Max(_size, destination.Length)), cancellationToken);
            _start = offset;
        }

        long relative = offset - _start;
        int n = (int)Math.Max(0, Math.Min(destination.Length, _count - relative));
        window.AsSpan((int)relative, n).CopyTo(destination);
        _lastEnd = offset + n;
        return n;
    }

    /// <summary>Returns the window to the pool. Later reads go straight to the source.</summary>
    public void Release()
    {
        _released = true;
        if (_window is { } window)
        {
            _window = null;
            ArrayPool<byte>.Shared.Return(window);
        }
    }
}

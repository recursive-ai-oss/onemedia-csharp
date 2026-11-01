using System.Runtime.CompilerServices;
using System.Buffers;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Serves sample payload reads from a read-ahead window, so a file of many small samples costs a few large reads instead of one read
/// per sample. The window starts small and doubles while reads continue where the previous one ended (samples of one chunk are
/// stored back to back); a jump elsewhere, such as over the chunks of a switched-off track, drops it back to the small size, so
/// reading one stream of an interleaved file does not fetch much of the others. After a jump the first window is as long as the run
/// just finished (one chunk of an interleaved track is about as long as the next), so a chunk costs one read rather than a climb
/// from the smallest window. Samples larger than half the largest window
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
    private long _runStart = -1; // where the current run of contiguous reads began
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
            if (continuing)
            {
                _size = Math.Min(_size * 2, MaxWindow);
            }
            else
            {
                // The run that just ended, plus an eighth for chunks a little longer than the last, in whole 4 KiB steps.
                long run = _runStart >= 0 && _lastEnd > _runStart ? _lastEnd - _runStart : 0;
                long wanted = (run + (run >> 3) + MinWindow - 1) / MinWindow * MinWindow;
                _size = (int)Math.Clamp(wanted, MinWindow, MaxWindow);
                _runStart = offset;
            }

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

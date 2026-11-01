namespace RecursiveAI.OneMedia.Asf;

/// <summary>
/// Where the ASF reader gets its bytes: a seekable source (read at any offset), a ranged asynchronous source, or a forward-only stream.
/// Small reads go through one read-ahead window so that walking header objects and packets costs few source reads.
/// </summary>
internal abstract class AsfInput
{
    // One window serves the header walk and the packet loop; packets are read in order, so a 32 KiB window holds several of them.
    private const int WindowSize = 32 * 1024;
    private readonly byte[] _window = new byte[WindowSize];
    private long _windowStart = -1;
    private int _windowLength;

    /// <summary>True when any offset can be read.</summary>
    public abstract bool CanSeek { get; }

    /// <summary>The length of the source, or -1 when it is not known.</summary>
    public abstract long Length { get; }

    /// <summary>Reads at an offset without the window; may return fewer bytes than asked for. Returns 0 at the end of the data.</summary>
    protected abstract ValueTask<int> ReadRawAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>Forgets the window, for a reader that has moved to another position.</summary>
    public void Invalidate()
    {
        _windowStart = -1;
        _windowLength = 0;
    }

    /// <summary>Reads until <paramref name="destination"/> is full or the data ends, and returns how many bytes were read.</summary>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            long at = offset + total;
            if (at >= _windowStart && at < _windowStart + _windowLength)
            {
                int n = (int)Math.Min(destination.Length - total, _windowStart + _windowLength - at);
                _window.AsMemory((int)(at - _windowStart), n).CopyTo(destination[total..]);
                total += n;
                continue;
            }

            int want = destination.Length - total;
            if (want >= WindowSize)
            {
                int n = await ReadRawAsync(at, destination[total..], cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
                continue;
            }

            int got = await ReadRawAsync(at, _window, cancellationToken).ConfigureAwait(false);
            _windowStart = at;
            _windowLength = Math.Max(got, 0);
            if (got <= 0)
            {
                break;
            }
        }

        return total;
    }
}

internal sealed class SyncAsfInput(IMediaSource source) : AsfInput
{
    public override bool CanSeek => true;

    public override long Length => source.Length;

    protected override ValueTask<int> ReadRawAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        new(source.Read(offset, destination.Span, cancellationToken));
}

internal sealed class AsyncAsfInput(IAsyncMediaSource source) : AsfInput
{
    public override bool CanSeek => true;

    public override long Length => source.Length;

    protected override ValueTask<int> ReadRawAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        source.ReadAsync(offset, destination, cancellationToken);
}

/// <summary>A forward-only stream: offsets are measured from the stream's position when the reader started, and only increase.</summary>
internal abstract class ForwardAsfInput : AsfInput
{
    private readonly byte[] _scratch = new byte[8192];
    private long _origin;
    private long _position;
    private bool _started;

    public override bool CanSeek => false;

    public override long Length => -1;

    /// <summary>Reads from the stream, returning at most <paramref name="destination"/>.Length bytes.</summary>
    protected abstract ValueTask<int> ReadStreamAsync(Memory<byte> destination, CancellationToken cancellationToken);

    protected abstract long StreamPosition { get; }

    protected sealed override async ValueTask<int> ReadRawAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _started = true;
            _origin = StreamPosition;
        }

        long absolute = offset + _origin;
        if (absolute < _position)
        {
            // Bytes already consumed are gone; a reader that only moves forward never asks for them.
            return 0;
        }

        while (_position < absolute)
        {
            int n = await ReadStreamAsync(_scratch.AsMemory(0, (int)Math.Min(_scratch.Length, absolute - _position)), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return 0;
            }

            _position += n;
        }

        int got = await ReadStreamAsync(destination, cancellationToken).ConfigureAwait(false);
        _position += Math.Max(got, 0);
        return got;
    }
}

internal sealed class SyncForwardAsfInput(IForwardMediaSource source) : ForwardAsfInput
{
    protected override long StreamPosition => source.Position;

    protected override ValueTask<int> ReadStreamAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        new(source.Read(destination.Span, cancellationToken));
}

internal sealed class AsyncForwardAsfInput(IForwardMediaSource source) : ForwardAsfInput
{
    protected override long StreamPosition => source.Position;

    protected override ValueTask<int> ReadStreamAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        source.ReadAsync(destination, cancellationToken);
}

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// A buffered, position-tracking byte reader over a random-access, ranged or forward-only source. The Matroska reader is written once,
/// against this class, with <see cref="ValueTask"/> methods; sources that never wait return completed tasks, so the synchronous demuxer
/// runs the same code without allocating.
/// </summary>
internal abstract class EbmlInput
{
    private const int Window = 32 * 1024;

    private readonly byte[] _buf = new byte[Window];
    private long _bufStart;
    private int _bufPos;
    private int _bufLen;

    /// <summary>Absolute position of the next unread byte.</summary>
    public long Position => _bufStart + _bufPos;

    /// <summary>True when <see cref="SeekTo"/> works.</summary>
    public abstract bool CanSeek { get; }

    /// <summary>Total length in bytes, or -1 when not known (forward-only sources).</summary>
    public abstract long Length { get; }

    /// <summary>How many bytes a refill asks the source for, at least what the caller needs. Lowered while hopping over large elements.</summary>
    public int ReadAhead { get; set; } = Window;

    /// <summary>Bytes buffered from <see cref="Position"/> on.</summary>
    public ReadOnlySpan<byte> Buffered => new(_buf, _bufPos, _bufLen - _bufPos);

    /// <summary>The largest <c>count</c> <see cref="EnsureAsync"/> can satisfy.</summary>
    public static int MaxEnsure => Window;

    protected abstract ValueTask<int> ReadRawAsync(long position, Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>Buffers at least <paramref name="count"/> bytes from the current position (or as many as remain).</summary>
    /// <returns>True when <paramref name="count"/> bytes are buffered.</returns>
    public ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (_bufLen - _bufPos >= count)
        {
            return new ValueTask<bool>(true);
        }

        return EnsureSlowAsync(count, cancellationToken);
    }

    private async ValueTask<bool> EnsureSlowAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Window);

        // Move the unread tail to the front so the refill has the whole window to fill.
        int remaining = _bufLen - _bufPos;
        if (_bufPos > 0)
        {
            Buffer.BlockCopy(_buf, _bufPos, _buf, 0, remaining);
            _bufStart += _bufPos;
            _bufPos = 0;
            _bufLen = remaining;
        }

        int want = Math.Clamp(ReadAhead, count, Window);
        while (_bufLen < count)
        {
            int n = await ReadRawAsync(_bufStart + _bufLen, _buf.AsMemory(_bufLen, Math.Max(want, count) - _bufLen), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            _bufLen += n;
        }

        return true;
    }

    /// <summary>
    /// Consumes bytes the caller has already ensured. Should a caller ask for more than is buffered (a header peeked before the buffer was
    /// dropped by a seek), the position still moves exactly that far and the buffer is emptied, so it never goes out of step with the file.
    /// </summary>
    public void Advance(int count)
    {
        count = Math.Max(0, count);
        if (count <= _bufLen - _bufPos)
        {
            _bufPos += count;
            return;
        }

        _bufStart += _bufPos + count;
        _bufPos = 0;
        _bufLen = 0;
    }

    /// <summary>Moves forward by <paramref name="count"/> bytes on a seekable input: a buffer adjustment or a jump, with no read.</summary>
    public void SkipSeekable(long count)
    {
        long buffered = _bufLen - _bufPos;
        if (count <= buffered)
        {
            _bufPos += (int)count;
            return;
        }

        _bufStart = Position + count;
        _bufPos = 0;
        _bufLen = 0;
    }

    /// <summary>Moves forward by <paramref name="count"/> bytes: a buffer adjustment or a seek when the source allows, otherwise a read-and-discard.</summary>
    /// <returns>The number of bytes actually skipped (fewer at the end of a forward-only source).</returns>
    public async ValueTask<long> SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return 0;
        }

        long buffered = _bufLen - _bufPos;
        if (count <= buffered)
        {
            _bufPos += (int)count;
            return count;
        }

        if (CanSeek)
        {
            _bufStart = Position + count;
            _bufPos = 0;
            _bufLen = 0;
            return count;
        }

        long skipped = buffered;
        _bufPos = _bufLen;
        while (skipped < count)
        {
            if (!await EnsureAsync(1, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            int take = (int)Math.Min(_bufLen - _bufPos, count - skipped);
            _bufPos += take;
            skipped += take;
        }

        return skipped;
    }

    /// <summary>Fills the buffer with the first bytes of a source that is still at its start, so the first read of them is not made again.</summary>
    public void Seed(ReadOnlySpan<byte> head)
    {
        if (_bufLen != 0 || _bufStart != 0 || _bufPos != 0)
        {
            throw new InvalidOperationException("Only an unread input can be seeded.");
        }

        head = head[..Math.Min(head.Length, Window)];
        head.CopyTo(_buf);
        _bufLen = head.Length;
    }

    /// <summary>Declares the absolute position of the first byte a forward-only source will deliver.</summary>
    public void StartAt(long position)
    {
        _bufStart = position;
        _bufPos = 0;
        _bufLen = 0;
    }

    /// <summary>Moves to an absolute position on a seekable source.</summary>
    public void SeekTo(long position)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The source is forward-only.");
        }

        if (position >= _bufStart && position <= _bufStart + _bufLen)
        {
            _bufPos = (int)(position - _bufStart);
            return;
        }

        _bufStart = position;
        _bufPos = 0;
        _bufLen = 0;
    }

    /// <summary>Reads exactly into <paramref name="destination"/> where possible: buffered bytes first, then straight from the source.</summary>
    /// <returns>The number of bytes read; fewer than requested only at the end of the data.</returns>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        int buffered = _bufLen - _bufPos;
        if (buffered > 0)
        {
            int take = Math.Min(buffered, destination.Length);
            _buf.AsSpan(_bufPos, take).CopyTo(destination.Span);
            _bufPos += take;
            total = take;
        }

        if (total == destination.Length)
        {
            return total;
        }

        // Small remainders go through the buffer so that many small reads do not each cost a source call.
        if (destination.Length - total <= Window / 4)
        {
            int need = destination.Length - total;
            bool ok = await EnsureAsync(need, cancellationToken).ConfigureAwait(false);
            int take = Math.Min(need, _bufLen - _bufPos);
            _buf.AsSpan(_bufPos, take).CopyTo(destination.Span[total..]);
            _bufPos += take;
            return ok ? destination.Length : total + take;
        }

        // Large remainders skip the buffer. Position is _bufStart + _bufPos with the buffer drained.
        _bufStart += _bufLen;
        _bufPos = 0;
        _bufLen = 0;
        while (total < destination.Length)
        {
            int n = await ReadRawAsync(_bufStart, destination[total..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
            _bufStart += n;
        }

        return total;
    }
}

/// <summary>A random-access synchronous source: every call completes before it returns.</summary>
internal sealed class SyncSourceInput(IMediaSource source) : EbmlInput
{
    public override bool CanSeek => true;

    public override long Length => source.Length;

    protected override ValueTask<int> ReadRawAsync(long position, Memory<byte> destination, CancellationToken cancellationToken) =>
        new(source.Read(position, destination.Span, cancellationToken));
}

/// <summary>A random-access source that may wait, such as a ranged HTTP reader.</summary>
internal sealed class AsyncSourceInput(IAsyncMediaSource source) : EbmlInput
{
    public override bool CanSeek => true;

    public override long Length => source.Length;

    protected override ValueTask<int> ReadRawAsync(long position, Memory<byte> destination, CancellationToken cancellationToken) =>
        source.ReadAsync(position, destination, cancellationToken);
}

/// <summary>A forward-only source read synchronously (a pipe or a socket stream).</summary>
internal sealed class SyncForwardInput(IForwardMediaSource source) : EbmlInput
{
    public override bool CanSeek => false;

    public override long Length => -1;

    protected override ValueTask<int> ReadRawAsync(long position, Memory<byte> destination, CancellationToken cancellationToken) =>
        new(source.Read(destination.Span, cancellationToken));
}

/// <summary>A forward-only source read asynchronously.</summary>
internal sealed class AsyncForwardInput(IForwardMediaSource source) : EbmlInput
{
    public override bool CanSeek => false;

    public override long Length => -1;

    protected override ValueTask<int> ReadRawAsync(long position, Memory<byte> destination, CancellationToken cancellationToken) =>
        source.ReadAsync(destination, cancellationToken);
}

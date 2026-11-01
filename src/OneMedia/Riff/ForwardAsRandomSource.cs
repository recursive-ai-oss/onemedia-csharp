namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Lets the chunk readers, which ask for bytes by offset, run over a source that can only be read once from front to back. A read ahead of the
/// stream's position skips the bytes in between; a read of bytes that were handed out earlier is answered from the ranges kept while
/// <see cref="Keep"/> is set (the headers and small chunks read before the sample data). Reads of anything else return nothing.
/// </summary>
internal sealed class ForwardAsRandomSource(IForwardMediaSource inner, MediaLimits limits) : IMediaSource
{
    private readonly List<(long Offset, byte[] Data)> _kept = [];
    private long _keptBytes;

    /// <summary>Set while reads are remembered; cleared once the sample data starts, so streaming costs no memory.</summary>
    public bool Keep { get; set; } = true;

    public long Length => -1;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int total = 0;
        // First the part that was already handed out, from the ranges kept.
        while (total < destination.Length && offset + total < inner.Position)
        {
            int n = CopyKept(offset + total, destination[total..]);
            if (n == 0)
            {
                return total;
            }

            total += n;
        }

        if (total == destination.Length)
        {
            return total;
        }

        long at = offset + total;
        if (at > inner.Position && inner.Skip(at - inner.Position, cancellationToken) < at - inner.Position)
        {
            return total;
        }

        int fresh = 0;
        while (total + fresh < destination.Length)
        {
            int n = inner.Read(destination[(total + fresh)..], cancellationToken);
            if (n == 0)
            {
                break;
            }

            fresh += n;
        }

        if (Keep && fresh > 0)
        {
            _keptBytes += fresh;
            limits.CheckAllocation(_keptBytes, at);
            _kept.Add((at, destination.Slice(total, fresh).ToArray()));
        }

        return total + fresh;
    }

    private int CopyKept(long offset, Span<byte> destination)
    {
        foreach ((long start, byte[] data) in _kept)
        {
            if (offset >= start && offset < start + data.Length)
            {
                int n = (int)Math.Min(destination.Length, start + data.Length - offset);
                data.AsSpan((int)(offset - start), n).CopyTo(destination);
                return n;
            }
        }

        return 0;
    }

    public void Dispose() => inner.Dispose();
}

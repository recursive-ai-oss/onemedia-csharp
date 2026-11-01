namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Sequential-friendly access to a stretch of a source: one read fills a window, and nearby reads are answered from it. A tag with many
/// small frames costs one read per window, not one per frame, and a large picture in the middle costs a header read either side.
/// </summary>
internal sealed class TagWindow
{
    private const int WindowBytes = 64 * 1024;

    private readonly IMediaSource _source;
    private byte[] _buffer = [];
    private long _bufferStart = -1;
    private int _bufferLength;

    public TagWindow(IMediaSource source, long start, long length)
    {
        _source = source;
        Start = start;
        Length = length;
    }

    /// <summary>Absolute offset of the first byte of the region.</summary>
    public long Start { get; }

    /// <summary>Length of the region in bytes; shortened when the source turns out to end early.</summary>
    public long Length { get; private set; }

    /// <summary>
    /// Returns up to <paramref name="count"/> bytes at <paramref name="relative"/> (relative to <see cref="Start"/>). Fewer come back at the end
    /// of the region or the source. Requests larger than the window are read directly and are not cached.
    /// </summary>
    public ReadOnlySpan<byte> Get(long relative, int count)
    {
        if (relative < 0 || relative >= Length || count <= 0)
        {
            return default;
        }

        count = (int)Math.Min(count, Length - relative);
        long absolute = Start + relative;
        if (count <= WindowBytes)
        {
            if (_bufferStart < 0 || absolute < _bufferStart || absolute + count > _bufferStart + _bufferLength)
            {
                Fill(absolute, count);
            }

            if (absolute >= _bufferStart && absolute < _bufferStart + _bufferLength)
            {
                int at = (int)(absolute - _bufferStart);
                return _buffer.AsSpan(at, Math.Min(count, _bufferLength - at));
            }

            return default;
        }

        byte[] big = new byte[count];
        int n = _source.Read(absolute, big);
        if (n < count)
        {
            Length = Math.Min(Length, relative + n);
        }

        return big.AsSpan(0, n);
    }

    private void Fill(long absolute, int count)
    {
        int want = (int)Math.Min(WindowBytes, Math.Max(count, Start + Length - absolute));
        if (_buffer.Length < want)
        {
            _buffer = new byte[WindowBytes];
        }

        int n = _source.Read(absolute, _buffer.AsSpan(0, want));
        _bufferStart = absolute;
        _bufferLength = n;
        if (n < want && absolute + n < Start + Length)
        {
            Length = absolute + n - Start;
        }
    }
}

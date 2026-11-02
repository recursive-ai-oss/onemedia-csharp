namespace RecursiveAI.OneMedia.Streaming;

/// <summary>
/// Collects what a muxer writes for one segment, so the sink can be handed the segment as one span. The buffer is kept from segment to
/// segment, so memory holds one segment (the largest so far), not the length of the input; a segment past
/// <see cref="MediaLimits.MaxSegmentBytes"/> raises <see cref="MediaLimitExceededException"/> instead of growing without bound.
/// </summary>
internal sealed class SegmentCapture(MediaLimits limits, int initialBytes) : Stream, IsoBmff.IWriteSizeHint
{
    // Growing by doubling leaves a chain of large dead arrays behind; past 1 MiB the buffer grows to what is needed plus one eighth.
    private const int DoublingLimit = 1 << 20;

    private byte[] _buffer = new byte[initialBytes];
    private int _length;

    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    public void Reset() => _length = 0;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    // A muxer that knows how much it is about to write says so, and the buffer is sized once instead of growing through the writes.
    public void ExpectBytes(long count)
    {
        long needed = _length + count;
        limits.CheckSegmentBytes(needed, _length);
        if (needed > _buffer.Length)
        {
            // A quarter of slack: segments of one stream differ by a few percent, and a buffer that is a little too small for each next
            // segment would be replaced (and the old one left to the collector) every time.
            Array.Resize(ref _buffer, (int)Math.Min(needed + (needed / 4), Array.MaxLength));
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        long needed = (long)_length + buffer.Length;
        limits.CheckSegmentBytes(needed, _length);
        if (needed > _buffer.Length)
        {
            long size = needed <= DoublingLimit ? Math.Max((long)_buffer.Length * 2, needed) : needed + (needed / 8);
            Array.Resize(ref _buffer, (int)Math.Min(size, Array.MaxLength));
        }

        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }
}

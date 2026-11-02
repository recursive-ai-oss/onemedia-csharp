using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// The first bytes of an AVI file (RIFF header, header list and what follows it), read in one request and served from memory while the
/// header list is parsed; the parser revisits those bytes many times. Reads that are not wholly inside go to the source. The bytes sit in
/// a pooled buffer that <see cref="Dispose"/> gives back; the source underneath is not disposed.
/// </summary>
internal sealed class FrontCacheSource : IMediaSource
{
    private const int MinBytes = 32 * 1024;
    private const int MaxBytes = 128 * 1024;

    private readonly IMediaSource _inner;
    private byte[]? _bytes;
    private int _count;

    private FrontCacheSource(IMediaSource inner, byte[]? bytes, int count)
    {
        _inner = inner;
        _bytes = bytes;
        _count = count;
    }

    /// <summary>Reads the header list and a little beyond; a file that is too small, or a source that gives fewer bytes than asked, is served by the source alone.</summary>
    public static FrontCacheSource Load(IMediaSource inner, ReadOnlySpan<byte> head, CancellationToken cancellationToken)
    {
        long length = inner.Length;
        long want = MinBytes;
        // RIFF header (12 bytes), then LIST <size> hdrl: the header list ends 8 + size bytes after offset 12.
        if (head.Length >= 24 && head[12..16].SequenceEqual("LIST"u8))
        {
            want = Math.Max(want, 20L + BinaryPrimitives.ReadUInt32LittleEndian(head[16..20]) + 4096);
        }

        int count = (int)Math.Min(Math.Min(want, MaxBytes), length);
        if (count <= head.Length)
        {
            return new FrontCacheSource(inner, null, 0);
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent(count);
        int got = inner.Read(0, bytes.AsSpan(0, count), cancellationToken);
        if (got != count)
        {
            ArrayPool<byte>.Shared.Return(bytes);
            return new FrontCacheSource(inner, null, 0);
        }

        return new FrontCacheSource(inner, bytes, count);
    }

    public long Length => _inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (_bytes is { } bytes && offset >= 0 && offset + destination.Length <= _count)
        {
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return destination.Length;
        }

        return _inner.Read(offset, destination, cancellationToken);
    }

    public void Dispose()
    {
        if (_bytes is { } bytes)
        {
            _bytes = null;
            _count = 0;
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}

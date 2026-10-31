using System.Buffers;

namespace RecursiveAI.OneMedia;

/// <summary>
/// A readable byte source with random access: a file, a stream that can seek, a block of memory. Reads are positional, so a reader
/// can jump to a box at the end of a file without scanning what lies between.
/// </summary>
/// <remarks>
/// Implementations in this library are safe to call from several threads at once. Reads that fail for reasons of the medium
/// (a missing file, a dropped connection) throw <see cref="IOException"/>, not <see cref="MediaException"/>: the data was never
/// seen, so it cannot be called malformed.
/// </remarks>
public interface IMediaSource : IDisposable
{
    /// <summary>
    /// Current length in bytes. May grow between calls for a file that is still being written. -1 when the source cannot say.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Reads up to <c>destination.Length</c> bytes starting at <paramref name="offset"/>. Returns fewer only at the end of the
    /// source; returns 0 at or past the end.
    /// </summary>
    /// <param name="offset">Absolute position; must not be negative.</param>
    /// <param name="destination">Where to put the bytes.</param>
    /// <param name="cancellationToken">Observed before the read starts.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ObjectDisposedException">The source was disposed.</exception>
    int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// A readable byte source with random access whose reads are asynchronous: the shape of HTTP range requests, blob storage or the
/// browser's <c>Blob</c>. Each call is one range request as far as the instrumentation hook is concerned.
/// </summary>
public interface IAsyncMediaSource : IAsyncDisposable
{
    /// <summary>Length in bytes as the source reported it when opened (it may grow for a live resource). -1 when unknown.</summary>
    long Length { get; }

    /// <summary>
    /// Reads up to <c>destination.Length</c> bytes starting at <paramref name="offset"/>. Returns fewer only at the end of the
    /// source; returns 0 at or past the end.
    /// </summary>
    /// <param name="offset">Absolute position; must not be negative.</param>
    /// <param name="destination">Where to put the bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// A byte source that can only be read once, front to back: a pipe, a socket, a live broadcast. Formats that can be parsed in one
/// pass (MPEG-TS, MPEG-PS, FLV, Ogg, fragmented MP4, live Matroska) accept these.
/// </summary>
public interface IForwardMediaSource : IDisposable, IAsyncDisposable
{
    /// <summary>Number of bytes consumed so far; the absolute offset of the next byte.</summary>
    long Position { get; }

    /// <summary>
    /// Reads at least one byte unless the source has ended, and at most <c>destination.Length</c>. May return fewer than
    /// requested while more data is still to come (so a live source never blocks waiting to fill a buffer).
    /// </summary>
    /// <param name="destination">Where to put the bytes.</param>
    /// <param name="cancellationToken">Observed before the read starts.</param>
    /// <returns>The number of bytes read; 0 means the end.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    int Read(Span<byte> destination, CancellationToken cancellationToken = default);

    /// <summary>Asynchronous <see cref="Read"/>.</summary>
    /// <param name="destination">Where to put the bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; 0 means the end.</returns>
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// The hook through which a harness counts what a source is asked to read: bytes and read calls (for a ranged source, each call is
/// one range request). Wrap a source in <see cref="ObservedMediaSource"/> or <see cref="ObservedForwardSource"/>.
/// </summary>
public interface IMediaReadObserver
{
    /// <summary>Called after every read call on the wrapped source.</summary>
    /// <param name="offset">Absolute offset the read started at.</param>
    /// <param name="requested">Bytes asked for.</param>
    /// <param name="returned">Bytes delivered.</param>
    void OnRead(long offset, int requested, int returned);
}

/// <summary>Helpers over the source interfaces for reads that must be complete.</summary>
public static class MediaSourceExtensions
{
    /// <summary>Reads exactly <c>destination.Length</c> bytes at <paramref name="offset"/> or reports that the source ended first.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Absolute position.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Observed before the read starts.</param>
    /// <returns>False when the source ends before the buffer is full.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryReadExactly(this IMediaSource source, long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Read(offset, destination, cancellationToken) == destination.Length;
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes at <paramref name="offset"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Absolute position.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Observed before the read starts.</param>
    /// <exception cref="MediaFormatException">The source ends first (kind <see cref="DefectKind.Truncated"/>).</exception>
    public static void ReadExactly(this IMediaSource source, long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (!source.TryReadExactly(offset, destination, cancellationToken))
        {
            throw Truncated(offset, destination.Length);
        }
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes at <paramref name="offset"/> or reports that the source ended first.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Absolute position.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when the source ends before the buffer is full.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static async ValueTask<bool> TryReadExactlyAsync(this IAsyncMediaSource source, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await source.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false) == destination.Length;
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes at <paramref name="offset"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Absolute position.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="MediaFormatException">The source ends first (kind <see cref="DefectKind.Truncated"/>).</exception>
    public static async ValueTask ReadExactlyAsync(this IAsyncMediaSource source, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (!await source.TryReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false))
        {
            throw Truncated(offset, destination.Length);
        }
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes from the current position or reports that the source ended first.</summary>
    /// <param name="source">The source.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Observed before each read.</param>
    /// <returns>False when the source ends before the buffer is full.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryReadExactly(this IForwardMediaSource source, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        while (!destination.IsEmpty)
        {
            int n = source.Read(destination, cancellationToken);
            if (n == 0)
            {
                return false;
            }

            destination = destination[n..];
        }

        return true;
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes from the current position or reports that the source ended first.</summary>
    /// <param name="source">The source.</param>
    /// <param name="destination">Filled completely on success.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when the source ends before the buffer is full.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static async ValueTask<bool> TryReadExactlyAsync(this IForwardMediaSource source, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        while (!destination.IsEmpty)
        {
            int n = await source.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            destination = destination[n..];
        }

        return true;
    }

    /// <summary>Discards <paramref name="count"/> bytes from a forward-only source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="count">Bytes to skip; must not be negative.</param>
    /// <param name="cancellationToken">Observed before each read.</param>
    /// <returns>The number of bytes actually skipped, fewer than <paramref name="count"/> only if the source ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static long Skip(this IForwardMediaSource source, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long skipped = 0;
            while (skipped < count)
            {
                int n = source.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, count - skipped)), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                skipped += n;
            }

            return skipped;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Discards <paramref name="count"/> bytes from a forward-only source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="count">Bytes to skip; must not be negative.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes actually skipped, fewer than <paramref name="count"/> only if the source ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static async ValueTask<long> SkipAsync(this IForwardMediaSource source, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long skipped = 0;
            while (skipped < count)
            {
                int n = await source.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, count - skipped)), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                skipped += n;
            }

            return skipped;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static MediaFormatException Truncated(long offset, int wanted) =>
        new($"The data ends before the {wanted} bytes at offset {offset}.") { Offset = offset, Kind = DefectKind.Truncated };
}

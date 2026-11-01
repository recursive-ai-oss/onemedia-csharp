using System.Buffers;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneMedia;

/// <summary>A run of bytes in a source: where it starts and how long it is. Structure trees hold these instead of content.</summary>
/// <param name="Offset">Absolute offset of the first byte, or -1 for <see cref="None"/>.</param>
/// <param name="Length">Number of bytes.</param>
public readonly record struct ByteRange(long Offset, long Length)
{
    /// <summary>No range: the bytes are not contiguous in the source (for example a packet reassembled from several transport packets).</summary>
    public static ByteRange None => new(-1, 0);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Offset < 0;

    /// <summary>Offset of the first byte after the range.</summary>
    public long End => Offset + Length;
}

/// <summary>Properties of a packet.</summary>
[Flags]
public enum PacketAttributes
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>A random-access point: decoding can start here (a sync sample, an IDR picture, every audio frame).</summary>
    Keyframe = 1 << 0,

    /// <summary>Decode it but do not present it (preroll or priming that edits or padding cut off).</summary>
    Discard = 1 << 1,

    /// <summary>The container flags the data as damaged or the reader detected damage; the bytes are delivered as found.</summary>
    Corrupt = 1 << 2,

    /// <summary>No other packet depends on this one, so a player may drop it under load.</summary>
    Disposable = 1 << 3,

    /// <summary>Timestamps jump here relative to the previous packet of the track (a splice, a timestamp reset).</summary>
    Discontinuity = 1 << 4,
}

/// <summary>Everything about a packet except its bytes.</summary>
/// <param name="TrackIndex">Index into the demuxer's tracks.</param>
/// <param name="Pts">Presentation time, or <see cref="MediaTime.Unknown"/>.</param>
/// <param name="Dts">Decode time; equals <paramref name="Pts"/> when there is no reordering, <see cref="MediaTime.Unknown"/> if not stated.</param>
/// <param name="Duration">Duration, or <see cref="MediaTime.Unknown"/>.</param>
/// <param name="Flags">Keyframe, discard and similar flags.</param>
/// <param name="SourceRange">Where the payload sits in the source when it is stored contiguously and unmodified, else <see cref="ByteRange.None"/>. Remux can copy such ranges straight across.</param>
/// <param name="Size">Payload length in bytes.</param>
/// <param name="DiscardPadding">Media time to cut from the end of this packet's decoded output (positive) or from its start
/// (negative), as a Matroska DiscardPadding says; zero when none.</param>
public readonly record struct PacketInfo(
    int TrackIndex,
    MediaTime Pts,
    MediaTime Dts,
    MediaTime Duration,
    PacketAttributes Flags,
    ByteRange SourceRange,
    int Size,
    MediaTime DiscardPadding = default)
{
    /// <summary>True when <see cref="PacketAttributes.Keyframe"/> is set.</summary>
    public bool IsKeyframe => (Flags & PacketAttributes.Keyframe) != 0;
}

/// <summary>
/// A packet handed out by the allocation-free pull path: its description and a view of its bytes. The bytes live in a buffer the
/// demuxer reuses, so they are valid only until the next call on the demuxer that produced the view. Copy them (or use
/// <see cref="DemuxerExtensions.TryReadOwnedPacket"/>) to keep them.
/// </summary>
public readonly ref struct PacketView
{
    /// <summary>Creates a view.</summary>
    /// <param name="info">The packet's description.</param>
    /// <param name="data">The payload bytes.</param>
    public PacketView(PacketInfo info, ReadOnlySpan<byte> data)
    {
        Info = info;
        Data = data;
    }

    /// <summary>The packet's description.</summary>
    public PacketInfo Info { get; }

    /// <summary>The payload; valid until the next call on the demuxer.</summary>
    public ReadOnlySpan<byte> Data { get; }
}

/// <summary>
/// A packet that owns its bytes, rented from a <see cref="MemoryPool{T}"/>. Dispose it to return the buffer.
/// </summary>
/// <param name="Info">The packet's description.</param>
/// <param name="Owner">The rented buffer; at least <c>Info.Size</c> bytes long.</param>
public readonly record struct OwnedPacket(PacketInfo Info, IMemoryOwner<byte> Owner) : IDisposable
{
    /// <summary>The payload, exactly <c>Info.Size</c> bytes.</summary>
    public ReadOnlyMemory<byte> Data => Owner.Memory[..Info.Size];

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose() => Owner.Dispose();
}

/// <summary>How <see cref="IMediaContainer"/> seeks resolve a target time to a packet.</summary>
public enum SeekMode
{
    /// <summary>Each track resumes at its last keyframe at or before the target.</summary>
    PreviousKeyframe,

    /// <summary>Each track resumes at its first keyframe at or after the target.</summary>
    NextKeyframe,
}

/// <summary>What a demuxer knows about the file it has open, before any packet is read.</summary>
public interface IMediaContainer
{
    /// <summary>Which format the data was read as.</summary>
    MediaFormatId Format { get; }

    /// <summary>The tracks, in container order. <see cref="MediaTrack.Index"/> equals the position in this list.</summary>
    IReadOnlyList<MediaTrack> Tracks { get; }

    /// <summary>Duration of the longest track in presentation time, or <see cref="MediaTime.Unknown"/>.</summary>
    MediaTime Duration { get; }

    /// <summary>Deviations found so far. Grows as reading proceeds; the same object for the demuxer's lifetime.</summary>
    DefectLog Defects { get; }

    /// <summary>True when <c>Seek</c> is supported (the source is seekable and the container has, or can build, an index).</summary>
    bool CanSeek { get; }

    /// <summary>
    /// True when the content is protected (encrypted or under digital rights management). A protected file still lists its tracks and
    /// metadata, but its demuxer delivers no payloads. False unless the container says otherwise.
    /// </summary>
    bool IsProtected => false;

    /// <summary>
    /// Turns a track off or on. Packets of a disabled track are skipped without reading their payload, so reading one stream of a
    /// large file costs only that stream's bytes. All tracks start enabled.
    /// </summary>
    /// <param name="trackIndex">Index into <see cref="Tracks"/>.</param>
    /// <param name="enabled">Whether the track's packets are delivered.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="trackIndex"/> is not a track.</exception>
    void SetTrackEnabled(int trackIndex, bool enabled);
}

/// <summary>
/// Reads packets from a container over a synchronous source. Packets of all enabled tracks come in the container's storage order
/// (interleaved as written). Not thread-safe.
/// </summary>
public interface IDemuxer : IMediaContainer, IDisposable
{
    /// <summary>
    /// Reads the next packet. The returned bytes are in a buffer the demuxer reuses and are valid until the next call to this
    /// demuxer, so this path allocates nothing per packet once the buffer has grown to the largest packet.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>False at the end of the data.</returns>
    /// <exception cref="MediaFormatException">The data is malformed beyond recovery.</exception>
    /// <exception cref="MediaLimitExceededException">A packet is larger than <see cref="MediaLimits.MaxPacketBytes"/>.</exception>
    bool TryReadPacket(out PacketView packet);

    /// <summary>
    /// Repositions so reading continues from a keyframe near <paramref name="target"/> in every enabled track.
    /// </summary>
    /// <param name="target">The wanted time on the file's timeline.</param>
    /// <param name="mode">Whether to land before or after the target.</param>
    /// <returns>The earliest decode time of the packets that will now be delivered; <see cref="MediaTime.Unknown"/> if none remain.</returns>
    /// <exception cref="NotSupportedException"><see cref="IMediaContainer.CanSeek"/> is false.</exception>
    MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe);
}

/// <summary>
/// Reads packets from a container over an asynchronous (ranged or remote) source. Call <see cref="ReadPacketAsync"/>, and when it
/// returns true read <see cref="Current"/>.
/// </summary>
public interface IAsyncDemuxer : IMediaContainer, IAsyncDisposable
{
    /// <summary>The packet found by the last successful <see cref="ReadPacketAsync"/>; valid until the next call.</summary>
    PacketView Current { get; }

    /// <summary>Advances to the next packet.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False at the end of the data.</returns>
    /// <exception cref="MediaFormatException">The data is malformed beyond recovery.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default);

    /// <summary>Asynchronous <see cref="IDemuxer.Seek"/>.</summary>
    /// <param name="target">The wanted time on the file's timeline.</param>
    /// <param name="mode">Whether to land before or after the target.</param>
    /// <param name="cancellationToken">Cancels the seek.</param>
    /// <returns>The earliest decode time of the packets that will now be delivered; <see cref="MediaTime.Unknown"/> if none remain.</returns>
    /// <exception cref="NotSupportedException"><see cref="IMediaContainer.CanSeek"/> is false.</exception>
    ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default);
}

/// <summary>Owned-packet and <see cref="IAsyncEnumerable{T}"/> layers on top of the pull path.</summary>
public static class DemuxerExtensions
{
    /// <summary>Reads the next packet into a buffer rented from <paramref name="pool"/>, so it stays valid after further reads.</summary>
    /// <param name="demuxer">The demuxer.</param>
    /// <param name="pool">Where buffers come from; <see cref="MemoryPool{T}.Shared"/> if null.</param>
    /// <param name="packet">The packet; the caller disposes it.</param>
    /// <returns>False at the end of the data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer"/> is null.</exception>
    public static bool TryReadOwnedPacket(this IDemuxer demuxer, MemoryPool<byte>? pool, out OwnedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        if (!demuxer.TryReadPacket(out PacketView view))
        {
            packet = default;
            return false;
        }

        packet = Own(view, pool);
        return true;
    }

    /// <summary>Streams the remaining packets as owned packets. The consumer disposes each one.</summary>
    /// <param name="demuxer">The demuxer.</param>
    /// <param name="pool">Where buffers come from; <see cref="MemoryPool{T}.Shared"/> if null.</param>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>The packets in storage order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer"/> is null.</exception>
    public static async IAsyncEnumerable<OwnedPacket> ReadPacketsAsync(
        this IDemuxer demuxer,
        MemoryPool<byte>? pool = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!demuxer.TryReadOwnedPacket(pool, out OwnedPacket packet))
            {
                yield break;
            }

            yield return packet;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>Reads the next packet of an asynchronous demuxer into a rented buffer.</summary>
    /// <param name="demuxer">The demuxer.</param>
    /// <param name="pool">Where buffers come from; <see cref="MemoryPool{T}.Shared"/> if null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The packet, or null at the end of the data. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer"/> is null.</exception>
    public static async ValueTask<OwnedPacket?> ReadOwnedPacketAsync(this IAsyncDemuxer demuxer, MemoryPool<byte>? pool = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        if (!await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Own(demuxer.Current, pool);
    }

    /// <summary>Streams the remaining packets of an asynchronous demuxer as owned packets. The consumer disposes each one.</summary>
    /// <param name="demuxer">The demuxer.</param>
    /// <param name="pool">Where buffers come from; <see cref="MemoryPool{T}.Shared"/> if null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The packets in storage order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="demuxer"/> is null.</exception>
    public static async IAsyncEnumerable<OwnedPacket> ReadPacketsAsync(
        this IAsyncDemuxer demuxer,
        MemoryPool<byte>? pool = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        while (await demuxer.ReadOwnedPacketAsync(pool, cancellationToken).ConfigureAwait(false) is { } packet)
        {
            yield return packet;
        }
    }

    private static OwnedPacket Own(PacketView view, MemoryPool<byte>? pool)
    {
        IMemoryOwner<byte> owner = (pool ?? MemoryPool<byte>.Shared).Rent(Math.Max(view.Data.Length, 1));
        view.Data.CopyTo(owner.Memory.Span);
        return new OwnedPacket(view.Info with { Size = view.Data.Length }, owner);
    }
}

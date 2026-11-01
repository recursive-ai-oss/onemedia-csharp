namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>Presents a ranged or remote source to the sequential readers, which read it in large windows (256 KiB), so each blocking call is one range request.</summary>
internal sealed class BlockingSourceAdapter(IAsyncMediaSource inner) : IMediaSource
{
    public long Length { get; } = inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        // The readers run on a pool thread (see AsyncDemuxer), so waiting here does not block the caller's context.
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            int n = inner.ReadAsync(offset, rented.AsMemory(0, destination.Length), cancellationToken).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, n).CopyTo(destination);
            return n;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose() => inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// An <see cref="IAsyncDemuxer"/> over a sequential reader that pulls from a ranged source. Every read of the underlying demuxer runs
/// on the thread pool, so the awaiting caller is never blocked; one range request is issued per 256 KiB window.
/// </summary>
internal sealed class AsyncDemuxer(IDemuxer inner) : IAsyncDemuxer, Metadata.IAsyncMetadataProvider
{
    private PacketInfo _info;
    private byte[] _data = new byte[4096];
    private int _length;

    public MediaFormatId Format => inner.Format;

    public IReadOnlyList<MediaTrack> Tracks => inner.Tracks;

    public MediaTime Duration => inner.Duration;

    public DefectLog Defects => inner.Defects;

    public bool CanSeek => inner.CanSeek;

    public PacketView Current => new(_info, _data.AsSpan(0, _length));

    public void SetTrackEnabled(int trackIndex, bool enabled) => inner.SetTrackEnabled(trackIndex, enabled);

    public async ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        // The packet is copied into this object's buffer, because the underlying view is only valid until its next read.
        return await Task.Run(() =>
        {
            if (!inner.TryReadPacket(out PacketView p))
            {
                return false;
            }

            if (_data.Length < p.Data.Length)
            {
                _data = new byte[Math.Max(p.Data.Length, _data.Length * 2)];
            }

            p.Data.CopyTo(_data);
            _length = p.Data.Length;
            _info = p.Info;
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) =>
        await Task.Run(() => inner.Seek(target, mode), cancellationToken).ConfigureAwait(false);

    public async ValueTask<Metadata.MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) =>
        await Task.Run(() => (inner as Metadata.IMetadataProvider ?? throw new NotSupportedException("This format has no metadata reader.")).ReadMetadata(cancellationToken), cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a sequential demuxer over a ranged source on a pool thread and wraps it.</summary>
    public static async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, Func<IMediaSource, IDemuxer> open, CancellationToken cancellationToken)
    {
        BlockingSourceAdapter adapter = new(source);
        IDemuxer d = await Task.Run(() => open(adapter), cancellationToken).ConfigureAwait(false);
        return new AsyncDemuxer(d);
    }
}

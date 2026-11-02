namespace RecursiveAI.OneMedia.Avi;

/// <summary>A packet view that stays valid until the next read; implemented by demuxers that can be wrapped for asynchronous use.</summary>
internal interface ICurrentPacket
{
    PacketView Current { get; }
}

/// <summary>
/// Presents a ranged source (HTTP range requests, blob storage) to the synchronous readers: each read waits for the range to arrive.
/// The asynchronous AVI and DV demuxers use it, so every request is still a ranged read of just the bytes the reader asks for;
/// the calling thread waits for each one, which a single-threaded host (browser WebAssembly) cannot do.
/// </summary>
internal sealed class BlockingMediaSource(IAsyncMediaSource inner) : IMediaSource
{
    public long Length => inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            int n = Input.EngineHost.Wait(inner.ReadAsync(offset, buffer.AsMemory(0, destination.Length), cancellationToken));
            buffer.AsSpan(0, n).CopyTo(destination);
            return n;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        // The wrapped source is disposed asynchronously by the owner.
    }
}

/// <summary>An asynchronous demuxer over a synchronous one that reads through a <see cref="BlockingMediaSource"/>.</summary>
internal sealed class SyncBackedAsyncDemuxer<T> : IAsyncDemuxer, Metadata.IAsyncMetadataProvider where T : class, IDemuxer, ICurrentPacket, Metadata.IMetadataProvider
{
    private readonly T _inner;
    private readonly IAsyncDisposable? _owned;

    public SyncBackedAsyncDemuxer(T inner, IAsyncDisposable? owned)
    {
        _inner = inner;
        _owned = owned;
    }

    public MediaFormatId Format => _inner.Format;

    public IReadOnlyList<MediaTrack> Tracks => _inner.Tracks;

    public MediaTime Duration => _inner.Duration;

    public DefectLog Defects => _inner.Defects;

    public bool CanSeek => _inner.CanSeek;

    public PacketView Current => _inner.Current;

    public void SetTrackEnabled(int trackIndex, bool enabled) => _inner.SetTrackEnabled(trackIndex, enabled);

    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default) => new(_inner.TryReadPacket(out _));

    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) => new(_inner.Seek(target, mode));

    public ValueTask<Metadata.MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) => new(_inner.ReadMetadata(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        _inner.Dispose();
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}

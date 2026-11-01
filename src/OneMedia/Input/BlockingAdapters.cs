using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Input;

/// <summary>
/// Presents an asynchronous ranged source to a reader written against <see cref="IMediaSource"/>. Each read waits for the ranged read to finish,
/// so it is only used from a pool thread (see <see cref="BlockingAsyncDemuxer"/>). The wrapped source is not owned.
/// </summary>
internal sealed class BlockingMediaSource(IAsyncMediaSource source) : IMediaSource
{
    public long Length => source.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            int n = EngineHost.Wait(source.ReadAsync(offset, scratch.AsMemory(0, destination.Length), cancellationToken));
            scratch.AsSpan(0, n).CopyTo(destination);
            return n;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The asynchronous face of a reader that is written synchronously (native FLAC, FLV): opening runs on a pool thread, and the reads inside a
/// packet wait for the ranged source in 64 KiB windows. Packets are returned on the calling thread.
/// </summary>
internal sealed class BlockingAsyncDemuxer : IAsyncDemuxer, IAsyncMetadataProvider
{
    private readonly IDemuxer _inner;
    private readonly IAsyncDisposable? _owned;

    private BlockingAsyncDemuxer(IDemuxer inner, IAsyncDisposable? owned)
    {
        _inner = inner;
        _owned = owned;
    }

    public static async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, Func<IMediaSource, MediaOpenOptions, CancellationToken, IDemuxer> open, CancellationToken ct)
    {
        MediaOpenOptions inner = options with { LeaveOpen = true };
        IDemuxer demuxer;
        try
        {
            demuxer = await Task.Run(() => open(new BlockingMediaSource(source), inner, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            if (!options.LeaveOpen)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new BlockingAsyncDemuxer(demuxer, options.LeaveOpen ? null : source);
    }

    public MediaFormatId Format => _inner.Format;

    public IReadOnlyList<MediaTrack> Tracks => _inner.Tracks;

    public MediaTime Duration => _inner.Duration;

    public DefectLog Defects => _inner.Defects;

    public bool CanSeek => _inner.CanSeek;

    // The inner demuxer's packet is only valid until its next call; one reused copy lets Current stay valid until the next ReadPacketAsync.
    private PacketInfo _info;
    private byte[] _data = [];
    private int _length;
    private bool _has;

    public PacketView Current => _has ? new PacketView(_info, _data.AsSpan(0, _length)) : default;

    public void SetTrackEnabled(int trackIndex, bool enabled) => _inner.SetTrackEnabled(trackIndex, enabled);

    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool ok = _inner.TryReadPacket(out PacketView p);
        _has = ok;
        if (ok)
        {
            _info = p.Info;
            if (_data.Length < p.Data.Length)
            {
                _data = new byte[Math.Max(p.Data.Length, _data.Length * 2)];
            }

            p.Data.CopyTo(_data);
            _length = p.Data.Length;
        }

        return new ValueTask<bool>(ok);
    }

    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) =>
        new(_inner.Seek(target, mode));

    public ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) =>
        new(((IMetadataProvider)_inner).ReadMetadata(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        _inner.Dispose();
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}

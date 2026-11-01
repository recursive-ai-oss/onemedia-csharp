using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Thrown by <see cref="RangeCache"/> when a synchronous reader asks for bytes that have not been fetched yet. The asynchronous wrapper catches it,
/// fetches the range and runs the step again; it never reaches callers of the public API except through metadata read lazily after the fact.
/// </summary>
internal sealed class RangeNotFetchedException(long offset, int length) : NotSupportedException("The requested bytes have not been fetched from the ranged source; use the asynchronous read methods.")
{
    public long RangeOffset { get; } = offset;

    public int RangeLength { get; } = length;
}

/// <summary>
/// Lets the synchronous chunk readers run over a ranged source. Reads are answered from ranges fetched earlier; a read of anything else throws
/// <see cref="RangeNotFetchedException"/> so the caller can fetch it and try again. Ranges fetched while <see cref="Pin"/> is set (opening, tags) are
/// kept; payload ranges live in a window of two entries, so memory stays constant however long the file is.
/// </summary>
internal sealed class RangeCache(IAsyncMediaSource source) : IMediaSource
{
    private const int HeaderBlock = 16 * 1024;
    private const int PayloadBlock = 64 * 1024;
    private readonly List<Block> _pinned = [];
    private readonly Block?[] _window = new Block?[2];
    private int _nextWindow;
    private long _eof = -1;

    public bool Pin { get; set; } = true;

    public long Length => source.Length >= 0 ? source.Length : _eof;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        long length = Length;
        int want = destination.Length;
        if (length >= 0)
        {
            if (offset >= length)
            {
                return 0;
            }

            want = (int)Math.Min(want, length - offset);
        }

        if (want == 0)
        {
            return 0;
        }

        Block? hit = Find(offset, want);
        if (hit is null)
        {
            throw new RangeNotFetchedException(offset, want);
        }

        int n = (int)Math.Min(want, hit.End - offset);
        hit.Data.AsSpan((int)(offset - hit.Offset), n).CopyTo(destination);
        return n;
    }

    private Block? Find(long offset, int want)
    {
        foreach (Block b in _pinned)
        {
            if (b.Covers(offset, want))
            {
                return b;
            }
        }

        foreach (Block? b in _window)
        {
            if (b is not null && b.Covers(offset, want))
            {
                return b;
            }
        }

        // Past a short fetch the file has ended.
        return null;
    }

    /// <summary>Fetches the bytes a failed read asked for (more than that: a block, so neighbouring reads are free).</summary>
    public async ValueTask FetchAsync(RangeNotFetchedException need, CancellationToken cancellationToken)
    {
        int block = Pin ? HeaderBlock : PayloadBlock;
        int size = Math.Max(need.RangeLength, block);
        long length = source.Length;
        if (length >= 0)
        {
            size = (int)Math.Min(size, length - need.RangeOffset);
        }

        byte[] data = new byte[size];
        int got = 0;
        while (got < size)
        {
            int n = await source.ReadAsync(need.RangeOffset + got, data.AsMemory(got, size - got), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        bool eof = got < size;
        if (eof)
        {
            _eof = need.RangeOffset + got;
        }

        Block fresh = new(need.RangeOffset, got == size ? data : data.AsSpan(0, got).ToArray(), eof);
        if (Pin)
        {
            _pinned.Add(fresh);
        }
        else
        {
            _window[_nextWindow] = fresh;
            _nextWindow = (_nextWindow + 1) % _window.Length;
        }
    }

    public void Dispose()
    {
    }

    private sealed class Block(long offset, byte[] data, bool eof)
    {
        public long Offset { get; } = offset;

        public byte[] Data { get; } = data;

        public long End => Offset + Data.Length;

        public bool Covers(long offset, int want) => offset >= Offset && (offset + want <= End || (eof && offset < End));
    }
}

/// <summary>
/// Reads one-stream audio files (WAVE, AIFF, CAF) over an asynchronous ranged source. The chunk headers are fetched in 16 KiB blocks while the file
/// is opened and kept; packets are then fetched in 64 KiB blocks, two at a time, so a file of any size is read in constant memory and the sample
/// data costs one request per block. The format logic is the synchronous reader's, run over the fetched ranges.
/// </summary>
public sealed class AudioFileAsyncDemuxer : IAsyncDemuxer, IAsyncMetadataProvider
{
    private readonly AudioFileDemuxer _inner;
    private readonly RangeCache _cache;
    private readonly IAsyncMediaSource _source;
    private readonly bool _leaveOpen;
    private readonly MediaLimits _limits;
    private PacketInfo _current;
    private bool _disposed;

    private AudioFileAsyncDemuxer(AudioFileDemuxer inner, RangeCache cache, IAsyncMediaSource source, MediaOpenOptions options)
    {
        _inner = inner;
        _cache = cache;
        _source = source;
        _leaveOpen = options.LeaveOpen;
        _limits = options.Limits;
    }

    /// <summary>Opens a ranged source with the given synchronous opener. Used by the three format entry points.</summary>
    internal static async ValueTask<AudioFileAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options, Func<IMediaSource, MediaOpenOptions, AudioFileDemuxer> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        RangeCache cache = new(source);
        try
        {
            // The first pass finds which ranges the headers need, with its defects thrown away; the second runs on cached bytes only.
            MediaOpenOptions scratch = options with { Defects = new DefectLog(options.Limits), LeaveOpen = true };
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    open(cache, scratch).Dispose();
                    break;
                }
                catch (RangeNotFetchedException need)
                {
                    await cache.FetchAsync(need, cancellationToken).ConfigureAwait(false);
                }
            }

            AudioFileDemuxer inner = open(cache, options with { LeaveOpen = true });
            cache.Pin = false;
            return new AudioFileAsyncDemuxer(inner, cache, source, options);
        }
        catch
        {
            if (!options.LeaveOpen)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => _inner.Format;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _inner.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _inner.Duration;

    /// <inheritdoc />
    public DefectLog Defects => _inner.Defects;

    /// <inheritdoc />
    public bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public PacketView Current => new(_current, _inner.LastPacketBytes(_current.Size));

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _inner.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public async ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool got = _inner.TryReadPacket(out PacketView view);
                _current = view.Info;
                return got;
            }
            catch (RangeNotFetchedException need)
            {
                await _cache.FetchAsync(need, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(_inner.Seek(target, mode));
    }

    /// <summary>
    /// Reads the tags and basic facts, fetching the tag chunks (which may sit after the sample data) from the ranged source. Each chunk is limited by
    /// <see cref="MediaLimits.MaxTagBytes"/>; the sample data is not read.
    /// </summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The metadata.</returns>
    public async ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool pin = _cache.Pin;
        _cache.Pin = true;
        try
        {
            // First pass with defects thrown away, to fetch every range; then the real pass over cached bytes.
            _inner.ScratchDefects = new DefectLog(_limits);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        _ = _inner.ReadMetadata(cancellationToken);
                        break;
                    }
                    catch (RangeNotFetchedException need)
                    {
                        await _cache.FetchAsync(need, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _inner.ScratchDefects = null;
            }

            return _inner.ReadMetadata(cancellationToken);
        }
        finally
        {
            _cache.Pin = pin;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
        if (!_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }
}

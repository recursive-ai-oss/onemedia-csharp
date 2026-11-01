using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>Thrown by <see cref="WindowedSource"/> when a read falls outside the bytes fetched so far; the asynchronous demuxer fetches them and tries again.</summary>
internal sealed class NeedRangeException(long offset, int length) : Exception("The bytes are not fetched yet.")
{
    public long Offset { get; } = offset;

    public int Length { get; } = length;
}

/// <summary>
/// A synchronous view over a few fetched windows of an asynchronous source. Reads inside a window are answered from memory; a read outside
/// raises <see cref="NeedRangeException"/> so the owner can fetch the range with a real asynchronous request. Only a handful of windows are kept, so
/// memory stays constant however large the source is; the first (head) and last (tail) windows stay because structural reads come back to them.
/// </summary>
internal sealed class WindowedSource(IAsyncMediaSource inner) : IMediaSource
{
    public const int WindowBytes = 256 * 1024;
    private const int MaxWindows = 5;

    private readonly List<(long Start, byte[] Data, int Length)> _windows = [];

    public long Length => inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.IsEmpty || (Length >= 0 && offset >= Length))
        {
            return 0;
        }

        foreach ((long start, byte[] data, int length) in _windows)
        {
            if (offset >= start && offset < start + length)
            {
                int n = (int)Math.Min(destination.Length, start + length - offset);
                data.AsSpan((int)(offset - start), n).CopyTo(destination);
                return n;
            }
        }

        throw new NeedRangeException(offset, destination.Length);
    }

    /// <summary>Fetches a window of at least <paramref name="length"/> bytes at <paramref name="offset"/> (bounded by the source's end).</summary>
    public async ValueTask FetchAsync(long offset, int length, CancellationToken cancellationToken)
    {
        int want = Math.Max(length, WindowBytes);
        if (inner.Length >= 0)
        {
            want = (int)Math.Min(want, Math.Max(inner.Length - offset, 0));
        }

        if (want <= 0)
        {
            return;
        }

        byte[] buffer = new byte[want];
        int got = 0;
        while (got < want)
        {
            int n = await inner.ReadAsync(offset + got, buffer.AsMemory(got, want - got), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        if (got == 0)
        {
            return;
        }

        if (_windows.Count >= MaxWindows)
        {
            // Keep the head and tail windows (indexes 0 and 1 are pinned when they were fetched first); drop the oldest of the rest.
            _windows.RemoveAt(Math.Min(2, _windows.Count - 1));
        }

        _windows.Add((offset, buffer, got));
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Reads an audio elementary stream (MPEG audio, ADTS, AC-3/E-AC-3, AMR) over a ranged or remote source. The stream readers are synchronous; this
/// wraps one over <see cref="WindowedSource"/>, so every request is a real asynchronous range read of the start, the end (trailing tags) and
/// then 256 KiB windows as packets are pulled. Packets are copied into a buffer that is reused, so <see cref="Current"/> is valid until the next call.
/// </summary>
public sealed class AudioStreamAsyncDemuxer : IAsyncDemuxer, IAsyncMetadataProvider
{
    private const int TailBytes = 16 * 1024;
    private const int MaxRetries = 4096;

    private readonly IAsyncMediaSource _source;
    private readonly WindowedSource _window;
    private readonly AudioStreamDemuxer _inner;
    private readonly bool _leaveOpen;
    private byte[] _buffer = new byte[4096];
    private PacketInfo _current;
    private bool _disposed;

    private AudioStreamAsyncDemuxer(IAsyncMediaSource source, WindowedSource window, AudioStreamDemuxer inner, bool leaveOpen)
    {
        _source = source;
        _window = window;
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Opens an asynchronous source with the synchronous reader <paramref name="open"/>, fetching the head and tail first so a normal file opens in two requests.</summary>
    internal static async ValueTask<AudioStreamAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, Func<IMediaSource, MediaOpenOptions, CancellationToken, AudioStreamDemuxer> open, CancellationToken cancellationToken, WindowedSource? fetched = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        WindowedSource window = fetched ?? new(source);
        try
        {
            if (fetched is null)
            {
                await window.FetchAsync(0, WindowedSource.WindowBytes, cancellationToken).ConfigureAwait(false);
            }

            if (source.Length > WindowedSource.WindowBytes)
            {
                long tail = Math.Max(WindowedSource.WindowBytes, source.Length - TailBytes);
                await window.FetchAsync(tail, TailBytes, cancellationToken).ConfigureAwait(false);
            }

            for (int attempt = 0; ; attempt++)
            {
                // A repeated open (only when a tag is larger than the fetched head) may log an open-time defect twice.
                DefectLog log = options.Defects ?? new DefectLog(options.Limits);
                MediaOpenOptions attemptOptions = new() { Limits = options.Limits, Defects = log, LeaveOpen = true };
                try
                {
                    AudioStreamDemuxer inner = open(window, attemptOptions, cancellationToken);
                    return new AudioStreamAsyncDemuxer(source, window, inner, options.LeaveOpen);
                }
                catch (NeedRangeException need) when (attempt < MaxRetries)
                {
                    await window.FetchAsync(need.Offset, need.Length, cancellationToken).ConfigureAwait(false);
                }
            }
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

    /// <summary>Reads up to <paramref name="length"/> bytes at <paramref name="offset"/> through the window, fetching what is missing.</summary>
    internal static async ValueTask<byte[]> PeekAsync(WindowedSource window, long offset, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        while (true)
        {
            try
            {
                int total = 0;
                while (total < length)
                {
                    int n = window.Read(offset + total, buffer.AsSpan(total), cancellationToken);
                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                }

                return buffer.AsSpan(0, total).ToArray();
            }
            catch (NeedRangeException need)
            {
                await window.FetchAsync(need.Offset, need.Length, cancellationToken).ConfigureAwait(false);
            }
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
    public bool CanSeek => _source.Length >= 0;

    /// <inheritdoc cref="AudioStreamDemuxer.IsDurationEstimated" />
    public bool IsDurationEstimated => _inner.IsDurationEstimated;

    /// <inheritdoc />
    public PacketView Current => new(_current, _buffer.AsSpan(0, _current.Size));

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _inner.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public async ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int attempt = 0; ; attempt++)
        {
            _inner.SetCancellation(cancellationToken);
            try
            {
                if (!_inner.TryReadPacket(out PacketView view))
                {
                    return false;
                }

                if (_buffer.Length < view.Data.Length)
                {
                    _buffer = new byte[Math.Max(view.Data.Length, _buffer.Length * 2)];
                }

                view.Data.CopyTo(_buffer);
                _current = view.Info;
                return true;
            }
            catch (NeedRangeException need) when (attempt < MaxRetries)
            {
                await _window.FetchAsync(need.Offset, need.Length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int attempt = 0; ; attempt++)
        {
            _inner.SetCancellation(cancellationToken);
            try
            {
                return _inner.Seek(target, mode);
            }
            catch (NeedRangeException need) when (attempt < MaxRetries)
            {
                await _window.FetchAsync(need.Offset, need.Length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the tags found when the stream was opened (the start and end of the source were fetched then); large values such as cover art are fetched as needed.</summary>
    /// <param name="cancellationToken">Cancels fetching.</param>
    /// <returns>The metadata.</returns>
    public async ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return _inner.ReadMetadata(cancellationToken);
            }
            catch (NeedRangeException need) when (attempt < MaxRetries)
            {
                await _window.FetchAsync(need.Offset, need.Length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _inner.Dispose();
        return _leaveOpen ? default : _source.DisposeAsync();
    }
}

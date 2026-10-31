namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Reads Matroska and WebM from a random-access or forward-only source, synchronously. Packets come from the allocation-free
/// <see cref="TryReadPacket"/>; the bytes of a packet are valid until the next call.
/// </summary>
public sealed class MatroskaDemuxer : IDemuxer
{
    private readonly MatroskaReader _reader;
    private readonly IDisposable? _source;

    private MatroskaDemuxer(MatroskaReader reader, DefectLog defects, IDisposable? source)
    {
        _reader = reader;
        Defects = defects;
        _source = source;
    }

    /// <summary>Opens a seekable source: the EBML header, the Segment's SeekHead, Info and Tracks are read; no media data is.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not Matroska or has no Segment or Tracks.</exception>
    /// <exception cref="UnsupportedMediaException">The EBML header asks for something this reader does not support.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static MatroskaDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(new SyncSourceInput(source), options, defects, options.LeaveOpen ? null : source, cancellationToken);
    }

    /// <summary>
    /// Opens a forward-only source, such as a live stream or a pipe. Unknown-size Segments and Clusters are handled; Tracks must come before
    /// the first Cluster. The result cannot seek.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not Matroska, or has no Tracks before its first Cluster.</exception>
    /// <exception cref="UnsupportedMediaException">The EBML header asks for something this reader does not support.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static MatroskaDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        SyncForwardInput input = new(source);
        input.StartAt(source.Position);
        return Create(input, options, defects, options.LeaveOpen ? null : source, cancellationToken);
    }

    private static MatroskaDemuxer Create(EbmlInput input, MediaOpenOptions options, DefectLog defects, IDisposable? owned, CancellationToken cancellationToken)
    {
        MatroskaReader reader = new(input, options.Limits, defects);
        try
        {
            SyncWait.Complete(reader.OpenAsync(cancellationToken));
        }
        catch
        {
            owned?.Dispose();
            throw;
        }

        return new MatroskaDemuxer(reader, defects, owned);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Matroska;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _reader.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _reader.Info.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _reader.CanSeek;

    /// <summary>The EBML header.</summary>
    public MatroskaHeader Header => _reader.Header;

    /// <summary>The Segment's Info element (defaults when the file has none).</summary>
    public MatroskaInfo Info => _reader.Info;

    /// <summary>The track entries as stored, in the same order as <see cref="Tracks"/>.</summary>
    public IReadOnlyList<MatroskaTrackEntry> Entries => _reader.Entries;

    /// <summary>The SeekHead entries read so far.</summary>
    public IReadOnlyList<MatroskaSeekEntry> SeekEntries => _reader.SeekEntries;

    /// <summary>Where the Segment's data starts in the source.</summary>
    public long SegmentDataOffset => _reader.SegmentStart;

    /// <summary>The Chapters element's range when it sits before the first Cluster (or the SeekHead could not place it), else <see cref="ByteRange.None"/>. Parsing it is left to the metadata layer.</summary>
    public ByteRange Chapters => _reader.Chapters;

    /// <summary>The first Tags element's range seen before the first Cluster, else <see cref="ByteRange.None"/>.</summary>
    public ByteRange Tags => _reader.Tags;

    /// <summary>The first Attachments element's range seen before the first Cluster, else <see cref="ByteRange.None"/>.</summary>
    public ByteRange Attachments => _reader.Attachments;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _reader.SetTrackEnabled(trackIndex, enabled);

    /// <summary>
    /// Reads the next packet of an enabled track in file order. The bytes in the view are valid until the next call. Damage is reported
    /// to <see cref="Defects"/> and reading continues at the next Cluster.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>False at the end of the data.</returns>
    public bool TryReadPacket(out PacketView packet)
    {
        if (!SyncWait.Complete(_reader.ReadPacketAsync(CancellationToken.None)))
        {
            packet = default;
            return false;
        }

        packet = new PacketView(_reader.CurrentInfo, _reader.CurrentData);
        return true;
    }

    /// <summary>
    /// Moves to the Cue (or, when the file has no Cues, the Cluster) at or before or after <paramref name="target"/>. Seeking lands on the
    /// start of a Cluster, so a few packets before the returned time may be read first; the Cues' reference track is the first video track.
    /// </summary>
    /// <param name="target">The time to reach.</param>
    /// <param name="mode">Whether to land before or after <paramref name="target"/>.</param>
    /// <returns>The time of the point reached, or <see cref="MediaTime.Unknown"/> when the file has no Clusters or nothing follows the target.</returns>
    /// <exception cref="NotSupportedException">The source is forward-only.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) =>
        SyncWait.Complete(_reader.SeekAsync(target, mode, CancellationToken.None));

    /// <inheritdoc />
    public void Dispose() => _source?.Dispose();
}

/// <summary>
/// Reads Matroska and WebM asynchronously, for ranged or remote sources (HTTP range requests, blob storage) and forward-only streams.
/// </summary>
public sealed class MatroskaAsyncDemuxer : IAsyncDemuxer
{
    private readonly MatroskaReader _reader;
    private readonly IAsyncDisposable? _source;

    private MatroskaAsyncDemuxer(MatroskaReader reader, DefectLog defects, IAsyncDisposable? source)
    {
        _reader = reader;
        Defects = defects;
        _source = source;
    }

    /// <summary>Opens a ranged source; like <see cref="MatroskaDemuxer.Open(IMediaSource, MediaOpenOptions?, CancellationToken)"/> it reads only the header and the Segment's structural elements.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not Matroska or has no Segment or Tracks.</exception>
    /// <exception cref="UnsupportedMediaException">The EBML header asks for something this reader does not support.</exception>
    public static async ValueTask<MatroskaAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return await CreateAsync(new AsyncSourceInput(source), options, defects, options.LeaveOpen ? null : source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a forward-only source asynchronously.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not Matroska, or has no Tracks before its first Cluster.</exception>
    /// <exception cref="UnsupportedMediaException">The EBML header asks for something this reader does not support.</exception>
    public static async ValueTask<MatroskaAsyncDemuxer> OpenAsync(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        AsyncForwardInput input = new(source);
        input.StartAt(source.Position);
        return await CreateAsync(input, options, defects, options.LeaveOpen ? null : source, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<MatroskaAsyncDemuxer> CreateAsync(EbmlInput input, MediaOpenOptions options, DefectLog defects, IAsyncDisposable? owned, CancellationToken cancellationToken)
    {
        MatroskaReader reader = new(input, options.Limits, defects);
        try
        {
            await reader.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new MatroskaAsyncDemuxer(reader, defects, owned);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Matroska;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _reader.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _reader.Info.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _reader.CanSeek;

    /// <summary>The EBML header.</summary>
    public MatroskaHeader Header => _reader.Header;

    /// <summary>The Segment's Info element (defaults when the file has none).</summary>
    public MatroskaInfo Info => _reader.Info;

    /// <summary>The track entries as stored, in the same order as <see cref="Tracks"/>.</summary>
    public IReadOnlyList<MatroskaTrackEntry> Entries => _reader.Entries;

    /// <inheritdoc />
    public PacketView Current => new(_reader.CurrentInfo, _reader.CurrentData);

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _reader.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default) => _reader.ReadPacketAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) =>
        _reader.SeekAsync(target, mode, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _source?.DisposeAsync() ?? default;
}

/// <summary>The format entry for the registry: sniffing by EBML header, and opening through the Matroska demuxers.</summary>
public sealed class MatroskaFormat : IMediaFormat
{
    private readonly EbmlSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MatroskaDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MatroskaDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await MatroskaAsyncDemuxer.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
}

/// <summary>Completes a <see cref="ValueTask"/> from a source that never waits, without allocating.</summary>
internal static class SyncWait
{
    public static T Complete<T>(ValueTask<T> task) => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    public static void Complete(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        task.AsTask().GetAwaiter().GetResult();
    }
}

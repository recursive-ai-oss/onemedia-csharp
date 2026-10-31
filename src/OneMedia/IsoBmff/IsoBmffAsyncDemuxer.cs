namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Reads packets from an ISO BMFF / QuickTime file over an asynchronous ranged source (HTTP range requests, blob storage, the browser).
/// The movie box is read once into memory (bounded by <see cref="MediaLimits.MaxAllocationBytes"/>) so sample tables cost no further requests;
/// each packet then costs one range read, and each movie fragment one read of its header box. Semantics otherwise as <see cref="IsoBmffDemuxer"/>.
/// </summary>
public sealed class IsoBmffAsyncDemuxer : IAsyncDemuxer
{
    private readonly IAsyncMediaSource _source;
    private readonly bool _leaveOpen;
    private readonly OpenedMovie _opened;
    private readonly IsoBmffEngine _engine;
    private readonly AsyncFragmentLoader _loader;
    private byte[] _buffer = new byte[64 * 1024];
    private PacketInfo _current;
    private long _nextFragmentPosition;
    private FragmentIndex? _fragmentIndex;
    private readonly MediaLimits _limits;
    private bool _ended;
    private bool _disposed;

    private IsoBmffAsyncDemuxer(IAsyncMediaSource source, OpenedMovie opened, MediaOpenOptions options, DefectLog defects)
    {
        _source = source;
        _opened = opened;
        _limits = options.Limits;
        _leaveOpen = options.LeaveOpen;
        Defects = defects;
        _engine = new IsoBmffEngine(opened.Movie, opened.FileType, opened.TableSource, opened.Length, options.Limits, defects, opened.SidxDuration);
        _loader = new AsyncFragmentLoader(source, options.Limits, defects);
        _nextFragmentPosition = opened.MoovEnd;
    }

    /// <summary>Opens a ranged source.</summary>
    /// <param name="source">The source; disposed with the demuxer unless <see cref="MediaOpenOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Limits and the defect log to fill.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The file has no usable movie box.</exception>
    public static async ValueTask<IsoBmffAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        OpenedMovie opened = await IsoBmffOpener.OpenAsync(source, options.Limits, defects, cancellationToken).ConfigureAwait(false);
        return new IsoBmffAsyncDemuxer(source, opened, options, defects);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.IsoBmff;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _engine.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _engine.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <summary>The parsed movie box.</summary>
    public IsoMovie Movie => _opened.Movie;

    /// <inheritdoc />
    public PacketView Current => new(_current, _buffer.AsSpan(0, _current.Size));

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _engine.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public async ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PacketInfo info;
            if (!_engine.TryNextTableSample(out info))
            {
                bool got = false;
                info = default;
                while (_engine.IsFragmented)
                {
                    if (_engine.TryNextFragmentSample(out info))
                    {
                        got = true;
                        break;
                    }

                    FragmentLocation loc;
                    try
                    {
                        loc = await _loader.LoadNextAsync(_nextFragmentPosition, _engine.Fragment, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MediaLimitExceededException e)
                    {
                        Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "isobmff.limit");
                        _ended = true;
                        return false;
                    }

                    if (loc.IsNone)
                    {
                        break;
                    }

                    _nextFragmentPosition = loc.End;
                    _engine.BeginFragment(loc.Offset);
                }

                if (!got)
                {
                    break;
                }
            }

            if (_buffer.Length < info.Size)
            {
                _buffer = new byte[Math.Max(info.Size, Math.Min(_buffer.Length * 2, Array.MaxLength))];
            }

            int n = await _source.ReadAsync(info.SourceRange.Offset, _buffer.AsMemory(0, info.Size), cancellationToken).ConfigureAwait(false);
            if (n == info.Size)
            {
                _current = info;
                return true;
            }

            Defects.Add(DefectKind.Truncated, DefectSeverity.Error, info.SourceRange.Offset, "The source returned fewer bytes than a sample's size.", "isobmff.short-read");
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ended = false;
        _engine.Fragment.Clear();
        MediaTime landing = _engine.SeekTables(target, mode);
        _engine.ResetFragmentTimes();
        _nextFragmentPosition = _opened.MoovEnd;
        bool anyTable = false;
        foreach (TrackState s in _engine.States)
        {
            anyTable |= s.Cursor is not null;
        }

        if (_engine.IsFragmented && !anyTable)
        {
            _fragmentIndex ??= new FragmentIndex
            {
                SegmentIndexes = _opened.SegmentIndexes,
                RandomAccess = await FragmentIndex.ReadRandomAccessAsync(_source, _limits, cancellationToken).ConfigureAwait(false),
            };
            landing = await FragmentSeeker.SeekAsync(_engine, _loader, _opened.MoovEnd, _fragmentIndex, target, mode, p => _nextFragmentPosition = p, cancellationToken).ConfigureAwait(false);
        }

        return landing;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        return _leaveOpen ? default : _source.DisposeAsync();
    }
}

/// <summary>The format entry for ISO BMFF / QuickTime files (MP4, MOV, 3GP/3G2, M4A/M4V, fragmented MP4 and CMAF), for the explicit registry.</summary>
public sealed class IsoBmffFormat : IMediaFormat
{
    private static readonly IsoBmffSniffer Sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => Sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => Sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => Sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        IsoBmffDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        IsoBmffDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await IsoBmffAsyncDemuxer.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
}

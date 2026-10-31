namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Reads packets from an ISO BMFF / QuickTime file (MP4, MOV, 3GP/3G2, M4A/M4V, fragmented MP4 and CMAF) over a seekable source, or from a
/// forward-only source for files whose <c>moov</c> comes first (fragmented MP4 streams, faststart files).
/// </summary>
/// <remarks>
/// <para>Packets come in file order across the enabled tracks (lowest source offset first), which for interleaved files is also near-decode order.
/// Timestamps are on the track's media timeline, in the track's timescale: <c>Dts</c> is the running sum of sample durations from the
/// decoding-time-to-sample table (or the fragment's base decode time) and <c>Pts</c> is <c>Dts</c> plus the composition offset. Edit lists are
/// not applied; they are reported in <see cref="MediaTrack.Edit"/> (the first non-empty edit's start is <c>Priming</c>, leading empty edits are
/// <c>StartTime</c>), and a packet that ends at or before that start carries <see cref="PacketAttributes.Discard"/>. To get presentation times
/// on the edited timeline, subtract <c>Priming</c> and add <c>StartTime</c>.</para>
/// <para>Memory is constant: sample tables are read through fixed windows as the cursor passes them, and a movie fragment is held in memory only while it is read.
/// <see cref="TryReadPacket"/> allocates nothing once its payload buffer has grown to the largest sample.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class IsoBmffDemuxer : IDemuxer
{
    private readonly IMediaSource? _source;
    private readonly IForwardMediaSource? _forward;
    private readonly bool _leaveOpen;
    private readonly OpenedMovie _opened;
    private readonly IsoBmffEngine _engine;
    private readonly MediaLimits _limits;
    private readonly CancellationToken _cancellation;
    private readonly RandomFragmentLoader? _loader;
    private byte[] _buffer = new byte[64 * 1024];
    private long _nextFragmentPosition;
    private long _mdatEnd;
    private BoxHeader? _pending;
    private bool _ended;
    private bool _disposed;
    private bool _reportedOrder;

    private IsoBmffDemuxer(IMediaSource? source, IForwardMediaSource? forward, OpenedMovie opened, MediaOpenOptions options, DefectLog defects, CancellationToken cancellationToken)
    {
        _source = source;
        _forward = forward;
        _opened = opened;
        _leaveOpen = options.LeaveOpen;
        _limits = options.Limits;
        _cancellation = cancellationToken;
        Defects = defects;
        _engine = new IsoBmffEngine(opened.Movie, opened.FileType, opened.TableSource, opened.Length, options.Limits, defects, opened.SidxDuration);
        _nextFragmentPosition = opened.MoovEnd;
        if (source is not null)
        {
            _loader = new RandomFragmentLoader(source, options.Limits, defects);
        }
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The file; it must stay open while the demuxer is used and is disposed with it unless <see cref="MediaOpenOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Limits and the defect log to fill.</param>
    /// <param name="cancellationToken">Cancels the open and later reads.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The file has no usable movie box.</exception>
    /// <exception cref="MediaLimitExceededException">A limit in <paramref name="options"/> was exceeded while reading the movie box.</exception>
    public static IsoBmffDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        OpenedMovie opened = IsoBmffOpener.Open(source, options.Limits, defects, cancellationToken);
        return new IsoBmffDemuxer(source, null, opened, options, defects, cancellationToken);
    }

    /// <summary>Opens a forward-only source. The movie box must come before the media data it describes, unless the file is fragmented.</summary>
    /// <param name="source">The stream, positioned at the start of the file.</param>
    /// <param name="options">Limits and the defect log to fill.</param>
    /// <param name="cancellationToken">Cancels the open and later reads.</param>
    /// <returns>The demuxer; <see cref="CanSeek"/> is false.</returns>
    /// <exception cref="MediaFormatException">The data has no movie box.</exception>
    /// <exception cref="UnsupportedMediaException">The media data precedes the movie box of a non-fragmented file, which a forward-only reader cannot use.</exception>
    public static IsoBmffDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        OpenedMovie opened = IsoBmffOpener.OpenForward(source, options.Limits, defects, cancellationToken);
        if (opened.MediaDataPrecededMovie && !opened.Movie.IsFragmented)
        {
            throw new UnsupportedMediaException("The movie box comes after the media data, so the file cannot be read from a forward-only source.");
        }

        return new IsoBmffDemuxer(null, source, opened, options, defects, cancellationToken);
    }

    /// <summary>Opens a seekable source without throwing for damaged input.</summary>
    /// <param name="source">The file.</param>
    /// <param name="options">Limits and the defect log to fill.</param>
    /// <param name="demuxer">The demuxer, when the result is true.</param>
    /// <param name="failure">Why the file could not be opened, when the result is false.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>True when a demuxer was created.</returns>
    public static bool TryOpen(IMediaSource source, MediaOpenOptions? options, out IsoBmffDemuxer? demuxer, out MediaDefect failure, CancellationToken cancellationToken = default)
    {
        try
        {
            demuxer = Open(source, options, cancellationToken);
            failure = default;
            return true;
        }
        catch (MediaFormatException e)
        {
            failure = new MediaDefect(e.Kind, DefectSeverity.Error, e.Offset, e.Message);
        }
        catch (MediaLimitExceededException e)
        {
            failure = new MediaDefect(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message);
        }

        demuxer = null;
        return false;
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
    public bool CanSeek => _source is not null;

    /// <summary>The file type box brands, or <c>null</c> when the file has none (older QuickTime movies).</summary>
    public FileTypeBox? FileType => _opened.FileType;

    /// <summary>The parsed movie box, with sample entries and edit lists as stored.</summary>
    public IsoMovie Movie => _opened.Movie;

    /// <summary>The 1-based sample description index of the packet last returned (index into <c>Movie.Tracks[i].SampleEntries</c>).</summary>
    public int SampleDescriptionIndex => _engine.LastDescriptionIndex;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _engine.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_ended)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!_engine.TryNextTableSample(out PacketInfo info) && !NextFragmentPacket(out info))
            {
                break;
            }

            if (ReadPayload(in info))
            {
                packet = new PacketView(info, _buffer.AsSpan(0, info.Size));
                return true;
            }
        }

        packet = default;
        return false;
    }

    private bool NextFragmentPacket(out PacketInfo info)
    {
        info = default;
        if (!_engine.IsFragmented)
        {
            return false;
        }

        while (true)
        {
            if (_engine.TryNextFragmentSample(out info))
            {
                return true;
            }

            if (!LoadNextFragment())
            {
                return false;
            }
        }
    }

    private bool LoadNextFragment()
    {
        try
        {
            if (_forward is not null)
            {
                return LoadNextFragmentForward();
            }

            FragmentLocation loc = _loader!.LoadNext(_nextFragmentPosition, _engine.Fragment, _cancellation);
            if (loc.IsNone)
            {
                return false;
            }

            _nextFragmentPosition = loc.End;
            _engine.BeginFragment(loc.Offset);
            return true;
        }
        catch (MediaLimitExceededException e)
        {
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "isobmff.limit");
            _ended = true;
            return false;
        }
    }

    // Forward-only: read the next movie fragment header box, then look ahead to the media data box that follows so its end is known.
    private bool LoadNextFragmentForward()
    {
        IForwardMediaSource fwd = _forward!;
        if (_mdatEnd > fwd.Position && !ForwardBoxes.SkipPayload(fwd, _mdatEnd - fwd.Position, _cancellation))
        {
            return false;
        }

        _mdatEnd = 0;
        BoxHeader h;
        while (true)
        {
            if (_pending is { } p)
            {
                h = p;
                _pending = null;
            }
            else if (!ForwardBoxes.TryReadHeader(fwd, Defects, _cancellation, out h))
            {
                return false;
            }

            if (h.Type == BoxTypes.Moof)
            {
                break;
            }

            if (h.IsSizeInferred || !ForwardBoxes.SkipPayload(fwd, h.PayloadSize, _cancellation))
            {
                return false;
            }
        }

        int total = FragmentSizing.Check(h, _limits);
        byte[] buffer = _engine.Fragment.RentBuffer(total);
        int headerLen = HeaderBytes.Write(h, buffer);
        if (!fwd.TryReadExactly(buffer.AsSpan(headerLen, total - headerLen), _cancellation))
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The data ends inside a movie fragment header box.");
            return false;
        }

        _engine.BeginFragment(h.Offset);
        while (ForwardBoxes.TryReadHeader(fwd, Defects, _cancellation, out BoxHeader next))
        {
            if (next.Type == BoxTypes.Mdat)
            {
                _mdatEnd = next.IsSizeInferred ? long.MaxValue : next.End;
                break;
            }

            if (next.Type == BoxTypes.Moof)
            {
                _pending = next;
                break;
            }

            if (next.IsSizeInferred || !ForwardBoxes.SkipPayload(fwd, next.PayloadSize, _cancellation))
            {
                break;
            }
        }

        return true;
    }

    private bool ReadPayload(in PacketInfo info)
    {
        int size = info.Size;
        if (_buffer.Length < size)
        {
            _buffer = new byte[Math.Max(size, Math.Min(_buffer.Length * 2, Array.MaxLength))];
        }

        long offset = info.SourceRange.Offset;
        if (_source is not null)
        {
            if (_source.Read(offset, _buffer.AsSpan(0, size), _cancellation) == size)
            {
                return true;
            }

            Defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The source returned fewer bytes than a sample's size.", "isobmff.short-read");
            return false;
        }

        IForwardMediaSource fwd = _forward!;
        long position = fwd.Position;
        if (offset < position)
        {
            if (!_reportedOrder)
            {
                _reportedOrder = true;
                Defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, offset, "A sample lies before the current position of a forward-only source and is skipped.", "isobmff.forward-order");
            }

            return false;
        }

        if (offset > position && !ForwardBoxes.SkipPayload(fwd, offset - position, _cancellation))
        {
            _ended = true;
            return false;
        }

        if (fwd.TryReadExactly(_buffer.AsSpan(0, size), _cancellation))
        {
            return true;
        }

        Defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The data ends inside a sample.", "isobmff.short-read");
        _ended = true;
        return false;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The demuxer reads a forward-only source.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is null)
        {
            throw new NotSupportedException("A forward-only source cannot seek.");
        }

        _ended = false;
        _engine.Fragment.Clear();
        MediaTime landing = _engine.SeekTables(target, mode);
        _nextFragmentPosition = _opened.MoovEnd;
        bool anyTable = false;
        foreach (TrackState s in _engine.States)
        {
            anyTable |= s.Cursor is not null;
        }

        if (_engine.IsFragmented && !anyTable)
        {
            landing = FragmentSeeker.SeekAsync(_engine, _loader!, _opened.MoovEnd, target, mode, p => _nextFragmentPosition = p, _cancellation).AsTask().GetAwaiter().GetResult();
        }

        return landing;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _source?.Dispose();
            _forward?.Dispose();
        }
    }
}

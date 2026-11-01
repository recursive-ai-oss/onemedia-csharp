using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Sniffing;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// Reads AVI files: classic RIFF AVI, OpenDML (AVI 2.0) files that continue in <c>RIFF AVIX</c> chunks beyond 1 GB or 4 GB, and DV in AVI
/// (type 1 as one DV video track whose packets are whole frames, type 2 as a video track plus separate audio).
/// Packets come out in file order. Time follows each stream's header: a tick lasts scale/rate seconds, and the clock of a stream with a
/// sample size counts samples (bytes divided by the sample size), any other stream counts chunks.
/// </summary>
public sealed class AviDemuxer : IDemuxer, IMetadataProvider, ICurrentPacket
{
    private readonly AviReader _reader;
    private readonly IDisposable? _owned;
    private readonly IMediaSource? _source;
    private readonly MediaLimits _limits;

    private AviDemuxer(AviReader reader, DefectLog defects, IDisposable? owned, IMediaSource? source, MediaLimits limits)
    {
        _reader = reader;
        Defects = defects;
        _owned = owned;
        _source = source;
        _limits = limits;
    }

    /// <summary>Opens an AVI file from a seekable source.</summary>
    /// <param name="source">The file.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="MediaFormatException">The data is not an AVI file or has no header list.</exception>
    public static AviDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        Span<byte> head = stackalloc byte[12]; // RIFF header, fixed
        int n = source.Read(0, head, cancellationToken);
        return Create(source, head[..n], options, cancellationToken);
    }

    internal static AviDemuxer Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) =>
        Create(source, head, options, cancellationToken);

    private static AviDemuxer Create(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        IDisposable? owned = options.LeaveOpen ? null : source;
        try
        {
            AviReader reader = AviReader.Open(source, head, options.Limits, defects, cancellationToken);
            return new AviDemuxer(reader, defects, owned, source, options.Limits);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an AVI file from a ranged source (HTTP range requests, blob storage). Only the byte ranges the reader needs are requested: the header
    /// list, the index chunks that key-frame lookup uses, and the payloads of the packets read. Reads wait for each range on the calling thread.
    /// </summary>
    /// <param name="source">The ranged source.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        MediaOpenOptions inner = options with { LeaveOpen = true };
        AviDemuxer d = Open(new BlockingMediaSource(source), inner, cancellationToken);
        return new ValueTask<IAsyncDemuxer>(new SyncBackedAsyncDemuxer<AviDemuxer>(d, options.LeaveOpen ? null : source));
    }

    /// <summary>
    /// Opens an AVI stream from a forward-only source. The index sits at the end of the file, so key-frame flags are not available:
    /// every packet is reported as a key frame, and seeking throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="source">The stream.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static AviDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        IDisposable? owned = options.LeaveOpen ? null : source;
        try
        {
            AviReader reader = AviReader.Open(source, options.Limits, defects, cancellationToken);
            return new AviDemuxer(reader, defects, owned, null, options.Limits);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Avi;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _reader.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _reader.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _reader.CanSeek;

    /// <summary>The parsed header list: <c>avih</c>, every stream's <c>strh</c>/<c>strf</c>, super indexes and the <c>dmlh</c> frame count.</summary>
    public AviHeaderList Headers => _reader.Headers;

    /// <summary>The root <c>RIFF AVI</c> chunk as a lazy structure tree, or <c>null</c> for a forward-only source.</summary>
    public IStructureNode? Structure => _reader.Root;

    /// <summary>True when the file has a usable index (OpenDML or <c>idx1</c>) and key-frame flags come from it.</summary>
    public bool HasIndex => _reader.HasIndex;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _reader.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        if (!_reader.ReadPacket())
        {
            packet = default;
            return false;
        }

        packet = new PacketView(_reader.CurrentInfo, _reader.CurrentData);
        return true;
    }

    PacketView ICurrentPacket.Current => new(_reader.CurrentInfo, _reader.CurrentData);

    /// <inheritdoc />
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) => _reader.Seek(target, mode);

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        if (_source is null)
        {
            // Forward-only: the header list is all there is; no tags are read.
            FactsBuilder facts = new(Defects);
            if (Duration.IsKnown)
            {
                facts.Duration.Add(0, new FactCandidate<MediaTime>(Duration, "container", Duration.ToString(), "the longest stream: length times scale over rate"));
            }

            return new MediaMetadata([], [], facts.Build(), Defects, Tracks.Count == 1);
        }

        return AviMetadataReader.Read(_source, _reader, _limits, Defects, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _reader.Dispose();
        _owned?.Dispose();
    }
}

/// <summary>Registers AVI with content sniffing.</summary>
public sealed class AviFormat : IMediaFormat, IHeadSeededFormat
{
    private readonly AviSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AviDemuxer.Open(source, options, cancellationToken);

    IDemuxer IHeadSeededFormat.Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) =>
        AviDemuxer.Open(source, head, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AviDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AviDemuxer.OpenAsync(source, options, cancellationToken);
}

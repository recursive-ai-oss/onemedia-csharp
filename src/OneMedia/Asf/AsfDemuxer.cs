using System.Globalization;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Asf;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>Reads ASF (WMV, WMA) from a seekable source: tracks from the header, packets in file order.</summary>
public sealed class AsfDemuxer : IDemuxer, IMetadataProvider
{
    private readonly AsfReader _reader;
    private readonly IDisposable? _owned;
    private readonly IMediaSource? _media;
    private readonly MediaLimits _limits;

    private AsfDemuxer(AsfReader reader, DefectLog defects, IDisposable? owned, IMediaSource? media, MediaLimits limits)
    {
        _reader = reader;
        Defects = defects;
        _owned = owned;
        _media = media;
        _limits = limits;
    }

    /// <summary>Opens an ASF file.</summary>
    /// <param name="source">A seekable source. It is disposed with the demuxer unless <see cref="MediaOpenOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="MediaFormatException">The data does not start with a valid ASF Header Object.</exception>
    /// <exception cref="MediaLimitExceededException">The header asks for more than <see cref="MediaLimits"/> allow.</exception>
    public static AsfDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(new SyncAsfInput(source), options, defects, options.LeaveOpen ? null : source, source, cancellationToken);
    }

    /// <summary>Opens an ASF stream that can only be read forward. Seeking is not available, and picture values are read from the bytes kept while the header went by.</summary>
    /// <param name="source">The stream, positioned at the first byte of the file.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="MediaFormatException">The data does not start with a valid ASF Header Object.</exception>
    public static AsfDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(new SyncForwardAsfInput(source), options, defects, options.LeaveOpen ? null : source, null, cancellationToken);
    }

    private static AsfDemuxer Create(AsfInput input, MediaOpenOptions options, DefectLog defects, IDisposable? owned, IMediaSource? media, CancellationToken ct)
    {
        AsfReader reader = new(input, options.Limits, defects);
        try
        {
            SyncWait.Complete(reader.OpenAsync(ct));
        }
        catch
        {
            owned?.Dispose();
            throw;
        }

        return new AsfDemuxer(reader, defects, owned, media, options.Limits);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Asf;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _reader.Tracks;

    /// <summary>The play duration less the preroll; unknown for a broadcast capture.</summary>
    public MediaTime Duration => _reader.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <summary>True for a seekable source with fixed-size packets and unprotected content.</summary>
    public bool CanSeek => _reader.CanSeek;

    /// <inheritdoc />
    public bool IsProtected => _reader.IsProtected;

    /// <summary>The decoded header.</summary>
    public AsfHeader Header => _reader.Header;

    /// <summary>How the content is protected, or <see cref="AsfProtection.None"/>.</summary>
    public AsfProtection Protection => _reader.Header.Protection;

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default) =>
        AsfMetadataReader.Read(_media, _reader.Header, _reader.Tracks, _reader.Duration, _limits, Defects, cancellationToken);

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _reader.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
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
    /// Moves to a keyframe of the first video stream (the first stream when there is no video). The file's Simple Index gives the keyframes; a file without one
    /// is scanned once, packet header by packet header, to find them.
    /// </summary>
    /// <exception cref="NotSupportedException"><see cref="CanSeek"/> is false.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) =>
        SyncWait.Complete(_reader.SeekAsync(target, mode, CancellationToken.None));

    /// <inheritdoc />
    public void Dispose() => _owned?.Dispose();
}

/// <summary>Reads ASF from an asynchronous ranged source or a forward-only stream.</summary>
public sealed class AsfAsyncDemuxer : IAsyncDemuxer, IAsyncMetadataProvider
{
    private readonly AsfReader _reader;
    private readonly IAsyncDisposable? _owned;
    private readonly IAsyncMediaSource? _ranged;
    private readonly MediaLimits _limits;

    private AsfAsyncDemuxer(AsfReader reader, DefectLog defects, IAsyncDisposable? owned, IAsyncMediaSource? ranged, MediaLimits limits)
    {
        _reader = reader;
        Defects = defects;
        _owned = owned;
        _ranged = ranged;
        _limits = limits;
    }

    /// <summary>Opens an ASF file from a ranged asynchronous source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="MediaFormatException">The data does not start with a valid ASF Header Object.</exception>
    public static async ValueTask<AsfAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return await CreateAsync(new AsyncAsfInput(source), options, defects, options.LeaveOpen ? null : source, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens an ASF stream that can only be read forward.</summary>
    /// <param name="source">The stream, positioned at the first byte of the file.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="MediaFormatException">The data does not start with a valid ASF Header Object.</exception>
    public static async ValueTask<AsfAsyncDemuxer> OpenAsync(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return await CreateAsync(new AsyncForwardAsfInput(source), options, defects, options.LeaveOpen ? null : source, null, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AsfAsyncDemuxer> CreateAsync(AsfInput input, MediaOpenOptions options, DefectLog defects, IAsyncDisposable? owned, IAsyncMediaSource? ranged, CancellationToken ct)
    {
        AsfReader reader = new(input, options.Limits, defects);
        try
        {
            await reader.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new AsfAsyncDemuxer(reader, defects, owned, ranged, options.Limits);
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Asf;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _reader.Tracks;

    /// <inheritdoc />
    public MediaTime Duration => _reader.Duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _reader.CanSeek;

    /// <inheritdoc />
    public bool IsProtected => _reader.IsProtected;

    /// <summary>The decoded header.</summary>
    public AsfHeader Header => _reader.Header;

    /// <inheritdoc />
    public PacketView Current => new(_reader.CurrentInfo, _reader.CurrentData);

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled) => _reader.SetTrackEnabled(trackIndex, enabled);

    /// <inheritdoc />
    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default) => _reader.ReadPacketAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default) =>
        _reader.SeekAsync(target, mode, cancellationToken);

    /// <summary>
    /// Reads the tags. For a ranged source the tag objects are fetched whole (up to the tag limit) first; for a forward-only stream the bytes
    /// kept while the header went by are used.
    /// </summary>
    public async ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (_ranged is null)
        {
            return AsfMetadataReader.Read(null, _reader.Header, _reader.Tracks, _reader.Duration, _limits, Defects, cancellationToken);
        }

        SparseSource sparse = new(_ranged.Length);
        foreach ((AsfObjectKind _, ByteRange r) in _reader.Header.TagObjects)
        {
            if (r.Length > _limits.MaxTagBytes)
            {
                Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, r.Offset, "A tag object is larger than the tag limit and is not read.", "tags.value-too-large");
                continue;
            }

            byte[] bytes = new byte[r.Length];
            int got = await _ranged.ReadAsync(r.Offset, bytes, cancellationToken).ConfigureAwait(false);
            sparse.Add(r.Offset, got == bytes.Length ? bytes : bytes[..got]);
        }

        return AsfMetadataReader.Read(sparse, _reader.Header, _reader.Tracks, _reader.Duration, _limits, Defects, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? default;
}

/// <summary>The ASF format: sniffs the Header Object GUID and opens files from every kind of source.</summary>
public sealed class AsfFormat : IMediaFormat
{
    private readonly Sniffing.AsfSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        AsfDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        AsfDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AsfAsyncDemuxer.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
}

internal static class AsfMetadataReader
{
    public static MediaMetadata Read(IMediaSource? source, AsfHeader header, IReadOnlyList<MediaTrack> tracks, MediaTime duration, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        IMediaSource? bytes = source ?? header.RetainedTags;
        List<ITagContainer> containers = [];
        if (bytes is not null)
        {
            foreach ((AsfObjectKind kind, ByteRange range) in header.TagObjects)
            {
                if (bytes is SparseSource sparse && !sparse.Has(range.Offset))
                {
                    continue;
                }

                try
                {
                    containers.Add(AsfTags.Read(kind, range, bytes, header.Languages, limits, defects, ct));
                }
                catch (MediaLimitExceededException e)
                {
                    defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
                }
            }
        }

        // An 'ID3' attribute holds a whole ID3v2 tag (observed in WMA files tagged by common converters); its frames are read as native tags too.
        // The binary attribute stays as it is.
        if (bytes is not null)
        {
            foreach (ITagContainer c in containers.ToArray())
            {
                foreach (NativeTag t in c.Tags)
                {
                    if (t.System == TagSystems.Asf && t.Key == "ID3" && t.Kind == TagValueKind.Binary && t.ValueRange.Length >= Id3Header.HeaderSize
                        && (bytes is not SparseSource sp || sp.Has(t.ValueRange.Offset))
                        && Id3Tag.TryRead(bytes, t.ValueRange.Offset, out Id3Tag? id3, limits, defects) && id3 is not null)
                    {
                        containers.Add(id3);
                    }
                }
            }
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        if (duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(duration, "container", duration.ToString(), "play duration less the preroll, from File Properties"));
        }

        if (header.FileProperties?.CreationUtc is { } created)
        {
            // The creation date is when the file was written, which for a converted file is not when the media was captured.
            facts.CaptureTime.Add(FactsBuilder.RankMovieHeader, new FactCandidate<MediaDateTime>(new MediaDateTime(created, null, TimeZoneCertainty.Utc), "asf:FileProperties.CreationDate",
                created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), "Windows file time in File Properties: the time the file was created", TimeZoneCertainty.Utc, header.FileProperties.Range));
        }

        foreach (MediaTrack t in tracks)
        {
            if (t.Video is { Width: > 0, Height: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "asf:StreamProperties", $"{v.Width}x{v.Height}", "dimensions in the video stream's bitmap header"));
                break;
            }
        }

        return new MediaMetadata(containers, [], facts.Build(), defects, tracks.Count == 1);
    }
}

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

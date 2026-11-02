namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// Recognises HLS playlists (a first line of <c>#EXTM3U</c>) and DASH manifests (an <c>MPD</c> root element) and opens them as presentations when the
/// source is a local file, whose directory the segment locations are resolved against. For any other source use
/// <see cref="PresentationSource.OpenAsync(Uri, IPresentationResolver, PresentationOptions?, MediaOpenOptions?, CancellationToken)"/>.
/// </summary>
public sealed class PresentationFormat : IMediaFormat
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Presentation;

    /// <inheritdoc />
    public string DisplayName => "HLS playlist or DASH manifest";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (HlsReader.IsPlaylist(head))
        {
            return new SniffResult(SniffConfidence.Certain, "hls");
        }

        return DashReader.IsManifest(head) ? new SniffResult(SniffConfidence.Certain, "dash") : SniffResult.None;
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        (Uri location, IPresentationResolver resolver) = Locate(source);
        byte[] bytes = ReadAll(source, options.Limits, cancellationToken);
        return PresentationDemuxer.CreateAsync(location, resolver, bytes, new PresentationOptions(), options, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        (Uri location, IPresentationResolver resolver) = Locate(source);
        long length = source.Length;
        if (length < 0 || length > options.Limits.MaxTagBytes)
        {
            throw new MediaLimitExceededException("MaxTagBytes", options.Limits.MaxTagBytes, length, 0);
        }

        byte[] bytes = new byte[length];
        await source.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
        return await PresentationDemuxer.CreateAsync(location, resolver, bytes, new PresentationOptions(), options, cancellationToken).ConfigureAwait(false);
    }

    private static (Uri, IPresentationResolver) Locate(object source)
    {
        // A playlist read from memory or a stream has no location, so its relative references cannot be resolved.
        if (source is not FileMediaSource file)
        {
            throw new UnsupportedMediaException("A playlist or manifest read from memory or a stream has no location to resolve its segments against; open it with PresentationSource.OpenAsync and a resolver.");
        }

        string full = Path.GetFullPath(file.Path);
        return (PresentationSource.FileUri(full), new FilePresentationResolver(Path.GetDirectoryName(full) ?? full));
    }

    private static byte[] ReadAll(IMediaSource source, MediaLimits limits, CancellationToken cancellationToken)
    {
        long length = source.Length;
        if (length < 0 || length > limits.MaxTagBytes)
        {
            throw new MediaLimitExceededException("MaxTagBytes", limits.MaxTagBytes, length, 0);
        }

        byte[] bytes = new byte[length];
        source.ReadExactly(0, bytes, cancellationToken);
        return bytes;
    }
}

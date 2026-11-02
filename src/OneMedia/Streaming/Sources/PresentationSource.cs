using System.Buffers;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>Where a presentation's playlist, initialization segments and media segments are fetched from. The library never opens a network connection itself: the caller maps a location to a ranged source.</summary>
public interface IPresentationResolver
{
    /// <summary>Opens the resource at <paramref name="location"/> for ranged reads. The presentation reads a playlist or a segment as one ranged read (or a few), and disposes the source when it no longer needs it.</summary>
    /// <param name="location">An absolute URI: the playlist's location with the playlist's relative references resolved against it.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The source.</returns>
    /// <exception cref="UnsupportedMediaException">The resolver cannot serve this kind of location.</exception>
    /// <exception cref="IOException">The resource cannot be reached.</exception>
    ValueTask<IAsyncMediaSource> OpenAsync(Uri location, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves <c>file:</c> locations under one directory. A playlist is untrusted input: a reference that leaves the directory (an absolute path, or
/// <c>..</c> out of it) or names another scheme is refused rather than followed, so opening a downloaded playlist cannot read other files.
/// Symbolic links inside the directory are followed.
/// </summary>
public sealed class FilePresentationResolver : IPresentationResolver
{
    private readonly string _root;

    /// <summary>Creates a resolver confined to <paramref name="rootDirectory"/> and the directories below it.</summary>
    /// <param name="rootDirectory">The directory the playlist is in, or a parent of every directory the presentation uses.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is empty.</exception>
    public FilePresentationResolver(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    /// <inheritdoc />
    public ValueTask<IAsyncMediaSource> OpenAsync(Uri location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsFile)
        {
            throw new UnsupportedMediaException($"The presentation refers to {location.Scheme}: locations; supply an IPresentationResolver that fetches them.");
        }

        string full = Path.GetFullPath(location.LocalPath);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnsupportedMediaException("The presentation refers to a file outside the directory it was opened from; it was not read.");
        }

        return new ValueTask<IAsyncMediaSource>(FileMediaSource.Open(full));
    }
}

/// <summary>A resolver made from a delegate: the way to plug in HTTP range requests, blob storage or a browser <c>Blob</c> map.</summary>
public sealed class DelegatePresentationResolver : IPresentationResolver
{
    private readonly Func<Uri, CancellationToken, ValueTask<IAsyncMediaSource>> _open;

    /// <summary>Creates a resolver.</summary>
    /// <param name="open">Opens a location for ranged reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is null.</exception>
    public DelegatePresentationResolver(Func<Uri, CancellationToken, ValueTask<IAsyncMediaSource>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
    }

    /// <inheritdoc />
    public ValueTask<IAsyncMediaSource> OpenAsync(Uri location, CancellationToken cancellationToken) => _open(location, cancellationToken);
}

/// <summary>Options for opening a presentation.</summary>
public sealed record PresentationOptions
{
    /// <summary>
    /// Which variant to play: an index into <see cref="PresentationDemuxer.Variants"/>. Default 0, the first. For HLS a variant is an
    /// <c>EXT-X-STREAM-INF</c> entry; for DASH it is a representation index applied to every adaptation set (an adaptation set with fewer
    /// representations uses its last). A value past the end selects the last.
    /// </summary>
    public int VariantIndex { get; init; }

    /// <summary>
    /// What to do at the end of a live HLS playlist (no <c>EXT-X-ENDLIST</c>): load it again under this policy and carry on with the new segments.
    /// Null (the default) reads the segments the playlist lists now and ends.
    /// </summary>
    public GrowthPolicy? LivePolicy { get; init; }

    /// <summary>The formats segments are recognised as. Default: every container the happy path opens (<see cref="MediaFormats.Default"/> without playlists).</summary>
    public MediaFormatRegistry? SegmentFormats { get; init; }
}

/// <summary>Opens HLS and DASH presentations as media.</summary>
public static class PresentationSource
{
    /// <summary>Opens a playlist or manifest in a local file. Segments are read from files below the playlist's directory.</summary>
    /// <param name="path">An HLS multivariant or media playlist, or a DASH MPD.</param>
    /// <param name="options">Variant choice and live behaviour.</param>
    /// <param name="open">Limits and defect log; <see cref="MediaOpenOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while the playlists and the first segments are read.</param>
    /// <returns>The presentation as a demuxer; dispose it.</returns>
    /// <exception cref="UnsupportedMediaException">The file is neither playlist nor manifest, or a segment is in no registered format.</exception>
    /// <exception cref="MediaFormatException">The playlist or the first segment is malformed.</exception>
    public static PresentationDemuxer Open(string path, PresentationOptions? options = null, MediaOpenOptions? open = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        return OpenAsync(FileUri(full), new FilePresentationResolver(Path.GetDirectoryName(full) ?? full), options, open, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Opens a playlist or manifest through a resolver: local files, HTTP range requests supplied by the caller, blob storage.</summary>
    /// <param name="location">The absolute location of the playlist; relative references inside it resolve against it.</param>
    /// <param name="resolver">Maps a location to a ranged source.</param>
    /// <param name="options">Variant choice and live behaviour.</param>
    /// <param name="open">Limits and defect log; <see cref="MediaOpenOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The presentation as a demuxer; dispose it.</returns>
    /// <exception cref="ArgumentException"><paramref name="location"/> is not absolute.</exception>
    /// <exception cref="UnsupportedMediaException">The resource is neither playlist nor manifest, or a segment is in no registered format.</exception>
    /// <exception cref="MediaFormatException">The playlist or the first segment is malformed.</exception>
    public static ValueTask<PresentationDemuxer> OpenAsync(Uri location, IPresentationResolver resolver, PresentationOptions? options = null, MediaOpenOptions? open = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!location.IsAbsoluteUri)
        {
            throw new ArgumentException("The playlist location must be an absolute URI.", nameof(location));
        }

        return PresentationDemuxer.CreateAsync(location, resolver, null, options ?? new PresentationOptions(), open ?? MediaOpenOptions.Default, cancellationToken);
    }

    /// <summary>A <c>file:</c> URI for a full path, escaping the characters a path may hold that a URI may not.</summary>
    internal static Uri FileUri(string fullPath) => new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = fullPath }.Uri;
}

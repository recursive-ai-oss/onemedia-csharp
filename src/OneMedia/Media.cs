namespace RecursiveAI.OneMedia;

/// <summary>
/// The happy path: one call per task, each a single line over the layers below. Everything here has an equivalent in the layer
/// it calls (<see cref="MediaFile"/>, <see cref="MediaFormats"/>, the sources and the demuxers), where the options live.
/// </summary>
public static class Media
{
    /// <summary>Opens a media file by path. Formats are recognised by content, never by extension.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The opened file; dispose it to close the file.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised or cannot be opened.</exception>
    /// <exception cref="MediaFormatException">The data is not valid for its format.</exception>
    public static MediaFile Open(string path) => MediaFile.Open(FileMediaSource.Open(path));

    /// <summary>Opens media from a stream, seekable or not.</summary>
    /// <remarks>
    /// A seekable stream behaves like a file. A stream that cannot seek is read front to back and only once: formats that can be read that way
    /// open (Matroska/WebM, MP4 laid out for it, ASF, elementary streams), formats that need to seek throw <see cref="UnsupportedMediaException"/>,
    /// and reading a second time throws <see cref="MediaReadStateException"/> with <see cref="MediaReadStateReason.CannotRewind"/>.
    /// </remarks>
    /// <param name="stream">A readable stream positioned at the start of the media. It is closed with the file unless <paramref name="leaveOpen"/> is true.</param>
    /// <param name="leaveOpen">Keep the stream open when the file is disposed.</param>
    /// <returns>The opened file.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised, or needs a seekable stream.</exception>
    public static MediaFile Open(Stream stream, bool leaveOpen = false) =>
        stream is { CanSeek: false } ? MediaFile.Open(new StreamForwardSource(stream, leaveOpen)) : MediaFile.Open(new StreamMediaSource(stream, leaveOpen));

    /// <summary>Opens media held in memory.</summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The opened file.</returns>
    public static MediaFile Open(ReadOnlyMemory<byte> data) => MediaFile.Open(new MemoryMediaSource(data));

    /// <summary>Opens a media file by path for asynchronous reading.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened file.</returns>
    public static ValueTask<MediaFile> OpenAsync(string path, CancellationToken cancellationToken = default) => MediaFile.OpenAsync(FileMediaSource.Open(path), cancellationToken: cancellationToken);

    /// <summary>Opens media behind a ranged reader (HTTP range requests, blob storage, a browser <c>Blob</c>) for asynchronous reading.</summary>
    /// <param name="length">The total length in bytes.</param>
    /// <param name="readRange">Fills the buffer from the given offset and returns the bytes delivered; 0 at the end.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened file.</returns>
    public static ValueTask<MediaFile> OpenAsync(long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readRange, CancellationToken cancellationToken = default) =>
        MediaFile.OpenAsync(new RangedMediaSource(length, readRange), cancellationToken: cancellationToken);
}

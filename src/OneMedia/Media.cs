namespace RecursiveAI.OneMedia;

/// <summary>
/// The happy path: one call per task, each a single line over the layers below. Everything here has an equivalent in the layer
/// it calls (<see cref="MediaFile"/>, <see cref="MediaFormats"/>, the sources and the demuxers), where the options live.
/// </summary>
public static partial class Media
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

    /// <summary>Judges whether a file plays in a browser profile.</summary>
    /// <param name="path">The file.</param>
    /// <param name="profile">The browser profile, for example <see cref="Playback.BrowserProfiles.Chrome"/>.</param>
    /// <returns>The verdict, with reasons.</returns>
    public static Playback.PlaybackVerdict CanPlay(string path, Playback.BrowserProfile profile)
    {
        using MediaFile file = Open(path);
        return file.CanPlay(profile);
    }

    /// <summary>Copies the tracks of a file into another container without touching the compressed data. The container comes from the output's extension.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file (<c>.mp4</c>, <c>.m4a</c>, <c>.mov</c>, <c>.3gp</c>, and so on); refused if it exists.</param>
    /// <returns>What was written and everything that did not carry over.</returns>
    /// <exception cref="ArgumentException">The extension names no container.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not recognised, or the container cannot be written.</exception>
    /// <exception cref="MuxRefusedException">No track of the input can be written.</exception>
    public static Remuxing.RemuxReport Remux(string input, string output) => Remuxing.Remuxer.Remux(input, output);

    /// <summary>Copies the tracks of a file into a chosen container without touching the compressed data.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="format">The container to write.</param>
    /// <returns>What was written and everything that did not carry over.</returns>
    /// <exception cref="UnsupportedMediaException">The input is not recognised, or the container cannot be written.</exception>
    /// <exception cref="MuxRefusedException">No track of the input can be written.</exception>
    public static Remuxing.RemuxReport Remux(string input, string output, Remuxing.RemuxFormat format) => Remuxing.Remuxer.Remux(input, output, format);

    /// <summary>Moves the movie box of a progressive MP4/MOV/3GP file to the front, copying all other bytes unchanged, so playback can start while the file is still downloading.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <returns>What was moved.</returns>
    /// <exception cref="UnsupportedMediaException">The input is not a progressive ISO base media file.</exception>
    public static Remuxing.FaststartReport Faststart(string input, string output) => Remuxing.Remuxer.Faststart(input, output);

    /// <summary>Makes a file play in a browser profile without decoding video: moves the movie box first or remuxes into a container the profile accepts.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file; refused if it exists. Written only when the result is <see cref="Playback.MakePlayableStatus.Made"/> or <see cref="Playback.MakePlayableStatus.AlreadyPlayable"/>.</param>
    /// <param name="profile">The browser profile, for example <see cref="Playback.BrowserProfiles.Chromium"/>.</param>
    /// <returns>What was decided and done. A file whose audio needs re-encoding is reported as <see cref="Playback.MakePlayableStatus.NeedsAudioTranscode"/> and nothing is written: the core decodes nothing. Add <c>RecursiveAI.OneMedia.Audio</c> and call its <c>MakePlayable</c> for that case. Video blockers are reported as <see cref="Playback.MakePlayableStatus.Refused"/> with their codes.</returns>
    /// <exception cref="IOException">The output exists, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The input is not recognised.</exception>
    public static Playback.MakePlayableReport MakePlayable(string input, string output, Playback.BrowserProfile profile) => Playback.PlayableMaker.MakePlayable(input, output, profile);

    /// <summary>Sets the capture time of a recording in every date field the file carries, each in its own notation and zone semantics, and writes a new file. Payload bytes are copied unchanged.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="when">The capture time with the offset the recorder was in.</param>
    /// <returns>Every field written with its old and new text, the fields left and why, and the dates that still disagree.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="when"/> is outside 1904 to 2262.</exception>
    /// <exception cref="UnsupportedMediaException">The format has no capture-date writer.</exception>
    public static Tags.CaptureTimeReport SetCaptureTime(string input, string output, DateTimeOffset when) => Tags.CaptureTimeWriter.SaveAs(input, output, when);
}

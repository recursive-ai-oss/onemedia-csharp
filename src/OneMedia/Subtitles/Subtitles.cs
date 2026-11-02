using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia;

/// <summary>
/// The happy path for subtitle files: read, convert and write in one call. Each method is one line over the layers
/// (<see cref="SubtitleFormats"/>, <see cref="SubtitleFormat"/>, <see cref="SubtitleAlignment"/>) and has no options of its own;
/// use those for encodings, limits, defect logs and overwrite control. Formats are recognised by content, never by extension; the
/// extension only chooses the format to write.
/// </summary>
public static class Subtitles
{
    /// <summary>
    /// Reads a subtitle file in whichever format its content shows (SRT, WebVTT, ASS/SSA, TTML, SBV, LRC, SCC, and the bitmap formats: PGS <c>.sup</c>, VobSub
    /// <c>.idx</c> with the <c>.sub</c> beside it, or a bare <c>.sub</c>; those give image cues).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the operation before it starts and between steps.</param>
    /// <returns>The cue model.</returns>
    /// <exception cref="UnsupportedMediaException">The content is not a recognised subtitle format.</exception>
    /// <exception cref="MediaLimitExceededException">The file is larger than <see cref="MediaLimits.MaxAllocationBytes"/> or holds too many cues.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SubtitleDocument Read(string path, CancellationToken cancellationToken = default) => SubtitleFiles.Read(path, cancellationToken);

    /// <summary>Reads a subtitle file asynchronously. See <see cref="Read(string, CancellationToken)"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<SubtitleDocument> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        SubtitleFiles.Read(path, await SubtitleFiles.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes a document to a new file in the format its extension names.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The new file; <c>.srt</c>, <c>.vtt</c>, <c>.ass</c>/<c>.ssa</c>, <c>.ttml</c>/<c>.dfxp</c>/<c>.xml</c>, <c>.sbv</c> or <c>.lrc</c>.</param>
    /// <param name="cancellationToken">Cancels the operation before it starts and between steps.</param>
    /// <returns>What the format could not carry.</returns>
    /// <exception cref="UnsupportedMediaException">The extension names no writable format.</exception>
    /// <exception cref="IOException">The file exists (nothing is overwritten) or cannot be written.</exception>
    public static ConversionReport Write(SubtitleDocument document, string path, CancellationToken cancellationToken = default) => SubtitleFiles.Write(document, path, cancellationToken);

    /// <summary>Writes a document to a new file asynchronously. See <see cref="Write(SubtitleDocument, string, CancellationToken)"/>.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The new file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task<ConversionReport> WriteAsync(SubtitleDocument document, string path, CancellationToken cancellationToken = default) =>
        SubtitleFiles.WriteAsync(document, path, cancellationToken);

    /// <summary>Converts a subtitle file to the format the output extension names, into a new file.</summary>
    /// <param name="input">The source file (format by content).</param>
    /// <param name="output">The new file (format by extension).</param>
    /// <param name="cancellationToken">Cancels the operation before it starts and between steps.</param>
    /// <returns>What the target format could not carry, per cue.</returns>
    /// <exception cref="UnsupportedMediaException">The input is not recognised or the extension names no writable format.</exception>
    /// <exception cref="IOException">The output exists or a file cannot be read or written.</exception>
    public static ConversionReport Convert(string input, string output, CancellationToken cancellationToken = default) => Write(Read(input, cancellationToken), output, cancellationToken);

    /// <summary>Converts asynchronously. See <see cref="Convert(string, string, CancellationToken)"/>.</summary>
    /// <param name="input">The source file.</param>
    /// <param name="output">The new file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ConversionReport> ConvertAsync(string input, string output, CancellationToken cancellationToken = default) =>
        await WriteAsync(await ReadAsync(input, cancellationToken).ConfigureAwait(false), output, cancellationToken).ConfigureAwait(false);
}

// File access for the happy path, kept apart so the layers above stay free of paths.
internal static class SubtitleFiles
{
    internal static SubtitleDocument Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Read(path, ReadBytes(path));
    }


    // A VobSub index needs its .sub from beside it, so bitmap files go by path; everything else is decided by the bytes.
    internal static SubtitleDocument Read(string path, byte[] data)
    {
        SubtitleFormat? format = SubtitleFormats.Sniff(data);
        return format is PgsSubtitleFormat or VobSubSubtitleFormat ? BitmapSubtitles.ReadFile(path) : SubtitleFormats.Read(data);
    }

    internal static byte[] ReadBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        MediaLimits.Default.CheckAllocation(stream.Length, 0);
        byte[] data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    internal static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            MediaLimits.Default.CheckAllocation(stream.Length, 0);
            byte[] data = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return data;
        }
    }

    private static SubtitleFormat TargetFor(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        SubtitleFormat? format = SubtitleFormats.Find(Path.GetExtension(path));
        if (format is null || !format.CanWrite)
        {
            throw new UnsupportedMediaException($"The extension '{Path.GetExtension(path)}' names no writable subtitle format.");
        }

        return format;
    }

    internal static ConversionReport Write(SubtitleDocument document, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(document);
        SubtitleOutput output = TargetFor(path).Write(document);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(output.Data);
        return output.Report;
    }

    internal static async Task<ConversionReport> WriteAsync(SubtitleDocument document, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        SubtitleOutput output = TargetFor(path).Write(document);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(output.Data, cancellationToken).ConfigureAwait(false);
        }

        return output.Report;
    }
}

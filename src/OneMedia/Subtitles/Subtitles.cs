namespace RecursiveAI.OneMedia.Subtitles;

/// <summary>A document moved on the timeline, with what the move cost.</summary>
/// <param name="Document">The shifted document.</param>
/// <param name="Report">Cues dropped because they end before time zero, and cues whose start was clamped to zero.</param>
public sealed record AlignedSubtitles(SubtitleDocument Document, ConversionReport Report);

/// <summary>
/// Moves subtitle timing onto another timeline: by an explicit offset, or to where a media file's first frame is presented.
/// </summary>
public static class SubtitleAlignment
{
    /// <summary>Adds <paramref name="offset"/> to every cue time and word time. Cues that end at or before zero afterwards are dropped; a start before zero becomes zero.</summary>
    /// <param name="document">The document.</param>
    /// <param name="offset">The amount to add; negative moves cues earlier. Exact: the result keeps a timescale in which the sum is whole where one exists, otherwise it is rounded to the nearest tick of the cue's own timescale.</param>
    /// <returns>The shifted document and a report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static AlignedSubtitles Shift(SubtitleDocument document, MediaTime offset)
    {
        ArgumentNullException.ThrowIfNull(document);
        var report = new ConversionReport { Source = document.Format, Target = document.Format ?? "" };
        var cues = new List<SubtitleCue>(document.Cues.Count);
        for (int i = 0; i < document.Cues.Count; i++)
        {
            SubtitleCue cue = document.Cues[i];
            MediaTime start = Plus(cue.Start, offset);
            MediaTime end = Plus(cue.End, offset);
            if (end <= MediaTime.Zero && offset < MediaTime.Zero)
            {
                report.Add(i, SubtitleFeatures.Timing, LossKind.Dropped, "The cue ends before time zero after the shift.");
                continue;
            }

            if (start < MediaTime.Zero)
            {
                report.Add(i, SubtitleFeatures.Timing, LossKind.Approximated, "The cue starts before time zero after the shift; its start is now zero.");
                start = MediaTime.Zero;
            }

            var spans = new List<SubtitleSpan>(cue.Spans.Count);
            foreach (SubtitleSpan s in cue.Spans)
            {
                spans.Add(s.WordStart is null ? s : s with
                {
                    WordStart = Plus(s.WordStart.Value, offset),
                    WordEnd = s.WordEnd is { } we ? Plus(we, offset) : null,
                });
            }

            cues.Add(cue with { Start = start, End = end, Spans = SubtitleList.From(spans) });
        }

        return new AlignedSubtitles(document with { Cues = SubtitleList.From(cues) }, report);
    }

    /// <summary>
    /// The offset that puts sidecar time zero on a media file's timeline: where the first presented frame of the video track (or, without
    /// video, of the first track) sits, from its edit list or start offset. Zero when the container says nothing.
    /// </summary>
    /// <param name="media">An opened media file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is null.</exception>
    public static MediaTime OffsetFor(MediaFile media)
    {
        ArgumentNullException.ThrowIfNull(media);
        MediaTrack? track = media.Video ?? (media.Tracks.Count > 0 ? media.Tracks[0] : null);
        if (track is null)
        {
            return MediaTime.Zero;
        }

        MediaTime start = track.Edit.StartTime;
        return start.IsKnown ? start : MediaTime.Zero;
    }

    /// <summary>Shifts a sidecar document by <see cref="OffsetFor"/>, for a sidecar timed from the first presented frame of <paramref name="media"/>.</summary>
    /// <param name="document">The sidecar.</param>
    /// <param name="media">The media file whose timeline to use.</param>
    public static AlignedSubtitles ToMediaTimeline(SubtitleDocument document, MediaFile media) => Shift(document, OffsetFor(media));

    private static MediaTime Plus(MediaTime a, MediaTime b) => SubtitleTimes.Add(a, b);
}

/// <summary>
/// The happy path for subtitle files: read, convert and write in one call. Each method is one line over the layers
/// (<see cref="SubtitleFormats"/>, <see cref="SubtitleFormat"/>, <see cref="SubtitleAlignment"/>) and has no options of its own;
/// use those for encodings, limits, defect logs and overwrite control. Formats are recognised by content, never by extension; the
/// extension only chooses the format to write.
/// </summary>
public static class Subtitles
{
    /// <summary>Reads a subtitle file in whichever format its content shows (SRT, WebVTT, ASS/SSA, TTML, SBV, LRC).</summary>
    /// <param name="path">The file.</param>
    /// <returns>The cue model.</returns>
    /// <exception cref="UnsupportedMediaException">The content is not a recognised subtitle format.</exception>
    /// <exception cref="MediaLimitExceededException">The file is larger than <see cref="MediaLimits.MaxAllocationBytes"/> or holds too many cues.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SubtitleDocument Read(string path) => SubtitleFormats.Read(SubtitleFiles.ReadBytes(path));

    /// <summary>Reads a subtitle file asynchronously. See <see cref="Read(string)"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<SubtitleDocument> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        SubtitleFormats.Read(await SubtitleFiles.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes a document to a new file in the format its extension names.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The new file; <c>.srt</c>, <c>.vtt</c>, <c>.ass</c>/<c>.ssa</c>, <c>.ttml</c>/<c>.dfxp</c>/<c>.xml</c>, <c>.sbv</c> or <c>.lrc</c>.</param>
    /// <returns>What the format could not carry.</returns>
    /// <exception cref="UnsupportedMediaException">The extension names no writable format.</exception>
    /// <exception cref="IOException">The file exists (nothing is overwritten) or cannot be written.</exception>
    public static ConversionReport Write(SubtitleDocument document, string path) => SubtitleFiles.Write(document, path);

    /// <summary>Writes a document to a new file asynchronously. See <see cref="Write(SubtitleDocument, string)"/>.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The new file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task<ConversionReport> WriteAsync(SubtitleDocument document, string path, CancellationToken cancellationToken = default) =>
        SubtitleFiles.WriteAsync(document, path, cancellationToken);

    /// <summary>Converts a subtitle file to the format the output extension names, into a new file.</summary>
    /// <param name="input">The source file (format by content).</param>
    /// <param name="output">The new file (format by extension).</param>
    /// <returns>What the target format could not carry, per cue.</returns>
    /// <exception cref="UnsupportedMediaException">The input is not recognised or the extension names no writable format.</exception>
    /// <exception cref="IOException">The output exists or a file cannot be read or written.</exception>
    public static ConversionReport Convert(string input, string output) => Write(Read(input), output);

    /// <summary>Converts asynchronously. See <see cref="Convert(string, string)"/>.</summary>
    /// <param name="input">The source file.</param>
    /// <param name="output">The new file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ConversionReport> ConvertAsync(string input, string output, CancellationToken cancellationToken = default) =>
        await WriteAsync(await ReadAsync(input, cancellationToken).ConfigureAwait(false), output, cancellationToken).ConfigureAwait(false);
}

// File access for the happy path, kept apart so the layers above stay free of paths.
internal static class SubtitleFiles
{
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

    internal static ConversionReport Write(SubtitleDocument document, string path)
    {
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

namespace RecursiveAI.OneMedia.Captions;

/// <summary>
/// A text subtitle track of an open file with its cues: what <c>MediaFile.Subtitles</c> lists. The track's properties come from the container;
/// the cues are read from the track's packets the first time they are asked for and kept.
/// </summary>
public sealed class SubtitleTrack
{
    private readonly MediaFile _file;
    private SubtitleDocument? _document;

    internal SubtitleTrack(MediaFile file, MediaTrack track, TextTrackFormat format)
    {
        _file = file;
        Track = track;
        Format = format;
    }

    /// <summary>The container's track: index, language, name, disposition, codec tag and timescale.</summary>
    public MediaTrack Track { get; }

    /// <summary>How the container stores the text.</summary>
    public TextTrackFormat Format { get; }

    /// <summary>The language as the container states it (an ISO 639-2/T code in MP4, a Matroska language or BCP 47 tag), or null.</summary>
    public string? Language => Track.Language;

    /// <summary>The track's title or handler name, or null.</summary>
    public string? Name => Track.Name;

    /// <summary>Default, forced and hearing-impaired flags and the others the container carries.</summary>
    public TrackDisposition Disposition => Track.Disposition;

    /// <summary>True when the cues have been read.</summary>
    public bool IsRead => _document is not null;

    /// <summary>
    /// The track as a cue document: cues in time order, styles and regions the format declares, the language, and the format-specific information
    /// needed to write the same format again. Reading the track needs the file's source to be readable again (see <see cref="MediaFile.Packets"/>).
    /// </summary>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source (use <see cref="ReadAsync"/>), or was read before and its source cannot seek.</exception>
    public SubtitleDocument Document => _document ??= TextTrackDecoder.Read(_file, Track);

    /// <summary>The cues of <see cref="Document"/>.</summary>
    /// <exception cref="MediaReadStateException">As for <see cref="Document"/>.</exception>
    public SubtitleList<SubtitleCue> Cues => Document.Cues;

    /// <summary>Reads the cues from either kind of source.</summary>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>The document, also kept for <see cref="Document"/>.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async ValueTask<SubtitleDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        _document ??= await TextTrackDecoder.ReadAsync(_file, Track, null, cancellationToken).ConfigureAwait(false);
}

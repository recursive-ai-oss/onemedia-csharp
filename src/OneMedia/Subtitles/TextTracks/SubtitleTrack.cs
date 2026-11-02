namespace RecursiveAI.OneMedia.Captions;

/// <summary>What a <see cref="SubtitleTrack"/> entry is carried as.</summary>
public enum SubtitleKind
{
    /// <summary>A text track of the container (tx3g, WebVTT, TTML, SubRip, ASS…): <see cref="SubtitleTrack.Format"/> says which.</summary>
    Text = 0,

    /// <summary>A bitmap track (Blu-ray PGS, DVD VobSub, DVB subtitles): the cues are RGBA images with timing and position.</summary>
    Bitmap,

    /// <summary>One teletext subtitle page of a teletext stream (<see cref="SubtitleTrack.Label"/> is the page number, such as <c>888</c>).</summary>
    Teletext,

    /// <summary>One CEA-608 channel (<see cref="SubtitleTrack.Label"/> is <c>CC1</c>…<c>CC4</c> or <c>Text1</c>…<c>Text4</c>) found in a video track or a <c>c608</c> track.</summary>
    Cea608,

    /// <summary>One CEA-708 service (<see cref="SubtitleTrack.Label"/> is <c>service N</c>) found in a video track.</summary>
    Cea708,
}

/// <summary>
/// A subtitle source of an open file with its cues: what <c>MediaFile.Subtitles</c> lists. A container text or bitmap track, a teletext page, a
/// CEA-608 channel or a CEA-708 service, all read into the same cue model. The properties come from the container or the stream; the cues are read
/// the first time they are asked for and kept.
/// </summary>
public sealed class SubtitleTrack
{
    private readonly MediaFile _file;
    private readonly Func<SubtitleDocument>? _read;
    private readonly string? _language;
    private SubtitleDocument? _document;

    internal SubtitleTrack(MediaFile file, MediaTrack track, TextTrackFormat format)
    {
        _file = file;
        Track = track;
        Format = format;
        Kind = SubtitleKind.Text;
        Codec = track.CodecTag ?? track.Codec.Name;
    }

    internal SubtitleTrack(MediaFile file, MediaTrack track, SubtitleKind kind, string codec, string? label, string? language, Func<SubtitleDocument> read)
    {
        _file = file;
        Track = track;
        Format = TextTrackFormat.Unknown;
        Kind = kind;
        Codec = codec;
        Label = label;
        _language = language;
        _read = read;
    }

    /// <summary>What the entry is: a container text track, a bitmap track, a teletext page, a CEA-608 channel or a CEA-708 service.</summary>
    public SubtitleKind Kind { get; }

    /// <summary>The codec as the container names it (sample entry code or CodecID for tracks, otherwise <c>dvb-teletext</c>, <c>cea608</c> or <c>cea708</c>).</summary>
    public string Codec { get; }

    /// <summary>For teletext, the page (<c>888</c>); for captions, the channel (<c>CC1</c>) or service (<c>service 1</c>); null for container tracks.</summary>
    public string? Label { get; }

    /// <summary>The container's track that carries the entry: index, language, name, disposition, codec tag and timescale. For captions found in video this is the video track.</summary>
    public MediaTrack Track { get; }

    /// <summary>How the container stores the text.</summary>
    public TextTrackFormat Format { get; }

    /// <summary>The language as the container states it (an ISO 639-2/T code in MP4, a Matroska language or BCP 47 tag), or null.</summary>
    public string? Language => _language ?? Track.Language;

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
    public SubtitleDocument Document => _document ??= _read is not null ? _read() : TextTrackDecoder.Read(_file, Track);

    /// <summary>The cues of <see cref="Document"/>.</summary>
    /// <exception cref="MediaReadStateException">As for <see cref="Document"/>.</exception>
    public SubtitleList<SubtitleCue> Cues => Document.Cues;

    /// <summary>Reads the cues from either kind of source. Only container text tracks read asynchronously; the other kinds are read on the calling thread and need a synchronous source.</summary>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>The document, also kept for <see cref="Document"/>.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async ValueTask<SubtitleDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        _document ??= _read is not null ? _read() : await TextTrackDecoder.ReadAsync(_file, Track, null, cancellationToken).ConfigureAwait(false);
}

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>What was wrong with a manifest that a lenient read recovered from.</summary>
public enum ManifestDefectKind
{
    /// <summary>An HLS playlist does not start with <c>#EXTM3U</c>.</summary>
    MissingPlaylistHeader,

    /// <summary>A segment URI has no <c>EXTINF</c> before it; the segment is kept with a zero duration.</summary>
    SegmentWithoutDuration,

    /// <summary>A URI in a multivariant playlist has no <c>EXT-X-STREAM-INF</c> before it; it is dropped.</summary>
    UriWithoutStreamInf,

    /// <summary>A tag's value or attributes are malformed or a required attribute is missing; the tag is ignored.</summary>
    MalformedTag,
}

/// <summary>A deviation from the standard found by a lenient read.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Offset">The index in the text that was passed in (UTF-16 code units, after a byte order mark was removed) of the first character of the line.</param>
/// <param name="Message">A description.</param>
public sealed record ManifestDefect(ManifestDefectKind Kind, int Line, long Offset, string Message);

/// <summary>The result of a lenient playlist read.</summary>
/// <param name="Playlist">The playlist, with everything recoverable.</param>
/// <param name="Defects">The defects found, in line order; empty for a playlist that follows the standard.</param>
public sealed record HlsParseResult(HlsPlaylist Playlist, IReadOnlyList<ManifestDefect> Defects);

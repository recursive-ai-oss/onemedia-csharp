namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// One rendition a presentation can be played as, as its playlist or manifest describes it: what a caller chooses between with
/// <see cref="PresentationOptions.VariantIndex"/>.
/// </summary>
/// <param name="Index">Position in <see cref="PresentationDemuxer.Variants"/>.</param>
/// <param name="Bandwidth">
/// Bits per second as declared. For an HLS multivariant playlist it is <c>AVERAGE-BANDWIDTH</c> when the entry has one and <c>BANDWIDTH</c> (the
/// peak, RFC 8216 §4.3.4.2) otherwise; for a DASH Representation it is its <c>@bandwidth</c>. 0 when none is given (a media playlist read on its own).
/// </param>
/// <param name="Codecs">The <c>CODECS</c> or <c>codecs</c> string as declared (RFC 6381 form), or null.</param>
/// <param name="Width">Declared picture width in pixels; 0 when not declared.</param>
/// <param name="Height">Declared picture height in pixels; 0 when not declared.</param>
/// <param name="Name">The variant's name or identifier (a DASH Representation id), or null.</param>
public sealed record PresentationVariant(int Index, long Bandwidth, string? Codecs, int Width, int Height, string? Name);

/// <summary>An initialization segment (an HLS <c>EXT-X-MAP</c>, a DASH <c>Initialization</c>): a resource, or a range of one.</summary>
internal sealed record PlannedMap(Uri Location, long Offset, long Length);

/// <summary>One media segment in presentation order.</summary>
internal sealed class PlannedSegment
{
    public required Uri Location { get; init; }

    /// <summary>First byte of the range inside the resource; 0 with <see cref="Length"/> -1 for the whole resource.</summary>
    public long Offset { get; init; }

    /// <summary>Length of the range; -1 for the rest of the resource.</summary>
    public long Length { get; init; } = -1;

    /// <summary>Duration as the playlist or manifest states it; <see cref="MediaTime.Unknown"/> when it does not.</summary>
    public MediaTime Duration { get; init; } = MediaTime.Unknown;

    /// <summary>The playlist marks a discontinuity before this segment (HLS <c>EXT-X-DISCONTINUITY</c>, a DASH period boundary).</summary>
    public bool Discontinuity { get; init; }

    public PlannedMap? Init { get; init; }

    /// <summary>The segment is encrypted (HLS <c>EXT-X-KEY</c> with a method other than NONE, DASH <c>ContentProtection</c>).</summary>
    public bool Encrypted { get; init; }

    /// <summary>
    /// A fixed shift to add to every time the segment's data states, from the manifest's own timeline (DASH: period start minus
    /// <c>presentationTimeOffset</c>). Null for HLS, where a discontinuity is resolved from the packets that precede it.
    /// </summary>
    public MediaTime? TimelineOffset { get; init; }

    /// <summary>Media sequence number (HLS), used to find the new segments when a live playlist is loaded again.</summary>
    public long Sequence { get; init; }
}

/// <summary>A sequence of segments that make up one stream of the presentation (an HLS media playlist, a DASH Representation across periods).</summary>
internal sealed class PlannedRendition
{
    public string? Id { get; set; }

    public TrackKind KindHint { get; set; }

    public string? Codecs { get; set; }

    public long Bandwidth { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? Language { get; set; }

    public string? Name { get; set; }

    public bool IsDefault { get; set; }

    public List<PlannedSegment> Segments { get; } = [];

    /// <summary>The playlist is complete: no more segments will be added.</summary>
    public bool Ended { get; set; } = true;

    /// <summary>Where the playlist came from, for loading it again when it is live.</summary>
    public Uri? PlaylistLocation { get; set; }

    /// <summary>Media sequence number of the first listed segment (HLS).</summary>
    public long FirstSequence { get; set; }

    public bool Protected { get; set; }

    /// <summary>
    /// Single-file DASH index (<c>SegmentBase</c> with <c>indexRange</c>): the subsegments are found by reading the <c>sidx</c> box at this range
    /// of <see cref="IndexedResource"/>. Null for everything else.
    /// </summary>
    public (long Offset, long Length)? IndexRange { get; set; }

    public Uri? IndexedResource { get; set; }

    public PlannedMap? IndexedInit { get; set; }

    public MediaTime? IndexedOffset { get; set; }

    public bool IndexedEncrypted { get; set; }
}

/// <summary>What a playlist or manifest comes to: the variants, and the renditions of the chosen one.</summary>
internal sealed class PresentationPlan
{
    public List<PresentationVariant> Variants { get; } = [];

    /// <summary>Per variant, the renditions that make it up (one for a muxed HLS variant, one per adaptation set for DASH).</summary>
    public List<List<PlannedRendition>> VariantRenditions { get; } = [];

    /// <summary>The duration the manifest states for the whole presentation, or unknown.</summary>
    public MediaTime Duration { get; set; } = MediaTime.Unknown;

    public bool IsMaster { get; set; }
}

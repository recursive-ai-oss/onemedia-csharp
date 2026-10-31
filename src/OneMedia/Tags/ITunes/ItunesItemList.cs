using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tags.ITunes;

/// <summary>
/// An iTunes-style metadata item list: <c>ilst</c> inside a <c>meta</c> box whose handler is <c>mdir</c>. Items are four-character
/// atoms (<c>©nam</c>, <c>covr</c>, <c>trkn</c>…) or freeform <c>----</c> items named by <c>mean</c> and <c>name</c>; each holds one or more
/// <c>data</c> atoms with a type code and locale. One <see cref="NativeTag"/> is produced per <c>data</c> atom.
/// </summary>
public sealed class ItunesItemList : ITagContainer
{
    private ItunesItemList(ByteRange range, long trackId, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        TrackId = trackId;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.ITunes;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>Reads an <c>ilst</c> box. Deviations are added to <paramref name="defects"/> with their offsets; the call does not throw for malformed items.</summary>
    /// <param name="ilst">The item list box.</param>
    /// <param name="source">The source the box was read from; tag values are read from it later.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="trackId">The track the list belongs to (0 for the movie).</param>
    /// <returns>The items.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ItunesItemList Read(BoxNode ilst, IMediaSource source, MediaLimits limits, DefectLog defects, long trackId = 0)
    {
        ArgumentNullException.ThrowIfNull(ilst);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        List<NativeTag> tags = [];
        ItemListParser.Parse(ilst, new TagIo(source, limits, defects), TagSystems.ITunes, trackId, null, tags);
        return new ItunesItemList(new ByteRange(ilst.Offset, ilst.Header.HeaderSize + ilst.PayloadSize), trackId, tags);
    }
}

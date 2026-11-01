namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// An ID3v2 tag found in front of an Ogg or FLAC stream, kept as one binary <see cref="NativeTag"/> (key <c>ID3v2</c>) until the ID3 reader decodes it.
/// The tag's bytes are the whole value: header, frames, padding and footer.
/// </summary>
public sealed class RawId3Prefix : ITagContainer
{
    /// <summary>The system name of ID3 tags.</summary>
    public const string SystemName = "id3";

    internal RawId3Prefix(ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => SystemName;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    internal static RawId3Prefix Make(ReadOnlyMemory<byte> bytes, MediaLimits limits, DefectLog defects)
    {
        ByteRange range = new(0, bytes.Length);
        TagIo io = new(new MemoryMediaSource(bytes), limits, defects);
        return new RawId3Prefix(range, [new NativeTag(io, SystemName, "ID3v2", 0, TagValueKind.Binary, 0, 0, null, range, range)]);
    }
}

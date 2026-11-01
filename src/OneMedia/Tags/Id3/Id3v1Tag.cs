using System.Text;

namespace RecursiveAI.OneMedia.Tags.Id3;

/// <summary>
/// An ID3v1 or ID3v1.1 tag: the fixed 128 bytes at the very end of a file (id3.org, "ID3v1"). Fields that are blank are not listed; the tag
/// holds the byte range of every field it lists, and the genre number as a number.
/// </summary>
public sealed class Id3v1Tag : ITagContainer
{
    /// <summary>The length of the tag.</summary>
    public const int Length = 128;

    private readonly List<NativeTag> _tags = [];

    private Id3v1Tag(ByteRange range) => Range = range;

    /// <inheritdoc />
    public string System => TagSystems.Id3v1;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <summary>True when the tag is ID3v1.1: the last two bytes of the comment hold a zero and a track number.</summary>
    public bool IsVersion11 { get; private set; }

    /// <summary>The 227-byte extended tag ("TAG+") that sits directly before the tag, or <see cref="ByteRange.None"/>. It is located, not decoded.</summary>
    public ByteRange Extended { get; private set; } = ByteRange.None;

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags => _tags;

    /// <summary>Reads an ID3v1 tag at <paramref name="offset"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Absolute offset of the <c>TAG</c> identifier (128 bytes before the end of the file in the usual case).</param>
    /// <param name="tag">The tag when true.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>False when the bytes at <paramref name="offset"/> do not start with <c>TAG</c> or the source ends first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryRead(IMediaSource source, long offset, out Id3v1Tag? tag, MediaLimits? limits = null, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        tag = null;
        if (offset < 0)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[Length]; // the fixed ID3v1 block
        if (source.Read(offset, b) < Length || !b.StartsWith("TAG"u8))
        {
            return false;
        }

        limits ??= MediaLimits.Default;
        TagIo io = new(source, limits, defects ?? new DefectLog(limits));
        Id3v1Tag t = new(new ByteRange(offset, Length));
        t.Add(io, b, "title", 3, 30);
        t.Add(io, b, "artist", 33, 30);
        t.Add(io, b, "album", 63, 30);
        t.Add(io, b, "year", 93, 4);
        // ID3v1.1: a zero at byte 125 followed by a non-zero byte 126 makes the comment 28 bytes long and byte 126 the track number.
        bool v11 = b[125] == 0 && b[126] != 0;
        t.IsVersion11 = v11;
        t.Add(io, b, "comment", 97, v11 ? 28 : 30);
        if (v11)
        {
            NativeTag track = new(io, TagSystems.Id3v1, "track", 0, TagValueKind.Number, 0, 0, null, new ByteRange(offset + 125, 2), new ByteRange(offset + 126, 1)) { Number = b[126] };
            t._tags.Add(track);
        }

        if (b[127] != 255)
        {
            t._tags.Add(new NativeTag(io, TagSystems.Id3v1, "genre", 0, TagValueKind.Number, 0, 0, null, new ByteRange(offset + 127, 1), new ByteRange(offset + 127, 1)) { Number = b[127] });
        }

        long extendedAt = offset - 227;
        if (extendedAt >= 0)
        {
            Span<byte> marker = stackalloc byte[4]; // the "TAG+" identifier
            if (source.Read(extendedAt, marker) == 4 && marker.SequenceEqual("TAG+"u8))
            {
                t.Extended = new ByteRange(extendedAt, 227);
                io.Defects.Add(DefectKind.Unsupported, DefectSeverity.Info, extendedAt, "An extended ID3v1 tag (TAG+) is present; it is located but not decoded.", "id3v1.extended");
            }
        }

        tag = t;
        return true;
    }

    private void Add(TagIo io, ReadOnlySpan<byte> b, string key, int at, int length)
    {
        ReadOnlySpan<byte> field = b.Slice(at, length);
        int end = field.Length;
        while (end > 0 && (field[end - 1] == 0 || field[end - 1] == (byte)' '))
        {
            end--;
        }

        // A field ends at its first zero byte; anything after it is left-over text of a longer previous value.
        int nul = field[..end].IndexOf((byte)0);
        if (nul >= 0)
        {
            end = nul;
        }

        if (end == 0)
        {
            return;
        }

        ByteRange range = new(Range.Offset + at, length);
        NativeTag tag = new(io, TagSystems.Id3v1, key, 0, TagValueKind.Text, 1, 0, null, range, range) { Encoding = TagTextEncoding.Latin1 };
        tag.SetText(Encoding.Latin1.GetString(field[..end]));
        _tags.Add(tag);
    }
}

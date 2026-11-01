using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Tags.Lyrics3;

/// <summary>
/// A Lyrics3 block (version 1 or the v2.00 field list) found at the end of an MP3 file, before any APE or ID3v1 tag. Lyrics3 is an old,
/// informal format: the block is located, split into its three-letter fields when it is v2.00, and otherwise kept as one binary value.
/// </summary>
public sealed class Lyrics3Tag : ITagContainer
{
    private const int Begin = 11; // "LYRICSBEGIN"
    private const int End200 = 9; // "LYRICS200"
    private const int SizeDigits = 6;
    private const int MaxV1 = 5100;
    private readonly List<NativeTag> _tags = [];

    private Lyrics3Tag(ByteRange range, int version)
    {
        Range = range;
        Version = version;
    }

    /// <inheritdoc />
    public string System => TagSystems.Lyrics3;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <summary>1 for <c>LYRICSBEGIN</c> … <c>LYRICSEND</c>, 2 for the v2.00 field list ending in <c>LYRICS200</c>.</summary>
    public int Version { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags => _tags;

    /// <summary>Looks for a Lyrics3 block that ends at <paramref name="end"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="end">Absolute offset just past the block (the start of the APE or ID3v1 tag behind it, or the end of the file).</param>
    /// <param name="tag">The block when true.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>False when no block ends there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryReadEndingAt(IMediaSource source, long end, out Lyrics3Tag? tag, MediaLimits? limits = null, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        tag = null;
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        Span<byte> tail = stackalloc byte[SizeDigits + End200]; // the six size digits and the end marker
        if (end < tail.Length || source.Read(end - tail.Length, tail) < tail.Length)
        {
            return false;
        }

        TagIo io = new(source, limits, defects);
        if (tail[SizeDigits..].SequenceEqual("LYRICS200"u8))
        {
            if (!int.TryParse(Encoding.ASCII.GetString(tail[..SizeDigits]), NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < Begin || end - tail.Length - size < 0)
            {
                return false;
            }

            long start = end - tail.Length - size;
            Span<byte> marker = stackalloc byte[Begin]; // the block's opening marker
            if (source.Read(start, marker) < Begin || !marker.SequenceEqual("LYRICSBEGIN"u8))
            {
                return false;
            }

            Lyrics3Tag t = new(new ByteRange(start, end - start), 2);
            t.ReadFields(io, start + Begin, size - Begin);
            tag = t;
            return true;
        }

        if (tail[(SizeDigits + End200 - 9)..].SequenceEqual("LYRICSEND"u8))
        {
            long window = Math.Min(end, MaxV1 + Begin + 9);
            byte[] bytes = new byte[window];
            int got = source.Read(end - window, bytes);
            int at = bytes.AsSpan(0, got).LastIndexOf("LYRICSBEGIN"u8);
            if (at < 0)
            {
                return false;
            }

            long start = end - window + at;
            ByteRange range = new(start, end - start);
            Lyrics3Tag t = new(range, 1);
            t._tags.Add(new NativeTag(io, TagSystems.Lyrics3, "LYRICS", 0, TagValueKind.Binary, 0, 0, null, range, new ByteRange(start + Begin, range.Length - Begin - 9)));
            tag = t;
            return true;
        }

        return false;
    }

    private void ReadFields(TagIo io, long start, long length)
    {
        // Lyrics3 v2.00: each field is a three-letter identifier, a five-digit decimal size and the data.
        long p = 0;
        while (p + 8 <= length)
        {
            byte[] h = io.Read(new ByteRange(start + p, 8));
            if (h.Length < 8 || !int.TryParse(Encoding.ASCII.GetString(h, 3, 5), NumberStyles.None, CultureInfo.InvariantCulture, out int size) || p + 8 + size > length)
            {
                io.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, start + p, "A Lyrics3 field has an unusable size; the rest of the block is not split.", "lyrics3.field");
                return;
            }

            string id = Encoding.ASCII.GetString(h, 0, 3);
            ByteRange field = new(start + p, 8 + size);
            ByteRange value = new(start + p + 8, size);
            NativeTag tag = new(io, TagSystems.Lyrics3, id, 0, TagValueKind.Text, 1, 0, null, field, value) { Encoding = TagTextEncoding.Latin1 };
            if (size <= 64 * 1024)
            {
                tag.SetText(Encoding.Latin1.GetString(io.Read(value)).TrimEnd('\0'));
            }

            _tags.Add(tag);
            p += 8 + size;
        }
    }
}

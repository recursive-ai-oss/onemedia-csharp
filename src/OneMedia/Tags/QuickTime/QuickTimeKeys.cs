using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tags.QuickTime;

/// <summary>
/// QuickTime metadata with a key table: a <c>meta</c> box with the <c>mdta</c> handler, a <c>keys</c> box listing reverse-DNS key names
/// and an <c>ilst</c> whose items are numbered by 1-based key index (Apple QuickTime File Format, "Metadata").
/// Keys such as <c>com.apple.quicktime.creationdate</c> and <c>com.apple.quicktime.location.ISO6709</c> are how iPhones record capture time and place.
/// </summary>
public sealed class QuickTimeKeys : ITagContainer
{
    private QuickTimeKeys(ByteRange range, long trackId, IReadOnlyList<string> keys, ByteRange keysRange, ByteRange listRange, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        TrackId = trackId;
        Keys = keys;
        KeysRange = keysRange;
        ItemListRange = listRange;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.QuickTimeKeys;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <summary>The <c>meta</c> box.</summary>
    public ByteRange Range { get; }

    /// <summary>
    /// The key names in table order (item number 1 is element 0). A key whose namespace is not <c>mdta</c> reads as <c>namespace:name</c>.
    /// </summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>The <c>keys</c> box, or <see cref="ByteRange.None"/> when the file has none.</summary>
    public ByteRange KeysRange { get; }

    /// <summary>The <c>ilst</c> box, or <see cref="ByteRange.None"/> when the file has none.</summary>
    public ByteRange ItemListRange { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>Reads the key table and items of a <c>meta</c> box. Truncated tables, bad key sizes and out-of-range indexes are defects; the call does not throw for them.</summary>
    /// <param name="meta">The <c>meta</c> box.</param>
    /// <param name="source">The source the box was read from; tag values are read from it later.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="trackId">The track the box belongs to (0 for the movie).</param>
    /// <returns>The keys and items.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static QuickTimeKeys Read(BoxNode meta, IMediaSource source, MediaLimits limits, DefectLog defects, long trackId = 0)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        BoxNode? keysBox = meta.Find(BoxTypes.Make("keys"));
        BoxNode? ilst = meta.Find(BoxTypes.Ilst);
        List<string> keys = keysBox is null ? [] : ReadKeys(keysBox, io);
        List<NativeTag> tags = [];
        if (ilst is not null)
        {
            ItemListParser.Parse(ilst, io, TagSystems.QuickTimeKeys, trackId, keys, tags);
        }

        return new QuickTimeKeys(
            new ByteRange(meta.Offset, meta.Header.HeaderSize + meta.PayloadSize),
            trackId,
            keys,
            keysBox is null ? ByteRange.None : new ByteRange(keysBox.Offset, keysBox.Header.HeaderSize + keysBox.PayloadSize),
            ilst is null ? ByteRange.None : new ByteRange(ilst.Offset, ilst.Header.HeaderSize + ilst.PayloadSize),
            tags);
    }

    private static List<string> ReadKeys(BoxNode box, TagIo io)
    {
        List<string> keys = [];
        if (box.PayloadSize < 8)
        {
            io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, box.Offset, "The keys box is shorter than its entry count.", "tags.keys-short");
            return keys;
        }

        io.Limits.CheckAllocation(Math.Min(box.PayloadSize, io.Limits.MaxTagBytes), box.Offset);
        byte[] p = box.ReadPayload(io.Limits.MaxTagBytes);
        long declared = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4));
        int pos = 8;
        while (keys.Count < declared)
        {
            // Each entry: 32-bit size including these 8 bytes, a 4-character namespace, then the key bytes.
            if (pos + 8 > p.Length)
            {
                io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, box.Header.PayloadOffset + pos, $"The keys box declares {declared} keys but ends after {keys.Count}.", "tags.keys-count");
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos));
            if (size < 8 || size > p.Length - pos)
            {
                io.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, box.Header.PayloadOffset + pos, $"Key {keys.Count + 1} has size {size}, which does not fit the keys box.", "tags.keys-size");
                break;
            }

            string ns = BoxReader.Latin1(p.AsSpan(pos + 4, 4));
            string name = TextDecoder.Decode(p.AsSpan(pos + 8, (int)size - 8), TagTextEncoding.Utf8, box.Header.PayloadOffset + pos + 8, io.Defects);
            keys.Add(ns == "mdta" ? name : ns + ":" + name);
            pos += (int)size;
        }

        return keys;
    }
}

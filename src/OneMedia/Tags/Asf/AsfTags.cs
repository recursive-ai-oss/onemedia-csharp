using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Asf;

namespace RecursiveAI.OneMedia.Tags.Asf;

/// <summary>
/// The tags of one ASF header object: the Content Description (title, author, copyright, description, rating), the Extended Content Description
/// (named, typed attributes), or a Metadata or Metadata Library object (attributes that belong to a stream or to the file, with 32-bit value
/// sizes that allow pictures). Tags keep the names the file gives them, such as <c>WM/AlbumTitle</c>. Values are read from the file when asked for.
/// </summary>
public sealed class AsfTags : ITagContainer
{
    private AsfTags(AsfObjectKind kind, ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Kind = kind;
        Range = range;
        Tags = tags;
    }

    /// <summary>Which object the tags come from.</summary>
    public AsfObjectKind Kind { get; }

    /// <inheritdoc />
    public string System => TagSystems.Asf;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    // Attribute value types as the files use them.
    private const int TypeUnicode = 0, TypeBytes = 1, TypeBool = 2, TypeDword = 3, TypeQword = 4, TypeWord = 5, TypeGuid = 6;

    /// <summary>The key under which the picture attribute is stored.</summary>
    public const string PictureKey = "WM/Picture";

    /// <summary>
    /// Reads the tags of a Content Description, Extended Content Description, Metadata or Metadata Library object. Only the attribute headers and
    /// small numeric values are read; text and binary values (pictures included) are read when the tag is asked for. A damaged object ends the
    /// walk with a defect, and the tags before the damage are kept.
    /// </summary>
    /// <param name="kind">The object's kind; any other kind yields no tags.</param>
    /// <param name="range">The object, header included.</param>
    /// <param name="source">The file (or the bytes kept from it).</param>
    /// <param name="languages">The file's Language List, for the language index of Metadata attributes.</param>
    /// <param name="limits">Limits: tag bytes and element counts.</param>
    /// <param name="defects">Receives the deviations found.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static AsfTags Read(AsfObjectKind kind, ByteRange range, IMediaSource source, IReadOnlyList<string> languages, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        Window w = new(source, range.End, cancellationToken);
        long at = range.Offset + 24;
        switch (kind)
        {
            case AsfObjectKind.ContentDescription:
                ReadContentDescription(io, w, at, range, tags);
                break;
            case AsfObjectKind.ExtendedContentDescription:
                ReadAttributes(io, w, at, range, languages, limits, defects, tags, extended: true);
                break;
            case AsfObjectKind.Metadata or AsfObjectKind.MetadataLibrary:
                ReadAttributes(io, w, at, range, languages, limits, defects, tags, extended: false);
                break;
        }

        return new AsfTags(kind, range, tags);
    }

    // Five 16-bit byte lengths (title, author, copyright, description, rating), then the five UTF-16LE strings in that order.
    private static void ReadContentDescription(TagIo io, Window w, long at, ByteRange range, List<NativeTag> tags)
    {
        if (!w.TryGet(at, 10, out ReadOnlySpan<byte> lengths))
        {
            io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at, "The Content Description object is shorter than its length fields.", "tags.asf-content-description");
            return;
        }

        Span<int> sizes = stackalloc int[5]; // the five string lengths
        long total = 0;
        for (int i = 0; i < 5; i++)
        {
            sizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(lengths[(i * 2)..]);
            total += sizes[i];
        }

        string[] names = ["Title", "Author", "Copyright", "Description", "Rating"];
        long valueAt = at + 10;
        if (valueAt + total > range.End)
        {
            io.Defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, at, "The strings of the Content Description object are longer than the object; the rest is ignored.", "tags.asf-content-description");
        }

        for (int i = 0; i < 5; i++)
        {
            if (sizes[i] > 0 && valueAt + sizes[i] <= range.End)
            {
                tags.Add(new NativeTag(io, TagSystems.Asf, names[i], 0, TagValueKind.Text, TypeUnicode, 0, null, range, new ByteRange(valueAt, sizes[i])) { Encoding = TagTextEncoding.Utf16LittleEndian });
            }

            valueAt += sizes[i];
        }
    }

    private static void ReadAttributes(TagIo io, Window w, long at, ByteRange range, IReadOnlyList<string> languages, MediaLimits limits, DefectLog defects, List<NativeTag> tags, bool extended)
    {
        if (!w.TryGet(at, 2, out ReadOnlySpan<byte> countBytes))
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at, "An ASF attribute object is shorter than its count field.", "tags.asf-attributes");
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        at += 2;
        for (int i = 0; i < count; i++)
        {
            limits.CheckElementCount(i + 1, at);
            long start = at;
            int languageIndex = 0, stream = 0, nameLength, type;
            long valueLength;
            int headerLength;
            if (extended)
            {
                // Name length (2), name, value type (2), value length (2), value.
                if (!w.TryGet(at, 2, out ReadOnlySpan<byte> h0))
                {
                    break;
                }

                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h0);
                if (nameLength > 2048 || !w.TryGet(at + 2 + nameLength, 4, out ReadOnlySpan<byte> h1))
                {
                    defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, at, "An attribute name is too long or the object ends inside the attribute; the rest of the object is ignored.", "tags.asf-attributes");
                    break;
                }

                type = BinaryPrimitives.ReadUInt16LittleEndian(h1);
                valueLength = BinaryPrimitives.ReadUInt16LittleEndian(h1[2..]);
                headerLength = 2 + nameLength + 4;
            }
            else
            {
                // Language list index (2), stream number (2), name length (2), value type (2), value length (4), name, value.
                if (!w.TryGet(at, 12, out ReadOnlySpan<byte> h))
                {
                    break;
                }

                languageIndex = BinaryPrimitives.ReadUInt16LittleEndian(h);
                stream = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]) & 0x7F;
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
                type = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
                valueLength = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
                headerLength = 12 + nameLength;
                if (nameLength > 2048)
                {
                    defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, at, "An attribute name is too long; the rest of the object is ignored.", "tags.asf-attributes");
                    break;
                }
            }

            long valueAt = start + headerLength;
            if (valueAt + valueLength > range.End)
            {
                defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, start, "An attribute's value is longer than its object; the rest of the object is ignored.", "tags.asf-attributes");
                break;
            }

            int nameAt = extended ? 2 : 12;
            if (!w.TryGet(start + nameAt, nameLength, out ReadOnlySpan<byte> nameBytes))
            {
                break;
            }

            string name = Encoding.Unicode.GetString(nameBytes[..(nameLength & ~1)]).TrimEnd('\0');
            string? language = languageIndex < languages.Count && !extended ? languages[languageIndex] : null;
            ByteRange whole = new(start, headerLength + valueLength);
            ByteRange value = new(valueAt, valueLength);
            at = valueAt + valueLength;
            NativeTag? tag = MakeTag(io, w, name, type, stream, languageIndex, language, whole, value, defects);
            if (tag is not null)
            {
                tags.Add(tag);
            }
        }
    }

    private static NativeTag? MakeTag(TagIo io, Window w, string name, int type, int stream, int languageIndex, string? language, ByteRange whole, ByteRange value, DefectLog defects)
    {
        switch (type)
        {
            case TypeUnicode:
                return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Text, TypeUnicode, (uint)languageIndex, language, whole, value) { Encoding = TagTextEncoding.Utf16LittleEndian };
            case TypeBool or TypeDword or TypeQword or TypeWord:
            {
                if (value.Length is not (1 or 2 or 4 or 8) || !w.TryGet(value.Offset, (int)value.Length, out ReadOnlySpan<byte> v))
                {
                    defects.Add(DefectKind.InvalidSize, DefectSeverity.Info, value.Offset, $"The numeric attribute '{name}' has a value of {value.Length} bytes.", "tags.asf-number");
                    return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Binary, (uint)type, (uint)languageIndex, language, whole, value);
                }

                long number = value.Length switch
                {
                    1 => v[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(v),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(v),
                    _ => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(v), long.MaxValue),
                };
                return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Number, (uint)type, (uint)languageIndex, language, whole, value) { Number = number };
            }

            case TypeGuid when value.Length == 16 && w.TryGet(value.Offset, 16, out ReadOnlySpan<byte> g):
            {
                NativeTag tag = new(io, TagSystems.Asf, name, stream, TagValueKind.Text, TypeGuid, (uint)languageIndex, language, whole, value);
                tag.SetText(new Guid(g).ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
                return tag;
            }

            case TypeBytes when name == PictureKey:
                return MakePicture(io, w, name, stream, languageIndex, language, whole, value, defects);
            default:
                return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Binary, (uint)type, (uint)languageIndex, language, whole, value);
        }
    }

    // A picture value: picture type (1), size of the image data (4), MIME type (UTF-16LE, NUL-terminated), description (UTF-16LE, NUL-terminated), the image data.
    private static NativeTag MakePicture(TagIo io, Window w, string name, int stream, int languageIndex, string? language, ByteRange whole, ByteRange value, DefectLog defects)
    {
        int probe = (int)Math.Min(value.Length, 2048);
        if (probe >= 7 && w.TryGet(value.Offset, probe, out ReadOnlySpan<byte> p))
        {
            int pictureType = p[0];
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(p[1..]);
            int at = 5;
            string mime = ReadNulTerminated(p, ref at);
            _ = ReadNulTerminated(p, ref at);
            if (at <= p.Length && at + dataSize <= value.Length && dataSize > 0)
            {
                return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Image, TypeBytes, (uint)languageIndex, language, whole, new ByteRange(value.Offset + at, dataSize))
                {
                    MimeType = mime.Length > 0 ? mime : "application/octet-stream",
                    PictureTypeNumber = pictureType,
                };
            }
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, value.Offset, "A WM/Picture attribute does not have the picture layout; it is kept as binary.", "tags.asf-picture");
        return new NativeTag(io, TagSystems.Asf, name, stream, TagValueKind.Binary, TypeBytes, (uint)languageIndex, language, whole, value);
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> p, ref int at)
    {
        int start = at;
        while (at + 1 < p.Length && (p[at] != 0 || p[at + 1] != 0))
        {
            at += 2;
        }

        string s = Encoding.Unicode.GetString(p[start..Math.Min(at, p.Length)]);
        at += 2;
        return s;
    }

    /// <summary>A small read window over the file, so that walking many short attribute headers costs few source reads.</summary>
    private sealed class Window(IMediaSource source, long end, CancellationToken ct)
    {
        private readonly byte[] _buffer = new byte[8192];
        private long _start = -1;
        private int _length;

        public bool TryGet(long offset, int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            if (length < 0 || length > _buffer.Length || offset < 0 || offset + length > end)
            {
                return false;
            }

            if (offset < _start || offset + length > _start + _length)
            {
                int want = (int)Math.Min(_buffer.Length, end - offset);
                _length = source.Read(offset, _buffer.AsSpan(0, want), ct);
                _start = offset;
                if (_length < length)
                {
                    _length = 0;
                    _start = -1;
                    return false;
                }
            }

            bytes = _buffer.AsSpan((int)(offset - _start), length);
            return true;
        }
    }
}

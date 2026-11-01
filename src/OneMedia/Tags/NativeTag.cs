using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>Names of the native tag systems, as used in <see cref="NativeTag.System"/> and by <c>Native[system, key]</c> lookups.</summary>
public static class TagSystems
{
    /// <summary>QuickTime user data atoms (<c>udta</c>): the © text atoms and other atoms directly under <c>udta</c>.</summary>
    public const string QuickTimeUserData = "udta";

    /// <summary>iTunes-style metadata item list (<c>meta</c>/<c>ilst</c> with the <c>mdir</c> handler).</summary>
    public const string ITunes = "ilst";

    /// <summary>QuickTime metadata with a key table (<c>meta</c> with the <c>mdta</c> handler, <c>keys</c> and <c>ilst</c>).</summary>
    public const string QuickTimeKeys = "mdta";

    /// <summary>3GPP asset information boxes (<c>titl</c>, <c>auth</c>, <c>loci</c>, … in <c>udta</c>), 3GPP TS 26.244 §8.</summary>
    public const string ThreeGpp = "3gpp";

    /// <summary>Matroska <c>Tags</c>: SimpleTag elements with their targets, and attachments (cover art).</summary>
    public const string Matroska = "mkv";

    /// <summary>Matroska Segment information: <c>Title</c>, <c>MuxingApp</c>, <c>WritingApp</c> and <c>DateUTC</c>, as text.</summary>
    public const string MatroskaInfo = "mkv-info";

    /// <summary>The XMP packet carried in a <c>uuid</c> or <c>XMP_</c> box.</summary>
    public const string Xmp = "xmp";

    /// <summary>Tags of an ASF file: the content description, extended content description and metadata objects.</summary>
    public const string Asf = "asf";
}

/// <summary>How the bytes of a <see cref="NativeTag"/> value are to be read.</summary>
public enum TagValueKind
{
    /// <summary>Opaque bytes with no declared meaning.</summary>
    Binary = 0,

    /// <summary>Text; <see cref="NativeTag.Text"/> holds it decoded.</summary>
    Text,

    /// <summary>A whole number; <see cref="NativeTag.Number"/> holds it.</summary>
    Number,

    /// <summary>A floating-point number, shown as text by <see cref="NativeTag.Text"/>.</summary>
    Real,

    /// <summary>An image (cover art); <see cref="NativeTag.MimeType"/> says which format.</summary>
    Image,
}

/// <summary>
/// One tag exactly as a file stores it, in one of the native systems (<see cref="TagSystems"/>). The tag holds where its bytes sit
/// (<see cref="Range"/>, <see cref="ValueRange"/>) and, for short text and numbers, the decoded value. Large values (cover art, long
/// text) are read only when asked, with <see cref="ReadValue"/>, so listing a file's tags reads no picture bytes.
/// </summary>
/// <remarks>The tag reads from the source it was built from, so it is valid while the container is open. Not thread-safe.</remarks>
public sealed class NativeTag
{
    private readonly TagIo _io;
    private string? _text;
    private bool _textDecoded;

    internal NativeTag(TagIo io, string system, string key, long trackId, TagValueKind kind, uint dataType, uint locale, string? language, ByteRange range, ByteRange valueRange)
    {
        _io = io;
        System = system;
        Key = key;
        TrackId = trackId;
        Kind = kind;
        DataType = dataType;
        Locale = locale;
        Language = language;
        Range = range;
        ValueRange = valueRange;
    }

    /// <summary>The tag system, one of <see cref="TagSystems"/>.</summary>
    public string System { get; }

    /// <summary>
    /// The key as the system names it: an atom type with Latin-1 characters (<c>©nam</c>), a reverse-DNS key
    /// (<c>com.apple.quicktime.make</c>), a freeform item as <c>----:mean:name</c>, or an XMP property as <c>namespace-uri#name</c>.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The track the tag belongs to: 0 for the whole movie or file, a track id otherwise. Matroska tags aimed at an edition, chapter or
    /// attachment, or at a track that cannot be found, have -1.
    /// </summary>
    public long TrackId { get; }

    /// <summary>What the value holds.</summary>
    public TagValueKind Kind { get; }

    /// <summary>
    /// The data type code the file declares: the well-known type of an iTunes or QuickTime <c>data</c> atom (1 UTF-8, 2 UTF-16, 13 JPEG,
    /// 14 PNG, 21 signed integer…), 1 for UTF-8 text in other systems, or 0 when none is declared.
    /// </summary>
    public uint DataType { get; }

    /// <summary>The locale word of a <c>data</c> atom (country in the high half, language in the low half), or 0.</summary>
    public uint Locale { get; }

    /// <summary>The language of the value as an ISO 639-2 code, or <c>null</c> when the file declares none.</summary>
    public string? Language { get; }

    /// <summary>The whole item in the file, header included (the box that a writer replaces).</summary>
    public ByteRange Range { get; }

    /// <summary>Just the value bytes (after the type and locale fields of a <c>data</c> atom).</summary>
    public ByteRange ValueRange { get; }

    /// <summary>For Matroska tags, the TargetTypeValue of the tag's targets (50 when the file gives none); <c>null</c> in other systems.</summary>
    public long? TargetTypeValue { get; internal set; }

    /// <summary>For <see cref="TagValueKind.Image"/>, the media type (<c>image/jpeg</c>, <c>image/png</c>, <c>image/bmp</c>, <c>image/gif</c>); else <c>null</c>.</summary>
    public string? MimeType { get; internal set; }

    /// <summary>For a picture, the role the file states for it, as an ID3v2 picture type number (3 is the front cover); null when the system does not state one.</summary>
    public int? PictureTypeNumber { get; internal set; }

    /// <summary>The whole number for <see cref="TagValueKind.Number"/> values, else <c>null</c>.</summary>
    public long? Number { get; internal set; }

    /// <summary>The number for <see cref="TagValueKind.Real"/> values (a display value), else <c>null</c>.</summary>
    public double? RealValue { get; internal set; }

    /// <summary>
    /// The decoded text for <see cref="TagValueKind.Text"/> values, the decimal form of numbers, and <c>null</c> for binary and image values.
    /// Text longer than 64 KiB is read on first access, within <see cref="MediaLimits.MaxTagBytes"/>; a longer value gives <c>null</c> and
    /// a <see cref="DefectKind.LimitReached"/> defect. Invalid UTF-8 or UTF-16 is replaced by U+FFFD and reported as a defect once.
    /// </summary>
    public string? Text
    {
        get
        {
            if (_textDecoded)
            {
                return _text;
            }

            _textDecoded = true;
            if (Kind == TagValueKind.Number && Number is { } i)
            {
                _text = i.ToString(CultureInfo.InvariantCulture);
            }
            else if (Kind == TagValueKind.Real && RealValue is { } f)
            {
                _text = f.ToString("R", CultureInfo.InvariantCulture);
            }
            else if (Kind == TagValueKind.Text)
            {
                _text = LoadText();
            }

            return _text;
        }
    }

    internal TagTextEncoding Encoding { get; init; } = TagTextEncoding.Utf8;

    internal void SetText(string? text)
    {
        _text = text;
        _textDecoded = true;
    }

    /// <summary>Reads the value bytes from the source.</summary>
    /// <returns>The bytes; fewer than <see cref="ValueRange"/> says when the source ends early (a defect is recorded).</returns>
    /// <exception cref="MediaLimitExceededException">The value is larger than <see cref="MediaLimits.MaxTagBytes"/> (<see cref="MediaLimits.MaxAttachmentBytes"/> for images).</exception>
    public byte[] ReadValue()
    {
        if (Kind == TagValueKind.Image)
        {
            _io.Limits.CheckAttachment(ValueRange.Length, ValueRange.Offset);
        }
        else
        {
            _io.Limits.CheckTag(ValueRange.Length, ValueRange.Offset);
        }

        return _io.Read(ValueRange);
    }

    private string? LoadText()
    {
        try
        {
            _io.Limits.CheckTag(ValueRange.Length, ValueRange.Offset);
        }
        catch (MediaLimitExceededException e)
        {
            _io.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, e.Offset, $"The text value of '{Key}' is larger than the tag limit and is not read.", "tags.value-too-large");
            return null;
        }

        return TextDecoder.Decode(_io.Read(ValueRange), Encoding, ValueRange.Offset, _io.Defects);
    }

    /// <inheritdoc />
    public override string ToString() => $"{System}:{Key}" + (Kind is TagValueKind.Text or TagValueKind.Number or TagValueKind.Real ? "=" + Text : $" ({Kind}, {ValueRange.Length} bytes)");
}

/// <summary>How a text value is encoded in the file.</summary>
internal enum TagTextEncoding
{
    Utf8,
    Utf16BigEndian,
    Utf16WithByteOrderMark,
    Utf16LittleEndian,
}

/// <summary>Where tag values are read from: the source, the limits, and where defects go.</summary>
internal sealed class TagIo(IMediaSource source, MediaLimits limits, DefectLog defects)
{
    public IMediaSource Source { get; } = source;

    public MediaLimits Limits { get; } = limits;

    public DefectLog Defects { get; } = defects;

    public byte[] Read(ByteRange range)
    {
        if (range.Length <= 0)
        {
            return [];
        }

        byte[] bytes = new byte[range.Length];
        int n = Source.Read(range.Offset, bytes);
        if (n < bytes.Length)
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Error, range.Offset + n, "The source ends inside a tag value.", "tags.short-read");
            Array.Resize(ref bytes, n);
        }

        return bytes;
    }

    /// <summary>Reads at most <paramref name="max"/> bytes of a range into a new array.</summary>
    public byte[] ReadUpTo(ByteRange range, long max) => Read(new ByteRange(range.Offset, Math.Min(range.Length, max)));
}

/// <summary>Text decoding with defects: invalid sequences are replaced and reported, never thrown.</summary>
internal static class TextDecoder
{
    public static string Decode(ReadOnlySpan<byte> bytes, TagTextEncoding encoding, long offset, DefectLog defects)
    {
        if (encoding == TagTextEncoding.Utf16WithByteOrderMark || (encoding == TagTextEncoding.Utf8 && bytes.Length >= 2 && IsUtf16Bom(bytes)))
        {
            return DecodeUtf16(bytes, offset, defects);
        }

        if (encoding == TagTextEncoding.Utf16BigEndian)
        {
            return DecodeUtf16Be(bytes, offset, defects);
        }

        if (encoding == TagTextEncoding.Utf16LittleEndian)
        {
            return DecodeUtf16Le(bytes, offset, defects);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        if (!Utf8.IsValid(bytes))
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "The text is not valid UTF-8; invalid sequences are replaced.", "tags.invalid-utf8");
        }

        return TrimNul(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static bool IsUtf16Bom(ReadOnlySpan<byte> b) => (b[0] == 0xFE && b[1] == 0xFF) || (b[0] == 0xFF && b[1] == 0xFE);

    private static string DecodeUtf16(ReadOnlySpan<byte> bytes, long offset, DefectLog defects)
    {
        bool little = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        if (bytes.Length >= 2 && (little || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            bytes = bytes[2..];
        }

        return little ? DecodeUtf16Le(bytes, offset, defects) : DecodeUtf16Be(bytes, offset, defects);
    }

    private static string DecodeUtf16Be(ReadOnlySpan<byte> bytes, long offset, DefectLog defects) => DecodeUtf16Core(bytes, offset, defects, System.Text.Encoding.BigEndianUnicode);

    private static string DecodeUtf16Le(ReadOnlySpan<byte> bytes, long offset, DefectLog defects) => DecodeUtf16Core(bytes, offset, defects, System.Text.Encoding.Unicode);

    private static string DecodeUtf16Core(ReadOnlySpan<byte> bytes, long offset, DefectLog defects, Encoding encoding)
    {
        if ((bytes.Length & 1) != 0)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, offset, "UTF-16 text has an odd number of bytes; the last byte is dropped.", "tags.invalid-utf16");
            bytes = bytes[..^1];
        }

        string s = encoding.GetString(bytes);
        foreach (char c in s)
        {
            if (c == '\uFFFD')
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "The UTF-16 text has unpaired surrogates; they are replaced.", "tags.invalid-utf16");
                break;
            }
        }

        return TrimNul(s);
    }

    /// <summary>Strips the terminating NULs some writers append.</summary>
    public static string TrimNul(string s) => s.TrimEnd('\0');
}

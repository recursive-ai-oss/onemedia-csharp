using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.IsoBmff;

// 3GPP timed text (3GPP TS 26.245 §5.15 to §5.17) as MP4 and 3GP files carry it in 'tx3g' sample entries and samples, and the older QuickTime
// 'text' entry (Apple QuickTime File Format, "Text Media"). These types are the wire layer: they hold what the bytes say and write the same
// bytes back. Mapping to the cue model is one layer up, in RecursiveAI.OneMedia.Subtitles.TextTracks.

/// <summary>Face style bits of a <see cref="TextStyleRecord"/> (3GPP TS 26.245 §5.15). No bit set means plain text.</summary>
[Flags]
public enum TextFaceStyle
{
    /// <summary>Plain.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1,

    /// <summary>Italic.</summary>
    Italic = 2,

    /// <summary>Underlined.</summary>
    Underline = 4,
}

/// <summary>Display flags of a <see cref="TimedTextSampleEntry"/> (3GPP TS 26.245 §5.16). Bits not listed are kept as read.</summary>
[Flags]
public enum TimedTextDisplay : uint
{
    /// <summary>None set.</summary>
    None = 0,

    /// <summary>Text scrolls in.</summary>
    ScrollIn = 0x00000020,

    /// <summary>Text scrolls out.</summary>
    ScrollOut = 0x00000040,

    /// <summary>The two-bit scroll direction field: 0 up, 1 left, 2 down, 3 right (§5.8).</summary>
    ScrollDirectionMask = 0x00000180,

    /// <summary>Karaoke highlights from the start of the text, not only the named range.</summary>
    ContinuousKaraoke = 0x00000800,

    /// <summary>Text is written vertically.</summary>
    WriteTextVertically = 0x00020000,

    /// <summary>The background fills the whole text region, not only the text box.</summary>
    FillTextRegion = 0x00040000,
}

/// <summary>A rectangle in the track's pixel grid, relative to the track region (3GPP TS 26.245 §5.16 BoxRecord).</summary>
/// <param name="Top">Top edge.</param>
/// <param name="Left">Left edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="Right">Right edge.</param>
public readonly record struct TextBoxRecord(short Top, short Left, short Bottom, short Right)
{
    /// <summary>True when all four values are zero, which is how a sample entry says "no box".</summary>
    public bool IsEmpty => Top == 0 && Left == 0 && Bottom == 0 && Right == 0;
}

/// <summary>A style applied to a run of characters (3GPP TS 26.245 §5.15 StyleRecord). In a sample entry both offsets are zero.</summary>
/// <param name="StartChar">Character offset of the first character the style applies to.</param>
/// <param name="EndChar">Character offset one past the last character.</param>
/// <param name="FontId">Identifier from the font table.</param>
/// <param name="Face">Bold, italic and underline.</param>
/// <param name="FontSize">Nominal pixel size.</param>
/// <param name="ColorRgba">Text colour as 0xRRGGBBAA; alpha 255 is opaque.</param>
public readonly record struct TextStyleRecord(int StartChar, int EndChar, int FontId, TextFaceStyle Face, int FontSize, uint ColorRgba)
{
    /// <summary>Bytes a style record occupies.</summary>
    public const int Size = 12;
}

/// <summary>A font table entry (3GPP TS 26.245 §5.16 FontRecord).</summary>
/// <param name="FontId">The identifier style records refer to.</param>
/// <param name="Name">A comma-separated list of font names in preference order; <c>Serif</c>, <c>Sans-serif</c> and <c>Monospace</c> are generic.</param>
public readonly record struct TimedTextFont(int FontId, string Name);

/// <summary>One step of a dynamic highlight (<c>krok</c>, §5.17.1.3).</summary>
/// <param name="EndTime">When the step ends, in ticks of the track's timescale, from the start of the sample.</param>
/// <param name="StartChar">First highlighted character.</param>
/// <param name="EndChar">One past the last highlighted character.</param>
public readonly record struct KaraokeStep(uint EndTime, int StartChar, int EndChar);

/// <summary>A hyperlink (<c>href</c>, §5.17.1.5).</summary>
/// <param name="StartChar">First linked character.</param>
/// <param name="EndChar">One past the last linked character.</param>
/// <param name="Url">The target.</param>
/// <param name="Alt">Text to show instead of the URL.</param>
public readonly record struct TimedTextLink(int StartChar, int EndChar, string Url, string Alt);

/// <summary>
/// The sample entry of a 3GPP timed text track, <c>tx3g</c> (3GPP TS 26.245 §5.16): defaults for justification, background, text box,
/// style and fonts that each sample may override.
/// </summary>
public sealed class TimedTextSampleEntry
{
    /// <summary>The sample entry code.</summary>
    public const string Code = "tx3g";

    // 32-bit flags, two justification bytes, a 4-byte colour, a 8-byte box and a 12-byte style record.
    private const int FixedSize = 4 + 1 + 1 + 4 + 8 + TextStyleRecord.Size;

    /// <summary>Display flags; see <see cref="TimedTextDisplay"/>. Default 0.</summary>
    public uint DisplayFlags { get; init; }

    /// <summary>Horizontal justification: 0 left, 1 centred, -1 right. Default 0.</summary>
    public sbyte HorizontalJustification { get; init; }

    /// <summary>Vertical justification: 0 top, 1 centred, -1 bottom. Default 0.</summary>
    public sbyte VerticalJustification { get; init; }

    /// <summary>Background colour as 0xRRGGBBAA. Default 0 (transparent).</summary>
    public uint BackgroundRgba { get; init; }

    /// <summary>The default text box; all zero when the entry sets none.</summary>
    public TextBoxRecord DefaultTextBox { get; init; }

    /// <summary>The default style; its character offsets are zero.</summary>
    public TextStyleRecord DefaultStyle { get; init; } = new(0, 0, 1, TextFaceStyle.None, 18, 0xFFFFFFFF);

    /// <summary>The font table (<c>ftab</c>); empty when the entry has none.</summary>
    public IReadOnlyList<TimedTextFont> Fonts { get; init; } = [];

    /// <summary>The entry's other child boxes (<c>btrt</c>, <c>disp</c>, unknown ones) as <c>size, type, payload</c> bytes, in file order, kept so the entry writes back unchanged.</summary>
    public ReadOnlyMemory<byte> OtherBoxes { get; init; }

    /// <summary>
    /// Reads the entry from the bytes after the 8-byte sample entry header (reserved bytes and data reference index), which is what
    /// <see cref="CodecConfiguration.Data"/> holds for a text track.
    /// </summary>
    /// <param name="configuration">The entry's fields and child boxes.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>False when there are fewer bytes than the fixed fields need. A malformed child box ends the list of boxes and the remaining bytes are kept in <see cref="OtherBoxes"/>; it is not an error.</returns>
    public static bool TryParse(ReadOnlySpan<byte> configuration, out TimedTextSampleEntry? entry)
    {
        entry = null;
        if (configuration.Length < FixedSize)
        {
            return false;
        }

        TextBoxRecord box = new(ReadI16(configuration, 10), ReadI16(configuration, 12), ReadI16(configuration, 14), ReadI16(configuration, 16));
        TextStyleRecord style = ReadStyle(configuration[18..]);
        List<TimedTextFont> fonts = [];
        using MemoryStream others = new();
        int at = FixedSize;
        while (at < configuration.Length)
        {
            if (!TryReadBox(configuration, at, out string type, out ReadOnlySpan<byte> payload, out int total))
            {
                others.Write(configuration[at..]);
                break;
            }

            if (type == "ftab" && fonts.Count == 0 && TryReadFonts(payload, fonts))
            {
                // consumed into Fonts
            }
            else
            {
                others.Write(configuration.Slice(at, total));
            }

            at += total;
        }

        entry = new TimedTextSampleEntry
        {
            DisplayFlags = BinaryPrimitives.ReadUInt32BigEndian(configuration),
            HorizontalJustification = unchecked((sbyte)configuration[4]),
            VerticalJustification = unchecked((sbyte)configuration[5]),
            BackgroundRgba = BinaryPrimitives.ReadUInt32BigEndian(configuration[6..]),
            DefaultTextBox = box,
            DefaultStyle = style,
            Fonts = fonts,
            OtherBoxes = others.ToArray(),
        };
        return true;
    }

    /// <summary>Writes the entry's fields and child boxes, as <see cref="TryParse"/> reads them (without the 8-byte sample entry header).</summary>
    /// <returns>The bytes: the fixed fields, then the font table when there are fonts, then <see cref="OtherBoxes"/>.</returns>
    public byte[] ToConfiguration()
    {
        using MemoryStream m = new();
        Span<byte> head = stackalloc byte[FixedSize]; // FixedSize is a 30-byte constant
        BinaryPrimitives.WriteUInt32BigEndian(head, DisplayFlags);
        head[4] = unchecked((byte)HorizontalJustification);
        head[5] = unchecked((byte)VerticalJustification);
        BinaryPrimitives.WriteUInt32BigEndian(head[6..], BackgroundRgba);
        WriteI16(head, 10, DefaultTextBox.Top);
        WriteI16(head, 12, DefaultTextBox.Left);
        WriteI16(head, 14, DefaultTextBox.Bottom);
        WriteI16(head, 16, DefaultTextBox.Right);
        WriteStyle(head[18..], DefaultStyle);
        m.Write(head);
        if (Fonts.Count > 0)
        {
            WriteFonts(m, Fonts);
        }

        m.Write(OtherBoxes.Span);
        return m.ToArray();
    }

    internal static bool TryReadBox(ReadOnlySpan<byte> data, int at, out string type, out ReadOnlySpan<byte> payload, out int total)
    {
        type = string.Empty;
        payload = default;
        total = 0;
        if (data.Length - at < 8)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        if (size < 8 || size > data.Length - at)
        {
            return false;
        }

        type = Encoding.Latin1.GetString(data.Slice(at + 4, 4));
        payload = data.Slice(at + 8, (int)size - 8);
        total = (int)size;
        return true;
    }

    internal static short ReadI16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt16BigEndian(data[at..]);

    internal static void WriteI16(Span<byte> data, int at, int value) => BinaryPrimitives.WriteInt16BigEndian(data[at..], unchecked((short)value));

    internal static TextStyleRecord ReadStyle(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt16BigEndian(data),
        BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
        BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
        (TextFaceStyle)data[6],
        data[7],
        BinaryPrimitives.ReadUInt32BigEndian(data[8..]));

    internal static void WriteStyle(Span<byte> data, in TextStyleRecord s)
    {
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)s.StartChar);
        BinaryPrimitives.WriteUInt16BigEndian(data[2..], (ushort)s.EndChar);
        BinaryPrimitives.WriteUInt16BigEndian(data[4..], (ushort)s.FontId);
        data[6] = (byte)s.Face;
        data[7] = (byte)Math.Clamp(s.FontSize, 0, 255);
        BinaryPrimitives.WriteUInt32BigEndian(data[8..], s.ColorRgba);
    }

    private static bool TryReadFonts(ReadOnlySpan<byte> payload, List<TimedTextFont> fonts)
    {
        // 3GPP TS 26.245 §5.16: a 16-bit entry count, then per entry a 16-bit id, an 8-bit name length and the name in UTF-8 (or UTF-16 after a byte order mark).
        if (payload.Length < 2)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(payload);
        int at = 2;
        List<TimedTextFont> read = [];
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - at < 3)
            {
                return false;
            }

            int id = BinaryPrimitives.ReadUInt16BigEndian(payload[at..]);
            int len = payload[at + 2];
            at += 3;
            if (payload.Length - at < len)
            {
                return false;
            }

            read.Add(new TimedTextFont(id, TimedTextSample.DecodeText(payload.Slice(at, len), out _)));
            at += len;
        }

        fonts.AddRange(read);
        return true;
    }

    private static void WriteFonts(MemoryStream m, IReadOnlyList<TimedTextFont> fonts)
    {
        using MemoryStream body = new();
        Span<byte> two = stackalloc byte[2]; // fixed 2-byte field
        BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)Math.Min(fonts.Count, ushort.MaxValue));
        body.Write(two);
        for (int i = 0; i < Math.Min(fonts.Count, ushort.MaxValue); i++)
        {
            byte[] name = Encoding.UTF8.GetBytes(fonts[i].Name);
            int len = Math.Min(name.Length, 255);
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)fonts[i].FontId);
            body.Write(two);
            body.WriteByte((byte)len);
            body.Write(name, 0, len);
        }

        Span<byte> header = stackalloc byte[8]; // box header
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + body.Length));
        "ftab"u8.CopyTo(header[4..]);
        m.Write(header);
        m.Write(body.GetBuffer(), 0, (int)body.Length);
    }
}

/// <summary>
/// The sample entry of a QuickTime text track, <c>text</c> (Apple QuickTime File Format, "Text Media" → "Text Sample Description").
/// Colours are 16 bits per channel there; this type keeps them as stored.
/// </summary>
public sealed class QuickTimeTextSampleEntry
{
    /// <summary>The sample entry code.</summary>
    public const string Code = "text";

    // flags 4, justification 4, background 6, box 8, reserved 8, font number 2, face 2, reserved 1 + 2, foreground 6, then a Pascal string.
    private const int FixedSize = 4 + 4 + 6 + 8 + 8 + 2 + 2 + 1 + 2 + 6;

    /// <summary>Display flags: 0x0002 don't auto scale, 0x0008 use movie background, 0x0020 scroll in, 0x0040 scroll out, 0x0080 horizontal scroll, 0x0100 reverse scroll, 0x0200 continuous scroll, 0x1000 drop shadow, 0x2000 anti-alias, 0x4000 key text.</summary>
    public uint DisplayFlags { get; init; }

    /// <summary>Justification: 0 left, 1 centred, -1 right.</summary>
    public int Justification { get; init; }

    /// <summary>Background colour, 16 bits per channel: red, green, blue.</summary>
    public (ushort R, ushort G, ushort B) Background { get; init; }

    /// <summary>The default text box; all zero when none.</summary>
    public TextBoxRecord DefaultTextBox { get; init; }

    /// <summary>Font face bits: 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend.</summary>
    public int FontFace { get; init; }

    /// <summary>Foreground colour, 16 bits per channel.</summary>
    public (ushort R, ushort G, ushort B) Foreground { get; init; }

    /// <summary>The font name (a Pascal string), or empty.</summary>
    public string FontName { get; init; } = string.Empty;

    /// <summary>Reads the entry from the bytes after the 8-byte sample entry header.</summary>
    /// <param name="configuration">The entry's fields.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>False when the fixed fields do not fit. A missing or truncated font name is read as empty.</returns>
    public static bool TryParse(ReadOnlySpan<byte> configuration, out QuickTimeTextSampleEntry? entry)
    {
        entry = null;
        if (configuration.Length < FixedSize)
        {
            return false;
        }

        string name = string.Empty;
        if (configuration.Length > FixedSize)
        {
            int len = configuration[FixedSize];
            int available = Math.Min(len, configuration.Length - FixedSize - 1);
            name = Encoding.Latin1.GetString(configuration.Slice(FixedSize + 1, available));
        }

        entry = new QuickTimeTextSampleEntry
        {
            DisplayFlags = BinaryPrimitives.ReadUInt32BigEndian(configuration),
            Justification = BinaryPrimitives.ReadInt32BigEndian(configuration[4..]),
            Background = (BinaryPrimitives.ReadUInt16BigEndian(configuration[8..]), BinaryPrimitives.ReadUInt16BigEndian(configuration[10..]), BinaryPrimitives.ReadUInt16BigEndian(configuration[12..])),
            DefaultTextBox = new TextBoxRecord(TimedTextSampleEntry.ReadI16(configuration, 14), TimedTextSampleEntry.ReadI16(configuration, 16), TimedTextSampleEntry.ReadI16(configuration, 18), TimedTextSampleEntry.ReadI16(configuration, 20)),
            FontFace = BinaryPrimitives.ReadUInt16BigEndian(configuration[32..]),
            Foreground = (BinaryPrimitives.ReadUInt16BigEndian(configuration[37..]), BinaryPrimitives.ReadUInt16BigEndian(configuration[39..]), BinaryPrimitives.ReadUInt16BigEndian(configuration[41..])),
            FontName = name,
        };
        return true;
    }

    /// <summary>Writes the entry's fields (without the 8-byte sample entry header).</summary>
    /// <returns>The fixed fields and the font name as a Pascal string.</returns>
    public byte[] ToConfiguration()
    {
        byte[] name = Encoding.Latin1.GetBytes(FontName);
        int nameLength = Math.Min(name.Length, 255);
        byte[] r = new byte[FixedSize + 1 + nameLength];
        BinaryPrimitives.WriteUInt32BigEndian(r, DisplayFlags);
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(4), Justification);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), Background.R);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), Background.G);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(12), Background.B);
        TimedTextSampleEntry.WriteI16(r, 14, DefaultTextBox.Top);
        TimedTextSampleEntry.WriteI16(r, 16, DefaultTextBox.Left);
        TimedTextSampleEntry.WriteI16(r, 18, DefaultTextBox.Bottom);
        TimedTextSampleEntry.WriteI16(r, 20, DefaultTextBox.Right);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(32), (ushort)FontFace);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(37), Foreground.R);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(39), Foreground.G);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(41), Foreground.B);
        r[FixedSize] = (byte)nameLength;
        name.AsSpan(0, nameLength).CopyTo(r.AsSpan(FixedSize + 1));
        return r;
    }
}

/// <summary>
/// A box that follows the text of a timed text sample (3GPP TS 26.245 §5.17.1), kept as stored. Readers (<c>TryGet…</c>) and creators
/// (<c>Create…</c>) cover the boxes the specification defines; any other box is carried by type and payload.
/// </summary>
/// <param name="Type">The four-character box type: <c>styl</c>, <c>hlit</c>, <c>hclr</c>, <c>krok</c>, <c>dlay</c>, <c>href</c>, <c>tbox</c>, <c>blnk</c>, <c>twrp</c>, <c>disp</c>, or another.</param>
/// <param name="Payload">The box content after its 8-byte header.</param>
public sealed record TextModifierBox(string Type, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Reads a <c>styl</c> box (§5.17.1.1).</summary>
    /// <param name="styles">The style records in file order.</param>
    /// <returns>False when the box is not <c>styl</c> or its count does not fit its size.</returns>
    public bool TryGetStyles(out IReadOnlyList<TextStyleRecord> styles)
    {
        styles = [];
        ReadOnlySpan<byte> p = Payload.Span;
        if (Type != "styl" || p.Length < 2)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(p);
        if (p.Length < 2 + (long)count * TextStyleRecord.Size)
        {
            return false;
        }

        List<TextStyleRecord> list = new(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(TimedTextSampleEntry.ReadStyle(p[(2 + (i * TextStyleRecord.Size))..]));
        }

        styles = list;
        return true;
    }

    /// <summary>Reads an <c>hlit</c> box (§5.17.1.2): the highlighted character range.</summary>
    /// <param name="startChar">First highlighted character.</param>
    /// <param name="endChar">One past the last.</param>
    /// <returns>False when the box is not <c>hlit</c> or is too short.</returns>
    public bool TryGetHighlight(out int startChar, out int endChar) => TryGetRange("hlit", out startChar, out endChar);

    /// <summary>Reads a <c>blnk</c> box (§5.17.1.7): the blinking character range.</summary>
    /// <param name="startChar">First blinking character.</param>
    /// <param name="endChar">One past the last.</param>
    /// <returns>False when the box is not <c>blnk</c> or is too short.</returns>
    public bool TryGetBlink(out int startChar, out int endChar) => TryGetRange("blnk", out startChar, out endChar);

    /// <summary>Reads an <c>hclr</c> box (§5.17.1.2): the highlight colour.</summary>
    /// <param name="colorRgba">The colour as 0xRRGGBBAA.</param>
    /// <returns>False when the box is not <c>hclr</c> or is too short.</returns>
    public bool TryGetHighlightColor(out uint colorRgba)
    {
        colorRgba = 0;
        if (Type != "hclr" || Payload.Length < 4)
        {
            return false;
        }

        colorRgba = BinaryPrimitives.ReadUInt32BigEndian(Payload.Span);
        return true;
    }

    /// <summary>Reads a <c>krok</c> box (§5.17.1.3): dynamic highlighting for karaoke and closed captions.</summary>
    /// <param name="startTime">Offset of the first step's start from the sample start, in track ticks.</param>
    /// <param name="steps">The steps in order.</param>
    /// <returns>False when the box is not <c>krok</c> or its count does not fit its size.</returns>
    public bool TryGetKaraoke(out uint startTime, out IReadOnlyList<KaraokeStep> steps)
    {
        startTime = 0;
        steps = [];
        ReadOnlySpan<byte> p = Payload.Span;
        if (Type != "krok" || p.Length < 6)
        {
            return false;
        }

        startTime = BinaryPrimitives.ReadUInt32BigEndian(p);
        int count = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        if (p.Length < 6 + (long)count * 8)
        {
            return false;
        }

        List<KaraokeStep> list = new(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> r = p[(6 + (i * 8))..];
            list.Add(new KaraokeStep(BinaryPrimitives.ReadUInt32BigEndian(r), BinaryPrimitives.ReadUInt16BigEndian(r[4..]), BinaryPrimitives.ReadUInt16BigEndian(r[6..])));
        }

        steps = list;
        return true;
    }

    /// <summary>Reads a <c>dlay</c> box (§5.17.1.4): the scroll delay.</summary>
    /// <param name="delay">The delay in track ticks.</param>
    /// <returns>False when the box is not <c>dlay</c> or is too short.</returns>
    public bool TryGetScrollDelay(out uint delay)
    {
        delay = 0;
        if (Type != "dlay" || Payload.Length < 4)
        {
            return false;
        }

        delay = BinaryPrimitives.ReadUInt32BigEndian(Payload.Span);
        return true;
    }

    /// <summary>Reads an <c>href</c> box (§5.17.1.5).</summary>
    /// <param name="link">The link.</param>
    /// <returns>False when the box is not <c>href</c> or its strings do not fit.</returns>
    public bool TryGetLink(out TimedTextLink link)
    {
        link = default;
        ReadOnlySpan<byte> p = Payload.Span;
        if (Type != "href" || p.Length < 5)
        {
            return false;
        }

        int urlLength = p[4];
        if (p.Length < 5 + urlLength + 1)
        {
            return false;
        }

        int altLength = p[5 + urlLength];
        if (p.Length < 6 + urlLength + altLength)
        {
            return false;
        }

        link = new TimedTextLink(
            BinaryPrimitives.ReadUInt16BigEndian(p),
            BinaryPrimitives.ReadUInt16BigEndian(p[2..]),
            Encoding.UTF8.GetString(p.Slice(5, urlLength)),
            Encoding.UTF8.GetString(p.Slice(6 + urlLength, altLength)));
        return true;
    }

    /// <summary>Reads a <c>tbox</c> box (§5.17.1.6): the text box that overrides the entry's.</summary>
    /// <param name="box">The box.</param>
    /// <returns>False when the box is not <c>tbox</c> or is too short.</returns>
    public bool TryGetTextBox(out TextBoxRecord box)
    {
        box = default;
        ReadOnlySpan<byte> p = Payload.Span;
        if (Type != "tbox" || p.Length < 8)
        {
            return false;
        }

        box = new TextBoxRecord(TimedTextSampleEntry.ReadI16(p, 0), TimedTextSampleEntry.ReadI16(p, 2), TimedTextSampleEntry.ReadI16(p, 4), TimedTextSampleEntry.ReadI16(p, 6));
        return true;
    }

    /// <summary>Reads a <c>twrp</c> box (§5.17.1.8): 0 no wrap, 1 automatic wrap.</summary>
    /// <param name="wrapFlag">The flag.</param>
    /// <returns>False when the box is not <c>twrp</c> or is empty.</returns>
    public bool TryGetWrap(out byte wrapFlag)
    {
        wrapFlag = 0;
        if (Type != "twrp" || Payload.Length < 1)
        {
            return false;
        }

        wrapFlag = Payload.Span[0];
        return true;
    }

    /// <summary>Reads a <c>disp</c> box (§5.17.1.9): the stereo disparity shift in 1/16 pixel.</summary>
    /// <param name="shift">The shift.</param>
    /// <returns>False when the box is not <c>disp</c> or is too short.</returns>
    public bool TryGetDisparity(out short shift)
    {
        shift = 0;
        if (Type != "disp" || Payload.Length < 2)
        {
            return false;
        }

        shift = BinaryPrimitives.ReadInt16BigEndian(Payload.Span);
        return true;
    }

    /// <summary>Builds a <c>styl</c> box.</summary>
    /// <param name="styles">The style records, ordered by start offset and not overlapping.</param>
    /// <returns>The box. At most 65,535 records are written.</returns>
    public static TextModifierBox CreateStyles(IReadOnlyList<TextStyleRecord> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        int count = Math.Min(styles.Count, ushort.MaxValue);
        byte[] b = new byte[2 + (count * TextStyleRecord.Size)];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)count);
        for (int i = 0; i < count; i++)
        {
            TimedTextSampleEntry.WriteStyle(b.AsSpan(2 + (i * TextStyleRecord.Size)), styles[i]);
        }

        return new TextModifierBox("styl", b);
    }

    /// <summary>Builds an <c>hlit</c> box.</summary>
    /// <param name="startChar">First highlighted character.</param>
    /// <param name="endChar">One past the last.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateHighlight(int startChar, int endChar) => CreateRange("hlit", startChar, endChar);

    /// <summary>Builds a <c>blnk</c> box.</summary>
    /// <param name="startChar">First blinking character.</param>
    /// <param name="endChar">One past the last.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateBlink(int startChar, int endChar) => CreateRange("blnk", startChar, endChar);

    /// <summary>Builds an <c>hclr</c> box.</summary>
    /// <param name="colorRgba">The colour as 0xRRGGBBAA.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateHighlightColor(uint colorRgba)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, colorRgba);
        return new TextModifierBox("hclr", b);
    }

    /// <summary>Builds a <c>krok</c> box.</summary>
    /// <param name="startTime">Offset of the first step's start from the sample start, in track ticks.</param>
    /// <param name="steps">The steps; at most 65,535 are written.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateKaraoke(uint startTime, IReadOnlyList<KaraokeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        int count = Math.Min(steps.Count, ushort.MaxValue);
        byte[] b = new byte[6 + (count * 8)];
        BinaryPrimitives.WriteUInt32BigEndian(b, startTime);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)count);
        for (int i = 0; i < count; i++)
        {
            Span<byte> r = b.AsSpan(6 + (i * 8));
            BinaryPrimitives.WriteUInt32BigEndian(r, steps[i].EndTime);
            BinaryPrimitives.WriteUInt16BigEndian(r[4..], (ushort)steps[i].StartChar);
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], (ushort)steps[i].EndChar);
        }

        return new TextModifierBox("krok", b);
    }

    /// <summary>Builds a <c>tbox</c> box.</summary>
    /// <param name="box">The text box.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateTextBox(TextBoxRecord box)
    {
        byte[] b = new byte[8];
        TimedTextSampleEntry.WriteI16(b, 0, box.Top);
        TimedTextSampleEntry.WriteI16(b, 2, box.Left);
        TimedTextSampleEntry.WriteI16(b, 4, box.Bottom);
        TimedTextSampleEntry.WriteI16(b, 6, box.Right);
        return new TextModifierBox("tbox", b);
    }

    /// <summary>Builds a <c>twrp</c> box.</summary>
    /// <param name="wrapFlag">0 no wrap, 1 automatic wrap.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateWrap(byte wrapFlag) => new("twrp", new byte[] { wrapFlag });

    /// <summary>Builds an <c>href</c> box.</summary>
    /// <param name="link">The link; the URL and the alternative text are cut to 255 UTF-8 bytes each.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateLink(TimedTextLink link)
    {
        byte[] url = Encoding.UTF8.GetBytes(link.Url);
        byte[] alt = Encoding.UTF8.GetBytes(link.Alt);
        int u = Math.Min(url.Length, 255), a = Math.Min(alt.Length, 255);
        byte[] b = new byte[6 + u + a];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)link.StartChar);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)link.EndChar);
        b[4] = (byte)u;
        url.AsSpan(0, u).CopyTo(b.AsSpan(5));
        b[5 + u] = (byte)a;
        alt.AsSpan(0, a).CopyTo(b.AsSpan(6 + u));
        return new TextModifierBox("href", b);
    }

    /// <summary>Builds a <c>dlay</c> box.</summary>
    /// <param name="delay">The scroll delay in track ticks.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateScrollDelay(uint delay)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, delay);
        return new TextModifierBox("dlay", b);
    }

    /// <summary>Builds a <c>disp</c> box.</summary>
    /// <param name="shift">The disparity shift in 1/16 pixel.</param>
    /// <returns>The box.</returns>
    public static TextModifierBox CreateDisparity(short shift)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, shift);
        return new TextModifierBox("disp", b);
    }

    private bool TryGetRange(string type, out int start, out int end)
    {
        start = end = 0;
        if (Type != type || Payload.Length < 4)
        {
            return false;
        }

        start = BinaryPrimitives.ReadUInt16BigEndian(Payload.Span);
        end = BinaryPrimitives.ReadUInt16BigEndian(Payload.Span[2..]);
        return true;
    }

    private static TextModifierBox CreateRange(string type, int start, int end)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)start);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)end);
        return new TextModifierBox(type, b);
    }
}

/// <summary>
/// One sample of a timed text track: a string of text and the modifier boxes after it (3GPP TS 26.245 §5.17). The QuickTime <c>text</c>
/// sample has the same shape: a 16-bit length, the text, then atoms.
/// </summary>
/// <remarks>
/// The sample size is the whole sample: whatever follows the text is read as boxes (§5.17: "to the end of the sample"). A box with a
/// size that does not fit ends the list; the rest is kept in <see cref="Trailing"/>, so writing gives back the bytes read. A sample of
/// two zero bytes, or of no bytes, is empty text and means "nothing is shown" (a gap).
/// </remarks>
public sealed class TimedTextSample
{
    /// <summary>Creates a sample.</summary>
    /// <param name="text">The text.</param>
    /// <param name="modifiers">Boxes after the text; null for none.</param>
    /// <param name="utf16">True to store the text as UTF-16 with a byte order mark (§5.1).</param>
    public TimedTextSample(string text, IReadOnlyList<TextModifierBox>? modifiers = null, bool utf16 = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Modifiers = modifiers ?? [];
        IsUtf16 = utf16;
    }

    /// <summary>The text, decoded from UTF-8, or from UTF-16 when it began with a byte order mark. Malformed sequences become U+FFFD.</summary>
    public string Text { get; }

    /// <summary>True when the text was stored as UTF-16 (with a byte order mark), or is to be written that way.</summary>
    public bool IsUtf16 { get; }

    /// <summary>The boxes after the text, in file order.</summary>
    public IReadOnlyList<TextModifierBox> Modifiers { get; }

    /// <summary>Bytes after the last well-formed box (a box that did not fit), kept as stored.</summary>
    public ReadOnlyMemory<byte> Trailing { get; init; }

    /// <summary>True when the sample shows nothing: no text and no boxes.</summary>
    public bool IsEmpty => Text.Length == 0 && Modifiers.Count == 0 && Trailing.IsEmpty;

    /// <summary>The first modifier box of <paramref name="type"/>, or null.</summary>
    /// <param name="type">A four-character box type.</param>
    public TextModifierBox? Find(string type)
    {
        foreach (TextModifierBox m in Modifiers)
        {
            if (m.Type == type)
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a character offset used by the boxes into an index into <see cref="Text"/>. Offsets count characters: code points for UTF-8
    /// text and 16-bit units for UTF-16 text; the specification leaves surrogate pairs open and this follows the two encodings'
    /// natural units. Offsets past the end clamp to the text length.
    /// </summary>
    /// <param name="charOffset">An offset from a style, highlight or link record.</param>
    /// <returns>The UTF-16 index in <see cref="Text"/>.</returns>
    public int ToTextIndex(int charOffset)
    {
        if (charOffset <= 0)
        {
            return 0;
        }

        if (IsUtf16)
        {
            return Math.Min(charOffset, Text.Length);
        }

        int index = 0;
        for (int n = 0; n < charOffset && index < Text.Length; n++)
        {
            index += char.IsHighSurrogate(Text[index]) && index + 1 < Text.Length && char.IsLowSurrogate(Text[index + 1]) ? 2 : 1;
        }

        return index;
    }

    /// <summary>The inverse of <see cref="ToTextIndex"/>: the character offset of an index into <see cref="Text"/>.</summary>
    /// <param name="textIndex">A UTF-16 index, clamped to the text.</param>
    /// <returns>The offset to store in a record.</returns>
    public int ToCharOffset(int textIndex)
    {
        textIndex = Math.Clamp(textIndex, 0, Text.Length);
        if (IsUtf16)
        {
            return textIndex;
        }

        int chars = 0;
        for (int i = 0; i < textIndex; i++)
        {
            if (!(char.IsLowSurrogate(Text[i]) && i > 0 && char.IsHighSurrogate(Text[i - 1])))
            {
                chars++;
            }
        }

        return chars;
    }

    /// <summary>Reads a sample.</summary>
    /// <param name="sample">The sample's bytes.</param>
    /// <param name="result">The sample.</param>
    /// <returns>False when the text length is larger than the sample (a truncated sample). An empty input is an empty sample.</returns>
    public static bool TryParse(ReadOnlySpan<byte> sample, out TimedTextSample? result)
    {
        result = null;
        if (sample.IsEmpty)
        {
            result = new TimedTextSample(string.Empty);
            return true;
        }

        if (sample.Length < 2)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(sample);
        if (length > sample.Length - 2)
        {
            return false;
        }

        string text = DecodeText(sample.Slice(2, length), out bool utf16);
        List<TextModifierBox> boxes = [];
        int at = 2 + length;
        ReadOnlyMemory<byte> trailing = default;
        while (at < sample.Length)
        {
            if (!TimedTextSampleEntry.TryReadBox(sample, at, out string type, out ReadOnlySpan<byte> payload, out int total))
            {
                trailing = sample[at..].ToArray();
                break;
            }

            boxes.Add(new TextModifierBox(type, payload.ToArray()));
            at += total;
        }

        result = new TimedTextSample(text, boxes, utf16) { Trailing = trailing };
        return true;
    }

    /// <summary>Writes the sample: the length, the text, the boxes, then <see cref="Trailing"/>.</summary>
    /// <returns>The bytes. Text longer than 65,535 bytes is cut at a character boundary, because the length field is 16 bits.</returns>
    public byte[] ToArray()
    {
        byte[] text = EncodeText(Text, IsUtf16);
        long size = 2 + text.Length + Trailing.Length;
        foreach (TextModifierBox m in Modifiers)
        {
            size += 8 + m.Payload.Length;
        }

        byte[] r = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)text.Length);
        text.CopyTo(r.AsSpan(2));
        int at = 2 + text.Length;
        foreach (TextModifierBox m in Modifiers)
        {
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(at), (uint)(8 + m.Payload.Length));
            Encoding.Latin1.GetBytes(m.Type, r.AsSpan(at + 4, 4));
            m.Payload.Span.CopyTo(r.AsSpan(at + 8));
            at += 8 + m.Payload.Length;
        }

        Trailing.Span.CopyTo(r.AsSpan(at));
        return r;
    }

    /// <summary>
    /// Decodes a text string as 3GPP TS 26.245 §5.1 defines it: UTF-8, or UTF-16 when it starts with a byte order mark. A byte-reversed
    /// mark is also accepted, since files in the wild use it although terminals need not.
    /// </summary>
    internal static string DecodeText(ReadOnlySpan<byte> bytes, out bool utf16)
    {
        utf16 = false;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            utf16 = true;
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            utf16 = true;
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] EncodeText(string text, bool utf16)
    {
        byte[] bytes;
        if (utf16)
        {
            byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
            bytes = new byte[2 + body.Length];
            bytes[0] = 0xFE;
            bytes[1] = 0xFF;
            body.CopyTo(bytes, 2);
            if (bytes.Length > ushort.MaxValue)
            {
                // An even count, so a 16-bit unit is not split; a split surrogate pair is the caller's text limit.
                Array.Resize(ref bytes, ushort.MaxValue - 1);
            }

            return bytes;
        }

        bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > ushort.MaxValue)
        {
            int cut = ushort.MaxValue;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            {
                cut--;
            }

            Array.Resize(ref bytes, cut);
        }

        return bytes;
    }
}

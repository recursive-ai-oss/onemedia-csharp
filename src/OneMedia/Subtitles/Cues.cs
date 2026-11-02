using System.Collections;

namespace RecursiveAI.OneMedia.Captions;

// The cue model: one shape for subtitles and captions whatever carried them (a sidecar file, a container text track, CEA-608/708).
// Everything here is immutable once built: records with init-only members and value-equal lists, so "the same cues" is == on the model.

/// <summary>
/// A read-only list with value equality: two lists are equal when they hold equal items in the same order. The cue model uses it so
/// that comparing two documents, cues or spans compares their contents.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class SubtitleList<T> : IReadOnlyList<T>, IEquatable<SubtitleList<T>>
{
    private readonly T[] _items;

    /// <summary>Copies <paramref name="items"/> into a new list.</summary>
    /// <param name="items">The items, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    public SubtitleList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = new List<T>(items).ToArray();
    }

    internal SubtitleList(T[] owned, bool _) => _items = owned;

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public T this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Items equal in the same order.</summary>
    public bool Equals(SubtitleList<T>? other)
    {
        if (other is null || other._items.Length != _items.Length)
        {
            return false;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _items.Length; i++)
        {
            if (!comparer.Equals(_items[i], other._items[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SubtitleList<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode h = default;
        h.Add(_items.Length);
        foreach (T item in _items)
        {
            h.Add(item);
        }

        return h.ToHashCode();
    }
}

/// <summary>Builders for <see cref="SubtitleList{T}"/>.</summary>
public static class SubtitleList
{
    /// <summary>The shared empty list of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    public static SubtitleList<T> Empty<T>() => EmptyHolder<T>.Value;

    /// <summary>A list of the given items.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items, in order; copied.</param>
    public static SubtitleList<T> Of<T>(params ReadOnlySpan<T> items) => items.IsEmpty ? Empty<T>() : new SubtitleList<T>(items.ToArray(), true);

    /// <summary>A list from a builder list, taking its items (empty lists share the empty instance).</summary>
    internal static SubtitleList<T> From<T>(List<T> list) => list.Count == 0 ? Empty<T>() : new SubtitleList<T>(list.ToArray(), true);

    private static class EmptyHolder<T>
    {
        internal static readonly SubtitleList<T> Value = new([], true);
    }
}

/// <summary>An RGBA colour, 8 bits per channel. <see cref="A"/> is opacity: 255 is opaque, 0 is transparent.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Opacity.</param>
public readonly record struct SubtitleColor(byte R, byte G, byte B, byte A = 255);

/// <summary>Horizontal text alignment. <see cref="Start"/>/<see cref="End"/> follow the writing direction; the others are absolute.</summary>
public enum SubtitleAlign
{
    /// <summary>Not stated; the target's default applies.</summary>
    Unspecified = 0,

    /// <summary>Start of the line in the writing direction.</summary>
    Start,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right.</summary>
    Right,

    /// <summary>End of the line in the writing direction.</summary>
    End,
}

/// <summary>Where the text block sits vertically within its area (top, middle or bottom anchor).</summary>
public enum SubtitleVerticalAlign
{
    /// <summary>Not stated; the target's default (bottom) applies.</summary>
    Unspecified = 0,

    /// <summary>Anchored at the top.</summary>
    Top,

    /// <summary>Anchored in the middle.</summary>
    Middle,

    /// <summary>Anchored at the bottom.</summary>
    Bottom,
}

/// <summary>Direction lines are written and stacked.</summary>
public enum SubtitleWriting
{
    /// <summary>Horizontal lines, stacked top to bottom (the default).</summary>
    Horizontal = 0,

    /// <summary>Vertical lines, stacked right to left.</summary>
    VerticalRightToLeft,

    /// <summary>Vertical lines, stacked left to right.</summary>
    VerticalLeftToRight,
}

/// <summary>The unit of a <see cref="CueLine"/>.</summary>
public enum CueLineUnit
{
    /// <summary>Line numbers; negative values count from the bottom (WebVTT).</summary>
    Lines = 0,

    /// <summary>Percent of the area's height from the top.</summary>
    Percent,

    /// <summary>A row of a character grid such as CEA-608's 15 rows (1 is the top row).</summary>
    Row,
}

/// <summary>A vertical position: a value, its unit, and whether it snaps to whole lines.</summary>
/// <param name="Value">The position in <paramref name="Unit"/>.</param>
/// <param name="Unit">What <paramref name="Value"/> counts.</param>
/// <param name="Snap">True to snap to lines (WebVTT's <c>snap-to-lines</c>).</param>
public readonly record struct CueLine(double Value, CueLineUnit Unit = CueLineUnit.Lines, bool Snap = true);

/// <summary>How a caption is revealed. Only embedded captions (CEA-608/708) use anything but <see cref="None"/>.</summary>
public enum CaptionMode
{
    /// <summary>An ordinary timed cue.</summary>
    None = 0,

    /// <summary>The whole caption appears at once.</summary>
    PopOn,

    /// <summary>Text scrolls up as new rows arrive.</summary>
    RollUp,

    /// <summary>Text is painted directly on screen as it is received.</summary>
    PaintOn,
}

/// <summary>The unit of a <see cref="SubtitleLength"/>.</summary>
public enum SubtitleLengthUnit
{
    /// <summary>Pixels of the document's reference size (ASS font sizes).</summary>
    Pixel = 0,

    /// <summary>Typographic points.</summary>
    Point,

    /// <summary>Percent of the default size.</summary>
    Percent,

    /// <summary>Multiples of the parent font size.</summary>
    Em,
}

/// <summary>A size such as a font size.</summary>
/// <param name="Value">The magnitude.</param>
/// <param name="Unit">What it counts.</param>
public readonly record struct SubtitleLength(double Value, SubtitleLengthUnit Unit = SubtitleLengthUnit.Pixel);

/// <summary>
/// A piece of format-specific information the model has no member for, kept so that writing the same format again loses nothing.
/// Other formats ignore extras that are not theirs (and say so in the conversion report).
/// </summary>
/// <param name="Format">The format name that owns it: <c>srt</c>, <c>webvtt</c>, <c>ass</c>, <c>ttml</c>, <c>sbv</c>, <c>lrc</c>.</param>
/// <param name="Key">A format-defined key, for example <c>Layer</c>, <c>note</c> or <c>override</c>.</param>
/// <param name="Value">The value, as the format writes it.</param>
public sealed record SubtitleExtra(string Format, string Key, string Value);

/// <summary>Character styling, used for a span of text and for a named style. Null members are unstated and inherit.</summary>
public sealed record TextStyle
{
    /// <summary>Bold.</summary>
    public bool? Bold { get; init; }

    /// <summary>Italic.</summary>
    public bool? Italic { get; init; }

    /// <summary>Underlined.</summary>
    public bool? Underline { get; init; }

    /// <summary>Struck through.</summary>
    public bool? Strike { get; init; }

    /// <summary>Text colour.</summary>
    public SubtitleColor? Color { get; init; }

    /// <summary>Background colour behind the text.</summary>
    public SubtitleColor? Background { get; init; }

    /// <summary>Font family name.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font size.</summary>
    public SubtitleLength? FontSize { get; init; }

    /// <summary>Space-separated class names (WebVTT <c>&lt;c.name&gt;</c>), for styling by stylesheet.</summary>
    public string? Classes { get; init; }

    /// <summary>True when no member is set.</summary>
    public bool IsEmpty => Bold is null && Italic is null && Underline is null && Strike is null && Color is null
        && Background is null && FontFamily is null && FontSize is null && Classes is null;
}

/// <summary>
/// A styled stretch of a cue's <see cref="SubtitleCue.Text"/>, by UTF-16 offset. Spans may nest and overlap; a span of length zero
/// marks a point (a karaoke tick, or a format-specific marker in <see cref="Raw"/>).
/// </summary>
/// <param name="Start">Offset of the first character in <see cref="SubtitleCue.Text"/>.</param>
/// <param name="Length">Number of UTF-16 code units; zero for a point marker.</param>
public sealed record SubtitleSpan(int Start, int Length)
{
    /// <summary>Character styling for the stretch.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Who speaks this stretch, when it differs from the cue's <see cref="SubtitleCue.Speaker"/> (WebVTT <c>&lt;v&gt;</c>).</summary>
    public string? Voice { get; init; }

    /// <summary>BCP 47 language of the stretch (WebVTT <c>&lt;lang&gt;</c>, TTML <c>xml:lang</c>).</summary>
    public string? Language { get; init; }

    /// <summary>Ruby annotation shown over the stretch (the stretch is the base text).</summary>
    public string? Ruby { get; init; }

    /// <summary>When the stretch is highlighted (karaoke) or spoken (a recognised word): the instant it starts, on the media timeline.</summary>
    public MediaTime? WordStart { get; init; }

    /// <summary>When the stretch stops being highlighted or spoken; null when unknown.</summary>
    public MediaTime? WordEnd { get; init; }

    /// <summary>A format-specific marker or setting that applies at this point or stretch, kept for same-format writing.</summary>
    public SubtitleExtra? Raw { get; init; }

    /// <summary>The offset one past the last character.</summary>
    public int End => Start + Length;
}

/// <summary>Where and how a cue is placed. Null members are unstated; percentages are of the area (region or video frame).</summary>
public sealed record CuePlacement
{
    /// <summary>The <see cref="SubtitleRegion.Id"/> the cue is shown in.</summary>
    public string? Region { get; init; }

    /// <summary>Horizontal text alignment.</summary>
    public SubtitleAlign Align { get; init; }

    /// <summary>Vertical anchor of the text block.</summary>
    public SubtitleVerticalAlign VerticalAlign { get; init; }

    /// <summary>Vertical position.</summary>
    public CueLine? Line { get; init; }

    /// <summary>Horizontal position of the block, percent from the left.</summary>
    public double? Position { get; init; }

    /// <summary>Which point of the block <see cref="Position"/> names: left, centre or right (unspecified: automatic).</summary>
    public SubtitleAlign PositionAnchor { get; init; }

    /// <summary>Width of the block, percent.</summary>
    public double? Size { get; init; }

    /// <summary>Writing direction.</summary>
    public SubtitleWriting Writing { get; init; }

    /// <summary>Row of a character grid (CEA-608: 1 to 15); null when the cue is not grid-placed.</summary>
    public int? Row { get; init; }

    /// <summary>Column of a character grid (CEA-608: 0 to 31) where the text starts; null when not grid-placed.</summary>
    public int? Column { get; init; }
}

/// <summary>A named box on screen cues can be placed in (WebVTT <c>REGION</c>, TTML <c>region</c>).</summary>
/// <param name="Id">The identifier cues refer to.</param>
public sealed record SubtitleRegion(string Id)
{
    /// <summary>Left edge, percent of the frame.</summary>
    public double? X { get; init; }

    /// <summary>Top edge, percent of the frame.</summary>
    public double? Y { get; init; }

    /// <summary>Width, percent of the frame.</summary>
    public double? Width { get; init; }

    /// <summary>Height, percent of the frame.</summary>
    public double? Height { get; init; }

    /// <summary>Number of text lines the region shows (WebVTT <c>lines</c>).</summary>
    public int? Lines { get; init; }

    /// <summary>Vertical anchor of text inside the region.</summary>
    public SubtitleVerticalAlign VerticalAlign { get; init; }

    /// <summary>True when new lines scroll the old ones up (WebVTT <c>scroll:up</c>).</summary>
    public bool ScrollUp { get; init; }

    /// <summary>Format-specific settings kept for same-format writing.</summary>
    public SubtitleList<SubtitleExtra> Extras { get; init; } = SubtitleList.Empty<SubtitleExtra>();
}

/// <summary>A named style declared in the document header (ASS <c>Style:</c>, TTML <c>style</c>).</summary>
/// <param name="Name">The name cues refer to in <see cref="SubtitleCue.StyleName"/>.</param>
public sealed record SubtitleStyle(string Name)
{
    /// <summary>Character styling.</summary>
    public TextStyle Text { get; init; } = new();

    /// <summary>Default placement for cues using the style.</summary>
    public CuePlacement? Placement { get; init; }

    /// <summary>Format-specific fields kept for same-format writing.</summary>
    public SubtitleList<SubtitleExtra> Extras { get; init; } = SubtitleList.Empty<SubtitleExtra>();
}

/// <summary>
/// A decoded bitmap shown with a cue (PGS, VobSub and DVB subtitles). Pixels are 8-bit R, G, B, A in that byte order, rows top to
/// bottom, <see cref="Stride"/> bytes apart. Equality compares the pixel bytes.
/// </summary>
/// <param name="X">Left edge, in pixels of the display size.</param>
/// <param name="Y">Top edge, in pixels of the display size.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes from the start of one row to the next; at least <c>Width * 4</c>.</param>
/// <param name="Pixels">The pixel bytes, at least <c>Stride * Height</c> long.</param>
public sealed record SubtitleImage(int X, int Y, int Width, int Height, int Stride, byte[] Pixels)
{
    /// <summary>Width of the video or display frame that <see cref="X"/> and <see cref="Y"/> refer to; 0 when unknown.</summary>
    public int DisplayWidth { get; init; }

    /// <summary>Height of the video or display frame that <see cref="X"/> and <see cref="Y"/> refer to; 0 when unknown.</summary>
    public int DisplayHeight { get; init; }

    /// <summary>True when colour channels are already multiplied by alpha; false (the default) means straight alpha.</summary>
    public bool IsPremultiplied { get; init; }

    /// <summary>Format-specific information such as the object id or palette id the bitmap came from.</summary>
    public SubtitleList<SubtitleExtra> Extras { get; init; } = SubtitleList.Empty<SubtitleExtra>();

    /// <inheritdoc />
    public bool Equals(SubtitleImage? other) =>
        other is not null && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height && Stride == other.Stride
        && DisplayWidth == other.DisplayWidth && DisplayHeight == other.DisplayHeight && IsPremultiplied == other.IsPremultiplied
        && Pixels.AsSpan().SequenceEqual(other.Pixels) && Extras.Equals(other.Extras);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height, Stride, Pixels.Length, IsPremultiplied);
}

/// <summary>One subtitle or caption: a stretch of time and the text shown during it.</summary>
public sealed record SubtitleCue
{
    /// <summary>An identifier (WebVTT cue id, TTML <c>xml:id</c>), or null.</summary>
    public string? Id { get; init; }

    /// <summary>When the cue appears. Exact; sidecar formats store it in milliseconds or centiseconds.</summary>
    public MediaTime Start { get; init; }

    /// <summary>When the cue disappears. Not before <see cref="Start"/> in well-formed documents (readers report a defect otherwise).</summary>
    public MediaTime End { get; init; }

    /// <summary>The plain text. Lines are separated by <c>\n</c>; no markup.</summary>
    public string Text { get; init; } = "";

    /// <summary>Styled stretches of <see cref="Text"/>.</summary>
    public SubtitleList<SubtitleSpan> Spans { get; init; } = SubtitleList.Empty<SubtitleSpan>();

    /// <summary>The speaker of the whole cue (WebVTT <c>&lt;v&gt;</c> around everything, ASS <c>Name</c>, TTML <c>ttm:agent</c>).</summary>
    public string? Speaker { get; init; }

    /// <summary>The <see cref="SubtitleStyle.Name"/> the cue uses, or null for the default style.</summary>
    public string? StyleName { get; init; }

    /// <summary>Where the cue is shown; null for the default place.</summary>
    public CuePlacement? Placement { get; init; }

    /// <summary>How an embedded caption is revealed; <see cref="CaptionMode.None"/> for ordinary cues.</summary>
    public CaptionMode Mode { get; init; }

    /// <summary>BCP 47 language of the cue when it differs from the document's.</summary>
    public string? Language { get; init; }

    /// <summary>Format-specific information kept for same-format writing.</summary>
    public SubtitleList<SubtitleExtra> Extras { get; init; } = SubtitleList.Empty<SubtitleExtra>();

    /// <summary>Bitmaps shown by the cue (several for a PGS composition). Empty for text cues; a bitmap cue may have no <see cref="Text"/>.</summary>
    public SubtitleList<SubtitleImage> Images { get; init; } = SubtitleList.Empty<SubtitleImage>();

    /// <summary>End minus start, in this cue's start timescale (rounded toward zero when the timescales differ).</summary>
    public MediaTime Duration => End.Subtract(Start, MediaRounding.TowardZero);

    /// <summary>The text of <paramref name="span"/>, clamped to <see cref="Text"/>.</summary>
    /// <param name="span">A span of this cue.</param>
    public string TextOf(SubtitleSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        int start = Math.Clamp(span.Start, 0, Text.Length);
        int end = Math.Clamp(span.End, start, Text.Length);
        return Text[start..end];
    }
}

/// <summary>
/// A whole subtitle file or track: its cues in file order, plus the header information the cues refer to.
/// </summary>
public sealed record SubtitleDocument
{
    /// <summary>The name of the format it was read from (<c>srt</c>, <c>webvtt</c>, <c>ass</c>, <c>ttml</c>, <c>sbv</c>, <c>lrc</c>, …), or null when built in memory.</summary>
    public string? Format { get; init; }

    /// <summary>BCP 47 language of the document.</summary>
    public string? Language { get; init; }

    /// <summary>A title (ASS <c>Title</c>, LRC <c>[ti:]</c>).</summary>
    public string? Title { get; init; }

    /// <summary>Named styles.</summary>
    public SubtitleList<SubtitleStyle> Styles { get; init; } = SubtitleList.Empty<SubtitleStyle>();

    /// <summary>Named regions.</summary>
    public SubtitleList<SubtitleRegion> Regions { get; init; } = SubtitleList.Empty<SubtitleRegion>();

    /// <summary>The cues, in file order (not necessarily sorted by time).</summary>
    public SubtitleList<SubtitleCue> Cues { get; init; } = SubtitleList.Empty<SubtitleCue>();

    /// <summary>Header-level format-specific information (stylesheet blocks, script info, metadata lines) kept for same-format writing.</summary>
    public SubtitleList<SubtitleExtra> Extras { get; init; } = SubtitleList.Empty<SubtitleExtra>();

    /// <summary>Width of the frame that pixel sizes and absolute positions refer to; 0 when unknown.</summary>
    public int ReferenceWidth { get; init; }

    /// <summary>Height of the frame that pixel sizes and absolute positions refer to; 0 when unknown.</summary>
    public int ReferenceHeight { get; init; }

    /// <summary>Name of the text encoding the file used (<c>utf-8</c>, <c>utf-16</c>, <c>windows-1252</c>…); writers reuse it. Default <c>utf-8</c>.</summary>
    public string TextEncoding { get; init; } = "utf-8";

    /// <summary>True when the file began with a byte order mark; writers reuse it. Default false.</summary>
    public bool ByteOrderMark { get; init; }

    /// <summary>The line ending the file used; writers reuse it. Default <c>"\n"</c>.</summary>
    public string NewLine { get; init; } = "\n";
}

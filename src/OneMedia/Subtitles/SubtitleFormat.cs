using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>What a subtitle format can carry; also names the thing a conversion loss is about.</summary>
[Flags]
public enum SubtitleFeatures : long
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Bold text.</summary>
    Bold = 1 << 0,

    /// <summary>Italic text.</summary>
    Italic = 1 << 1,

    /// <summary>Underlined text.</summary>
    Underline = 1 << 2,

    /// <summary>Struck-through text.</summary>
    Strike = 1 << 3,

    /// <summary>Text colour.</summary>
    Color = 1 << 4,

    /// <summary>Background colour behind text.</summary>
    Background = 1 << 5,

    /// <summary>Font family and size.</summary>
    Font = 1 << 6,

    /// <summary>Ruby annotations.</summary>
    Ruby = 1 << 7,

    /// <summary>Per-word timing (karaoke, recognised words).</summary>
    WordTiming = 1 << 8,

    /// <summary>A speaker or voice name.</summary>
    Voice = 1 << 9,

    /// <summary>Language of the document, a cue or a stretch.</summary>
    Language = 1 << 10,

    /// <summary>Style class names.</summary>
    Classes = 1 << 11,

    /// <summary>Horizontal and vertical alignment of the text block.</summary>
    Alignment = 1 << 12,

    /// <summary>Line, position and size of the text block, or a character-grid cell.</summary>
    Position = 1 << 13,

    /// <summary>Named regions.</summary>
    Region = 1 << 14,

    /// <summary>Vertical writing.</summary>
    Writing = 1 << 15,

    /// <summary>Named styles declared in the header.</summary>
    Styles = 1 << 16,

    /// <summary>Cue identifiers.</summary>
    Id = 1 << 17,

    /// <summary>Caption reveal mode (pop-on, roll-up, paint-on).</summary>
    CaptionMode = 1 << 18,

    /// <summary>Both start and end time of a cue, to the precision of the cue model.</summary>
    Timing = 1 << 19,

    /// <summary>Information that only another format can use (its <see cref="SubtitleExtra"/> entries).</summary>
    Extras = 1 << 20,

    /// <summary>Characters the target's text encoding cannot represent.</summary>
    Text = 1 << 21,

    /// <summary>Document title and header fields.</summary>
    Header = 1 << 22,

    /// <summary>Bitmap images shown with a cue.</summary>
    Images = 1 << 23,
}

/// <summary>How a conversion treated something the target format cannot carry exactly.</summary>
public enum LossKind
{
    /// <summary>It is not in the output.</summary>
    Dropped = 0,

    /// <summary>It is in the output in a different, nearby form (rounded, mapped to the closest equivalent).</summary>
    Approximated,
}

/// <summary>One entry of a <see cref="ConversionReport"/>.</summary>
/// <param name="CueIndex">Index of the cue in the document, or -1 for the document as a whole.</param>
/// <param name="Feature">What was affected (a single flag).</param>
/// <param name="Kind">Dropped or approximated.</param>
/// <param name="Message">What happened, in words.</param>
public readonly record struct ConversionLoss(int CueIndex, SubtitleFeatures Feature, LossKind Kind, string Message)
{
    /// <inheritdoc />
    public override string ToString() => CueIndex < 0
        ? $"{Kind} {Feature}: {Message}"
        : $"{Kind} {Feature} (cue {CueIndex}): {Message}";
}

/// <summary>
/// What a conversion or write dropped or approximated. Empty when the output carries everything the document held.
/// Keeps at most <see cref="MaxEntries"/> entries and counts the rest in <see cref="Omitted"/>.
/// </summary>
public sealed class ConversionReport
{
    /// <summary>The most entries a report lists; later ones are counted in <see cref="Omitted"/>.</summary>
    public const int MaxEntries = 10_000;

    private readonly List<ConversionLoss> _losses = [];

    // Counts per single-flag feature and kind in a flat array (the usual case, and cheap enough to do per cue); combined flags,
    // which no writer uses, go to the dictionary.
    private readonly int[] _flagCounts = new int[2 * 24];
    private Dictionary<(SubtitleFeatures, LossKind), int>? _otherCounts;

    /// <summary>The source format name, or null when the document was built in memory.</summary>
    public string? Source { get; init; }

    /// <summary>The target format name.</summary>
    public string Target { get; init; } = "";

    /// <summary>The listed losses, in cue order.</summary>
    public IReadOnlyList<ConversionLoss> Losses => _losses;

    /// <summary>Losses beyond <see cref="MaxEntries"/> that are counted but not listed.</summary>
    public int Omitted { get; private set; }

    /// <summary>True when nothing was dropped or approximated.</summary>
    public bool IsLossless => _losses.Count == 0 && Omitted == 0;

    /// <summary>A one-line summary. The data is in the properties.</summary>
    public override string ToString() => IsLossless ? $"Converted to {Target}: lossless" : $"Converted to {Target}: {_losses.Count + Omitted} loss{(_losses.Count + Omitted == 1 ? "" : "es")} (see Losses)";

    /// <summary>How many entries (listed or not) there are for a feature and kind.</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="kind">Dropped or approximated.</param>
    public int Count(SubtitleFeatures feature, LossKind kind)
    {
        if (FlagSlot(feature, kind) is int slot and >= 0)
        {
            return _flagCounts[slot];
        }

        return _otherCounts is not null && _otherCounts.TryGetValue((feature, kind), out int n) ? n : 0;
    }

    /// <summary>The features with at least one entry of the given kind.</summary>
    /// <param name="kind">Dropped or approximated.</param>
    public SubtitleFeatures Affected(LossKind kind)
    {
        SubtitleFeatures all = SubtitleFeatures.None;
        for (int bit = 0; bit < 24; bit++)
        {
            if (_flagCounts[(bit * 2) + (kind == LossKind.Dropped ? 0 : 1)] > 0)
            {
                all |= (SubtitleFeatures)(1L << bit);
            }
        }

        if (_otherCounts is not null)
        {
            foreach (((SubtitleFeatures feature, LossKind k), int n) in _otherCounts)
            {
                if (k == kind && n > 0)
                {
                    all |= feature;
                }
            }
        }

        return all;
    }

    // The array slot of a single-flag feature, or -1.
    private static int FlagSlot(SubtitleFeatures feature, LossKind kind)
    {
        long f = (long)feature;
        if (f <= 0 || (f & (f - 1)) != 0 || f >= (1L << 24) || kind is not (LossKind.Dropped or LossKind.Approximated))
        {
            return -1;
        }

        return (System.Numerics.BitOperations.TrailingZeroCount(f) * 2) + (kind == LossKind.Dropped ? 0 : 1);
    }

    internal void Add(int cue, SubtitleFeatures feature, LossKind kind, string message)
    {
        if (FlagSlot(feature, kind) is int slot and >= 0)
        {
            _flagCounts[slot]++;
        }
        else
        {
            _otherCounts ??= [];
            _otherCounts[(feature, kind)] = Count(feature, kind) + 1;
        }

        if (_losses.Count < MaxEntries)
        {
            _losses.Add(new ConversionLoss(cue, feature, kind, message));
        }
        else
        {
            Omitted++;
        }
    }
}

/// <summary>The result of writing a document in a format.</summary>
/// <param name="Data">The encoded file, using the document's text encoding unless the format requires another.</param>
/// <param name="Text">The same content as text.</param>
/// <param name="Report">What the target format could not carry.</param>
public sealed record SubtitleOutput(byte[] Data, string Text, ConversionReport Report);

/// <summary>Options for reading subtitle bytes.</summary>
public sealed record SubtitleReadOptions
{
    /// <summary>
    /// The text encoding to use when the bytes have no byte order mark and are not valid UTF-8 (any name or code page the BCL knows,
    /// including those from the code-pages provider). Null (the default) means windows-1252, the usual encoding of legacy files in
    /// western languages.
    /// </summary>
    public string? FallbackEncoding { get; init; }

    /// <summary>
    /// When set, the bytes are decoded as this encoding whatever they contain (a byte order mark still wins). Null (the default)
    /// detects: byte order mark, then UTF-16 without one by its zero bytes, then UTF-8 if valid, then <see cref="FallbackEncoding"/>.
    /// </summary>
    public string? Encoding { get; init; }
}

/// <summary>
/// A subtitle file format: reads bytes or text into a <see cref="SubtitleDocument"/> and writes a document back, reporting what the
/// format could not carry. Instances are stateless and thread-safe; get them from <see cref="SubtitleFormats"/>.
/// </summary>
public abstract class SubtitleFormat
{
    /// <summary>Short lower-case name used in <see cref="SubtitleDocument.Format"/> and <see cref="SubtitleExtra.Format"/>: <c>srt</c>, <c>webvtt</c>…</summary>
    public abstract string Name { get; }

    /// <summary>A human-readable name.</summary>
    public abstract string DisplayName { get; }

    /// <summary>File extensions (with the dot, lower case); the first is the one written.</summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>What the format can carry. A document feature outside this set is dropped on write and listed in the report.</summary>
    public abstract SubtitleFeatures Features { get; }

    /// <summary>True when the format requires UTF-8 whatever the document says (WebVTT, TTML).</summary>
    public virtual bool RequiresUtf8 => false;

    /// <summary>True when this build can read the format (false for a format that is recognised but has no reader).</summary>
    public virtual bool CanRead => true;

    /// <summary>True when this build can write the format.</summary>
    public virtual bool CanWrite => true;

    /// <summary>How likely the text is this format, from 0 (not at all) to 100 (certain). Looks at the start of the text only; never the extension.</summary>
    /// <param name="head">The first characters of the decoded text (up to a few KiB).</param>
    public abstract int Sniff(ReadOnlySpan<char> head);

    /// <summary>Reads already-decoded text.</summary>
    /// <param name="text">The file text without a byte order mark.</param>
    /// <param name="defects">Receives every deviation found, with byte offsets (as if the text were UTF-8); null to discard.</param>
    /// <param name="limits">Resource limits; null for <see cref="MediaLimits.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="MediaLimitExceededException">The text has more cues than <see cref="MediaLimits.MaxCues"/>, or is larger than <see cref="MediaLimits.MaxAllocationBytes"/>.</exception>
    /// <exception cref="MediaFormatException">The text is not this format at all (for example XML that is not well formed).</exception>
    public SubtitleDocument ReadText(string text, DefectLog? defects = null, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= MediaLimits.Default;
        limits.CheckAllocation(text.Length, 0);
        var source = new SubtitleSource(text, defects ?? new DefectLog(limits), limits, System.Text.Encoding.UTF8, 0);
        SubtitleDocument doc = ReadCore(source);
        return doc with { Format = Name };
    }

    /// <summary>Decodes the bytes (see <see cref="SubtitleReadOptions"/>) and reads them.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="defects">Receives every deviation found, with byte offsets; null to discard.</param>
    /// <param name="limits">Resource limits; null for <see cref="MediaLimits.Default"/>.</param>
    /// <param name="options">Encoding options; null for detection.</param>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    /// <exception cref="MediaFormatException">The bytes are not this format at all.</exception>
    public virtual SubtitleDocument Read(ReadOnlySpan<byte> data, DefectLog? defects = null, MediaLimits? limits = null, SubtitleReadOptions? options = null)
    {
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        limits.CheckAllocation(data.Length, 0);
        string text = SubtitleText.Decode(data, options, defects, out Encoding encoding, out int bomLength);
        var source = new SubtitleSource(text, defects, limits, encoding, bomLength);
        SubtitleDocument doc = ReadCore(source);
        return doc with
        {
            Format = Name,
            TextEncoding = SubtitleText.NameOf(encoding),
            ByteOrderMark = bomLength > 0,
        };
    }

    /// <summary>Like <see cref="Read"/> but returns false instead of throwing on a limit or a file that is not this format.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="document">The document, or null on failure.</param>
    /// <param name="defects">Receives every deviation found; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    /// <param name="options">Encoding options; null for detection.</param>
    public bool TryRead(ReadOnlySpan<byte> data, out SubtitleDocument? document, DefectLog? defects = null, MediaLimits? limits = null, SubtitleReadOptions? options = null)
    {
        try
        {
            document = Read(data, defects, limits, options);
            return true;
        }
        catch (MediaException)
        {
            document = null;
            return false;
        }
    }

    /// <summary>Writes the document in this format.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The file bytes and text, and the report of what this format could not carry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="NotSupportedException">This build has no writer for the format (<see cref="CanWrite"/> is false).</exception>
    public SubtitleOutput Write(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanWrite)
        {
            throw new NotSupportedException($"No writer for {Name}.");
        }

        var report = new ConversionReport { Source = document.Format, Target = Name };
        SubtitleLoss.Analyze(document, this, report);
        string text = WriteCore(document, report);
        Encoding encoding = new UTF8Encoding(false);
        if (!RequiresUtf8)
        {
            encoding = SubtitleText.EncodingFor(document.TextEncoding, out bool known);
            if (!known)
            {
                report.Add(-1, SubtitleFeatures.Text, LossKind.Approximated, $"Unknown encoding '{document.TextEncoding}'; wrote UTF-8.");
                encoding = new UTF8Encoding(false);
            }
        }

        bool bom = !RequiresUtf8 && document.ByteOrderMark;
        byte[] data = SubtitleText.Encode(text, encoding, bom, report);
        return new SubtitleOutput(data, text, report);
    }

    /// <summary>Parses decoded text (internal reader).</summary>
    internal abstract SubtitleDocument ReadCore(SubtitleSource source);

    /// <summary>Produces the text (internal writer); logs approximations in <paramref name="report"/>.</summary>
    internal abstract string WriteCore(SubtitleDocument document, ConversionReport report);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The subtitle formats this build knows, as an explicit table (no reflection). Content decides the format, never the extension.
/// </summary>
public static class SubtitleFormats
{
    private static readonly object Gate = new();
    private static SubtitleFormat[] s_all =
    [
        WebVttFormat.Instance,
        TtmlFormat.Instance,
        AssFormat.Instance,
        SrtFormat.Instance,
        SbvFormat.Instance,
        LrcFormat.Instance,
        RecursiveAI.OneMedia.Captions.Scc.SccSubtitleFormat.Instance,
        PgsSubtitleFormat.Instance,
        VobSubSubtitleFormat.Instance,
    ];

    /// <summary>SubRip (<c>.srt</c>).</summary>
    public static SubtitleFormat Srt => SrtFormat.Instance;

    /// <summary>WebVTT (<c>.vtt</c>).</summary>
    public static SubtitleFormat WebVtt => WebVttFormat.Instance;

    /// <summary>Advanced SubStation Alpha and SubStation Alpha (<c>.ass</c>, <c>.ssa</c>).</summary>
    public static SubtitleFormat Ass => AssFormat.Instance;

    /// <summary>TTML2, DFXP and the IMSC1 text profile (<c>.ttml</c>, <c>.dfxp</c>, <c>.xml</c>).</summary>
    public static SubtitleFormat Ttml => TtmlFormat.Instance;

    /// <summary>YouTube SBV (<c>.sbv</c>).</summary>
    public static SubtitleFormat Sbv => SbvFormat.Instance;

    /// <summary>LRC lyrics with enhanced word times (<c>.lrc</c>).</summary>
    public static SubtitleFormat Lrc => LrcFormat.Instance;

    /// <summary>Every registered format, most distinctive first.</summary>
    public static IReadOnlyList<SubtitleFormat> All => Volatile.Read(ref s_all);

    /// <summary>
    /// Adds a format (for example Scenarist SCC from the captions layer). Formats are tried in registration order after the built-in ones.
    /// Call once at start-up; replaces an existing format of the same <see cref="SubtitleFormat.Name"/>.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    public static void Register(SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        lock (Gate)
        {
            var list = new List<SubtitleFormat>(s_all);
            int existing = list.FindIndex(f => f.Name == format.Name);
            if (existing >= 0)
            {
                list[existing] = format;
            }
            else
            {
                list.Add(format);
            }

            Volatile.Write(ref s_all, list.ToArray());
        }
    }

    /// <summary>The features the cues, header and styles of a document use (what a target format would have to carry to be lossless).</summary>
    /// <param name="document">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static SubtitleFeatures FeaturesUsed(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SubtitleFeatures used = SubtitleFeatures.None;
        foreach (SubtitleCue cue in document.Cues)
        {
            used |= SubtitleLoss.Used(cue);
        }

        if (document.Styles.Count > 0)
        {
            used |= SubtitleFeatures.Styles;
        }

        if (document.Regions.Count > 0)
        {
            used |= SubtitleFeatures.Region;
        }

        if (document.Language is not null)
        {
            used |= SubtitleFeatures.Language;
        }

        if (document.Title is not null)
        {
            used |= SubtitleFeatures.Header;
        }

        return used;
    }

    /// <summary>Finds a format by name (<c>srt</c>, <c>webvtt</c>, …; <c>vtt</c>, <c>ssa</c>, <c>dfxp</c>, <c>imsc</c> are accepted too) or by extension.</summary>
    /// <param name="nameOrExtension">A name, or an extension with or without its dot; case-insensitive.</param>
    /// <returns>The format, or null.</returns>
    public static SubtitleFormat? Find(string nameOrExtension)
    {
        ArgumentNullException.ThrowIfNull(nameOrExtension);
        string key = nameOrExtension.TrimStart('.').ToLowerInvariant();
        key = key switch
        {
            "vtt" => "webvtt",
            "ssa" => "ass",
            "dfxp" or "imsc" or "imsc1" or "tt" => "ttml",
            _ => key,
        };
        foreach (SubtitleFormat f in All)
        {
            if (f.Name == key)
            {
                return f;
            }

            foreach (string ext in f.Extensions)
            {
                if (ext.Length > 1 && ext.AsSpan(1).SequenceEqual(key))
                {
                    return f;
                }
            }
        }

        return null;
    }

    /// <summary>The format whose <see cref="SubtitleFormat.Sniff"/> scores highest on the text (at least 50), or null.</summary>
    /// <param name="head">The first characters of the decoded text.</param>
    public static SubtitleFormat? Sniff(ReadOnlySpan<char> head)
    {
        SubtitleFormat? best = null;
        int bestScore = 49;
        foreach (SubtitleFormat f in All)
        {
            int score = f.Sniff(head);
            if (score > bestScore)
            {
                best = f;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Detects the format of raw bytes by content: decodes the start of them and sniffs.</summary>
    /// <param name="data">The file bytes (the first 8 KiB are looked at).</param>
    /// <returns>The format, or null when nothing matches.</returns>
    public static SubtitleFormat? Sniff(ReadOnlySpan<byte> data)
    {
        // Binary formats first: their bytes are not text.
        if (BitmapSubtitles.LooksLikePgs(data))
        {
            return PgsSubtitleFormat.Instance;
        }

        if (BitmapSubtitles.LooksLikeProgramStream(data))
        {
            return VobSubSubtitleFormat.Instance;
        }

        ReadOnlySpan<byte> head = data.Length > 8192 ? data[..8192] : data;
        string text = SubtitleText.Decode(head, null, new DefectLog(), out _, out _, tolerateTruncatedEnd: true);
        return Sniff(text);
    }

    /// <summary>Reads bytes in whichever format their content shows.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    /// <param name="options">Encoding options; null for detection.</param>
    /// <exception cref="UnsupportedMediaException">No registered format matches the content.</exception>
    public static SubtitleDocument Read(ReadOnlySpan<byte> data, DefectLog? defects = null, MediaLimits? limits = null, SubtitleReadOptions? options = null)
    {
        SubtitleFormat format = Sniff(data) ?? throw new UnsupportedMediaException("The content is not a recognised subtitle format.");
        if (!format.CanRead)
        {
            throw new UnsupportedMediaException($"{format.DisplayName} is recognised but cannot be read by this build.");
        }

        return format.Read(data, defects, limits, options);
    }
}

// What a document uses, compared with what a format carries: the generic part of every loss report.
internal static class SubtitleLoss
{
    internal static void Analyze(SubtitleDocument doc, SubtitleFormat target, ConversionReport report)
    {
        SubtitleFeatures can = target.Features;
        string format = target.Name;

        if (!can.HasFlag(SubtitleFeatures.Styles) && doc.Styles.Count > 0)
        {
            report.Add(-1, SubtitleFeatures.Styles, LossKind.Dropped, $"{doc.Styles.Count} named style(s) are not carried by {format}.");
        }

        if (!can.HasFlag(SubtitleFeatures.Region) && doc.Regions.Count > 0)
        {
            report.Add(-1, SubtitleFeatures.Region, LossKind.Dropped, $"{doc.Regions.Count} region(s) are not carried by {format}.");
        }

        if (!can.HasFlag(SubtitleFeatures.Language) && doc.Language is not null)
        {
            report.Add(-1, SubtitleFeatures.Language, LossKind.Dropped, "The document language is not carried.");
        }

        if (!can.HasFlag(SubtitleFeatures.Header) && doc.Title is not null)
        {
            report.Add(-1, SubtitleFeatures.Header, LossKind.Dropped, "The title is not carried.");
        }

        int foreign = CountForeign(doc.Extras, format);
        if (foreign > 0)
        {
            report.Add(-1, SubtitleFeatures.Extras, LossKind.Dropped, $"{foreign} header entr{(foreign == 1 ? "y" : "ies")} specific to {Origin(doc.Extras, format)} not carried.");
        }

        for (int i = 0; i < doc.Cues.Count; i++)
        {
            SubtitleCue cue = doc.Cues[i];
            SubtitleFeatures used = Used(cue);
            SubtitleFeatures missing = used & ~can;
            for (long bit = 1; missing != 0 && bit != 0 && bit <= (long)SubtitleFeatures.Images; bit <<= 1)
            {
                if ((missing & (SubtitleFeatures)bit) != 0)
                {
                    report.Add(i, (SubtitleFeatures)bit, LossKind.Dropped, Describe((SubtitleFeatures)bit, format));
                }
            }

            int cueForeign = CountForeign(cue.Extras, format);
            for (int k = 0; k < cue.Spans.Count; k++)
            {
                if (cue.Spans[k].Raw is { } raw && raw.Format != format)
                {
                    cueForeign++;
                }
            }

            if (cueForeign > 0)
            {
                report.Add(i, SubtitleFeatures.Extras, LossKind.Dropped, $"{cueForeign} entr{(cueForeign == 1 ? "y" : "ies")} specific to another format not carried.");
            }
        }
    }

    private static string Origin(SubtitleList<SubtitleExtra> extras, string target)
    {
        foreach (SubtitleExtra e in extras)
        {
            if (e.Format != target)
            {
                return e.Format;
            }
        }

        return "another format";
    }

    private static int CountForeign(SubtitleList<SubtitleExtra> extras, string format)
    {
        int n = 0;
        for (int k = 0; k < extras.Count; k++)
        {
            if (extras[k].Format != format)
            {
                n++;
            }
        }

        return n;
    }

    internal static SubtitleFeatures Used(SubtitleCue cue)
    {
        SubtitleFeatures used = SubtitleFeatures.None;
        if (cue.Id is not null)
        {
            used |= SubtitleFeatures.Id;
        }

        if (cue.Speaker is not null)
        {
            used |= SubtitleFeatures.Voice;
        }

        if (cue.StyleName is not null)
        {
            used |= SubtitleFeatures.Styles;
        }

        if (cue.Language is not null)
        {
            used |= SubtitleFeatures.Language;
        }

        if (cue.Mode != CaptionMode.None)
        {
            used |= SubtitleFeatures.CaptionMode;
        }

        if (cue.Images.Count > 0)
        {
            used |= SubtitleFeatures.Images;
        }

        if (cue.Placement is { } p)
        {
            if (p.Align != SubtitleAlign.Unspecified || p.VerticalAlign != SubtitleVerticalAlign.Unspecified)
            {
                used |= SubtitleFeatures.Alignment;
            }

            if (p.Line is not null || p.Position is not null || p.Size is not null || p.Row is not null || p.Column is not null)
            {
                used |= SubtitleFeatures.Position;
            }

            if (p.Region is not null)
            {
                used |= SubtitleFeatures.Region;
            }

            if (p.Writing != SubtitleWriting.Horizontal)
            {
                used |= SubtitleFeatures.Writing;
            }
        }

        for (int k = 0; k < cue.Spans.Count; k++)
        {
            SubtitleSpan span = cue.Spans[k];
            if (span.Style is { } s)
            {
                if (s.Bold == true)
                {
                    used |= SubtitleFeatures.Bold;
                }

                if (s.Italic == true)
                {
                    used |= SubtitleFeatures.Italic;
                }

                if (s.Underline == true)
                {
                    used |= SubtitleFeatures.Underline;
                }

                if (s.Strike == true)
                {
                    used |= SubtitleFeatures.Strike;
                }

                if (s.Color is not null)
                {
                    used |= SubtitleFeatures.Color;
                }

                if (s.Background is not null)
                {
                    used |= SubtitleFeatures.Background;
                }

                if (s.FontFamily is not null || s.FontSize is not null)
                {
                    used |= SubtitleFeatures.Font;
                }

                if (s.Classes is not null)
                {
                    used |= SubtitleFeatures.Classes;
                }
            }

            if (span.Voice is not null)
            {
                used |= SubtitleFeatures.Voice;
            }

            if (span.Language is not null)
            {
                used |= SubtitleFeatures.Language;
            }

            if (span.Ruby is not null)
            {
                used |= SubtitleFeatures.Ruby;
            }

            if (span.WordStart is not null)
            {
                used |= SubtitleFeatures.WordTiming;
            }
        }

        return used;
    }

    private static string Describe(SubtitleFeatures feature, string format) => feature switch
    {
        SubtitleFeatures.Bold => $"Bold is not carried by {format}.",
        SubtitleFeatures.Italic => $"Italic is not carried by {format}.",
        SubtitleFeatures.Underline => $"Underline is not carried by {format}.",
        SubtitleFeatures.Strike => $"Strike-through is not carried by {format}.",
        SubtitleFeatures.Color => $"Text colour is not carried by {format}.",
        SubtitleFeatures.Background => $"Background colour is not carried by {format}.",
        SubtitleFeatures.Font => $"Font family and size are not carried by {format}.",
        SubtitleFeatures.Ruby => $"Ruby annotations are not carried by {format}; the base text stays.",
        SubtitleFeatures.WordTiming => $"Word timing is not carried by {format}.",
        SubtitleFeatures.Voice => $"The speaker is not carried by {format}.",
        SubtitleFeatures.Language => $"The language is not carried by {format}.",
        SubtitleFeatures.Classes => $"Style classes are not carried by {format}.",
        SubtitleFeatures.Alignment => $"Alignment is not carried by {format}.",
        SubtitleFeatures.Position => $"Position and size are not carried by {format}.",
        SubtitleFeatures.Region => $"The region is not carried by {format}.",
        SubtitleFeatures.Writing => $"Vertical writing is not carried by {format}.",
        SubtitleFeatures.Styles => $"The style name is not carried by {format}.",
        SubtitleFeatures.Id => $"The cue identifier is not carried by {format}.",
        SubtitleFeatures.CaptionMode => $"The caption mode is not carried by {format}.",
        SubtitleFeatures.Images => $"Image cue dropped: {format} carries text only.",
        _ => $"{feature} is not carried by {format}.",
    };

    // Records an Approximated entry when a time did not survive the target's precision.
    internal static long Quantize(MediaTime time, long timescale, int cue, SubtitleFeatures feature, ConversionReport report, string what)
    {
        // The usual sidecar cases without the general rescale: the same scale, and milliseconds to centiseconds (ties away from zero).
        if (time.Timescale == timescale)
        {
            return time.Ticks;
        }

        if (time.Timescale == 1000 && timescale == 100)
        {
            long ticks = time.Ticks;
            long q = ticks / 10;
            long rem = ticks % 10;
            if (rem != 0)
            {
                if (rem >= 5 || rem <= -5)
                {
                    q += ticks < 0 ? -1 : 1;
                }

                report.Add(cue, feature, LossKind.Approximated, what == "Time" ? "Time rounded to 1/100 s." : $"{what} rounded to 1/100 s.");
            }

            return q;
        }

        MediaTime r = time.Rescale(timescale, MediaRounding.NearestAwayFromZero);
        if (r != time)
        {
            report.Add(cue, feature, LossKind.Approximated, $"{what} rounded to 1/{timescale.ToString(CultureInfo.InvariantCulture)} s.");
        }

        return r.Ticks;
    }
}

namespace RecursiveAI.OneMedia.Captions;

// The bitmap subtitle files as entries of the format registry. They are read-only: the pixels have no text form to write back.
// A .sup is recognised by its first segment; an .idx by its header text. The index has no pictures of its own, so reading it needs the .sub beside it,
// which only a path can give (Subtitles.Read(path), BitmapSubtitles.ReadFile).

/// <summary>Blu-ray PGS (<c>.sup</c>) as a read-only subtitle format.</summary>
public sealed class PgsSubtitleFormat : SubtitleFormat
{
    private static readonly string[] Exts = [".sup"];

    private PgsSubtitleFormat()
    {
    }

    /// <summary>The single instance.</summary>
    public static PgsSubtitleFormat Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => "pgs";

    /// <inheritdoc />
    public override string DisplayName => "Blu-ray PGS (SUP)";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => Exts;

    /// <inheritdoc />
    public override SubtitleFeatures Features => SubtitleFeatures.Images | SubtitleFeatures.Position | SubtitleFeatures.Timing;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override int Sniff(ReadOnlySpan<char> head) => 0; // binary: recognised from bytes by SubtitleFormats.Sniff(ReadOnlySpan<byte>)

    /// <summary>Decodes the file bytes; options are ignored (the content is binary).</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    /// <param name="options">Ignored.</param>
    public override SubtitleDocument Read(ReadOnlySpan<byte> data, DefectLog? defects = null, MediaLimits? limits = null, SubtitleReadOptions? options = null) =>
        BitmapSubtitles.ReadSup(data, new BitmapSubtitleOptions { Limits = limits }, defects);

    internal override SubtitleDocument ReadCore(SubtitleSource source) => throw new UnsupportedMediaException("PGS is binary: read the bytes or the file, not text.");

    internal override string WriteCore(SubtitleDocument document, ConversionReport report) => throw new NotSupportedException("No writer for pgs.");
}

/// <summary>DVD VobSub (<c>.idx</c> with <c>.sub</c>) as a read-only subtitle format.</summary>
public sealed class VobSubSubtitleFormat : SubtitleFormat
{
    private static readonly string[] Exts = [".idx", ".sub"];

    private VobSubSubtitleFormat()
    {
    }

    /// <summary>The single instance.</summary>
    public static VobSubSubtitleFormat Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => "vobsub";

    /// <inheritdoc />
    public override string DisplayName => "DVD VobSub (IDX/SUB)";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => Exts;

    /// <inheritdoc />
    public override SubtitleFeatures Features => SubtitleFeatures.Images | SubtitleFeatures.Position | SubtitleFeatures.Timing;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override int Sniff(ReadOnlySpan<char> head) =>
        head.TrimStart().TrimStart('\uFEFF').StartsWith("# VobSub index", StringComparison.OrdinalIgnoreCase) ? 100 : 0;

    /// <summary>Decodes a bare <c>.sub</c> program stream; an index alone cannot be read from bytes (it needs the <c>.sub</c> beside it).</summary>
    /// <param name="data">The bytes of a <c>.sub</c> program stream.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    /// <param name="options">Ignored.</param>
    /// <exception cref="UnsupportedMediaException">The bytes are an index: use <see cref="Subtitles.Read(string)"/> or <see cref="BitmapSubtitles.ReadVobSub"/> with both files.</exception>
    public override SubtitleDocument Read(ReadOnlySpan<byte> data, DefectLog? defects = null, MediaLimits? limits = null, SubtitleReadOptions? options = null)
    {
        if (BitmapSubtitles.LooksLikeProgramStream(data))
        {
            return BitmapSubtitles.ReadProgramStream(data, new BitmapSubtitleOptions { Limits = limits }, defects);
        }

        throw new UnsupportedMediaException("A VobSub index needs the .sub file beside it: read the path (Subtitles.Read) or pass both files to BitmapSubtitles.ReadVobSub.");
    }

    internal override SubtitleDocument ReadCore(SubtitleSource source) => throw new UnsupportedMediaException("VobSub is binary: read the bytes or the file, not text.");

    internal override string WriteCore(SubtitleDocument document, ConversionReport report) => throw new NotSupportedException("No writer for vobsub.");
}

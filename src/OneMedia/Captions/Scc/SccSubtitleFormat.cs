using System.Globalization;
using System.Linq;

namespace RecursiveAI.OneMedia.Captions.Scc;

/// <summary>
/// Scenarist Closed Caption (SCC) as a sidecar format of the cue registry. Reading decodes the CEA-608 byte pairs (channel CC1, or CC2 when CC1
/// holds nothing) into cues with grid placement, styling and reveal mode; writing encodes cues as pop-on captions on CC1 with drop-frame
/// timecodes at 29.97 fps. Register it with <see cref="SubtitleFormats.Register"/> to have it found by name, extension and sniffing.
/// </summary>
public sealed class SccSubtitleFormat : SubtitleFormat
{
    private static readonly string[] Exts = [".scc"];

    private SccSubtitleFormat()
    {
    }

    /// <summary>The single instance.</summary>
    public static SccSubtitleFormat Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => "scc";

    /// <inheritdoc />
    public override string DisplayName => "Scenarist Closed Caption (SCC)";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => Exts;

    /// <inheritdoc />
    public override SubtitleFeatures Features =>
        SubtitleFeatures.Italic | SubtitleFeatures.Underline | SubtitleFeatures.Color | SubtitleFeatures.Position | SubtitleFeatures.CaptionMode
        | SubtitleFeatures.Timing | SubtitleFeatures.Extras | SubtitleFeatures.Text;

    /// <inheritdoc />
    public override int Sniff(ReadOnlySpan<char> head)
    {
        ReadOnlySpan<char> t = head.TrimStart().TrimStart('\uFEFF');
        if (t.StartsWith("Scenarist_SCC", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        // Without the header: a timecode line followed by tab and hexadecimal pairs.
        int nl = t.IndexOfAny('\r', '\n');
        ReadOnlySpan<char> line = nl < 0 ? t : t[..nl];
        return line.Length > 12 && char.IsAsciiDigit(line[0]) && line[2] == ':' && line[5] == ':' && (line[8] == ':' || line[8] == ';') && (line[11] == '\t' || line[11] == ' ') ? 70 : 0;
    }

    internal override SubtitleDocument ReadCore(SubtitleSource source)
    {
        if (!SccDocument.TryParse(source.Text, out SccDocument? scc) || scc is null)
        {
            throw new MediaFormatException("Malformed SCC timecode or byte pair.") { Kind = DefectKind.InvalidValue };
        }

        IReadOnlyList<Cea608.Cea608Caption> captions = scc.Decode();
        Cea608.Cea608Channel channel = captions.Any(c => c.Channel == Cea608.Cea608Channel.CC1) || !captions.Any(c => c.Channel == Cea608.Cea608Channel.CC2)
            ? Cea608.Cea608Channel.CC1
            : Cea608.Cea608Channel.CC2;
        return CaptionCues.FromCea608(captions, channel).Document;
    }

    internal override string WriteCore(SubtitleDocument document, ConversionReport report)
    {
        Cea608Conversion c = CaptionCues.ToCea608(document);
        foreach (string loss in c.Losses)
        {
            report.Add(-1, SubtitleFeatures.Text, LossKind.Approximated, "CEA-608: " + loss + ".");
        }

        report.Add(-1, SubtitleFeatures.Timing, LossKind.Approximated, "Cue times are rounded to whole frames at 29.97 fps.");
        return SccDocument.FromCaptions(c.Captions).Write();
    }
}

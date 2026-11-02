using System.Linq;
using RecursiveAI.OneMedia.Captions.Cea608;
using RecursiveAI.OneMedia.Captions.Cea708;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>The result of mapping between caption data and the cue model.</summary>
/// <param name="Document">The cues.</param>
/// <param name="Losses">What the mapping could not carry, one entry per kind of loss with a count, in plain words.</param>
public sealed record CaptionCueConversion(SubtitleDocument Document, IReadOnlyList<string> Losses);

/// <summary>The result of mapping cues to CEA-608 caption screens.</summary>
/// <param name="Captions">Pop-on caption screens on one channel, ready for <see cref="Cea608Encoder"/> or <see cref="Scc.SccDocument.FromCaptions"/>.</param>
/// <param name="Losses">What CEA-608 could not carry, in plain words.</param>
public sealed record Cea608Conversion(IReadOnlyList<Cea608Caption> Captions, IReadOnlyList<string> Losses);

/// <summary>Maps decoded CEA-608/708 captions to and from the cue model.</summary>
public static class CaptionCues
{
    private static readonly SubtitleColor[] Colors608 =
    [
        new(255, 255, 255), new(0, 255, 0), new(0, 0, 255), new(0, 255, 255), new(255, 0, 0), new(255, 255, 0), new(255, 0, 255),
    ];

    /// <summary>
    /// CEA-608 captions of one channel as cues: text is the rows joined by line feeds; italics, underline and colour become spans; the first row's
    /// grid position becomes <see cref="CuePlacement.Row"/> and <see cref="CuePlacement.Column"/> and every row's position is kept in the
    /// <c>cea608</c>/<c>rows</c> extra as <c>row:column</c> pairs; the reveal mode becomes <see cref="SubtitleCue.Mode"/>.
    /// </summary>
    /// <param name="captions">Captions from <see cref="Cea608Decoder"/>; other channels are skipped.</param>
    /// <param name="channel">The channel to convert.</param>
    /// <param name="language">Language tag for the cues, if known (608 does not say; XDS may).</param>
    /// <returns>The document (format <c>cea608</c>) and its losses: flashing, which cues cannot express.</returns>
    public static CaptionCueConversion FromCea608(IEnumerable<Cea608Caption> captions, Cea608Channel channel, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var cues = new List<SubtitleCue>();
        int flash = 0;
        foreach (Cea608Caption c in captions.Where(x => x.Channel == channel).OrderBy(x => x.Start))
        {
            var text = new System.Text.StringBuilder();
            var spans = new List<SubtitleSpan>();
            var rowPositions = new List<string>();
            foreach (Cea608Row row in c.Rows)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                int offset = text.Length;
                text.Append(row.Text);
                rowPositions.Add(FormattableString.Invariant($"{row.Row}:{row.Column - 1}"));
                foreach (Cea608Span s in row.Spans)
                {
                    flash += s.Flash ? 1 : 0;
                    spans.Add(new SubtitleSpan(offset + s.Start, s.Length)
                    {
                        Style = new TextStyle
                        {
                            Italic = s.Italic ? true : null,
                            Underline = s.Underline ? true : null,
                            Color = s.Color == Cea608Color.White ? null : Colors608[(int)s.Color],
                        },
                    });
                }
            }

            Cea608Row first = c.Rows[0];
            cues.Add(new SubtitleCue
            {
                Start = c.Start,
                End = c.End,
                Text = text.ToString(),
                Spans = new SubtitleList<SubtitleSpan>(spans),
                Mode = c.Mode,
                Language = language,
                Placement = new CuePlacement { Row = first.Row, Column = first.Column - 1 },
                Extras = new SubtitleList<SubtitleExtra>([new SubtitleExtra("cea608", "channel", channel.ToString()), new SubtitleExtra("cea608", "rows", string.Join(' ', rowPositions))]),
            });
        }

        var losses = new List<string>();
        if (flash > 0)
        {
            losses.Add($"{flash} flashing run(s) shown steady");
        }

        return new CaptionCueConversion(new SubtitleDocument { Format = "cea608", Language = language, Cues = new SubtitleList<SubtitleCue>(cues) }, losses);
    }

    /// <summary>
    /// CEA-708 captions of one service as cues, one cue per visible window per screen so each keeps its own anchor: relative anchors become
    /// <see cref="CuePlacement.Line"/> (percent of the height) and <see cref="CuePlacement.Position"/> (percent of the width); anchors in grid
    /// units (0–74 rows, 0–209 columns) are scaled to percent. Italics, underline and foreground colour become spans.
    /// </summary>
    /// <param name="captions">Captions from <see cref="Cea708Decoder"/>.</param>
    /// <param name="service">The service number, 1–63.</param>
    /// <param name="language">Language tag, if known.</param>
    /// <returns>The document (format <c>cea708</c>) and what it drops: pen size, window and pen styling beyond the above, opacity, edges, fonts, effects, delays.</returns>
    public static CaptionCueConversion FromCea708(IEnumerable<Cea708Caption> captions, int service, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var cues = new List<SubtitleCue>();
        int sizes = 0;
        foreach (Cea708Caption c in captions.Where(x => x.Service == service).OrderBy(x => x.Start))
        {
            foreach (Cea708Window w in c.Windows)
            {
                var text = new System.Text.StringBuilder();
                var spans = new List<SubtitleSpan>();
                foreach (Cea708Row row in w.Rows)
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    int offset = text.Length;
                    text.Append(row.Text);
                    foreach (Cea708Span s in row.Spans)
                    {
                        sizes += s.Size != 1 ? 1 : 0;
                        TextStyle style = new()
                        {
                            Italic = s.Italic ? true : null,
                            Underline = s.Underline ? true : null,
                            Color = s.Foreground is int rgb ? new SubtitleColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null,
                        };
                        if (!style.IsEmpty)
                        {
                            spans.Add(new SubtitleSpan(offset + s.Start, s.Length) { Style = style });
                        }
                    }
                }

                double v = w.RelativePosition ? w.AnchorVertical : w.AnchorVertical * 100.0 / 74;
                double h = w.RelativePosition ? w.AnchorHorizontal : w.AnchorHorizontal * 100.0 / 209;
                cues.Add(new SubtitleCue
                {
                    Start = c.Start,
                    End = c.End,
                    Text = text.ToString(),
                    Spans = new SubtitleList<SubtitleSpan>(spans),
                    Language = language,
                    Placement = new CuePlacement { Line = new CueLine(v, CueLineUnit.Percent, false), Position = h },
                    Extras = new SubtitleList<SubtitleExtra>([new SubtitleExtra("cea708", "service", service.ToString(System.Globalization.CultureInfo.InvariantCulture)), new SubtitleExtra("cea708", "window", w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
                });
            }
        }

        var losses = new List<string> { "window and pen styling other than italics, underline and foreground colour (opacity, background, edges, fonts, justification, scroll and print direction, effects) is not modelled" };
        if (sizes > 0)
        {
            losses.Add($"{sizes} run(s) with a non-standard pen size");
        }

        return new CaptionCueConversion(new SubtitleDocument { Format = "cea708", Language = language, Cues = new SubtitleList<SubtitleCue>(cues) }, losses);
    }

    /// <summary>
    /// Cues as CEA-608 pop-on captions: lines wider than 32 columns are wrapped at spaces, up to 15 rows; rows go to the cue's grid
    /// position when it has one, otherwise to the bottom rows, centred. Italics, underline and the seven 608 colours (exact RGB matches) are kept.
    /// </summary>
    /// <param name="document">Cues from any format.</param>
    /// <param name="channel">The channel the captions are addressed to (CC1 or CC2 for SCC).</param>
    /// <returns>The captions and what 608 could not carry: characters outside its sets (written as <c>?</c>), colours other than the seven, styling beyond italics/underline/colour, wrapping, truncation beyond 15 rows.</returns>
    public static Cea608Conversion ToCea608(SubtitleDocument document, Cea608Channel channel = Cea608Channel.CC1)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<Cea608Caption>();
        int wrapped = 0, truncated = 0, badColor = 0, otherStyle = 0, unsupported = 0;
        foreach (SubtitleCue cue in document.Cues)
        {
            var lines = new List<(string Text, int Offset)>();
            int pos = 0;
            foreach (string raw in cue.Text.Split('\n'))
            {
                string rest = raw;
                int offset = pos;
                while (rest.Length > 32)
                {
                    int cut = rest.LastIndexOf(' ', 32);
                    if (cut <= 0)
                    {
                        cut = 32;
                    }

                    lines.Add((rest[..cut], offset));
                    wrapped++;
                    int skip = cut < rest.Length && rest[cut] == ' ' ? 1 : 0;
                    offset += cut + skip;
                    rest = rest[(cut + skip)..];
                }

                lines.Add((rest, offset));
                pos += raw.Length + 1;
            }

            if (lines.Count > 15)
            {
                truncated += lines.Count - 15;
                lines.RemoveRange(15, lines.Count - 15);
            }

            int firstRow = cue.Placement?.Row is int r && r >= 1 && r + lines.Count - 1 <= 15 ? r : 15 - lines.Count + 1;
            var rows = new List<Cea608Row>();
            for (int i = 0; i < lines.Count; i++)
            {
                (string text, int offset) = lines[i];
                var clean = new System.Text.StringBuilder(text.Length);
                foreach (char ch in text)
                {
                    bool ok = Cea608Encoder.CanEncode(ch);
                    clean.Append(ok ? ch : '?');
                    unsupported += ok ? 0 : 1;
                }

                var spans = new List<Cea608Span>();
                foreach (SubtitleSpan s in cue.Spans)
                {
                    int a = Math.Max(s.Start, offset) - offset;
                    int b = Math.Min(s.End, offset + text.Length) - offset;
                    if (b <= a || s.Style is null)
                    {
                        continue;
                    }

                    TextStyle st = s.Style;
                    otherStyle += (st.Bold is not null || st.Strike is not null || st.Background is not null || st.FontFamily is not null || st.FontSize is not null) ? 1 : 0;
                    Cea608Color color = Cea608Color.White;
                    if (st.Color is SubtitleColor sc)
                    {
                        int idx = Array.FindIndex(Colors608, k => k.R == sc.R && k.G == sc.G && k.B == sc.B);
                        if (idx >= 0)
                        {
                            color = (Cea608Color)idx;
                        }
                        else
                        {
                            badColor++;
                        }
                    }

                    if (color != Cea608Color.White || st.Italic == true || st.Underline == true)
                    {
                        spans.Add(new Cea608Span(a, b - a, color, st.Italic == true, st.Underline == true, false));
                    }
                }

                int column = cue.Placement?.Column is int cc && cc >= 0 && cc + text.Length <= 32 ? cc + 1 : ((32 - text.Length) / 2) + 1;
                rows.Add(new Cea608Row(firstRow + i, column, clean.ToString(), spans));
            }

            if (rows.Count > 0)
            {
                result.Add(new Cea608Caption(channel, cue.Start, cue.End, rows) { Mode = CaptionMode.PopOn });
            }
        }

        var losses = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
            {
                losses.Add($"{n} {what}");
            }
        }

        Add(wrapped, "line(s) wrapped at 32 columns");
        Add(truncated, "line(s) beyond 15 rows dropped");
        Add(unsupported, "character(s) outside the CEA-608 sets written as '?'");
        Add(badColor, "colour(s) that are not one of the seven CEA-608 colours shown white");
        Add(otherStyle, "span(s) with bold, strike, background, font or size dropped");
        return new Cea608Conversion(result, losses);
    }
}

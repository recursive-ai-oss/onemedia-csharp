using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Subtitles;

// 3GPP timed text (tx3g) and QuickTime text to and from the cue model. Mapping choices that the specification leaves open are listed in
// docs/text-tracks.md.

internal static class Tx3gMapping
{
    public const string Format = "tx3g";

    public static SubtitleColor ColorOf(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    public static uint Rgba(SubtitleColor c) => ((uint)c.R << 24) | ((uint)c.G << 16) | ((uint)c.B << 8) | c.A;

    /// <summary>
    /// Replaces CRLF, CR, NEL, line separator and paragraph separator by LF (3GPP TS 26.245 §5.11 asks terminals to start a new line for each).
    /// <paramref name="map"/> gives, for each index of the original text (and its end), the index in the result, so character offsets from style records follow.
    /// </summary>
    public static string NormalizeNewlines(string text, out int[] map)
    {
        map = new int[text.Length + 1];
        bool any = false;
        foreach (char c in text)
        {
            if (c is '\r' or '\u0085' or '\u2028' or '\u2029')
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            for (int i = 0; i <= text.Length; i++)
            {
                map[i] = i;
            }

            return text;
        }

        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = sb.Length;
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    map[++i] = sb.Length;
                }
            }
            else if (c is '\u0085' or '\u2028' or '\u2029')
            {
                sb.Append('\n');
            }
            else
            {
                sb.Append(c);
            }
        }

        map[text.Length] = sb.Length;
        return sb.ToString();
    }
}

internal sealed class Tx3gDecoder : ITextTrackDecoder
{
    private readonly MediaTrack _track;
    private readonly DefectLog _defects;
    private readonly TimedTextSampleEntry? _entry;
    private readonly TextStyleRecord _default;
    private readonly Dictionary<int, string> _fonts = [];
    private readonly List<SubtitleCue> _cues = [];
    private readonly CuePlacement? _placement;

    public Tx3gDecoder(MediaTrack track, DefectLog defects)
    {
        _track = track;
        _defects = defects;
        if (TimedTextSampleEntry.TryParse(track.Configuration.Data.Span, out TimedTextSampleEntry? entry) && entry is not null)
        {
            _entry = entry;
            _default = entry.DefaultStyle;
            foreach (TimedTextFont f in entry.Fonts)
            {
                _fonts[f.FontId] = f.Name;
            }

            _placement = PlacementOf(entry);
        }
        else
        {
            _default = new TextStyleRecord(0, 0, 1, TextFaceStyle.None, 18, 0xFFFFFFFF);
            if (!track.Configuration.IsEmpty)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, -1, "The tx3g sample entry is shorter than its fixed fields; default styling is assumed.", "text.tx3g.entry");
            }
        }
    }

    // 3GPP TS 26.245 §5.16: justification 0 is left/top, 1 centred, -1 right/bottom. Files from common muxers say centred, bottom, which is where
    // subtitles belong, and that is the model's default; anything else is stated as placement.
    private static CuePlacement? PlacementOf(TimedTextSampleEntry e)
    {
        if (e.HorizontalJustification == 1 && e.VerticalJustification == -1)
        {
            return null;
        }

        return new CuePlacement
        {
            Align = e.HorizontalJustification switch { 1 => SubtitleAlign.Center, < 0 => SubtitleAlign.Right, _ => SubtitleAlign.Left },
            VerticalAlign = e.VerticalJustification switch { 1 => SubtitleVerticalAlign.Middle, < 0 => SubtitleVerticalAlign.Bottom, _ => SubtitleVerticalAlign.Top },
        };
    }

    public void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        if (!TimedTextSample.TryParse(data, out TimedTextSample? sample) || sample is null)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, info.SourceRange.Offset, "A timed text sample's text length is larger than the sample; the sample is skipped.", "text.tx3g.sample");
            return;
        }

        // A sample with no text shows nothing: a gap. (QuickTime also uses text-less samples to change the highlight; there is no text to show then.)
        if (sample.Text.Length == 0)
        {
            return;
        }

        if (!sample.Trailing.IsEmpty)
        {
            _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, info.SourceRange.Offset, $"{sample.Trailing.Length} bytes after the last box of a timed text sample are not a box.", "text.tx3g.trailing");
        }

        string text = Tx3gMapping.NormalizeNewlines(sample.Text, out int[] map);
        List<SubtitleSpan> spans = [];
        List<SubtitleExtra> extras = [];
        uint? highlightColor = null;
        foreach (TextModifierBox box in sample.Modifiers)
        {
            if (box.TryGetHighlightColor(out uint hc))
            {
                highlightColor = hc;
            }
        }

        MediaTime end = duration.IsKnown ? start.Add(duration, MediaRounding.NearestAwayFromZero) : start;
        bool anyRange = false;
        foreach (TextModifierBox box in sample.Modifiers)
        {
            if (box.TryGetStyles(out IReadOnlyList<TextStyleRecord> styles))
            {
                foreach (TextStyleRecord r in styles)
                {
                    SubtitleSpan? span = StyleSpan(r, sample, map, text.Length);
                    if (span is not null)
                    {
                        spans.Add(span);
                    }
                }
            }
            else if (box.TryGetHighlight(out int hs, out int he))
            {
                anyRange = true;
                (int s, int l) = Range(sample, map, text.Length, hs, he);
                spans.Add(new SubtitleSpan(s, l)
                {
                    Style = highlightColor is { } c ? new TextStyle { Background = Tx3gMapping.ColorOf(c) } : null,
                    Raw = new SubtitleExtra(Tx3gMapping.Format, "highlight", highlightColor is null ? "inverse" : "color"),
                });
            }
            else if (box.TryGetKaraoke(out uint kStart, out IReadOnlyList<KaraokeStep> steps))
            {
                anyRange = true;
                long previous = kStart;
                foreach (KaraokeStep step in steps)
                {
                    (int s, int l) = Range(sample, map, text.Length, step.StartChar, step.EndChar);
                    spans.Add(new SubtitleSpan(s, l)
                    {
                        WordStart = start.Add(new MediaTime(previous, _track.Timescale), MediaRounding.NearestAwayFromZero),
                        WordEnd = start.Add(new MediaTime(step.EndTime, _track.Timescale), MediaRounding.NearestAwayFromZero),
                    });
                    previous = step.EndTime;
                }
            }
            else if (box.TryGetLink(out TimedTextLink link))
            {
                (int s, int l) = Range(sample, map, text.Length, link.StartChar, link.EndChar);
                spans.Add(new SubtitleSpan(s, l) { Raw = new SubtitleExtra(Tx3gMapping.Format, "href", link.Url + "\t" + link.Alt) });
            }
            else if (box.TryGetBlink(out int bs, out int be))
            {
                (int s, int l) = Range(sample, map, text.Length, bs, be);
                spans.Add(new SubtitleSpan(s, l) { Raw = new SubtitleExtra(Tx3gMapping.Format, "blink", string.Empty) });
            }
            else if (box.TryGetTextBox(out TextBoxRecord tb))
            {
                extras.Add(new SubtitleExtra(Tx3gMapping.Format, "tbox", string.Create(CultureInfo.InvariantCulture, $"{tb.Top},{tb.Left},{tb.Bottom},{tb.Right}")));
            }
            else if (box.TryGetScrollDelay(out uint delay))
            {
                extras.Add(new SubtitleExtra(Tx3gMapping.Format, "dlay", delay.ToString(CultureInfo.InvariantCulture)));
            }
            else if (box.TryGetWrap(out byte wrap))
            {
                extras.Add(new SubtitleExtra(Tx3gMapping.Format, "twrp", wrap.ToString(CultureInfo.InvariantCulture)));
            }
            else if (box.TryGetDisparity(out short disp))
            {
                extras.Add(new SubtitleExtra(Tx3gMapping.Format, "disp", disp.ToString(CultureInfo.InvariantCulture)));
            }
            else if (box.Type != "hclr")
            {
                // §5.17: "any unrecognised box found in the text sample should be skipped"; kept here so writing tx3g again gives it back.
                extras.Add(new SubtitleExtra(Tx3gMapping.Format, "box:" + box.Type, Convert.ToBase64String(box.Payload.Span)));
            }
        }

        if (highlightColor is { } hcolor && !anyRange)
        {
            extras.Add(new SubtitleExtra(Tx3gMapping.Format, "hclr", hcolor.ToString("x8", CultureInfo.InvariantCulture)));
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        _cues.Add(new SubtitleCue
        {
            Start = start,
            End = end,
            Text = text,
            Spans = SubtitleList.From(spans),
            Placement = _placement,
            Extras = SubtitleList.From(extras),
        });
    }

    private static (int Start, int Length) Range(TimedTextSample sample, int[] map, int textLength, int startChar, int endChar)
    {
        int s = Map(map, sample.ToTextIndex(startChar), textLength);
        int e = Map(map, sample.ToTextIndex(endChar), textLength);
        return (s, Math.Max(0, e - s));
    }

    private static int Map(int[] map, int originalIndex, int newLength) => originalIndex < map.Length ? Math.Min(map[originalIndex], newLength) : newLength;

    private SubtitleSpan? StyleSpan(in TextStyleRecord r, TimedTextSample sample, int[] map, int textLength)
    {
        (int start, int length) = Range(sample, map, textLength, r.StartChar, r.EndChar);
        if (length == 0)
        {
            return null;
        }

        TextFaceStyle changed = r.Face ^ _default.Face;
        TextStyle style = new()
        {
            Bold = (changed & TextFaceStyle.Bold) != 0 ? (r.Face & TextFaceStyle.Bold) != 0 : null,
            Italic = (changed & TextFaceStyle.Italic) != 0 ? (r.Face & TextFaceStyle.Italic) != 0 : null,
            Underline = (changed & TextFaceStyle.Underline) != 0 ? (r.Face & TextFaceStyle.Underline) != 0 : null,
            Color = r.ColorRgba != _default.ColorRgba ? Tx3gMapping.ColorOf(r.ColorRgba) : null,
            FontSize = r.FontSize != _default.FontSize && r.FontSize > 0 ? new SubtitleLength(r.FontSize, SubtitleLengthUnit.Pixel) : null,
            FontFamily = r.FontId != _default.FontId && _fonts.TryGetValue(r.FontId, out string? family) ? family : null,
        };
        return style.IsEmpty ? null : new SubtitleSpan(start, length) { Style = style };
    }

    public SubtitleDocument Finish()
    {
        _cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        List<SubtitleExtra> docExtras = [];
        List<SubtitleStyle> styles = [];
        if (_entry is not null)
        {
            // The whole sample entry, so that writing tx3g again gives the same justification, background, box, fonts and flags back.
            docExtras.Add(new SubtitleExtra(Tx3gMapping.Format, "sample-entry", Convert.ToBase64String(_entry.ToConfiguration())));
            TextStyle baseStyle = new()
            {
                Bold = (_default.Face & TextFaceStyle.Bold) != 0 ? true : null,
                Italic = (_default.Face & TextFaceStyle.Italic) != 0 ? true : null,
                Underline = (_default.Face & TextFaceStyle.Underline) != 0 ? true : null,
                Color = _default.ColorRgba != 0xFFFFFFFF ? Tx3gMapping.ColorOf(_default.ColorRgba) : null,
            };
            if (!baseStyle.IsEmpty)
            {
                styles.Add(new SubtitleStyle("Default") { Text = baseStyle });
            }
        }

        return new SubtitleDocument
        {
            Format = Tx3gMapping.Format,
            Language = _track.Language is { Length: > 0 } l && l != "und" ? l : null,
            Cues = SubtitleList.From(_cues),
            Styles = SubtitleList.From(styles),
            Extras = SubtitleList.From(docExtras),
        };
    }
}

internal sealed class QuickTimeTextDecoder : ITextTrackDecoder
{
    private readonly MediaTrack _track;
    private readonly DefectLog _defects;
    private readonly List<SubtitleCue> _cues = [];

    public QuickTimeTextDecoder(MediaTrack track, DefectLog defects)
    {
        _track = track;
        _defects = defects;
    }

    public void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        // QuickTime File Format, "Text Sample Data": a 16-bit length, the text, then atoms. The atoms (TextEdit style scrap, font table, highlight,
        // drop shadow) are kept as extras; the QuickTime style scrap is not mapped to spans.
        if (!TimedTextSample.TryParse(data, out TimedTextSample? sample) || sample is null)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, info.SourceRange.Offset, "A QuickTime text sample's text length is larger than the sample; the sample is skipped.", "text.qt.sample");
            return;
        }

        if (sample.Text.Length == 0)
        {
            return;
        }

        string text = Tx3gMapping.NormalizeNewlines(sample.Text, out _);
        List<SubtitleExtra> extras = [];
        foreach (TextModifierBox box in sample.Modifiers)
        {
            extras.Add(new SubtitleExtra("qttext", "atom:" + box.Type, Convert.ToBase64String(box.Payload.Span)));
        }

        _cues.Add(new SubtitleCue
        {
            Start = start,
            End = duration.IsKnown ? start.Add(duration, MediaRounding.NearestAwayFromZero) : start,
            Text = text,
            Extras = SubtitleList.From(extras),
        });
    }

    public SubtitleDocument Finish()
    {
        _cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return new SubtitleDocument
        {
            Format = "qttext",
            Language = _track.Language is { Length: > 0 } l && l != "und" ? l : null,
            Cues = SubtitleList.From(_cues),
        };
    }
}

using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>A stretch of the timeline with the same set of cues showing.</summary>
/// <param name="Start">Where the stretch starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Active">The cues showing during it, in start order; empty for a gap.</param>
internal readonly record struct TextSegment(MediaTime Start, MediaTime End, IReadOnlyList<SubtitleCue> Active);

internal static class TextSegmenter
{
    /// <summary>
    /// Cuts the timeline at every cue start and end. Containers whose samples cannot overlap (tx3g, WebVTT in ISO) store one sample per segment;
    /// a cue that spans several segments is repeated in each.
    /// </summary>
    /// <param name="cues">Cues sorted by start, all with an end after their start.</param>
    /// <param name="overlapped">Set when two cues are showing together at some point.</param>
    /// <returns>The segments in time order, gaps between cues included as segments with no active cue.</returns>
    public static List<TextSegment> Split(List<SubtitleCue> cues, out bool overlapped)
    {
        overlapped = false;
        List<TextSegment> result = [];
        if (cues.Count == 0)
        {
            return result;
        }

        List<MediaTime> times = new(cues.Count * 2);
        foreach (SubtitleCue c in cues)
        {
            times.Add(c.Start);
            times.Add(c.End);
        }

        times.Sort();
        List<MediaTime> distinct = new(times.Count);
        foreach (MediaTime t in times)
        {
            if (distinct.Count == 0 || distinct[^1] != t)
            {
                distinct.Add(t);
            }
        }

        List<SubtitleCue> active = [];
        int next = 0;
        for (int i = 0; i + 1 < distinct.Count; i++)
        {
            MediaTime t = distinct[i];
            active.RemoveAll(c => c.End <= t);
            while (next < cues.Count && cues[next].Start <= t)
            {
                active.Add(cues[next++]);
            }

            if (active.Count > 1)
            {
                overlapped = true;
            }

            result.Add(new TextSegment(t, distinct[i + 1], active.ToArray()));
        }

        return result;
    }
}

internal sealed class LossCounter
{
    private readonly Dictionary<string, (string Detail, int Count)> _items = [];
    private readonly List<string> _order = [];

    public void Add(string feature, string detail)
    {
        if (_items.TryGetValue(feature, out (string Detail, int Count) v))
        {
            _items[feature] = (v.Detail, v.Count + 1);
        }
        else
        {
            _items[feature] = (detail, 1);
            _order.Add(feature);
        }
    }

    public void AddTrackWide(string feature, string detail)
    {
        if (!_items.ContainsKey(feature))
        {
            _items[feature] = (detail, 0);
            _order.Add(feature);
        }
    }

    public List<TextTrackLoss> ToList()
    {
        List<TextTrackLoss> l = new(_order.Count);
        foreach (string f in _order)
        {
            l.Add(new TextTrackLoss(f, _items[f].Detail, _items[f].Count));
        }

        return l;
    }
}

internal static class Tx3gEncoder
{
    private const int MaxChar = ushort.MaxValue;

    public static TextTrackEncoding Encode(SubtitleDocument doc, TextTrackOptions options)
    {
        List<SubtitleCue> cues = TextTrackTimes.Playable(doc, out int dropped);
        LossCounter losses = new();
        List<string> notes = [];
        for (int i = 0; i < dropped; i++)
        {
            losses.Add("untimed-cue", "Cues without a start time, or whose end is not after their start, have no place in a timed text track and were left out.");
        }

        TimedTextSampleEntry entry = BuildEntry(doc, cues, out Dictionary<string, int> fontIds);
        TextStyleRecord def = entry.DefaultStyle;
        long timescale = options.Timescale;
        bool inexact = false;
        List<TextTrackPacket> packets = [];
        List<TextSegment> segments = TextSegmenter.Split(cues, out bool overlapped);
        if (overlapped)
        {
            losses.AddTrackWide("overlap", "A timed text sample shows one block of text, so cues that show together were merged into one sample per stretch of time, their lines stacked in start order.");
        }

        long lastEnd = 0;
        long cursor = 0;
        int gaps = 0;
        foreach (TextSegment seg in segments)
        {
            long s = TextTrackTimes.ToTicks(seg.Start, timescale, ref inexact);
            long e = TextTrackTimes.ToTicks(seg.End, timescale, ref inexact);
            if (e <= s || s < cursor)
            {
                // Two segments rounded onto the same tick: the later one starts where the earlier one ended.
                s = Math.Max(s, cursor);
                if (e <= s)
                {
                    continue;
                }
            }

            if (packets.Count == 0 && s > 0)
            {
                // The oracles start the track at zero with an empty sample, so the timeline of the text track begins with the movie's.
                packets.Add(Packet(0, s, new TimedTextSample(string.Empty).ToArray()));
                gaps++;
            }
            else if (s > cursor)
            {
                packets.Add(Packet(cursor, s - cursor, new TimedTextSample(string.Empty).ToArray()));
                gaps++;
            }

            byte[] data = seg.Active.Count == 0 ? new TimedTextSample(string.Empty).ToArray() : Sample(seg, s, entry, def, fontIds, timescale, losses, doc);
            if (seg.Active.Count == 0)
            {
                gaps++;
            }

            packets.Add(Packet(s, e - s, data));
            cursor = e;
            lastEnd = e;
        }

        if (gaps > 0)
        {
            notes.Add($"{gaps} empty samples were written for the gaps between cues, as timed text expects.");
        }

        if (inexact)
        {
            notes.Add($"Cue times were rounded to the track timescale of {timescale} ticks per second.");
        }

        MediaTrack track = new()
        {
            Index = 0,
            Id = options.TrackId,
            Kind = TrackKind.Subtitle,
            Codec = CodecId.TimedText,
            CodecTag = TimedTextSampleEntry.Code,
            Configuration = new CodecConfiguration(entry.ToConfiguration(), TimedTextSampleEntry.Code),
            Timescale = timescale,
            Duration = new MediaTime(lastEnd, timescale),
            Language = options.Language ?? doc.Language,
            Name = options.Name,
            Disposition = options.Disposition,
            PacketCount = packets.Count,
        };
        return new TextTrackEncoding(track, packets, losses.ToList(), notes);

        TextTrackPacket Packet(long pts, long duration, byte[] bytes) => new(
            new PacketInfo(0, new MediaTime(pts, timescale), new MediaTime(pts, timescale), new MediaTime(duration, timescale), PacketAttributes.Keyframe, ByteRange.None, bytes.Length),
            bytes);
    }

    // The sample entry: the one a tx3g track was read with (lossless same-format round trip), else a standard one built from the document's default style.
    private static TimedTextSampleEntry BuildEntry(SubtitleDocument doc, List<SubtitleCue> cues, out Dictionary<string, int> fontIds)
    {
        TimedTextSampleEntry? baseEntry = null;
        foreach (SubtitleExtra x in doc.Extras)
        {
            if (x.Format == Tx3gMapping.Format && x.Key == "sample-entry")
            {
                try
                {
                    if (TimedTextSampleEntry.TryParse(Convert.FromBase64String(x.Value), out TimedTextSampleEntry? parsed) && parsed is not null)
                    {
                        baseEntry = parsed;
                    }
                }
                catch (FormatException)
                {
                    // An extra that is not base64 is ignored: the document was not read from a tx3g track.
                }

                break;
            }
        }

        List<TimedTextFont> fonts = [];
        TextStyleRecord style;
        if (baseEntry is not null)
        {
            fonts.AddRange(baseEntry.Fonts);
            style = baseEntry.DefaultStyle;
        }
        else
        {
            TextFaceStyle face = TextFaceStyle.None;
            uint color = 0xFFFFFFFF;
            string family = "Serif";
            foreach (SubtitleStyle st in doc.Styles)
            {
                if (st.Name == "Default")
                {
                    face |= st.Text.Bold == true ? TextFaceStyle.Bold : 0;
                    face |= st.Text.Italic == true ? TextFaceStyle.Italic : 0;
                    face |= st.Text.Underline == true ? TextFaceStyle.Underline : 0;
                    color = st.Text.Color is { } c ? Tx3gMapping.Rgba(c) : color;
                    family = st.Text.FontFamily ?? family;
                    break;
                }
            }

            fonts.Add(new TimedTextFont(1, family));
            style = new TextStyleRecord(0, 0, 1, face, 18, color);
        }

        fontIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int maxId = 0;
        foreach (TimedTextFont f in fonts)
        {
            fontIds.TryAdd(f.Name, f.FontId);
            maxId = Math.Max(maxId, f.FontId);
        }

        foreach (SubtitleCue cue in cues)
        {
            foreach (SubtitleSpan sp in cue.Spans)
            {
                if (sp.Style?.FontFamily is { Length: > 0 } fam && fontIds.TryAdd(fam, maxId + 1))
                {
                    fonts.Add(new TimedTextFont(++maxId, fam));
                }
            }
        }

        if (baseEntry is not null)
        {
            return new TimedTextSampleEntry
            {
                DisplayFlags = baseEntry.DisplayFlags,
                HorizontalJustification = baseEntry.HorizontalJustification,
                VerticalJustification = baseEntry.VerticalJustification,
                BackgroundRgba = baseEntry.BackgroundRgba,
                DefaultTextBox = baseEntry.DefaultTextBox,
                DefaultStyle = style,
                Fonts = fonts,
                OtherBoxes = baseEntry.OtherBoxes,
            };
        }

        // Centred at the bottom, which is where subtitles go; the cues' own alignment decides when they agree on another.
        (sbyte h, sbyte v) = CommonJustification(cues);
        return new TimedTextSampleEntry { HorizontalJustification = h, VerticalJustification = v, DefaultStyle = style, Fonts = fonts };
    }

    private static (sbyte H, sbyte V) CommonJustification(List<SubtitleCue> cues)
    {
        Dictionary<(sbyte, sbyte), int> counts = [];
        foreach (SubtitleCue c in cues)
        {
            if (c.Placement is { } p && (p.Align != SubtitleAlign.Unspecified || p.VerticalAlign != SubtitleVerticalAlign.Unspecified))
            {
                sbyte h = p.Align switch { SubtitleAlign.Left or SubtitleAlign.Start => 0, SubtitleAlign.Right or SubtitleAlign.End => -1, _ => 1 };
                sbyte v = p.VerticalAlign switch { SubtitleVerticalAlign.Top => 0, SubtitleVerticalAlign.Middle => 1, _ => -1 };
                counts[(h, v)] = counts.GetValueOrDefault((h, v)) + 1;
            }
        }

        (sbyte, sbyte) best = (1, -1);
        int bestCount = 0;
        foreach (KeyValuePair<(sbyte, sbyte), int> kv in counts)
        {
            if (kv.Value > bestCount)
            {
                best = kv.Key;
                bestCount = kv.Value;
            }
        }

        return best;
    }

    private static byte[] Sample(TextSegment seg, long startTicks, TimedTextSampleEntry entry, TextStyleRecord def, Dictionary<string, int> fontIds, long timescale, LossCounter losses, SubtitleDocument doc)
    {
        // One text block per sample. Cues showing together are stacked, one line group after the other.
        StringBuilder text = new();
        List<(SubtitleCue Cue, int Base)> parts = [];
        foreach (SubtitleCue c in seg.Active)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            parts.Add((c, text.Length));
            text.Append(c.Text);
        }

        TimedTextSample positions = new(text.ToString());
        List<TextStyleRecord> records = [];
        List<TextModifierBox> boxes = [];
        foreach ((SubtitleCue cue, int baseIndex) in parts)
        {
            NoteLosses(cue, entry, losses);
            foreach ((int s, int e, TextStyle style) in Runs(cue, doc))
            {
                int cs = positions.ToCharOffset(baseIndex + s);
                int ce = positions.ToCharOffset(baseIndex + e);
                if (ce > MaxChar || ce <= cs)
                {
                    continue;
                }

                TextStyleRecord r = RecordFor(style, def, fontIds, cs, ce);
                if (r.Face != def.Face || r.ColorRgba != def.ColorRgba || r.FontSize != def.FontSize || r.FontId != def.FontId)
                {
                    records.Add(r);
                }
            }
        }

        if (records.Count > 0)
        {
            boxes.Add(TextModifierBox.CreateStyles(records));
        }

        if (parts.Count == 1)
        {
            SubtitleCue only = parts[0].Cue;
            AddRangeBoxes(only, positions, boxes);
            AddKaraoke(only, startTicks, timescale, positions, boxes, losses);
            AddExtras(only, boxes);
        }

        return new TimedTextSample(positions.Text, boxes).ToArray();
    }

    // The effective style of each stretch of the cue's text: the document style the cue names, then its spans from outermost to innermost.
    private static List<(int Start, int End, TextStyle Style)> Runs(SubtitleCue cue, SubtitleDocument doc)
    {
        TextStyle? named = null;
        if (cue.StyleName is { } name)
        {
            foreach (SubtitleStyle st in doc.Styles)
            {
                if (st.Name == name)
                {
                    named = st.Text;
                    break;
                }
            }
        }

        SortedSet<int> cuts = [0, cue.Text.Length];
        foreach (SubtitleSpan sp in cue.Spans)
        {
            if (sp.Style is { IsEmpty: false } && sp.Length > 0)
            {
                cuts.Add(Math.Clamp(sp.Start, 0, cue.Text.Length));
                cuts.Add(Math.Clamp(sp.End, 0, cue.Text.Length));
            }
        }

        List<(int, int, TextStyle)> runs = [];
        int[] points = [.. cuts];
        for (int i = 0; i + 1 < points.Length; i++)
        {
            TextStyle merged = named ?? new TextStyle();
            foreach (SubtitleSpan sp in cue.Spans)
            {
                if (sp.Style is { IsEmpty: false } st && sp.Length > 0 && sp.Start <= points[i] && sp.End >= points[i + 1])
                {
                    merged = Merge(merged, st);
                }
            }

            if (!merged.IsEmpty)
            {
                runs.Add((points[i], points[i + 1], merged));
            }
        }

        return runs;
    }

    private static TextStyle Merge(TextStyle a, TextStyle b) => a with
    {
        Bold = b.Bold ?? a.Bold,
        Italic = b.Italic ?? a.Italic,
        Underline = b.Underline ?? a.Underline,
        Strike = b.Strike ?? a.Strike,
        Color = b.Color ?? a.Color,
        Background = b.Background ?? a.Background,
        FontFamily = b.FontFamily ?? a.FontFamily,
        FontSize = b.FontSize ?? a.FontSize,
        Classes = b.Classes ?? a.Classes,
    };

    private static TextStyleRecord RecordFor(TextStyle style, TextStyleRecord def, Dictionary<string, int> fontIds, int start, int end)
    {
        TextFaceStyle face = def.Face;
        face = style.Bold switch { true => face | TextFaceStyle.Bold, false => face & ~TextFaceStyle.Bold, _ => face };
        face = style.Italic switch { true => face | TextFaceStyle.Italic, false => face & ~TextFaceStyle.Italic, _ => face };
        face = style.Underline switch { true => face | TextFaceStyle.Underline, false => face & ~TextFaceStyle.Underline, _ => face };
        int size = def.FontSize;
        if (style.FontSize is { } fs)
        {
            double px = fs.Unit switch
            {
                SubtitleLengthUnit.Percent => def.FontSize * fs.Value / 100.0,
                SubtitleLengthUnit.Em => def.FontSize * fs.Value,
                _ => fs.Value,
            };
            size = (int)Math.Clamp(Math.Round(px), 1, 255);
        }

        int font = style.FontFamily is { } fam && fontIds.TryGetValue(fam, out int id) ? id : def.FontId;
        return new TextStyleRecord(start, end, font, face, size, style.Color is { } c ? Tx3gMapping.Rgba(c) : def.ColorRgba);
    }

    private static void AddRangeBoxes(SubtitleCue cue, TimedTextSample positions, List<TextModifierBox> boxes)
    {
        foreach (SubtitleSpan sp in cue.Spans)
        {
            if (sp.Raw is not { Format: Tx3gMapping.Format } raw || sp.Length <= 0)
            {
                continue;
            }

            int cs = positions.ToCharOffset(sp.Start), ce = positions.ToCharOffset(sp.End);
            switch (raw.Key)
            {
                case "href":
                    {
                        string[] parts = raw.Value.Split('\t', 2);
                        boxes.Add(TextModifierBox.CreateLink(new TimedTextLink(cs, ce, parts[0], parts.Length > 1 ? parts[1] : string.Empty)));
                        break;
                    }

                case "blink":
                    boxes.Add(TextModifierBox.CreateBlink(cs, ce));
                    break;
                case "highlight":
                    boxes.Add(TextModifierBox.CreateHighlight(cs, ce));
                    break;
                default:
                    break;
            }
        }
    }

    private static void AddKaraoke(SubtitleCue cue, long startTicks, long timescale, TimedTextSample positions, List<TextModifierBox> boxes, LossCounter losses)
    {
        List<SubtitleSpan> words = [];
        foreach (SubtitleSpan sp in cue.Spans)
        {
            if (sp.WordStart is { IsKnown: true } && sp.Length >= 0)
            {
                words.Add(sp);
            }
        }

        if (words.Count == 0)
        {
            return;
        }

        words.Sort(static (a, b) => a.WordStart!.Value.CompareTo(b.WordStart!.Value));
        bool inexact = false;
        List<KaraokeStep> steps = [];
        long first = Math.Max(0, TextTrackTimes.ToTicks(words[0].WordStart!.Value, timescale, ref inexact) - startTicks);
        long cursor = first;
        for (int i = 0; i < words.Count; i++)
        {
            SubtitleSpan w = words[i];
            long ws = Math.Max(cursor, TextTrackTimes.ToTicks(w.WordStart!.Value, timescale, ref inexact) - startTicks);
            long we = w.WordEnd is { IsKnown: true } e ? TextTrackTimes.ToTicks(e, timescale, ref inexact) - startTicks
                : i + 1 < words.Count ? TextTrackTimes.ToTicks(words[i + 1].WordStart!.Value, timescale, ref inexact) - startTicks : ws;
            we = Math.Max(we, ws);
            int cs = positions.ToCharOffset(w.Start), ce = positions.ToCharOffset(w.End);
            if (ws > cursor)
            {
                steps.Add(new KaraokeStep((uint)Math.Min(ws, uint.MaxValue), cs, cs)); // a pause: equal offsets (§5.17.1.3)
            }

            steps.Add(new KaraokeStep((uint)Math.Min(we, uint.MaxValue), cs, ce));
            cursor = we;
        }

        boxes.Add(TextModifierBox.CreateKaraoke((uint)Math.Min(first, uint.MaxValue), steps));
        if (words.Count > 0)
        {
            losses.AddTrackWide("karaoke-colour", "Karaoke timing is written as a dynamic highlight; the highlight colour is the player's default because the cue model does not state one.");
        }
    }

    private static void AddExtras(SubtitleCue cue, List<TextModifierBox> boxes)
    {
        foreach (SubtitleExtra x in cue.Extras)
        {
            if (x.Format != Tx3gMapping.Format)
            {
                continue;
            }

            switch (x.Key)
            {
                case "tbox":
                    {
                        string[] p = x.Value.Split(',');
                        if (p.Length == 4 && short.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out short t) && short.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out short l)
                            && short.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out short b) && short.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out short r))
                        {
                            boxes.Add(TextModifierBox.CreateTextBox(new TextBoxRecord(t, l, b, r)));
                        }

                        break;
                    }

                case "dlay" when uint.TryParse(x.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint delay):
                    boxes.Add(TextModifierBox.CreateScrollDelay(delay));
                    break;
                case "twrp" when byte.TryParse(x.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte wrap):
                    boxes.Add(TextModifierBox.CreateWrap(wrap));
                    break;
                case "disp" when short.TryParse(x.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out short disp):
                    boxes.Add(TextModifierBox.CreateDisparity(disp));
                    break;
                case "hclr" when uint.TryParse(x.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hc):
                    boxes.Add(TextModifierBox.CreateHighlightColor(hc));
                    break;
                default:
                    if (x.Key.StartsWith("box:", StringComparison.Ordinal) && x.Key.Length == 8)
                    {
                        try
                        {
                            boxes.Add(new TextModifierBox(x.Key[4..], Convert.FromBase64String(x.Value)));
                        }
                        catch (FormatException)
                        {
                            // Not a box this layer kept: ignored.
                        }
                    }

                    break;
            }
        }
    }

    private static void NoteLosses(SubtitleCue cue, TimedTextSampleEntry entry, LossCounter losses)
    {
        if (cue.Placement is { } p && (p.Line is not null || p.Position is not null || p.Size is not null || p.Region is not null || p.Row is not null || p.Column is not null || p.Writing != SubtitleWriting.Horizontal
            || !SameJustification(p, entry)))
        {
            losses.Add("placement", "Timed text places text by the sample entry's justification and an optional text box; positions, sizes, regions and per-cue alignment were not carried.");
        }

        if (cue.Speaker is not null)
        {
            losses.Add("speaker", "Timed text has no speaker field.");
        }

        if (cue.Language is not null)
        {
            losses.Add("language", "Timed text states a language for the whole track only.");
        }

        if (cue.Id is not null)
        {
            losses.Add("identifier", "Timed text cues have no identifier.");
        }

        if (cue.Mode != CaptionMode.None)
        {
            losses.Add("caption-mode", "Timed text cannot say how a caption is revealed (pop-on, roll-up, paint-on).");
        }

        bool style = false, voice = false, ruby = false;
        foreach (SubtitleSpan sp in cue.Spans)
        {
            style |= sp.Style is { } s && (s.Strike is not null || s.Background is not null || s.Classes is not null) && sp.Raw is null;
            voice |= sp.Voice is not null || sp.Language is not null;
            ruby |= sp.Ruby is not null;
        }

        if (style)
        {
            losses.Add("style", "Timed text styles carry bold, italic, underline, colour, size and font; strike-through, background colour and style classes were dropped.");
        }

        if (voice)
        {
            losses.Add("voice", "Voices and languages of stretches inside a cue have no place in timed text.");
        }

        if (ruby)
        {
            losses.Add("ruby", "Timed text has no ruby annotations.");
        }

        foreach (SubtitleExtra x in cue.Extras)
        {
            if (x.Format != Tx3gMapping.Format)
            {
                losses.Add("format-specific", "Information that belongs to another subtitle format was dropped.");
                break;
            }
        }
    }

    private static bool SameJustification(CuePlacement p, TimedTextSampleEntry entry)
    {
        sbyte h = p.Align switch { SubtitleAlign.Left or SubtitleAlign.Start => 0, SubtitleAlign.Right or SubtitleAlign.End => -1, SubtitleAlign.Center => 1, _ => entry.HorizontalJustification };
        sbyte v = p.VerticalAlign switch { SubtitleVerticalAlign.Top => 0, SubtitleVerticalAlign.Middle => 1, SubtitleVerticalAlign.Bottom => -1, _ => entry.VerticalJustification };
        return h == entry.HorizontalJustification && v == entry.VerticalJustification;
    }
}

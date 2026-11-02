using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Captions;

// SubStation Alpha (.ssa) and Advanced SubStation Alpha (.ass). There is no formal standard; this follows the widely published
// description of the sections ([Script Info], [V4+ Styles], [Events]), the Format-line driven field order, and the override tags.
internal sealed class AssFormat : SubtitleFormat
{
    internal static readonly AssFormat Instance = new();

    private static readonly string[] Exts = [".ass", ".ssa"];

    // The columns the writer emits, in order (ASS v4.00+).
    internal static readonly string[] StyleColumns =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "OutlineColour", "BackColour", "Bold", "Italic", "Underline",
        "StrikeOut", "ScaleX", "ScaleY", "Spacing", "Angle", "BorderStyle", "Outline", "Shadow", "Alignment", "MarginL", "MarginR",
        "MarginV", "Encoding",
    ];

    private static readonly string[] StyleDefaults =
    [
        "Default", "Arial", "20", "&H00FFFFFF", "&H000000FF", "&H00000000", "&H00000000", "0", "0", "0",
        "0", "100", "100", "0", "0", "1", "2", "2", "2", "10", "10", "10", "1",
    ];

    private static readonly string[] LegacyStyleColumns =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "TertiaryColour", "BackColour", "Bold", "Italic", "BorderStyle",
        "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV", "AlphaLevel", "Encoding",
    ];

    private static readonly string[] EventColumns = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    private AssFormat()
    {
    }

    public override string Name => "ass";

    public override string DisplayName => "Advanced SubStation Alpha (ASS/SSA)";

    public override IReadOnlyList<string> Extensions => Exts;

    public override SubtitleFeatures Features =>
        SubtitleFeatures.Bold | SubtitleFeatures.Italic | SubtitleFeatures.Underline | SubtitleFeatures.Strike | SubtitleFeatures.Color
        | SubtitleFeatures.Font | SubtitleFeatures.WordTiming | SubtitleFeatures.Voice | SubtitleFeatures.Alignment | SubtitleFeatures.Position
        | SubtitleFeatures.Styles | SubtitleFeatures.Header | SubtitleFeatures.Extras;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        head = head.TrimStart('\uFEFF').TrimStart();
        int info = head.IndexOf("[Script Info]", StringComparison.OrdinalIgnoreCase);
        if (info >= 0 && info < 400)
        {
            return 95;
        }

        return head.IndexOf("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) >= 0 || head.IndexOf("[V4 Styles]", StringComparison.OrdinalIgnoreCase) >= 0 ? 80 : 0;
    }

    internal override SubtitleDocument ReadCore(SubtitleSource src)
    {
        string text = src.Text;
        var info = new List<SubtitleExtra>();
        var styles = new List<SubtitleStyle>();
        var cues = new List<SubtitleCue>();
        var docExtras = new List<SubtitleExtra>();
        var pending = new List<SubtitleExtra>();
        string? title = null;
        int playW = 0;
        int playH = 0;
        string section = "";
        string sec = "";
        bool legacyStyles = false;
        string[] styleFormat = StyleColumns;
        string[] eventFormat = EventColumns;
        var map = new DialogueMap(EventColumns);
        var scratch = new StringBuilder();
        var otherSection = new StringBuilder();
        string? otherName = null;
        bool sawSection = false;

        void FlushOther()
        {
            if (otherName is not null)
            {
                docExtras.Add(new SubtitleExtra("ass", "section:" + otherName, otherSection.ToString()));
                otherSection.Clear();
                otherName = null;
            }
        }

        int pos = 0;
        while (src.NextLine(ref pos, out int start, out int length))
        {
            ReadOnlySpan<char> line = text.AsSpan(start, length);
            if (start == 0 && line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
                start++;
            }

            ReadOnlySpan<char> trimmed = line.Trim();
            if (trimmed.IsEmpty)
            {
                if (otherName is not null)
                {
                    otherSection.Append('\n');
                }

                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                FlushOther();
                section = trimmed[1..^1].ToString();
                sawSection = true;
                string lower = section.ToLowerInvariant();
                sec = lower;
                if (lower is not ("script info" or "v4 styles" or "v4+ styles" or "events"))
                {
                    otherName = section;
                }

                if (lower == "v4 styles")
                {
                    legacyStyles = true;
                    styleFormat = LegacyStyleColumns;
                }

                continue;
            }

            if (!sawSection)
            {
                src.Report(DefectKind.UnaccountedBytes, DefectSeverity.Info, start, "Text before the first section; ignored.", "subtitles.ass.preamble");
                continue;
            }

            if (otherName is not null)
            {
                otherSection.Append(line).Append('\n');
                continue;
            }

            if (sec == "script info")
            {
                if (trimmed[0] == ';' || trimmed[0] == '!')
                {
                    info.Add(new SubtitleExtra("ass", "info", trimmed.ToString()));
                    continue;
                }

                int colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, start, "Script Info line without a colon; kept.", "subtitles.ass.info");
                    info.Add(new SubtitleExtra("ass", "info", trimmed.ToString()));
                    continue;
                }

                string key = trimmed[..colon].Trim().ToString();
                string value = trimmed[(colon + 1)..].Trim().ToString();
                if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
                {
                    title = value;
                }
                else if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int px))
                {
                    playW = px;
                }
                else if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int py))
                {
                    playH = py;
                }
                else if (key.Equals("ScriptType", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Equals("v4.00", StringComparison.OrdinalIgnoreCase))
                    {
                        legacyStyles = true;
                    }
                }
                else
                {
                    info.Add(new SubtitleExtra("ass", "info", trimmed.ToString()));
                }

                continue;
            }

            int c = trimmed.IndexOf(':');
            if (c <= 0)
            {
                src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, start, "Line without 'Name:' prefix skipped.", "subtitles.ass.line");
                continue;
            }

            ReadOnlySpan<char> name = trimmed[..c];
            ReadOnlySpan<char> rest = trimmed[(c + 1)..].TrimStart();
            if (sec is "v4+ styles" or "v4 styles")
            {
                if (name.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    styleFormat = SplitFields(rest, int.MaxValue);
                    continue;
                }

                if (name.Equals("Style", StringComparison.OrdinalIgnoreCase))
                {
                    styles.Add(ParseStyle(src, SplitFields(rest, styleFormat.Length), styleFormat, legacyStyles, start));
                }

                continue;
            }

            // [Events]
            if (name.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                eventFormat = SplitFields(rest, int.MaxValue);
                map = new DialogueMap(eventFormat);
                continue;
            }

            if (!name.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                // Comment: lines (disabled dialogue) and Picture/Sound/Movie/Command lines are kept beside the next cue.
                pending.Add(new SubtitleExtra("ass", "line-before", trimmed.ToString()));
                continue;
            }

            src.CheckCue(cues.Count + 1, start);
            SubtitleCue? cue = ParseDialogue(src, rest, map, start, pending, playW > 0 ? playW : 384, playH > 0 ? playH : 288, scratch);
            if (cue is not null)
            {
                pending.Clear();
                cues.Add(cue);
            }
        }

        FlushOther();
        foreach (SubtitleExtra p in pending)
        {
            docExtras.Add(new SubtitleExtra("ass", "line-end", p.Value));
        }

        var all = new List<SubtitleExtra>(info);
        all.AddRange(docExtras);
        return new SubtitleDocument
        {
            Title = title,
            ReferenceWidth = playW,
            ReferenceHeight = playH,
            Styles = SubtitleList.From(styles),
            Cues = SubtitleList.From(cues),
            Extras = SubtitleList.From(all),
            NewLine = src.NewLine,
        };
    }

    private static string[] SplitFields(ReadOnlySpan<char> s, int max)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length && parts.Count < max - 1; i++)
        {
            if (s[i] == ',')
            {
                parts.Add(s[start..i].Trim().ToString());
                start = i + 1;
            }
        }

        parts.Add(max == int.MaxValue ? s[start..].Trim().ToString() : s[start..].TrimStart().ToString());
        return parts.ToArray();
    }

    private static string Field(string[] values, string[] columns, string name)
    {
        for (int i = 0; i < columns.Length && i < values.Length; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return values[i];
            }
        }

        return "";
    }

    private static SubtitleStyle ParseStyle(SubtitleSource src, string[] values, string[] columns, bool legacy, int index)
    {
        if (values.Length < columns.Length)
        {
            src.Report(DefectKind.Inconsistent, DefectSeverity.Warning, index, $"Style has {values.Length} fields where {columns.Length} were declared.", "subtitles.ass.style-fields");
        }

        var extras = new List<SubtitleExtra>();
        string[] mapped = ["Name", "Fontname", "Fontsize", "PrimaryColour", "Bold", "Italic", "Underline", "StrikeOut", "Alignment"];
        for (int i = 0; i < columns.Length && i < values.Length; i++)
        {
            if (Array.IndexOf(mapped, columns[i]) < 0)
            {
                extras.Add(new SubtitleExtra("ass", columns[i], values[i]));
            }
        }

        string name = Field(values, columns, "Name");
        double.TryParse(Field(values, columns, "Fontsize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size);
        SubtitleColor? color = ParseColor(Field(values, columns, "PrimaryColour"), out SubtitleColor parsed) ? parsed : null;
        int.TryParse(Field(values, columns, "Alignment"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int al);
        if (legacy)
        {
            // SubStation Alpha 4 numbers alignment 1-3 bottom, 5-7 top (+4), 9-11 middle (+8).
            al = al switch { >= 9 and <= 11 => al - 9 + 4, >= 5 and <= 7 => al - 5 + 7, >= 1 and <= 3 => al, _ => 2 };
        }

        CuePlacement? placement = null;
        if (al is >= 1 and <= 9)
        {
            NumpadAlignment.From(al, out SubtitleAlign a, out SubtitleVerticalAlign v);
            placement = new CuePlacement { Align = a, VerticalAlign = v };
        }

        return new SubtitleStyle(name)
        {
            Text = new TextStyle
            {
                FontFamily = Field(values, columns, "Fontname") is { Length: > 0 } fn ? fn : null,
                FontSize = size > 0 ? new SubtitleLength(size, SubtitleLengthUnit.Pixel) : null,
                Color = color,
                Bold = Flag(Field(values, columns, "Bold")),
                Italic = Flag(Field(values, columns, "Italic")),
                Underline = Flag(Field(values, columns, "Underline")),
                Strike = Flag(Field(values, columns, "StrikeOut")),
            },
            Placement = placement,
            Extras = SubtitleList.From(extras),
        };
    }

    private static bool? Flag(string v) => v.Length == 0 ? null : v != "0";

    // &HAABBGGRR (ASS), &HBBGGRR& (inline), or a decimal BGR number (SSA). Alpha 00 is opaque.
    internal static bool ParseColor(ReadOnlySpan<char> s, out SubtitleColor color)
    {
        color = default;
        s = s.Trim();
        if (s.IsEmpty)
        {
            return false;
        }

        uint v;
        if (s.Length > 2 && s[0] == '&' && (s[1] == 'H' || s[1] == 'h'))
        {
            s = s[2..];
            if (s.EndsWith("&"))
            {
                s = s[..^1];
            }

            if (s.IsEmpty || s.Length > 8 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v))
            {
                return false;
            }
        }
        else if (!uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v))
        {
            return false;
        }

        color = new SubtitleColor((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(255 - (v >> 24)));
        return true;
    }

    internal static string FormatColor(SubtitleColor c) =>
        string.Create(CultureInfo.InvariantCulture, $"&H{255 - c.A:X2}{c.B:X2}{c.G:X2}{c.R:X2}");

    // Where each field the reader uses sits in the [Events] Format line (first column of that name, as the lookup by name finds it).
    private sealed class DialogueMap
    {
        internal readonly int Count;
        internal readonly int Layer;
        internal readonly int Start;
        internal readonly int End;
        internal readonly int Style;
        internal readonly int Name;
        internal readonly int Actor;
        internal readonly int Marked;
        internal readonly int MarginL;
        internal readonly int MarginR;
        internal readonly int MarginV;
        internal readonly int Effect;
        internal readonly int Text;

        // Field ranges of the line being read, relative to the text after "Dialogue:".
        internal readonly int[] FieldStart;
        internal readonly int[] FieldLength;
        internal string LastStyle = "";
        internal string LastName = "";

        internal DialogueMap(string[] columns)
        {
            Count = columns.Length;
            Layer = IndexOf(columns, "Layer");
            Start = IndexOf(columns, "Start");
            End = IndexOf(columns, "End");
            Style = IndexOf(columns, "Style");
            Name = IndexOf(columns, "Name");
            Actor = IndexOf(columns, "Actor");
            Marked = IndexOf(columns, "Marked");
            MarginL = IndexOf(columns, "MarginL");
            MarginR = IndexOf(columns, "MarginR");
            MarginV = IndexOf(columns, "MarginV");
            Effect = IndexOf(columns, "Effect");
            Text = IndexOf(columns, "Text");
            FieldStart = new int[Count];
            FieldLength = new int[Count];
        }

        private static int IndexOf(string[] columns, string name)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        // The same split as SplitFields(rest, Count): at most Count fields, every one trimmed, the last one only at its start.
        internal int Split(ReadOnlySpan<char> rest)
        {
            int parts = 0;
            int start = 0;
            while (parts < Count - 1)
            {
                int comma = rest[start..].IndexOf(',');
                if (comma < 0)
                {
                    break;
                }

                ReadOnlySpan<char> part = rest.Slice(start, comma);
                int lead = 0;
                int length = part.Length;
                if (length > 0 && (char.IsWhiteSpace(part[0]) || char.IsWhiteSpace(part[^1])))
                {
                    ReadOnlySpan<char> trimmed = part.Trim();
                    length = trimmed.Length;
                    lead = trimmed.IsEmpty ? 0 : part.Length - part.TrimStart().Length;
                }

                FieldStart[parts] = start + lead;
                FieldLength[parts] = length;
                parts++;
                start += comma + 1;
            }

            ReadOnlySpan<char> lastPart = rest[start..];
            int lastLead = lastPart.Length > 0 && char.IsWhiteSpace(lastPart[0]) ? lastPart.Length - lastPart.TrimStart().Length : 0;
            FieldStart[parts] = start + lastLead;
            FieldLength[parts] = lastPart.Length - lastLead;
            return parts + 1;
        }

        internal ReadOnlySpan<char> Get(ReadOnlySpan<char> rest, int index, int parts) =>
            index >= 0 && index < parts ? rest.Slice(FieldStart[index], FieldLength[index]) : default;
    }

    private static void Keep(ref List<SubtitleExtra>? extras, string key, ReadOnlySpan<char> value, string defaultValue)
    {
        if (value.Length > 0 && !value.SequenceEqual(defaultValue))
        {
            (extras ??= []).Add(new SubtitleExtra("ass", key, value.ToString()));
        }
    }

    private static SubtitleCue? ParseDialogue(SubtitleSource src, ReadOnlySpan<char> rest, DialogueMap map, int index, List<SubtitleExtra> pending, int refW, int refH, StringBuilder scratch)
    {
        int parts = map.Split(rest);
        if (!SrtTime.TryParse(map.Get(rest, map.Start, parts), out MediaTime start, out _, out _) || !SrtTime.TryParse(map.Get(rest, map.End, parts), out MediaTime end, out _, out _))
        {
            src.Report(DefectKind.InvalidValue, DefectSeverity.Error, index, "Dialogue start or end time is not valid; line skipped.", "subtitles.ass.time");
            return null;
        }

        if (end < start)
        {
            src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, index, "Cue ends before it starts.", "subtitles.ass.end-before-start");
        }

        List<SubtitleExtra>? extras = pending.Count > 0 ? new List<SubtitleExtra>(pending) : null;
        Keep(ref extras, "Layer", map.Get(rest, map.Layer, parts), "0");
        Keep(ref extras, "Marked", map.Get(rest, map.Marked, parts), "Marked=0");
        Keep(ref extras, "MarginL", map.Get(rest, map.MarginL, parts), "0");
        Keep(ref extras, "MarginR", map.Get(rest, map.MarginR, parts), "0");
        Keep(ref extras, "MarginV", map.Get(rest, map.MarginV, parts), "0");
        Keep(ref extras, "Effect", map.Get(rest, map.Effect, parts), "");
        ReadOnlySpan<char> style = map.Get(rest, map.Style, parts);
        ReadOnlySpan<char> actor = map.Get(rest, map.Name, parts);
        if (actor.Length == 0)
        {
            actor = map.Get(rest, map.Actor, parts);
        }

        // Style and speaker names repeat from cue to cue: reuse the last string when the text is the same.
        string? styleName = null;
        if (style.Length > 0)
        {
            if (!style.SequenceEqual(map.LastStyle))
            {
                map.LastStyle = style.ToString();
            }

            styleName = map.LastStyle;
        }

        string? speaker = null;
        if (actor.Length > 0)
        {
            if (!actor.SequenceEqual(map.LastName))
            {
                map.LastName = actor.ToString();
            }

            speaker = map.LastName;
        }

        ReadOnlySpan<char> raw = map.Get(rest, map.Text, parts);
        string plain;
        SubtitleList<SubtitleSpan> spanList;
        CuePlacement? placement = null;
        if (raw.IndexOf('{') < 0)
        {
            // No override block: only \N, \n and \h need translating, and there are no spans.
            plain = AssText.DecodeBreaks(raw, scratch);
            spanList = SubtitleList.Empty<SubtitleSpan>();
        }
        else
        {
            AssText.Parse(raw.ToString(), src, index, start, refW, refH, out plain, out List<SubtitleSpan> spans, out placement);
            spanList = SubtitleList.From(spans);
        }

        return new SubtitleCue
        {
            Start = start,
            End = end,
            Text = plain,
            Spans = spanList,
            Speaker = speaker,
            StyleName = styleName,
            Placement = placement,
            Extras = extras is null ? SubtitleList.Empty<SubtitleExtra>() : SubtitleList.From(extras),
        };
    }

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        var sb = new StringBuilder(MarkupWriter.EstimateCapacity(doc.Cues.Count, 88));
        sb.Append("[Script Info]").Append(nl);
        if (doc.Title is { } title)
        {
            sb.Append("Title: ").Append(title.Replace('\n', ' ').Replace('\r', ' ')).Append(nl);
        }

        sb.Append("ScriptType: v4.00+").Append(nl);
        int w = doc.ReferenceWidth > 0 ? doc.ReferenceWidth : 384;
        int h = doc.ReferenceHeight > 0 ? doc.ReferenceHeight : 288;
        sb.Append("PlayResX: ").Append(w.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("PlayResY: ").Append(h.ToString(CultureInfo.InvariantCulture)).Append(nl);
        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name && e.Key == "info")
            {
                sb.Append(e.Value).Append(nl);
            }
        }

        sb.Append(nl).Append("[V4+ Styles]").Append(nl);
        var columns = new List<string>(StyleColumns);
        foreach (SubtitleStyle s in doc.Styles)
        {
            foreach (SubtitleExtra e in s.Extras)
            {
                if (e.Format == Name && !columns.Contains(e.Key))
                {
                    columns.Add(e.Key);
                }
            }
        }

        sb.Append("Format: ").Append(string.Join(", ", columns)).Append(nl);
        bool hasDefault = false;
        foreach (SubtitleStyle s in doc.Styles)
        {
            hasDefault |= s.Name == "Default";
        }

        var styles = new List<SubtitleStyle>(doc.Styles);
        if (!hasDefault)
        {
            styles.Insert(0, new SubtitleStyle("Default"));
        }

        foreach (SubtitleStyle s in styles)
        {
            sb.Append("Style: ").Append(StyleLine(s, columns)).Append(nl);
        }

        sb.Append(nl).Append("[Events]").Append(nl);
        sb.Append("Format: ").Append(string.Join(", ", EventColumns)).Append(nl);
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            SubtitleCue cue = doc.Cues[c];
            string layer = "0";
            string marginL = "0";
            string marginR = "0";
            string marginV = "0";
            string effect = "";
            for (int x = 0; x < cue.Extras.Count; x++)
            {
                SubtitleExtra e = cue.Extras[x];
                if (e.Format != Name)
                {
                    continue;
                }

                switch (e.Key)
                {
                    case "line-before":
                        sb.Append(e.Value).Append(nl);
                        break;
                    case "Layer":
                        layer = e.Value;
                        break;
                    case "MarginL":
                        marginL = e.Value;
                        break;
                    case "MarginR":
                        marginR = e.Value;
                        break;
                    case "MarginV":
                        marginV = e.Value;
                        break;
                    case "Effect":
                        effect = e.Value;
                        break;
                }
            }

            sb.Append("Dialogue: ").Append(layer).Append(',');
            AppendTime(sb, cue.Start, c, report);
            sb.Append(',');
            AppendTime(sb, cue.End, c, report);
            sb.Append(',')
                .Append(cue.StyleName ?? "Default").Append(',')
                .Append((cue.Speaker ?? "").Replace(',', ' ').Replace('\n', ' ')).Append(',')
                .Append(marginL).Append(',').Append(marginR).Append(',').Append(marginV).Append(',').Append(effect).Append(',');
            AssText.Write(sb, cue, w, h, c, report);
            sb.Append(nl);
        }

        foreach (SubtitleExtra e in doc.Extras)
        {
            if (e.Format == Name && e.Key == "line-end")
            {
                sb.Append(e.Value).Append(nl);
            }
            else if (e.Format == Name && e.Key.StartsWith("section:", StringComparison.Ordinal))
            {
                sb.Append(nl).Append('[').Append(e.Key.AsSpan(8)).Append(']').Append(nl).Append(e.Value.Replace("\n", nl, StringComparison.Ordinal));
            }
        }

        return sb.ToString();
    }

    private static void AppendTime(StringBuilder sb, MediaTime t, int cue, ConversionReport report)
    {
        long cs = SubtitleLoss.Quantize(t, 100, cue, SubtitleFeatures.Timing, report, "Time");
        if (cs < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            cs = 0;
        }

        long s = cs / 100;
        SrtTime.AppendPadded(sb, s / 3600, 1);
        sb.Append(':');
        SrtTime.AppendPadded(sb, s / 60 % 60, 2);
        sb.Append(':');
        SrtTime.AppendPadded(sb, s % 60, 2);
        sb.Append('.');
        SrtTime.AppendPadded(sb, cs % 100, 2);
    }

    private static string StyleLine(SubtitleStyle s, List<string> columns)
    {
        var values = new string[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            string column = columns[i];
            string? v = null;
            foreach (SubtitleExtra e in s.Extras)
            {
                if (e.Format == "ass" && e.Key == column)
                {
                    v = e.Value;
                }
            }

            v ??= column switch
            {
                "Name" => s.Name,
                "Fontname" => s.Text.FontFamily ?? StyleDefaults[1],
                "Fontsize" => s.Text.FontSize is { } fs ? SubtitleNumbers.Format(fs.Value) : StyleDefaults[2],
                "PrimaryColour" => s.Text.Color is { } c ? FormatColor(c) : StyleDefaults[3],
                "Bold" => s.Text.Bold == true ? "-1" : "0",
                "Italic" => s.Text.Italic == true ? "-1" : "0",
                "Underline" => s.Text.Underline == true ? "-1" : "0",
                "StrikeOut" => s.Text.Strike == true ? "-1" : "0",
                "Alignment" => s.Placement is { } p && NumpadAlignment.To(p.Align, p.VerticalAlign) is int an and > 0 ? an.ToString(CultureInfo.InvariantCulture) : "2",
                _ => Array.IndexOf(StyleColumns, column) is int k and >= 0 ? StyleDefaults[k] : "",
            };
            values[i] = v.Replace(',', ' ');
        }

        return string.Join(',', values);
    }
}

internal static class AssText
{
    // Longest names first so \fscx wins over \fs and \1c over \c.
    private static readonly string[] Tags =
    [
        "alpha", "blur", "bord", "clip", "fade", "fscx", "fscy", "iclip", "move", "rndx", "rndy", "rndz", "shad", "xbord", "xshad", "ybord", "yshad",
        "1a", "1c", "2a", "2c", "3a", "3c", "4a", "4c", "an", "be", "fad", "fax", "fay", "fn", "frx", "fry", "frz", "fsc", "fsp", "kf", "ko", "org",
        "pbo", "pos", "rnd", "a", "b", "c", "fr", "fs", "i", "k", "K", "p", "q", "r", "s", "t", "u",
    ];

    private sealed class State
    {
        internal bool? Bold;
        internal bool? Italic;
        internal bool? Underline;
        internal bool? Strike;
        internal SubtitleColor? Color;
        internal string? Font;
        internal double? Size;

        internal bool IsEmpty => Bold is null && Italic is null && Underline is null && Strike is null && Color is null && Font is null && Size is null;

        internal void Reset() => Bold = Italic = Underline = Strike = null;

        internal TextStyle ToStyle() => new()
        {
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            Strike = Strike,
            Color = Color,
            FontFamily = Font,
            FontSize = Size is { } s ? new SubtitleLength(s, SubtitleLengthUnit.Pixel) : null,
        };
    }

    /// <summary>
    /// The text of an event with no override block: <c>\N</c> and <c>\n</c> become a line feed and <c>\h</c> a no-break space; every
    /// other character stays. Returns the text as one string, using <paramref name="scratch"/> only when something changes.
    /// </summary>
    internal static string DecodeBreaks(ReadOnlySpan<char> raw, StringBuilder scratch)
    {
        int first = raw.IndexOf('\\');
        if (first < 0)
        {
            return raw.ToString();
        }

        scratch.Clear();
        int i = 0;
        while (i < raw.Length)
        {
            int k = raw[i..].IndexOf('\\');
            if (k < 0)
            {
                scratch.Append(raw[i..]);
                break;
            }

            scratch.Append(raw.Slice(i, k));
            i += k;
            if (i + 1 < raw.Length && raw[i + 1] is 'N' or 'n')
            {
                scratch.Append('\n');
                i += 2;
            }
            else if (i + 1 < raw.Length && raw[i + 1] == 'h')
            {
                scratch.Append('\u00A0');
                i += 2;
            }
            else
            {
                scratch.Append('\\');
                i++;
            }
        }

        return scratch.ToString();
    }

    private sealed class Pos
    {
        internal double? X;
        internal double? Y;
    }

    internal static void Parse(string raw, SubtitleSource src, int index, MediaTime cueStart, int refW, int refH, out string text, out List<SubtitleSpan> spans, out CuePlacement? placement)
    {
        var sb = new StringBuilder(raw.Length);
        var found = new List<SubtitleSpan>();
        CuePlacement? place = null;
        var state = new State();
        var pos = new Pos();
        int runStart = 0;
        MediaTime cursor = cueStart;
        int kPos = -1;
        long kDuration = 0;
        string kVariant = "k";
        int i = 0;

        void FlushRun()
        {
            if (sb.Length > runStart && !state.IsEmpty)
            {
                found.Add(new SubtitleSpan(runStart, sb.Length - runStart) { Style = state.ToStyle() });
            }

            runStart = sb.Length;
        }

        void FlushWord()
        {
            if (kPos < 0)
            {
                return;
            }

            MediaTime wordEnd = SubtitleTimes.Add(cursor, new MediaTime(kDuration, 100));
            if (sb.Length > kPos)
            {
                found.Add(new SubtitleSpan(kPos, sb.Length - kPos)
                {
                    WordStart = cursor,
                    WordEnd = wordEnd,
                    Raw = kVariant == "k" ? null : new SubtitleExtra("ass", "karaoke", kVariant),
                });
            }

            cursor = wordEnd;
            kPos = -1;
        }

        // One tag buffer and one pair of delegates per cue, not per override block.
        StringBuilder? rawTags = null;
        Action flushRun = FlushRun;
        Action<string, long> startWord = (variant, cs) =>
        {
            FlushWord();
            kPos = sb.Length;
            kDuration = cs;
            kVariant = variant;
        };
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '{')
            {
                int close = raw.IndexOf('}', i + 1);
                if (close > 0)
                {
                    rawTags ??= new StringBuilder();
                    rawTags.Clear();
                    ParseBlock(raw.AsSpan(i + 1, close - i - 1), src, index + i, rawTags, ref place, pos, state, flushRun, startWord);
                    if (rawTags.Length > 0)
                    {
                        found.Add(new SubtitleSpan(sb.Length, 0) { Raw = new SubtitleExtra("ass", "override", rawTags.ToString()) });
                    }

                    i = close + 1;
                    continue;
                }
            }
            else if (c == '\\' && i + 1 < raw.Length)
            {
                char n = raw[i + 1];
                if (n is 'N' or 'n')
                {
                    sb.Append('\n');
                    i += 2;
                    continue;
                }

                if (n == 'h')
                {
                    sb.Append('\u00A0');
                    i += 2;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        FlushRun();
        FlushWord();
        if (pos.X is { } px && pos.Y is { } py)
        {
            // \pos is in the script's reference frame; the model keeps percentages (rounding back to whole pixels on write).
            place = (place ?? new CuePlacement()) with { Position = px * 100 / refW, Line = new CueLine(py * 100 / refH, CueLineUnit.Percent, false) };
        }

        placement = place;
        text = sb.ToString();
        found.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        spans = found;
    }

    private static void ParseBlock(
        ReadOnlySpan<char> block,
        SubtitleSource src,
        int index,
        StringBuilder rawTags,
        ref CuePlacement? placement,
        Pos pos,
        State state,
        Action flushRun,
        Action<string, long> startWord)
    {
        int i = 0;
        while (i < block.Length)
        {
            if (block[i] != '\\')
            {
                // Text inside braces that is not a tag is a comment.
                i++;
                continue;
            }

            i++;
            string? name = null;
            foreach (string t in Tags)
            {
                if (block[i..].StartsWith(t, StringComparison.Ordinal))
                {
                    name = t;
                    break;
                }
            }

            if (name is null)
            {
                // Unknown tag: keep everything up to the next backslash at depth zero.
                int e = TagEnd(block, i);
                rawTags.Append('\\').Append(block[i..e]);
                i = e;
                continue;
            }

            int argStart = i + name.Length;
            int end = TagEnd(block, argStart);
            ReadOnlySpan<char> arg = block[argStart..end];
            i = end;
            switch (name)
            {
                case "b":
                    flushRun();
                    state.Bold = arg.IsEmpty ? null : arg is "0" ? false : arg is "1" ? true : int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int weight) ? weight >= 700 : null;
                    break;
                case "i":
                    flushRun();
                    state.Italic = arg.IsEmpty ? null : arg is "1";
                    break;
                case "u":
                    flushRun();
                    state.Underline = arg.IsEmpty ? null : arg is "1";
                    break;
                case "s":
                    flushRun();
                    state.Strike = arg.IsEmpty ? null : arg is "1";
                    break;
                case "c" or "1c":
                    flushRun();
                    state.Color = AssFormat.ParseColor(arg, out SubtitleColor col) ? col : null;
                    break;
                case "fn":
                    flushRun();
                    state.Font = arg.IsEmpty ? null : arg.ToString();
                    break;
                case "fs":
                    flushRun();
                    state.Size = SubtitleNumbers.TryParseDouble(arg, out double fs) && fs > 0 ? fs : null;
                    break;
                case "an" when arg.Length == 1 && arg[0] is >= '1' and <= '9':
                    NumpadAlignment.From(arg[0] - '0', out SubtitleAlign a, out SubtitleVerticalAlign v);
                    placement = (placement ?? new CuePlacement()) with { Align = a, VerticalAlign = v };
                    break;
                case "pos" when TryPos(arg, out double x, out double y):
                    pos.X = x;
                    pos.Y = y;
                    break;
                case "k" or "kf" or "ko" or "K":
                    if (long.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out long cs) && cs <= 10_000_000)
                    {
                        flushRun();
                        startWord(name, cs);
                    }
                    else
                    {
                        src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, index, $"Karaoke duration '{arg}' is not valid; tag kept as is.", "subtitles.ass.karaoke");
                        rawTags.Append('\\').Append(name).Append(arg);
                    }

                    break;
                case "r":
                    flushRun();
                    state.Reset();
                    state.Color = null;
                    state.Font = null;
                    state.Size = null;
                    if (!arg.IsEmpty)
                    {
                        rawTags.Append("\\r").Append(arg);
                    }

                    break;
                default:
                    rawTags.Append('\\').Append(name).Append(arg);
                    break;
            }
        }
    }

    // A tag's argument ends at the next backslash outside parentheses (\t(\fs20) holds backslashes inside).
    private static int TagEnd(ReadOnlySpan<char> block, int from)
    {
        int depth = 0;
        for (int i = from; i < block.Length; i++)
        {
            if (block[i] == '(')
            {
                depth++;
            }
            else if (block[i] == ')' && depth > 0)
            {
                depth--;
            }
            else if (block[i] == '\\' && depth == 0)
            {
                return i;
            }
        }

        return block.Length;
    }

    private static bool TryPos(ReadOnlySpan<char> arg, out double x, out double y)
    {
        x = y = 0;
        arg = arg.Trim();
        if (arg.Length < 3 || arg[0] != '(' || arg[^1] != ')')
        {
            return false;
        }

        arg = arg[1..^1];
        int comma = arg.IndexOf(',');
        return comma > 0 && SubtitleNumbers.TryParseDouble(arg[..comma], out x) && SubtitleNumbers.TryParseDouble(arg[(comma + 1)..], out y);
    }

    private static readonly System.Buffers.SearchValues<char> TextSpecial = System.Buffers.SearchValues.Create("\n\u00A0{}\r");

    // Text as the event field holds it: line feeds as \N, no-break spaces as \h, braces (which would open an override block) as
    // parentheses, carriage returns dropped.
    private static void AppendText(StringBuilder sb, ReadOnlySpan<char> s, int cueIndex, ConversionReport report)
    {
        while (!s.IsEmpty)
        {
            int k = s.IndexOfAny(TextSpecial);
            if (k < 0)
            {
                sb.Append(s);
                return;
            }

            sb.Append(s[..k]);
            switch (s[k])
            {
                case '\n':
                    sb.Append("\\N");
                    break;
                case '\u00A0':
                    sb.Append("\\h");
                    break;
                case '{':
                    sb.Append('(');
                    report.Add(cueIndex, SubtitleFeatures.Text, LossKind.Approximated, "A brace in the text was written as a parenthesis; ASS would read it as an override block.");
                    break;
                case '}':
                    sb.Append(')');
                    break;
            }

            s = s[(k + 1)..];
        }
    }

    internal static void Write(StringBuilder sb, SubtitleCue cue, int width, int height, int cueIndex, ConversionReport report)
    {
        string text = cue.Text;
        if (cue.Spans.Count == 0)
        {
            // No spans: one run of text, preceded by the placement tags if the cue has a placement.
            if (cue.Placement is not null)
            {
                var placementTags = new StringBuilder();
                AppendPlacement(placementTags, cue, width, height, cueIndex, report);
                if (placementTags.Length > 0)
                {
                    sb.Append('{').Append(placementTags).Append('}');
                }
            }

            AppendText(sb, text, cueIndex, report);
            return;
        }

        var boundaries = new List<int> { 0 };
        for (int k = 0; k < cue.Spans.Count; k++)
        {
            SubtitleSpan s = cue.Spans[k];
            bool relevant = s.Style is { IsEmpty: false } || s.WordStart is not null || (s.Raw is { Format: "ass" } && s.Length == 0);
            if (relevant && s.Start <= text.Length)
            {
                boundaries.Add(Math.Clamp(s.Start, 0, text.Length));
                if (s.Length > 0)
                {
                    boundaries.Add(Math.Clamp(s.End, 0, text.Length));
                }
            }
        }

        boundaries.Sort();
        int distinct = 0;
        for (int k = 0; k < boundaries.Count; k++)
        {
            if (k == 0 || boundaries[k] != boundaries[k - 1])
            {
                boundaries[distinct++] = boundaries[k];
            }
        }

        boundaries.RemoveRange(distinct, boundaries.Count - distinct);
        State prev = new();
        bool first = true;
        int[] bounds = boundaries.ToArray();
        for (int b = 0; b < bounds.Length; b++)
        {
            int at = bounds[b];
            if (at >= text.Length && at > 0 && !HasMarkerAt(cue, at))
            {
                break;
            }

            int next = b + 1 < bounds.Length ? bounds[b + 1] : text.Length;
            State cur = StateAt(cue, at);
            var tags = new StringBuilder();
            if (first)
            {
                AppendPlacement(tags, cue, width, height, cueIndex, report);
            }

            bool lost = (prev.Bold is not null && cur.Bold is null) || (prev.Italic is not null && cur.Italic is null)
                || (prev.Underline is not null && cur.Underline is null) || (prev.Strike is not null && cur.Strike is null)
                || (prev.Color is not null && cur.Color is null) || (prev.Font is not null && cur.Font is null) || (prev.Size is not null && cur.Size is null);
            if (lost)
            {
                tags.Append("\\r");
                prev = new State();
            }

            if (cur.Bold != prev.Bold && cur.Bold is { } bo)
            {
                tags.Append(bo ? "\\b1" : "\\b0");
            }

            if (cur.Italic != prev.Italic && cur.Italic is { } it)
            {
                tags.Append(it ? "\\i1" : "\\i0");
            }

            if (cur.Underline != prev.Underline && cur.Underline is { } un)
            {
                tags.Append(un ? "\\u1" : "\\u0");
            }

            if (cur.Strike != prev.Strike && cur.Strike is { } st)
            {
                tags.Append(st ? "\\s1" : "\\s0");
            }

            if (cur.Color != prev.Color && cur.Color is { } co)
            {
                tags.Append("\\c&H").Append(string.Create(CultureInfo.InvariantCulture, $"{co.B:X2}{co.G:X2}{co.R:X2}")).Append('&');
                if (co.A != 255)
                {
                    tags.Append("\\1a&H").Append(string.Create(CultureInfo.InvariantCulture, $"{255 - co.A:X2}")).Append('&');
                }
            }

            if (cur.Font != prev.Font && cur.Font is { } fn)
            {
                tags.Append("\\fn").Append(fn.Replace('\\', ' ').Replace('}', ' '));
            }

            if (cur.Size != prev.Size && cur.Size is { } fs)
            {
                tags.Append("\\fs").Append(SubtitleNumbers.Format(fs));
            }

            AppendKaraoke(tags, cue, at, cueIndex, report);
            foreach (SubtitleSpan s in cue.Spans)
            {
                if (s.Length == 0 && s.Start == at && s.Raw is { Format: "ass", Key: "override" } raw)
                {
                    tags.Append(raw.Value);
                }
            }

            if (tags.Length > 0)
            {
                sb.Append('{').Append(tags).Append('}');
            }

            prev = cur;
            first = false;
            AppendText(sb, text.AsSpan(at, Math.Max(0, next - at)), cueIndex, report);
        }
    }

    private static bool HasMarkerAt(SubtitleCue cue, int at)
    {
        foreach (SubtitleSpan s in cue.Spans)
        {
            if (s.Length == 0 && s.Start == at && s.Raw is { Format: "ass", Key: "override" })
            {
                return true;
            }
        }

        return false;
    }

    // Spans may nest and overlap; the style at a position is the cue's spans that cover it, later ones overriding earlier ones.
    private static State StateAt(SubtitleCue cue, int at)
    {
        var st = new State();
        foreach (SubtitleSpan s in cue.Spans)
        {
            if (s.Style is not { } t || s.Length == 0 || at < s.Start || at >= s.End)
            {
                continue;
            }

            st.Bold = t.Bold ?? st.Bold;
            st.Italic = t.Italic ?? st.Italic;
            st.Underline = t.Underline ?? st.Underline;
            st.Strike = t.Strike ?? st.Strike;
            st.Color = t.Color ?? st.Color;
            st.Font = t.FontFamily ?? st.Font;
            st.Size = t.FontSize?.Value ?? st.Size;
        }

        return st;
    }

    private static void AppendKaraoke(StringBuilder tags, SubtitleCue cue, int at, int cueIndex, ConversionReport report)
    {
        foreach (SubtitleSpan s in cue.Spans)
        {
            if (s.WordStart is not { } ws || s.Start != at || s.Length == 0)
            {
                continue;
            }

            MediaTime end = s.WordEnd ?? cue.End;
            MediaTime dur = SubtitleTimes.Subtract(end, ws);
            long cs = SubtitleLoss.Quantize(dur, 100, cueIndex, SubtitleFeatures.WordTiming, report, "Word duration");
            string variant = s.Raw is { Format: "ass", Key: "karaoke" } k ? k.Value : "k";
            tags.Append('\\').Append(variant).Append(Math.Max(0, cs).ToString(CultureInfo.InvariantCulture));
            return;
        }
    }

    private static void AppendPlacement(StringBuilder tags, SubtitleCue cue, int width, int height, int cueIndex, ConversionReport report)
    {
        if (cue.Placement is not { } p)
        {
            return;
        }

        SubtitleAlign align = p.Align == SubtitleAlign.Unspecified ? p.PositionAnchor : p.Align;
        SubtitleVerticalAlign vertical = p.VerticalAlign;
        if (vertical == SubtitleVerticalAlign.Unspecified && p.Line is { } ln && p.Position is null)
        {
            // A line number or percentage without a horizontal position is a vertical placement: pick the nearest anchor.
            vertical = ln.Unit switch
            {
                CueLineUnit.Percent => ln.Value < 33 ? SubtitleVerticalAlign.Top : ln.Value < 66 ? SubtitleVerticalAlign.Middle : SubtitleVerticalAlign.Bottom,
                CueLineUnit.Row => ln.Value <= 5 ? SubtitleVerticalAlign.Top : ln.Value <= 10 ? SubtitleVerticalAlign.Middle : SubtitleVerticalAlign.Bottom,
                _ => ln.Value < 0 ? SubtitleVerticalAlign.Bottom : SubtitleVerticalAlign.Top,
            };
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Line position mapped to a top, middle or bottom anchor.");
        }

        int an = NumpadAlignment.To(align, vertical);
        if (an > 0)
        {
            tags.Append("\\an").Append(an.ToString(CultureInfo.InvariantCulture));
        }

        if (p.Position is { } x && p.Line is { Unit: CueLineUnit.Percent } y)
        {
            tags.Append("\\pos(").Append(SubtitleNumbers.Format(Math.Round(x * width / 100))).Append(',').Append(SubtitleNumbers.Format(Math.Round(y.Value * height / 100))).Append(')');
        }
        else if (p.Position is not null || p.Size is not null || p.Row is not null || p.Column is not null)
        {
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Dropped, "ASS can place text only at an absolute point (line in percent together with position); the position, size or grid cell was not written.");
        }
    }
}

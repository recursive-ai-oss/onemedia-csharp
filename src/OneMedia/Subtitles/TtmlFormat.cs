using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace RecursiveAI.OneMedia.Captions;

// TTML2 / DFXP / IMSC1 text profile (W3C TTML2, W3C IMSC 1.0.1). The reader is tolerant about namespaces and units; the writer
// produces the IMSC1 text profile with media time base (IMSC1 §6.9: ttp:profile on tt, xml:lang required).
internal sealed class TtmlFormat : SubtitleFormat
{
    internal static readonly TtmlFormat Instance = new();

    private static readonly string[] Exts = [".ttml", ".dfxp", ".xml"];

    private TtmlFormat()
    {
    }

    public override string Name => "ttml";

    public override string DisplayName => "TTML / DFXP / IMSC1 text";

    public override IReadOnlyList<string> Extensions => Exts;

    public override bool RequiresUtf8 => true;

    public override SubtitleFeatures Features =>
        SubtitleFeatures.Bold | SubtitleFeatures.Italic | SubtitleFeatures.Underline | SubtitleFeatures.Strike | SubtitleFeatures.Color
        | SubtitleFeatures.Background | SubtitleFeatures.Font | SubtitleFeatures.Ruby | SubtitleFeatures.WordTiming | SubtitleFeatures.Voice
        | SubtitleFeatures.Language | SubtitleFeatures.Alignment | SubtitleFeatures.Position | SubtitleFeatures.Region | SubtitleFeatures.Writing
        | SubtitleFeatures.Styles | SubtitleFeatures.Id | SubtitleFeatures.Header | SubtitleFeatures.Extras;

    public override int Sniff(ReadOnlySpan<char> head)
    {
        head = head.TrimStart('\uFEFF').TrimStart();
        if (head.IsEmpty || head[0] != '<')
        {
            return 0;
        }

        if (head.IndexOf("http://www.w3.org/ns/ttml", StringComparison.Ordinal) >= 0 || head.IndexOf("http://www.w3.org/2006/10/ttaf1", StringComparison.Ordinal) >= 0
            || head.IndexOf("http://www.w3.org/2006/04/ttaf1", StringComparison.Ordinal) >= 0)
        {
            return 95;
        }

        return head.IndexOf("<tt ", StringComparison.Ordinal) >= 0 || head.IndexOf("<tt>", StringComparison.Ordinal) >= 0 ? 60 : 0;
    }

    private const string NsTtml = "http://www.w3.org/ns/ttml";
    private const string NsStyling = "http://www.w3.org/ns/ttml#styling";
    private const string NsParameter = "http://www.w3.org/ns/ttml#parameter";
    private const string NsMetadata = "http://www.w3.org/ns/ttml#metadata";
    private const string NsItts = "http://www.w3.org/ns/ttml/profile/imsc1#styling";
    private const string NsEbutts = "urn:ebu:tt:style";

    // ---- reading ----

    private sealed class Node
    {
        internal string Name = "";
        internal List<(string Key, string Value)> Attrs = [];
        internal List<object> Children = [];
        internal int Index;

        internal string? Attr(string key)
        {
            foreach ((string k, string v) in Attrs)
            {
                if (k == key)
                {
                    return v;
                }
            }

            return null;
        }
    }

    private static string Canonical(string ns, string local, string prefix) => ns switch
    {
        "" => local,
        "http://www.w3.org/XML/1998/namespace" => "xml:" + local,
        NsStyling or "http://www.w3.org/2006/10/ttaf1#style" or "http://www.w3.org/2006/04/ttaf1#style" => "tts:" + local,
        NsParameter or "http://www.w3.org/2006/10/ttaf1#parameter" or "http://www.w3.org/2006/04/ttaf1#parameter" => "ttp:" + local,
        NsMetadata or "http://www.w3.org/2006/10/ttaf1#metadata" or "http://www.w3.org/2006/04/ttaf1#metadata" => "ttm:" + local,
        NsItts => "itts:" + local,
        NsEbutts => "ebutts:" + local,
        _ => "?" + prefix + ":" + local,
    };

    internal override SubtitleDocument ReadCore(SubtitleSource src)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            MaxCharactersInDocument = src.Limits.MaxAllocationBytes,
            MaxCharactersFromEntities = 1024,
        };
        Node root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(src.Text.TrimStart('\uFEFF')), settings);
            root = Load(reader, src);
        }
        catch (XmlException e)
        {
            long offset = src.ByteOffset(src.IndexOfLine(e.LineNumber, e.LinePosition));
            var defect = new MediaDefect(DefectKind.InvalidValue, DefectSeverity.Error, offset, "The XML is not well formed: " + e.Message, "subtitles.ttml.xml");
            src.Log.Add(defect);
            throw new MediaFormatException(defect);
        }

        if (root.Name != "tt")
        {
            var defect = new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The root element is not <tt>.", "subtitles.ttml.root");
            src.Log.Add(defect);
            throw new MediaFormatException(defect);
        }

        return new TtmlReader(src, root).Read();
    }

    private static Node Load(XmlReader reader, SubtitleSource src)
    {
        var stack = new Stack<Node>();
        Node? root = null;
        long count = 0;
        var info = (IXmlLineInfo)reader;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    int index = src.IndexOfLine(info.LineNumber, info.LinePosition);
                    src.Limits.CheckDepth(stack.Count + 1, src.ByteOffset(index));
                    src.Limits.CheckElementCount(++count, src.ByteOffset(index));
                    var node = new Node { Name = reader.LocalName, Index = index };
                    bool empty = reader.IsEmptyElement;
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.Prefix == "xmlns" || reader.Name == "xmlns")
                            {
                                continue;
                            }

                            string key = Canonical(reader.NamespaceURI, reader.LocalName, reader.Prefix);
                            if (key.StartsWith('?'))
                            {
                                src.Report(DefectKind.Unsupported, DefectSeverity.Info, index, $"Attribute {reader.Name} in an unknown namespace ignored.", "subtitles.ttml.attr-ns");
                                continue;
                            }

                            node.Attrs.Add((key, reader.Value));
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (stack.Count > 0)
                    {
                        stack.Peek().Children.Add(node);
                    }
                    else
                    {
                        root = node;
                    }

                    if (!empty)
                    {
                        stack.Push(node);
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                    stack.Pop();
                    break;
                case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA:
                    if (stack.Count > 0)
                    {
                        stack.Peek().Children.Add(reader.Value);
                    }

                    break;
            }
        }

        return root ?? throw new XmlException("No root element.");
    }

    private sealed class StyleBag
    {
        internal TextStyle Text = new();
        internal SubtitleAlign Align;
        internal SubtitleVerticalAlign VAlign;
        internal SubtitleWriting Writing;
        internal List<SubtitleExtra> Extras = [];
    }

    private sealed class TtmlReader
    {
        private readonly SubtitleSource _src;
        private readonly Node _root;
        private readonly Dictionary<string, Node> _styles = [];
        private readonly Dictionary<string, string> _agents = [];
        private readonly List<SubtitleCue> _cues = [];
        private int _frameRate = 30;
        private long _frameMulN = 1;
        private long _frameMulD = 1;
        private long _tickRate = 1;
        private int _extentW;
        private int _extentH;

        internal TtmlReader(SubtitleSource src, Node root)
        {
            _src = src;
            _root = root;
        }

        internal SubtitleDocument Read()
        {
            ReadParameters();
            string? title = null;
            var regions = new List<SubtitleRegion>();
            var styles = new List<SubtitleStyle>();
            Node? body = null;
            foreach (object o in _root.Children)
            {
                if (o is not Node n)
                {
                    continue;
                }

                if (n.Name == "head")
                {
                    ReadHead(n, ref title, regions);
                }
                else if (n.Name == "body")
                {
                    body = n;
                }
            }

            foreach (KeyValuePair<string, Node> kv in _styles)
            {
                StyleBag bag = Resolve(kv.Value, 0);
                CuePlacement? placement = bag.Align != SubtitleAlign.Unspecified || bag.VAlign != SubtitleVerticalAlign.Unspecified || bag.Writing != SubtitleWriting.Horizontal
                    ? new CuePlacement { Align = bag.Align, VerticalAlign = bag.VAlign, Writing = bag.Writing }
                    : null;
                styles.Add(new SubtitleStyle(kv.Key) { Text = bag.Text, Placement = placement, Extras = SubtitleList.From(bag.Extras) });
            }

            var extras = new List<SubtitleExtra>();
            if (body is not null)
            {
                string? regionDefault = body.Attr("region");
                Container(body, MediaTime.Zero, body.Attr("style"), regionDefault, IsPreserve(body, false), null);
            }

            return new SubtitleDocument
            {
                Language = _root.Attr("xml:lang") is { Length: > 0 } l ? l : null,
                Title = title,
                Styles = SubtitleList.From(styles),
                Regions = SubtitleList.From(regions),
                Cues = SubtitleList.From(_cues),
                Extras = SubtitleList.From(extras),
                ReferenceWidth = _extentW,
                ReferenceHeight = _extentH,
                NewLine = _src.NewLine,
            };
        }

        private void ReadParameters()
        {
            if (int.TryParse(_root.Attr("ttp:frameRate"), NumberStyles.None, CultureInfo.InvariantCulture, out int fr) && fr > 0)
            {
                _frameRate = fr;
            }

            if (_root.Attr("ttp:frameRateMultiplier") is { } m)
            {
                string[] parts = m.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long n) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long d) && n > 0 && d > 0)
                {
                    _frameMulN = n;
                    _frameMulD = d;
                }
            }

            long sub = 1;
            if (long.TryParse(_root.Attr("ttp:subFrameRate"), NumberStyles.None, CultureInfo.InvariantCulture, out long sf) && sf > 0)
            {
                sub = sf;
            }

            _tickRate = _root.Attr("ttp:frameRate") is not null ? (long)_frameRate * sub : 1;
            if (long.TryParse(_root.Attr("ttp:tickRate"), NumberStyles.None, CultureInfo.InvariantCulture, out long tr) && tr > 0)
            {
                _tickRate = tr;
            }

            if (_root.Attr("tts:extent") is { } ext)
            {
                string[] p = ext.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && TryPixels(p[0], out double w) && TryPixels(p[1], out double h))
                {
                    _extentW = (int)w;
                    _extentH = (int)h;
                }
            }
        }

        private static bool TryPixels(string s, out double value)
        {
            value = 0;
            return s.EndsWith("px", StringComparison.Ordinal) && SubtitleNumbers.TryParseDouble(s.AsSpan(0, s.Length - 2), out value);
        }

        private void ReadHead(Node head, ref string? title, List<SubtitleRegion> regions)
        {
            foreach (object o in head.Children)
            {
                if (o is not Node section)
                {
                    continue;
                }

                switch (section.Name)
                {
                    case "metadata":
                        foreach (object m in section.Children)
                        {
                            if (m is Node { Name: "title" } t)
                            {
                                title = Text(t).Trim();
                            }
                            else if (m is Node { Name: "agent" } a && a.Attr("xml:id") is { } id)
                            {
                                _agents[id] = a.Children.OfType<Node>().FirstOrDefault(c => c.Name == "name") is { } nm ? Text(nm).Trim() : id;
                            }
                        }

                        break;
                    case "styling":
                        foreach (object s in section.Children)
                        {
                            if (s is Node { Name: "style" } st && st.Attr("xml:id") is { } sid)
                            {
                                _styles[sid] = st;
                            }
                        }

                        break;
                    case "layout":
                        foreach (object r in section.Children)
                        {
                            if (r is Node { Name: "region" } rn && rn.Attr("xml:id") is { } rid)
                            {
                                regions.Add(ReadRegion(rn, rid));
                            }
                        }

                        break;
                }
            }
        }

        private static string Text(Node n)
        {
            var sb = new StringBuilder();
            foreach (object c in n.Children)
            {
                if (c is string s)
                {
                    sb.Append(s);
                }
                else if (c is Node child)
                {
                    sb.Append(Text(child));
                }
            }

            return sb.ToString();
        }

        private SubtitleRegion ReadRegion(Node rn, string id)
        {
            StyleBag bag = Resolve(rn, 0);
            double? x = null;
            double? y = null;
            double? w = null;
            double? h = null;
            var extras = new List<SubtitleExtra>(bag.Extras);
            if (rn.Attr("tts:origin") is { } origin && TryPair(origin, out double ox, out double oy))
            {
                x = ox;
                y = oy;
            }

            if (rn.Attr("tts:extent") is { } extent && TryPair(extent, out double ew, out double eh))
            {
                w = ew;
                h = eh;
            }

            // The origin and extent are applied in Resolve's extras as unmapped attributes; remove them from the extras.
            extras.RemoveAll(e => e.Key == "attr" && (e.Value.StartsWith("tts:origin=", StringComparison.Ordinal) || e.Value.StartsWith("tts:extent=", StringComparison.Ordinal)));
            return new SubtitleRegion(id)
            {
                X = x,
                Y = y,
                Width = w,
                Height = h,
                VerticalAlign = bag.VAlign,
                Extras = SubtitleList.From(extras),
            };
        }

        // "10% 20%" or "100px 50px" (pixels need the root extent) as percentages.
        private bool TryPair(string s, out double a, out double b)
        {
            a = b = 0;
            string[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 2 && TryLength(p[0], _extentW, out a) && TryLength(p[1], _extentH, out b);
        }

        private static bool TryLength(string s, int reference, out double percent)
        {
            percent = 0;
            if (s.EndsWith('%'))
            {
                return SubtitleNumbers.TryParseDouble(s.AsSpan(0, s.Length - 1), out percent);
            }

            if (s.EndsWith("px", StringComparison.Ordinal) && reference > 0 && SubtitleNumbers.TryParseDouble(s.AsSpan(0, s.Length - 2), out double px))
            {
                percent = px * 100 / reference;
                return true;
            }

            return false;
        }

        private StyleBag Resolve(Node n, int depth)
        {
            var bag = new StyleBag();
            if (depth > 8)
            {
                return bag;
            }

            if (n.Attr("style") is { } refs)
            {
                foreach (string id in refs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (_styles.TryGetValue(id, out Node? target) && !ReferenceEquals(target, n))
                    {
                        StyleBag inherited = Resolve(target, depth + 1);
                        bag.Text = Merge(bag.Text, inherited.Text);
                        bag.Align = inherited.Align != SubtitleAlign.Unspecified ? inherited.Align : bag.Align;
                        bag.VAlign = inherited.VAlign != SubtitleVerticalAlign.Unspecified ? inherited.VAlign : bag.VAlign;
                        bag.Writing = inherited.Writing != SubtitleWriting.Horizontal ? inherited.Writing : bag.Writing;
                    }
                }
            }

            foreach ((string key, string value) in n.Attrs)
            {
                Apply(bag, key, value);
            }

            return bag;
        }

        internal static TextStyle Merge(TextStyle a, TextStyle b) => a with
        {
            Bold = b.Bold ?? a.Bold,
            Italic = b.Italic ?? a.Italic,
            Underline = b.Underline ?? a.Underline,
            Strike = b.Strike ?? a.Strike,
            Color = b.Color ?? a.Color,
            Background = b.Background ?? a.Background,
            FontFamily = b.FontFamily ?? a.FontFamily,
            FontSize = b.FontSize ?? a.FontSize,
        };

        private static void Apply(StyleBag bag, string key, string value)
        {
            switch (key)
            {
                case "tts:color" when SubtitleColors.TryParse(value, out SubtitleColor c):
                    bag.Text = bag.Text with { Color = c };
                    return;
                case "tts:backgroundColor" when SubtitleColors.TryParse(value, out SubtitleColor bc):
                    bag.Text = bag.Text with { Background = bc };
                    return;
                case "tts:fontFamily":
                    bag.Text = bag.Text with { FontFamily = value.Trim().Trim('"', '\'') };
                    return;
                case "tts:fontSize" when TryFontSize(value, out SubtitleLength size):
                    bag.Text = bag.Text with { FontSize = size };
                    return;
                case "tts:fontStyle":
                    bag.Text = bag.Text with { Italic = value is "italic" or "oblique" };
                    return;
                case "tts:fontWeight":
                    bag.Text = bag.Text with { Bold = value == "bold" };
                    return;
                case "tts:textDecoration":
                    bag.Text = bag.Text with
                    {
                        Underline = value.Contains("underline", StringComparison.Ordinal) ? true : value.Contains("noUnderline", StringComparison.Ordinal) ? false : bag.Text.Underline,
                        Strike = value.Contains("lineThrough", StringComparison.Ordinal) ? true : value.Contains("noLineThrough", StringComparison.Ordinal) ? false : bag.Text.Strike,
                    };
                    return;
                case "tts:textAlign":
                    bag.Align = value switch
                    {
                        "left" => SubtitleAlign.Left,
                        "center" => SubtitleAlign.Center,
                        "right" => SubtitleAlign.Right,
                        "start" => SubtitleAlign.Start,
                        "end" => SubtitleAlign.End,
                        _ => bag.Align,
                    };
                    return;
                case "tts:displayAlign":
                    bag.VAlign = value switch
                    {
                        "before" => SubtitleVerticalAlign.Top,
                        "center" => SubtitleVerticalAlign.Middle,
                        "after" => SubtitleVerticalAlign.Bottom,
                        _ => bag.VAlign,
                    };
                    return;
                case "tts:writingMode":
                    bag.Writing = value switch
                    {
                        "tbrl" or "tb" => SubtitleWriting.VerticalRightToLeft,
                        "tblr" => SubtitleWriting.VerticalLeftToRight,
                        _ => SubtitleWriting.Horizontal,
                    };
                    return;
            }

            if (key.StartsWith("tts:", StringComparison.Ordinal) || key.StartsWith("itts:", StringComparison.Ordinal) || key.StartsWith("ebutts:", StringComparison.Ordinal))
            {
                bag.Extras.Add(new SubtitleExtra("ttml", "attr", key + "=" + value));
            }
        }

        private static bool TryFontSize(string v, out SubtitleLength size)
        {
            size = default;
            string s = v.Trim();
            int split = s.IndexOf(' ');
            if (split > 0)
            {
                s = s[..split];
            }

            (string suffix, SubtitleLengthUnit unit)[] units =
            [
                ("px", SubtitleLengthUnit.Pixel), ("pt", SubtitleLengthUnit.Point), ("em", SubtitleLengthUnit.Em), ("%", SubtitleLengthUnit.Percent),
            ];
            foreach ((string suffix, SubtitleLengthUnit unit) in units)
            {
                if (s.EndsWith(suffix, StringComparison.Ordinal) && SubtitleNumbers.TryParseDouble(s.AsSpan(0, s.Length - suffix.Length), out double n))
                {
                    size = new SubtitleLength(n, unit);
                    return true;
                }
            }

            return false;
        }

        private static bool IsPreserve(Node n, bool inherited) => n.Attr("xml:space") is { } v ? v == "preserve" : inherited;

        private void Container(Node n, MediaTime offset, string? style, string? region, bool preserve, string? language)
        {
            foreach (object o in n.Children)
            {
                if (o is not Node child)
                {
                    continue;
                }

                if (child.Name == "div")
                {
                    MediaTime off = offset;
                    if (child.Attr("begin") is { } b && TryTime(b, out MediaTime t))
                    {
                        off = SubtitleTimes.Add(offset, t);
                    }

                    Container(child, off, child.Attr("style") ?? style, child.Attr("region") ?? region, IsPreserve(child, preserve), child.Attr("xml:lang") ?? language);
                }
                else if (child.Name == "p")
                {
                    _src.CheckCue(_cues.Count + 1, child.Index);
                    Paragraph(child, offset, style, region, preserve, language);
                }
            }
        }

        private void Paragraph(Node p, MediaTime offset, string? inheritedStyle, string? inheritedRegion, bool inheritedPreserve, string? language)
        {
            MediaTime start = offset;
            if (p.Attr("begin") is { } b)
            {
                if (TryTime(b, out MediaTime t))
                {
                    start = SubtitleTimes.Add(offset, t);
                }
                else
                {
                    _src.Report(DefectKind.InvalidValue, DefectSeverity.Warning, p.Index, $"Time expression '{b}' is not valid.", "subtitles.ttml.time");
                }
            }

            MediaTime end = start;
            bool haveEnd = false;
            if (p.Attr("end") is { } e && TryTime(e, out MediaTime et))
            {
                end = SubtitleTimes.Add(offset, et);
                haveEnd = true;
            }
            else if (p.Attr("dur") is { } d && TryTime(d, out MediaTime dt))
            {
                end = SubtitleTimes.Add(start, dt);
                haveEnd = true;
            }

            if (!haveEnd)
            {
                _src.Report(DefectKind.MissingRequired, DefectSeverity.Warning, p.Index, "Paragraph has no end or dur; it is shown for no time.", "subtitles.ttml.no-end");
            }
            else if (end < start)
            {
                _src.Report(DefectKind.InvalidOrder, DefectSeverity.Warning, p.Index, "Cue ends before it starts.", "subtitles.ttml.end-before-start");
            }

            bool preserve = IsPreserve(p, inheritedPreserve);
            var builder = new TextBuilder(this, start, preserve);
            builder.Walk(p);
            builder.Finish(out string text, out List<SubtitleSpan> spans);

            // Styling given on the paragraph itself covers all of its text.
            StyleBag own = new();
            var extras = new List<SubtitleExtra>();
            foreach ((string key, string value) in p.Attrs)
            {
                if (key is "begin" or "end" or "dur" or "region" or "style" or "xml:id" or "xml:lang" or "xml:space" or "ttm:agent")
                {
                    continue;
                }

                int before = own.Extras.Count;
                Apply(own, key, value);
                if (own.Extras.Count > before && key is not ("tts:textAlign" or "tts:displayAlign" or "tts:writingMode"))
                {
                    extras.Add(own.Extras[^1]);
                }
                else if (own.Extras.Count == before && !key.StartsWith("tts:", StringComparison.Ordinal))
                {
                    extras.Add(new SubtitleExtra("ttml", "attr", key + "=" + value));
                }
            }

            if (!own.Text.IsEmpty && text.Length > 0)
            {
                spans.Insert(0, new SubtitleSpan(0, text.Length) { Style = own.Text });
            }

            string? styleAttr = p.Attr("style") ?? inheritedStyle;
            string? styleName = styleAttr?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            string? regionId = p.Attr("region") ?? inheritedRegion;
            CuePlacement? placement = regionId is not null || own.Align != SubtitleAlign.Unspecified || own.VAlign != SubtitleVerticalAlign.Unspecified || own.Writing != SubtitleWriting.Horizontal
                ? new CuePlacement { Region = regionId, Align = own.Align, VerticalAlign = own.VAlign, Writing = own.Writing }
                : null;
            string? speaker = null;
            if (p.Attr("ttm:agent") is { } agent)
            {
                string id = agent.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                speaker = _agents.TryGetValue(id, out string? name) ? name : id;
            }

            string? lang = p.Attr("xml:lang");
            _cues.Add(new SubtitleCue
            {
                Id = p.Attr("xml:id"),
                Start = start,
                End = end,
                Text = text,
                Spans = SubtitleList.From(spans),
                Speaker = speaker,
                StyleName = styleName,
                Placement = placement,
                Language = lang is not null && lang != language ? lang : null,
                Extras = SubtitleList.From(extras),
            });
        }

        private sealed class TextBuilder
        {
            private readonly TtmlReader _owner;
            private readonly MediaTime _pStart;
            private readonly StringBuilder _sb = new();
            private readonly List<SubtitleSpan> _spans = [];
            private bool _pendingSpace;
            private (int Start, int End)? _lastBase;
            private readonly bool _preserve;

            internal TextBuilder(TtmlReader owner, MediaTime pStart, bool preserve)
            {
                _owner = owner;
                _pStart = pStart;
                _preserve = preserve;
            }

            internal void Walk(Node n) => WalkChildren(n, _preserve);

            internal void Finish(out string text, out List<SubtitleSpan> spans)
            {
                if (!_preserve)
                {
                    while (_sb.Length > 0 && _sb[^1] == ' ')
                    {
                        _sb.Length--;
                    }
                }

                text = _sb.ToString();
                for (int i = 0; i < _spans.Count; i++)
                {
                    SubtitleSpan s = _spans[i];
                    if (s.End > text.Length)
                    {
                        _spans[i] = s with { Length = Math.Max(0, text.Length - s.Start) };
                    }
                }

                spans = _spans;
                spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            }

            private void Append(string t, bool preserve)
            {
                if (preserve)
                {
                    _sb.Append(t.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                    return;
                }

                foreach (char c in t)
                {
                    if (c is ' ' or '\t' or '\n' or '\r')
                    {
                        _pendingSpace = true;
                        continue;
                    }

                    if (_pendingSpace && _sb.Length > 0 && _sb[^1] != '\n')
                    {
                        _sb.Append(' ');
                    }

                    _pendingSpace = false;
                    _sb.Append(c);
                }
            }

            private void WalkChildren(Node n, bool preserve)
            {
                foreach (object o in n.Children)
                {
                    if (o is string s)
                    {
                        Append(s, preserve);
                    }
                    else if (o is Node e)
                    {
                        switch (e.Name)
                        {
                            case "br":
                                _pendingSpace = false;
                                _sb.Append('\n');
                                break;
                            case "span":
                                Span(e, preserve);
                                break;
                            case "metadata" or "set" or "animate":
                                break;
                            default:
                                WalkChildren(e, preserve);
                                break;
                        }
                    }
                }
            }

            private void Span(Node e, bool inheritedPreserve)
            {
                bool preserve = IsPreserve(e, inheritedPreserve);
                string? ruby = e.Attr("tts:ruby");
                if (ruby == "text")
                {
                    var annotation = new TextBuilder(_owner, _pStart, preserve);
                    annotation.WalkChildren(e, preserve);
                    annotation.Finish(out string t, out _);
                    if (_lastBase is { } b && b.End > b.Start)
                    {
                        _spans.Add(new SubtitleSpan(b.Start, b.End - b.Start) { Ruby = t.Trim() });
                    }

                    return;
                }

                if (!preserve && _pendingSpace && _sb.Length > 0 && _sb[^1] != '\n')
                {
                    // The space between words belongs before the span, not inside it.
                    _sb.Append(' ');
                    _pendingSpace = false;
                }

                int start = _sb.Length;
                WalkChildren(e, preserve);
                int end = _sb.Length;
                if (ruby == "base")
                {
                    _lastBase = (start, end);
                }

                StyleBag bag = _owner.Resolve(e, 0);
                MediaTime? wordStart = null;
                MediaTime? wordEnd = null;
                if (e.Attr("begin") is { } b2 && _owner.TryTime(b2, out MediaTime t0))
                {
                    wordStart = SubtitleTimes.Add(_pStart, t0);
                    if (e.Attr("end") is { } e2 && _owner.TryTime(e2, out MediaTime t1))
                    {
                        wordEnd = SubtitleTimes.Add(_pStart, t1);
                    }
                    else if (e.Attr("dur") is { } d2 && _owner.TryTime(d2, out MediaTime t2))
                    {
                        wordEnd = SubtitleTimes.Add(wordStart.Value, t2);
                    }
                }

                string? voice = null;
                if (e.Attr("ttm:agent") is { } agent)
                {
                    string id = agent.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    voice = _owner._agents.TryGetValue(id, out string? name) ? name : id;
                }

                string? lang = e.Attr("xml:lang");
                SubtitleExtra? raw = bag.Extras.Count > 0 ? new SubtitleExtra("ttml", "attrs", string.Join('\n', bag.Extras.Select(x => x.Value))) : null;
                bool has = !bag.Text.IsEmpty || wordStart is not null || voice is not null || lang is not null || raw is not null;
                if (has && end > start)
                {
                    _spans.Add(new SubtitleSpan(start, end - start)
                    {
                        Style = bag.Text.IsEmpty ? null : bag.Text,
                        WordStart = wordStart,
                        WordEnd = wordEnd,
                        Voice = voice,
                        Language = lang,
                        Raw = raw,
                    });
                }
            }
        }

        // Time expressions: clock-time (hh:mm:ss[.fraction] or hh:mm:ss:frames) and offset-time (number + h m s ms f t); TTML2 §10.3.
        internal bool TryTime(string s, out MediaTime time)
        {
            time = default;
            s = s.Trim();
            if (s.Length == 0)
            {
                return false;
            }

            string[] parts = s.Split(':');
            if (parts.Length is 3 or 4)
            {
                if (!ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong h) || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong m)
                    || h > 9_999_999 || m > 59_999)
                {
                    return false;
                }

                long whole;
                long fracTicks = 0;
                long fracScale = 1;
                string sec = parts[2];
                int dot = sec.IndexOf('.');
                string secInt = dot >= 0 ? sec[..dot] : sec;
                if (parts.Length == 3 && dot >= 0)
                {
                    string digits = sec[(dot + 1)..];
                    if (digits.Length == 0 || digits.Length > 9 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out fracTicks))
                    {
                        return false;
                    }

                    fracScale = Pow10(digits.Length);
                }

                if (!ulong.TryParse(secInt, NumberStyles.None, CultureInfo.InvariantCulture, out ulong sv) || sv > 59_999)
                {
                    return false;
                }

                whole = (long)(((h * 60) + m) * 60 + sv);
                if (parts.Length == 3)
                {
                    time = new MediaTime((whole * fracScale) + fracTicks, fracScale);
                    return true;
                }

                // hh:mm:ss:ff — frames at the frame rate (and its multiplier).
                string fr = parts[3];
                int fdot = fr.IndexOf('.');
                if (fdot >= 0)
                {
                    fr = fr[..fdot];
                }

                if (!long.TryParse(fr, NumberStyles.None, CultureInfo.InvariantCulture, out long frames))
                {
                    return false;
                }

                long perSecond = _frameRate * _frameMulN;
                time = new MediaTime((whole * perSecond) + (frames * _frameMulD), perSecond);
                return true;
            }

            if (parts.Length != 1)
            {
                return false;
            }

            string[] units = ["ms", "h", "m", "s", "f", "t"];
            foreach (string unit in units)
            {
                if (!s.EndsWith(unit, StringComparison.Ordinal))
                {
                    continue;
                }

                string num = s[..^unit.Length];
                int dot = num.IndexOf('.');
                string digits = dot >= 0 ? num[..dot] + num[(dot + 1)..] : num;
                int fracDigits = dot >= 0 ? num.Length - dot - 1 : 0;
                if (digits.Length == 0 || digits.Length > 15 || fracDigits > 9 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                {
                    return false;
                }

                long scale = Pow10(fracDigits);
                switch (unit)
                {
                    case "h":
                        time = new MediaTime(ticks * 3600, scale);
                        break;
                    case "m":
                        time = new MediaTime(ticks * 60, scale);
                        break;
                    case "s":
                        time = new MediaTime(ticks, scale);
                        break;
                    case "ms":
                        time = new MediaTime(ticks, scale * 1000);
                        break;
                    case "f":
                        time = new MediaTime(ticks * _frameMulD, scale * _frameRate * _frameMulN);
                        break;
                    default:
                        time = new MediaTime(ticks, scale * _tickRate);
                        break;
                }

                return true;
            }

            return false;
        }

        private static long Pow10(int n)
        {
            long v = 1;
            for (int i = 0; i < n; i++)
            {
                v *= 10;
            }

            return v;
        }
    }

    // ---- writing ----

    internal override string WriteCore(SubtitleDocument doc, ConversionReport report)
    {
        string nl = doc.NewLine;
        // Times are written as clock times in milliseconds when every time is a whole number of milliseconds; otherwise in ticks of
        // the finest timescale present (ttp:tickRate), so frame-based times stay exact.
        long tickRate = TickRateFor(doc);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(nl);
        sb.Append("<tt xmlns=\"").Append(NsTtml).Append("\" xmlns:tts=\"").Append(NsStyling).Append("\" xmlns:ttp=\"").Append(NsParameter)
            .Append("\" xmlns:ttm=\"").Append(NsMetadata).Append('"');
        bool usesItts = false;
        bool usesEbutts = false;
        foreach (SubtitleStyle s in doc.Styles)
        {
            foreach (SubtitleExtra e in s.Extras)
            {
                usesItts |= e.Format == "ttml" && e.Value.StartsWith("itts:", StringComparison.Ordinal);
                usesEbutts |= e.Format == "ttml" && e.Value.StartsWith("ebutts:", StringComparison.Ordinal);
            }
        }

        if (usesItts)
        {
            sb.Append(" xmlns:itts=\"").Append(NsItts).Append('"');
        }

        if (usesEbutts)
        {
            sb.Append(" xmlns:ebutts=\"").Append(NsEbutts).Append('"');
        }

        sb.Append(" xml:lang=\"").Append(Esc(doc.Language ?? "")).Append("\" ttp:timeBase=\"media\" ttp:profile=\"http://www.w3.org/ns/ttml/profile/imsc1/text\"");
        if (tickRate > 0)
        {
            sb.Append(" ttp:tickRate=\"").Append(tickRate.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        if (doc.ReferenceWidth > 0 && doc.ReferenceHeight > 0)
        {
            sb.Append(" tts:extent=\"").Append(doc.ReferenceWidth.ToString(CultureInfo.InvariantCulture)).Append("px ").Append(doc.ReferenceHeight.ToString(CultureInfo.InvariantCulture)).Append("px\"");
        }

        sb.Append('>').Append(nl);

        // Speakers become agents; placements without a region get generated regions.
        var agents = new List<string>();
        foreach (SubtitleCue cue in doc.Cues)
        {
            if (cue.Speaker is { } sp && !agents.Contains(sp))
            {
                agents.Add(sp);
            }

            foreach (SubtitleSpan span in cue.Spans)
            {
                if (span.Voice is { } v && !agents.Contains(v))
                {
                    agents.Add(v);
                }
            }
        }

        var autoRegions = new List<(string Id, string Origin, string Extent, string? DisplayAlign, string? Writing)>();
        var cueRegion = new string?[doc.Cues.Count];
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            cueRegion[c] = RegionFor(doc, doc.Cues[c], c, autoRegions, report);
        }

        sb.Append("  <head>").Append(nl);
        if (doc.Title is not null || agents.Count > 0)
        {
            sb.Append("    <metadata>").Append(nl);
            if (doc.Title is { } title)
            {
                sb.Append("      <ttm:title>").Append(Esc(title)).Append("</ttm:title>").Append(nl);
            }

            for (int a = 0; a < agents.Count; a++)
            {
                sb.Append("      <ttm:agent xml:id=\"a").Append((a + 1).ToString(CultureInfo.InvariantCulture)).Append("\" type=\"person\"><ttm:name type=\"alias\">")
                    .Append(Esc(agents[a])).Append("</ttm:name></ttm:agent>").Append(nl);
            }

            sb.Append("    </metadata>").Append(nl);
        }

        if (doc.Styles.Count > 0)
        {
            sb.Append("    <styling>").Append(nl);
            foreach (SubtitleStyle s in doc.Styles)
            {
                sb.Append("      <style xml:id=\"").Append(Esc(s.Name)).Append('"');
                AppendStyleAttrs(sb, s.Text, s.Placement?.Align ?? SubtitleAlign.Unspecified, report, -1);
                AppendExtraAttrs(sb, s.Extras);
                sb.Append("/>").Append(nl);
            }

            sb.Append("    </styling>").Append(nl);
        }

        if (doc.Regions.Count > 0 || autoRegions.Count > 0)
        {
            sb.Append("    <layout>").Append(nl);
            foreach (SubtitleRegion r in doc.Regions)
            {
                sb.Append("      <region xml:id=\"").Append(Esc(r.Id)).Append('"');
                if (r.X is { } x && r.Y is { } y)
                {
                    sb.Append(" tts:origin=\"").Append(SubtitleNumbers.Format(x)).Append("% ").Append(SubtitleNumbers.Format(y)).Append("%\"");
                }

                if (r.Width is { } w && r.Height is { } h)
                {
                    sb.Append(" tts:extent=\"").Append(SubtitleNumbers.Format(w)).Append("% ").Append(SubtitleNumbers.Format(h)).Append("%\"");
                }

                string? da = r.VerticalAlign switch { SubtitleVerticalAlign.Top => "before", SubtitleVerticalAlign.Middle => "center", SubtitleVerticalAlign.Bottom => "after", _ => null };
                if (da is not null)
                {
                    sb.Append(" tts:displayAlign=\"").Append(da).Append('"');
                }

                AppendExtraAttrs(sb, r.Extras);
                sb.Append("/>").Append(nl);
            }

            foreach ((string id, string origin, string extent, string? displayAlign, string? writing) in autoRegions)
            {
                sb.Append("      <region xml:id=\"").Append(id).Append("\" tts:origin=\"").Append(origin).Append("\" tts:extent=\"").Append(extent).Append('"');
                if (displayAlign is not null)
                {
                    sb.Append(" tts:displayAlign=\"").Append(displayAlign).Append('"');
                }

                if (writing is not null)
                {
                    sb.Append(" tts:writingMode=\"").Append(writing).Append('"');
                }

                sb.Append("/>").Append(nl);
            }

            sb.Append("    </layout>").Append(nl);
        }

        sb.Append("  </head>").Append(nl).Append("  <body>").Append(nl).Append("    <div>").Append(nl);
        for (int c = 0; c < doc.Cues.Count; c++)
        {
            WriteParagraph(sb, doc, doc.Cues[c], c, cueRegion[c], agents, tickRate, report);
            sb.Append(nl);
        }

        sb.Append("    </div>").Append(nl).Append("  </body>").Append(nl).Append("</tt>").Append(nl);
        return sb.ToString();
    }

    // 0 when clock times in milliseconds are exact for every time in the document; otherwise the common timescale (or the finest one).
    private static long TickRateFor(SubtitleDocument doc)
    {
        long common = 0;
        bool exactMs = true;
        void Look(MediaTime t)
        {
            if (!t.IsKnown)
            {
                return;
            }

            if (!t.TryRescale(1000, MediaRounding.TowardZero, out MediaTime ms) || ms != t)
            {
                exactMs = false;
            }

            if (common == 0)
            {
                common = t.Timescale;
            }
            else if (common != t.Timescale)
            {
                common = -1;
            }
        }

        foreach (SubtitleCue cue in doc.Cues)
        {
            Look(cue.Start);
            Look(cue.End);
            foreach (SubtitleSpan s in cue.Spans)
            {
                if (s.WordStart is { } ws)
                {
                    Look(ws);
                }

                if (s.WordEnd is { } we)
                {
                    Look(we);
                }
            }
        }

        return exactMs || common <= 0 || common > 1_000_000_000 ? 0 : common;
    }

    private static string Clock(MediaTime t, long tickRate, int cue, ConversionReport report)
    {
        if (tickRate > 0)
        {
            return t.Rescale(tickRate, MediaRounding.NearestAwayFromZero).Ticks.ToString(CultureInfo.InvariantCulture) + "t";
        }

        long ms = SubtitleLoss.Quantize(t, 1000, cue, SubtitleFeatures.Timing, report, "Time");
        if (ms < 0)
        {
            report.Add(cue, SubtitleFeatures.Timing, LossKind.Approximated, "Negative time clamped to zero.");
            ms = 0;
        }

        long s = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}.{ms % 1000:000}");
    }

    private static string? RegionFor(SubtitleDocument doc, SubtitleCue cue, int cueIndex, List<(string Id, string Origin, string Extent, string? DisplayAlign, string? Writing)> autoRegions, ConversionReport report)
    {
        if (cue.Placement is not { } p)
        {
            return null;
        }

        if (p.Region is { } declared)
        {
            foreach (SubtitleRegion r in doc.Regions)
            {
                if (r.Id == declared)
                {
                    return declared;
                }
            }
        }

        bool needs = p.VerticalAlign != SubtitleVerticalAlign.Unspecified || p.Line is not null || p.Position is not null || p.Size is not null
            || p.Writing != SubtitleWriting.Horizontal || p.Row is not null || p.Column is not null;
        if (!needs)
        {
            return null;
        }

        // A region is a rectangle: the position and size the cue states become its origin and extent.
        double w = p.Size ?? (p.Position is null ? 100 : 80);
        double x = p.Position is { } pos
            ? p.PositionAnchor switch { SubtitleAlign.Left => pos, SubtitleAlign.Right => pos - w, _ => pos - (w / 2) }
            : (100 - w) / 2;
        double h = 100;
        double y = 0;
        string? displayAlign = p.VerticalAlign switch { SubtitleVerticalAlign.Top => "before", SubtitleVerticalAlign.Middle => "center", SubtitleVerticalAlign.Bottom => "after", _ => null };
        if (p.Line is { } line || p.Row is not null)
        {
            h = 10;
            double pct = p.Line is { } l
                ? l.Unit switch { CueLineUnit.Percent => l.Value, CueLineUnit.Row => (l.Value - 1) * 100 / 15, _ => l.Value < 0 ? 100 + (l.Value * 5.33) - 5 : l.Value * 5.33 }
                : (p.Row!.Value - 1) * 100.0 / 15;
            y = Math.Clamp(pct, 0, 90);
            displayAlign ??= "before";
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Line position written as a generated region of 10% height.");
        }
        else if (p.VerticalAlign != SubtitleVerticalAlign.Unspecified)
        {
            h = 90;
            y = 5;
        }

        if (p.Column is { } col && p.Position is null)
        {
            x = col * 100.0 / 32;
            w = 100 - x;
            report.Add(cueIndex, SubtitleFeatures.Position, LossKind.Approximated, "Grid column written as the region's left edge (32 columns assumed).");
        }

        x = Math.Clamp(x, 0, 100);
        w = Math.Clamp(w, 1, 100 - x);
        string origin = SubtitleNumbers.Format(x) + "% " + SubtitleNumbers.Format(y) + "%";
        string extent = SubtitleNumbers.Format(w) + "% " + SubtitleNumbers.Format(h) + "%";
        string? writing = p.Writing switch { SubtitleWriting.VerticalRightToLeft => "tbrl", SubtitleWriting.VerticalLeftToRight => "tblr", _ => null };
        for (int i = 0; i < autoRegions.Count; i++)
        {
            if (autoRegions[i].Origin == origin && autoRegions[i].Extent == extent && autoRegions[i].DisplayAlign == displayAlign && autoRegions[i].Writing == writing)
            {
                return autoRegions[i].Id;
            }
        }

        string id = "r" + (autoRegions.Count + 1).ToString(CultureInfo.InvariantCulture);
        autoRegions.Add((id, origin, extent, displayAlign, writing));
        return id;
    }

    private static void WriteParagraph(StringBuilder sb, SubtitleDocument doc, SubtitleCue cue, int cueIndex, string? region, List<string> agents, long tickRate, ConversionReport report)
    {
        sb.Append("      <p");
        if (cue.Id is { } id)
        {
            sb.Append(" xml:id=\"").Append(Esc(id)).Append('"');
        }

        sb.Append(" begin=\"").Append(Clock(cue.Start, tickRate, cueIndex, report)).Append("\" end=\"").Append(Clock(cue.End, tickRate, cueIndex, report)).Append('"');
        if (region is not null)
        {
            sb.Append(" region=\"").Append(Esc(region)).Append('"');
        }

        if (cue.StyleName is { } style)
        {
            sb.Append(" style=\"").Append(Esc(style)).Append('"');
        }

        if (cue.Speaker is { } speaker)
        {
            sb.Append(" ttm:agent=\"a").Append((agents.IndexOf(speaker) + 1).ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        if (cue.Language is { } lang)
        {
            sb.Append(" xml:lang=\"").Append(Esc(lang)).Append('"');
        }

        if (cue.Placement is { } pl)
        {
            string? align = pl.Align switch
            {
                SubtitleAlign.Left => "left",
                SubtitleAlign.Center => "center",
                SubtitleAlign.Right => "right",
                SubtitleAlign.Start => "start",
                SubtitleAlign.End => "end",
                _ => null,
            };
            if (align is not null)
            {
                sb.Append(" tts:textAlign=\"").Append(align).Append('"');
            }
        }

        AppendExtraAttrs(sb, cue.Extras);
        sb.Append('>');

        var intervals = new List<MarkupInterval>();
        var points = new List<MarkupPoint>();
        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.Length == 0)
            {
                continue;
            }

            var attrs = new StringBuilder();
            if (span.Style is { } st)
            {
                AppendStyleAttrs(attrs, st, SubtitleAlign.Unspecified, report, cueIndex);
            }

            if (span.Language is { } sl)
            {
                attrs.Append(" xml:lang=\"").Append(Esc(sl)).Append('"');
            }

            if (span.Voice is { } v)
            {
                attrs.Append(" ttm:agent=\"a").Append((agents.IndexOf(v) + 1).ToString(CultureInfo.InvariantCulture)).Append('"');
            }

            if (span.WordStart is { } ws)
            {
                MediaTime rel = SubtitleTimes.Subtract(ws, cue.Start);
                attrs.Append(" begin=\"").Append(Clock(rel, tickRate, cueIndex, report)).Append('"');
                if (span.WordEnd is { } we)
                {
                    attrs.Append(" end=\"").Append(Clock(SubtitleTimes.Subtract(we, cue.Start), tickRate, cueIndex, report)).Append('"');
                }
            }

            if (span.Raw is { Format: "ttml", Key: "attrs" } raw)
            {
                foreach (string line in raw.Value.Split('\n'))
                {
                    AppendOneExtra(attrs, line);
                }
            }

            if (attrs.Length > 0)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<span" + attrs + ">", "</span>", 1));
            }

            if (span.Ruby is { } ruby)
            {
                intervals.Add(new MarkupInterval(span.Start, span.End, "<span tts:ruby=\"container\"><span tts:ruby=\"base\">", "</span><span tts:ruby=\"text\">" + Esc(ruby) + "</span></span>", 0));
            }
        }

        MarkupWriter.Write(sb, Sanitize(cue.Text), intervals, points, (o, s) =>
        {
            foreach (char ch in s)
            {
                if (ch == '\n')
                {
                    o.Append("<br/>");
                }
                else
                {
                    EscChar(o, ch);
                }
            }
        });
        sb.Append("</p>");
    }

    private static void AppendStyleAttrs(StringBuilder sb, TextStyle st, SubtitleAlign align, ConversionReport report, int cue)
    {
        if (st.Color is { } c)
        {
            sb.Append(" tts:color=\"").Append(SubtitleColors.Hex(c, true)).Append('"');
        }

        if (st.Background is { } bg)
        {
            sb.Append(" tts:backgroundColor=\"").Append(SubtitleColors.Hex(bg, true)).Append('"');
        }

        if (st.FontFamily is { } ff)
        {
            sb.Append(" tts:fontFamily=\"").Append(Esc(ff)).Append('"');
        }

        if (st.FontSize is { } fs)
        {
            (string suffix, double value) = fs.Unit switch
            {
                SubtitleLengthUnit.Percent => ("%", fs.Value),
                SubtitleLengthUnit.Em => ("em", fs.Value),
                SubtitleLengthUnit.Point => ("px", fs.Value * 4 / 3),
                _ => ("px", fs.Value),
            };
            if (fs.Unit == SubtitleLengthUnit.Point)
            {
                report.Add(cue, SubtitleFeatures.Font, LossKind.Approximated, "Font size in points written as pixels (4/3).");
            }

            sb.Append(" tts:fontSize=\"").Append(SubtitleNumbers.Format(value)).Append(suffix).Append('"');
        }

        if (st.Italic is { } it)
        {
            sb.Append(" tts:fontStyle=\"").Append(it ? "italic" : "normal").Append('"');
        }

        if (st.Bold is { } b)
        {
            sb.Append(" tts:fontWeight=\"").Append(b ? "bold" : "normal").Append('"');
        }

        if (st.Underline is not null || st.Strike is not null)
        {
            var deco = new List<string>();
            deco.Add(st.Underline == true ? "underline" : "noUnderline");
            deco.Add(st.Strike == true ? "lineThrough" : "noLineThrough");
            if (st.Underline == true || st.Strike == true)
            {
                deco.RemoveAll(d => d.StartsWith("no", StringComparison.Ordinal));
            }

            sb.Append(" tts:textDecoration=\"").Append(string.Join(' ', deco)).Append('"');
        }

        string? a = align switch { SubtitleAlign.Left => "left", SubtitleAlign.Center => "center", SubtitleAlign.Right => "right", SubtitleAlign.Start => "start", SubtitleAlign.End => "end", _ => null };
        if (a is not null)
        {
            sb.Append(" tts:textAlign=\"").Append(a).Append('"');
        }
    }

    private static void AppendExtraAttrs(StringBuilder sb, SubtitleList<SubtitleExtra> extras)
    {
        foreach (SubtitleExtra e in extras)
        {
            if (e.Format == "ttml" && e.Key == "attr")
            {
                AppendOneExtra(sb, e.Value);
            }
        }
    }

    private static void AppendOneExtra(StringBuilder sb, string nameValue)
    {
        int eq = nameValue.IndexOf('=');
        if (eq > 0)
        {
            string name = nameValue[..eq];
            bool ok = true;
            foreach (char ch in name)
            {
                ok &= char.IsAsciiLetterOrDigit(ch) || ch is ':' or '-' or '_' or '.';
            }

            if (ok)
            {
                sb.Append(' ').Append(name).Append("=\"").Append(Esc(nameValue[(eq + 1)..])).Append('"');
            }
        }
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in Sanitize(s))
        {
            EscChar(sb, c);
        }

        return sb.ToString();
    }

    // Lone surrogates cannot appear in XML; each becomes U+FFFD (one character for one, so span offsets stay valid).
    private static string Sanitize(string s)
    {
        StringBuilder? fixedText = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool bad = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]) : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
            if (bad)
            {
                fixedText ??= new StringBuilder(s);
                fixedText[i] = '\uFFFD';
            }
        }

        return fixedText?.ToString() ?? s;
    }

    // XML 1.0 forbids most control characters and lone surrogates; they cannot be escaped, so they are dropped.
    private static void EscChar(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '&':
                sb.Append("&amp;");
                break;
            case '<':
                sb.Append("&lt;");
                break;
            case '>':
                sb.Append("&gt;");
                break;
            case '"':
                sb.Append("&quot;");
                break;
            case '\t':
                sb.Append("&#9;");
                break;
            case '\r':
                break;
            default:
                if (c >= ' ' && c != '\uFFFE' && c != '\uFFFF')
                {
                    sb.Append(c);
                }

                break;
        }
    }
}

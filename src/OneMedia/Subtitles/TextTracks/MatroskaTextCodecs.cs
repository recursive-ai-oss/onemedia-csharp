using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Captions;

// Matroska and WebM text tracks (RFC 9559 codec mappings; matroska.org "Subtitles" and "Codec specs" pages; WebM WebVTT blocks).

internal abstract class CueCollector : ITextTrackDecoder
{
    protected readonly MediaTrack Track;
    protected readonly DefectLog Defects;
    protected readonly MediaLimits Limits;
    protected readonly List<SubtitleCue> Cues = [];

    protected CueCollector(MediaTrack track, DefectLog defects, MediaLimits limits)
    {
        Track = track;
        Defects = defects;
        Limits = limits;
    }

    public abstract void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional);

    public virtual SubtitleDocument Finish() => Document(null, null, [], [], "text");

    protected SubtitleDocument Document(string? format, SubtitleDocument? header, IEnumerable<SubtitleExtra> extras, IEnumerable<SubtitleStyle> styles, string fallbackFormat)
    {
        Cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        SubtitleDocument baseDoc = header ?? new SubtitleDocument();
        return baseDoc with
        {
            Format = format ?? fallbackFormat,
            Language = SidecarBridge.LanguageOrNull(Track) ?? baseDoc.Language,
            Cues = SubtitleList.From(Cues),
        };
    }

    protected static MediaTime EndOf(MediaTime start, MediaTime duration) => duration.IsKnown && duration.Ticks > 0 ? start + duration : start;
}

internal sealed class SubRipDecoder : CueCollector
{
    public SubRipDecoder(MediaTrack t, DefectLog d, MediaLimits l) : base(t, d, l) { }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        string body = SidecarBridge.OneBlock(SidecarBridge.DecodeText(data));
        if (body.Trim().Length == 0)
        {
            return;
        }

        SubtitleCue? cue = ParseOne(body, Defects, Limits);
        if (cue is null)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, info.SourceRange.Offset, "S_TEXT/UTF8 block did not read as text; skipped.", "texttrack.subrip.block");
            return;
        }

        Cues.Add(cue with { Start = start, End = EndOf(start, duration) });
    }

    internal static SubtitleCue? ParseOne(string body, DefectLog defects, MediaLimits limits)
    {
        SubtitleDocument doc = SubtitleFormats.Srt.ReadText("1\n00:00:00,000 --> 00:00:01,000\n" + body + "\n", defects, limits);
        return doc.Cues.Count > 0 ? doc.Cues[0] : null;
    }

    public override SubtitleDocument Finish() => Document("srt", null, [], [], "srt");
}

internal static class SubRipEncoder
{
    public static TextTrackEncoding Encode(SubtitleDocument d, TextTrackOptions o)
    {
        LossCounter losses = new();
        SubtitleDocument doc = SidecarBridge.Playable(d, losses, out List<SubtitleCue> cues);
        SubtitleOutput output = SubtitleFormats.Srt.Write(doc);
        SidecarBridge.AddLosses(output.Report, losses);
        List<string> bodies = SrtBodies(output.Text);
        List<TextTrackPacket> packets = [];
        SidecarBridge.PerCuePackets(cues, o, (i, _) => (Encoding.UTF8.GetBytes(i < bodies.Count ? bodies[i].Replace("\n", "\r\n", StringComparison.Ordinal) : string.Empty), null), packets, out long lastEnd, out bool inexact);
        MediaTrack track = SidecarBridge.Track(o, d, CodecId.SubRip, "S_TEXT/UTF8", [], lastEnd, packets.Count);
        return new TextTrackEncoding(track, packets, losses.ToList(), SidecarBridge.Notes(inexact, o.Timescale));
    }

    private static List<string> SrtBodies(string text)
    {
        List<string> bodies = [];
        foreach (string block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            string[] lines = block.Trim('\n').Split('\n');
            int t = Array.FindIndex(lines, static l => l.Contains("-->", StringComparison.Ordinal));
            if (t >= 0)
            {
                bodies.Add(string.Join('\n', lines, t + 1, lines.Length - t - 1));
            }
        }

        return bodies;
    }
}

internal sealed class AssDecoder : CueCollector
{
    private readonly string _header;
    private readonly List<(string Line, MediaTime Start, MediaTime End)> _events = [];

    public AssDecoder(MediaTrack t, DefectLog d, MediaLimits l) : base(t, d, l)
    {
        string h = SidecarBridge.DecodeText(t.Configuration.Data.Span).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        if (!h.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
        {
            h += "\n\n[Events]";
        }

        if (!h.Contains("\nFormat:", StringComparison.Ordinal) || h.LastIndexOf("\nFormat:", StringComparison.Ordinal) < h.IndexOf("[Events]", StringComparison.OrdinalIgnoreCase))
        {
            h += "\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
        }

        _header = h;
    }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        if (!MatroskaAssEvent.TryParse(data, out MatroskaAssEvent? e) || e is null)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, info.SourceRange.Offset, "S_TEXT/ASS block does not have the nine event fields; skipped.", "texttrack.ass.block");
            return;
        }

        int n = _events.Count;
        string line = string.Create(CultureInfo.InvariantCulture, $"Dialogue: {e.Layer},{Stamp(n)},{Stamp(n + 1)},{e.Style},{e.Name},{e.MarginL},{e.MarginR},{e.MarginV},{e.Effect},{e.Text}");
        _events.Add((line, start, EndOf(start, duration)));
    }

    // Each event gets its own second as a start so that the parsed cue can be matched back to its packet; the real times replace them.
    private static string Stamp(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}.00");

    public override SubtitleDocument Finish()
    {
        StringBuilder sb = new(_header);
        sb.Append('\n');
        foreach ((string line, _, _) in _events)
        {
            sb.Append(line).Append('\n');
        }

        SubtitleDocument doc = SubtitleFormats.Ass.ReadText(sb.ToString(), Defects, Limits);
        foreach (SubtitleCue c in doc.Cues)
        {
            long idx = c.Start.Rescale(1, MediaRounding.TowardZero).Ticks;
            if (idx >= 0 && idx < _events.Count)
            {
                Cues.Add(c with { Start = _events[(int)idx].Start, End = _events[(int)idx].End });
            }
        }

        return Document("ass", doc, [], [], "ass");
    }
}

internal static class AssEncoder
{
    public static TextTrackEncoding Encode(SubtitleDocument d, TextTrackOptions o)
    {
        LossCounter losses = new();
        SubtitleDocument doc = SidecarBridge.Playable(d, losses, out List<SubtitleCue> cues);
        SubtitleOutput output = SubtitleFormats.Ass.Write(doc);
        SidecarBridge.AddLosses(output.Report, losses);
        StringBuilder header = new();
        List<string> events = [];
        foreach (string line in output.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("Dialogue:", StringComparison.Ordinal))
            {
                events.Add(line[9..].TrimStart());
            }
            else if (events.Count == 0)
            {
                header.Append(line).Append("\r\n");
            }
        }

        List<TextTrackPacket> packets = [];
        SidecarBridge.PerCuePackets(cues, o, (i, _) =>
        {
            // Dialogue: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text  ->  ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text
            string[] f = (i < events.Count ? events[i] : ",,,,,,,,").Split(',', 10);
            string text = f.Length > 9 ? f[9] : string.Empty;
            string block = string.Create(CultureInfo.InvariantCulture, $"{i},{f[0]},{Field(f, 3)},{Field(f, 4)},{Field(f, 5)},{Field(f, 6)},{Field(f, 7)},{Field(f, 8)},{text}");
            return (Encoding.UTF8.GetBytes(block), null);
        }, packets, out long lastEnd, out bool inexact);
        MediaTrack track = SidecarBridge.Track(o, d, new CodecId("ass"), "S_TEXT/ASS", Encoding.UTF8.GetBytes(header.ToString()), lastEnd, packets.Count);
        return new TextTrackEncoding(track, packets, losses.ToList(), SidecarBridge.Notes(inexact, o.Timescale));
    }

    private static string Field(string[] f, int i) => i < f.Length ? f[i] : string.Empty;
}

internal class MatroskaWebVttDecoder : CueCollector
{
    private readonly SubtitleDocument? _header;

    public MatroskaWebVttDecoder(MediaTrack t, DefectLog d, MediaLimits l) : base(t, d, l)
    {
        string h = SidecarBridge.DecodeText(t.Configuration.Data.Span);
        if (h.Trim().Length > 0)
        {
            try
            {
                _header = SubtitleFormats.WebVtt.ReadText(h.TrimEnd() + "\n\n", d, l);
            }
            catch (MediaException)
            {
                d.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 0, "The WebVTT CodecPrivate did not read as a WebVTT header.", "texttrack.webvtt.header");
            }
        }
    }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        MatroskaWebVttAdditions a = MatroskaWebVttAdditions.Parse(additional);
        AddCue(info.SourceRange.Offset, start, duration, a.Identifier, a.Settings, SidecarBridge.DecodeText(data), a.Comments);
    }

    protected void AddCue(long offset, MediaTime start, MediaTime duration, string id, string settings, string payload, string comments)
    {
        MediaTime end = EndOf(start, duration);
        string body = SidecarBridge.ShiftInnerTimestamps(SidecarBridge.OneBlock(payload), SidecarBridge.StartMs(new SubtitleCue { Start = start }));
        if (body.Trim().Length == 0)
        {
            return;
        }

        string idLine = id.Length > 0 && !id.Contains("-->", StringComparison.Ordinal) ? id.Replace('\n', ' ') + "\n" : string.Empty;
        string text = string.Concat("WEBVTT\n\n", idLine, SidecarBridge.Millis(start, '.'), " --> ", SidecarBridge.Millis(end > start ? end : start + new MediaTime(1, 1000), '.'), settings.Length > 0 ? " " + settings.Replace('\n', ' ') : string.Empty, "\n", body, "\n");
        SubtitleDocument doc = SubtitleFormats.WebVtt.ReadText(text, Defects, Limits);
        if (doc.Cues.Count == 0)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "WebVTT block did not read as a cue; skipped.", "texttrack.webvtt.block");
            return;
        }

        SubtitleCue cue = doc.Cues[0] with { Start = start, End = end };
        if (comments.Length > 0)
        {
            cue = cue with { Extras = SubtitleList.From([.. cue.Extras, new SubtitleExtra("webvtt", "note", comments)]) };
        }

        Cues.Add(cue);
    }

    public override SubtitleDocument Finish() => Document("webvtt", _header, [], [], "webvtt");
}

internal sealed class WebmWebVttDecoder : MatroskaWebVttDecoder
{
    public WebmWebVttDecoder(MediaTrack t, DefectLog d, MediaLimits l) : base(t, d, l) { }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        WebmWebVttBlock b = WebmWebVttBlock.Parse(data);
        AddCue(info.SourceRange.Offset, start, duration, b.Identifier, b.Settings, b.Payload, string.Empty);
    }
}

internal static class MatroskaWebVttEncoder
{
    public static TextTrackEncoding Encode(SubtitleDocument d, TextTrackOptions o, bool webm)
    {
        LossCounter losses = new();
        SubtitleDocument doc = SidecarBridge.Playable(d, losses, out List<SubtitleCue> cues);
        SubtitleOutput output = SubtitleFormats.WebVtt.Write(doc);
        SidecarBridge.AddLosses(output.Report, losses);
        VttBlocks blocks = VttBlocks.Parse(output.Text);
        List<TextTrackPacket> packets = [];
        SidecarBridge.PerCuePackets(cues, o, (i, cue) =>
        {
            VttCueBlock b = i < blocks.Cues.Count ? blocks.Cues[i] : new VttCueBlock(string.Empty, string.Empty, cue.Text, string.Empty);
            string payload = SidecarBridge.ShiftInnerTimestamps(b.Payload, -SidecarBridge.StartMs(cue));
            if (webm)
            {
                return (new WebmWebVttBlock(b.Id, b.Settings, payload).ToBytes(), null);
            }

            string comments = b.Comments;
            byte[] add = new MatroskaWebVttAdditions(b.Settings, b.Id, comments).ToBytes();
            return (Encoding.UTF8.GetBytes(payload), add.Length == 0 ? null : add);
        }, packets, out long lastEnd, out bool inexact);
        if (webm)
        {
            MediaTrack wt = SidecarBridge.Track(o, d, CodecId.WebVtt, "D_WEBVTT/SUBTITLES", [], lastEnd, packets.Count);
            return new TextTrackEncoding(wt, packets, losses.ToList(), SidecarBridge.Notes(inexact, o.Timescale));
        }

        MediaTrack track = SidecarBridge.Track(o, d, CodecId.WebVtt, "S_TEXT/WEBVTT", Encoding.UTF8.GetBytes(blocks.Header + "\n"), lastEnd, packets.Count);
        return new TextTrackEncoding(track, packets, losses.ToList(), SidecarBridge.Notes(inexact, o.Timescale));
    }
}

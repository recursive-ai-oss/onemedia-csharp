using System.Text;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Captions;

// WebVTT (wvtt) and XML subtitles (stpp) in ISO base media files. ISO/IEC 14496-30 is not available; the carriage here follows ISO/IEC 14496-12
// sample entries, the W3C formats, and files written by MP4Box (see docs/text-tracks.md for what was observed and what is assumed).

internal sealed class WebVttIsoDecoder : CueCollector
{
    private readonly SubtitleDocument? _header;
    private readonly Dictionary<(string?, string?, string), int> _open = [];
    private string? _pendingNote;

    public WebVttIsoDecoder(MediaTrack t, DefectLog d) : base(t, d, MediaLimits.Default)
    {
        if (WebVttSampleEntry.TryParse(t.Configuration.Data.Span, out WebVttSampleEntry? entry) && entry is not null)
        {
            try
            {
                _header = SubtitleFormats.WebVtt.ReadText(entry.HeaderText.TrimEnd() + "\n\n", d, Limits);
            }
            catch (MediaException)
            {
                d.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 0, "The vttC header did not read as a WebVTT header.", "texttrack.wvtt.header");
            }
        }
    }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        _ = WebVttSample.TryParse(data, out WebVttSample? sample);
        MediaTime end = EndOf(start, duration);
        Dictionary<(string?, string?, string), int> seen = [];
        foreach (WebVttSampleItem item in sample!.Items)
        {
            if (item is WebVttAdditionalText note)
            {
                _pendingNote = note.Text;
            }
            else if (item is WebVttCueBox cue)
            {
                var key = (cue.Identifier, cue.Settings, cue.Payload);
                // A cue that continues from the previous sample (same text, identifier and settings, and a cue time) extends the cue already read.
                if (cue.CueTime is not null && _open.TryGetValue(key, out int at) && Cues[at].End == start)
                {
                    Cues[at] = Cues[at] with { End = end };
                    seen[key] = at;
                    continue;
                }

                int made = Make(info.SourceRange.Offset, start, end, cue);
                if (made >= 0)
                {
                    seen[key] = made;
                }
            }
        }

        _open.Clear();
        foreach (KeyValuePair<(string?, string?, string), int> kv in seen)
        {
            _open[kv.Key] = kv.Value;
        }
    }

    private int Make(long offset, MediaTime start, MediaTime end, WebVttCueBox box)
    {
        string body = SidecarBridge.OneBlock(box.Payload);
        string idLine = box.Identifier is { Length: > 0 } id && !id.Contains("-->", StringComparison.Ordinal) ? id.Replace('\n', ' ') + "\n" : string.Empty;
        string text = string.Concat("WEBVTT\n\n", idLine, SidecarBridge.Millis(start, '.'), " --> ", SidecarBridge.Millis(end > start ? end : start + new MediaTime(1, 1000), '.'),
            box.Settings is { Length: > 0 } s ? " " + s.Replace('\n', ' ') : string.Empty, "\n", body.Length == 0 ? " " : body, "\n");
        SubtitleDocument doc = SubtitleFormats.WebVtt.ReadText(text, Defects, Limits);
        if (doc.Cues.Count == 0)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "A vttc box did not read as a cue; skipped.", "texttrack.wvtt.cue");
            return -1;
        }

        SubtitleCue cue = doc.Cues[0] with { Start = start, End = end };
        if (_pendingNote is not null)
        {
            cue = cue with { Extras = SubtitleList.From([.. cue.Extras, new SubtitleExtra("webvtt", "note", _pendingNote)]) };
            _pendingNote = null;
        }

        Cues.Add(cue);
        return Cues.Count - 1;
    }

    public override SubtitleDocument Finish() => Document("webvtt", _header, [], [], "webvtt");
}

internal static class WebVttIsoEncoder
{
    public static TextTrackEncoding Encode(SubtitleDocument d, TextTrackOptions o)
    {
        LossCounter losses = new();
        SubtitleDocument doc = SidecarBridge.Playable(d, losses, out List<SubtitleCue> cues);
        SubtitleOutput output = SubtitleFormats.WebVtt.Write(doc);
        SidecarBridge.AddLosses(output.Report, losses);
        VttBlocks blocks = VttBlocks.Parse(output.Text);
        Dictionary<SubtitleCue, int> index = new(ReferenceEqualityComparer.Instance as IEqualityComparer<SubtitleCue>);
        for (int i = 0; i < cues.Count; i++)
        {
            index[cues[i]] = i;
        }

        List<TextTrackPacket> packets = [];
        bool inexact = false;
        long cursor = 0;
        long lastEnd = 0;
        int gaps = 0;
        byte[] empty = new WebVttSample([new WebVttEmptyCue()]).ToArray();
        foreach (TextSegment seg in TextSegmenter.Split(cues, out _))
        {
            long s = Math.Max(TextTrackTimes.ToTicks(seg.Start, o.Timescale, ref inexact), cursor);
            long e = TextTrackTimes.ToTicks(seg.End, o.Timescale, ref inexact);
            if (e <= s)
            {
                continue;
            }

            if (s > cursor)
            {
                packets.Add(SidecarBridge.Packet(cursor, s - cursor, o.Timescale, empty));
                gaps++;
            }
            else if (packets.Count == 0 && s > 0)
            {
                packets.Add(SidecarBridge.Packet(0, s, o.Timescale, empty));
                gaps++;
            }

            byte[] bytes;
            if (seg.Active.Count == 0)
            {
                bytes = empty;
                gaps++;
            }
            else
            {
                List<WebVttSampleItem> items = [];
                foreach (SubtitleCue c in seg.Active)
                {
                    VttCueBlock b = blocks.Cues[index[c]];
                    bool continuing = c.Start < seg.Start;
                    if (!continuing && b.Comments.Length > 0)
                    {
                        items.Add(new WebVttAdditionalText(b.Comments));
                    }

                    items.Add(new WebVttCueBox(b.Payload)
                    {
                        Settings = b.Settings.Length > 0 ? b.Settings : null,
                        Identifier = b.Id.Length > 0 ? b.Id : null,
                        CueTime = continuing ? SidecarBridge.Millis(c.Start, '.') : null,
                    });
                }

                bytes = new WebVttSample(items).ToArray();
            }

            packets.Add(SidecarBridge.Packet(s, e - s, o.Timescale, bytes));
            cursor = e;
            lastEnd = e;
        }

        List<string> notes = SidecarBridge.Notes(inexact, o.Timescale);
        if (gaps > 0)
        {
            notes.Add($"{gaps} empty-cue samples were written for the gaps between cues.");
        }

        byte[] config = new WebVttSampleEntry { HeaderText = blocks.Header }.ToConfiguration();
        MediaTrack track = SidecarBridge.Track(o, d, CodecId.WebVtt, WebVttSampleEntry.Code, [], lastEnd, packets.Count) with { Configuration = new CodecConfiguration(config, WebVttSampleEntry.Code) };
        return new TextTrackEncoding(track, packets, losses.ToList(), notes);
    }
}

internal sealed class XmlSubtitleIsoDecoder : CueCollector
{
    private SubtitleDocument? _first;
    private readonly HashSet<(long, long, long, long, string, string?)> _seen = [];

    public XmlSubtitleIsoDecoder(MediaTrack t, DefectLog d, MediaLimits l) : base(t, d, l) { }

    public override void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional)
    {
        string xml = SidecarBridge.DecodeText(data);
        if (xml.Trim().Length == 0)
        {
            return;
        }

        SubtitleDocument doc;
        try
        {
            doc = SubtitleFormats.Ttml.ReadText(xml, Defects, Limits);
        }
        catch (MediaException)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, info.SourceRange.Offset, "An stpp sample did not read as a TTML document; skipped.", "texttrack.stpp.sample");
            return;
        }

        _first ??= doc;

        // Times inside the document count from the start of the track's timeline (observed in MP4Box-written files), so they only shift by the offset between decode and presentation time.
        MediaTime shift = start - info.Pts;
        foreach (SubtitleCue c in doc.Cues)
        {
            SubtitleCue moved = c with { Start = c.Start + shift, End = c.End + shift };
            (long, long, long, long, string, string?) key = (moved.Start.Ticks, moved.Start.Timescale, moved.End.Ticks, moved.End.Timescale, moved.Text, moved.Id);
            if (_seen.Add(key))
            {
                Cues.Add(moved);
            }
        }
    }

    public override SubtitleDocument Finish() => Document("ttml", _first is null ? null : _first with { Cues = SubtitleList.Empty<SubtitleCue>() }, [], [], "ttml");
}

internal static class XmlSubtitleIsoEncoder
{
    public static TextTrackEncoding Encode(SubtitleDocument d, TextTrackOptions o)
    {
        LossCounter losses = new();
        SubtitleDocument doc = SidecarBridge.Playable(d, losses, out List<SubtitleCue> cues);
        SidecarBridge.AddLosses(SubtitleFormats.Ttml.Write(doc).Report, losses);
        List<TextTrackPacket> packets = [];
        bool inexact = false;
        long cursor = 0;
        long lastEnd = 0;
        int gaps = 0;
        byte[] empty = Encoding.UTF8.GetBytes(SubtitleFormats.Ttml.Write(doc with { Cues = SubtitleList.Empty<SubtitleCue>() }).Text);
        foreach (TextSegment seg in TextSegmenter.Split(cues, out _))
        {
            long s = Math.Max(TextTrackTimes.ToTicks(seg.Start, o.Timescale, ref inexact), cursor);
            long e = TextTrackTimes.ToTicks(seg.End, o.Timescale, ref inexact);
            if (e <= s)
            {
                continue;
            }

            if (s > cursor || (packets.Count == 0 && s > 0))
            {
                packets.Add(SidecarBridge.Packet(cursor, s - cursor, o.Timescale, empty));
                gaps++;
            }

            byte[] bytes = empty;
            if (seg.Active.Count == 0)
            {
                gaps++;
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(SubtitleFormats.Ttml.Write(doc with { Cues = SubtitleList.From([.. seg.Active]) }).Text);
            }

            packets.Add(SidecarBridge.Packet(s, e - s, o.Timescale, bytes));
            cursor = e;
            lastEnd = e;
        }

        List<string> notes = SidecarBridge.Notes(inexact, o.Timescale);
        if (gaps > 0)
        {
            notes.Add($"{gaps} empty documents were written for the gaps between cues.");
        }

        byte[] config = new XmlSubtitleSampleEntry().ToConfiguration();
        MediaTrack track = SidecarBridge.Track(o, d, new CodecId("ttml"), XmlSubtitleSampleEntry.Code, [], lastEnd, packets.Count) with { Configuration = new CodecConfiguration(config, XmlSubtitleSampleEntry.Code) };
        return new TextTrackEncoding(track, packets, losses.ToList(), notes);
    }
}

using System.Globalization;
using System.Text;
using System.Xml;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Writes MPDs with <see cref="XmlWriter"/>, children in the order the MPD schema lists them.</summary>
internal static class DashMpdWriter
{
    private const string Namespace = "urn:mpeg:dash:schema:mpd:2011";

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter() : base(CultureInfo.InvariantCulture) { }

        public override Encoding Encoding => new UTF8Encoding(false);
    }

    public static string Write(DashMpd mpd)
    {
        Utf8StringWriter sw = new() { NewLine = "\n" };
        XmlWriterSettings settings = new() { Indent = true, IndentChars = "  ", NewLineChars = "\n", Encoding = new UTF8Encoding(false), CloseOutput = false };
        using (XmlWriter w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("MPD", Namespace);
            w.WriteAttributeString("type", mpd.Type == DashPresentationType.Dynamic ? "dynamic" : "static");
            if (mpd.Profiles.Length > 0)
            {
                w.WriteAttributeString("profiles", mpd.Profiles);
            }

            Time(w, "minBufferTime", mpd.MinBufferTime);
            Time(w, "mediaPresentationDuration", mpd.MediaPresentationDuration);
            Time(w, "minimumUpdatePeriod", mpd.MinimumUpdatePeriod);
            Time(w, "timeShiftBufferDepth", mpd.TimeShiftBufferDepth);
            Time(w, "suggestedPresentationDelay", mpd.SuggestedPresentationDelay);
            Time(w, "maxSegmentDuration", mpd.MaxSegmentDuration);
            if (mpd.AvailabilityStartTime is { } ast)
            {
                w.WriteAttributeString("availabilityStartTime", ManifestText.DateTime(ast));
            }

            if (mpd.PublishTime is { } pt)
            {
                w.WriteAttributeString("publishTime", ManifestText.DateTime(pt));
            }

            // ISO/IEC 23009-1 Annex A.2: ProgramInformation, BaseURL, Location..., Period+, Metrics, properties, UTCTiming.
            WriteOthers(w, mpd.OtherElements, static n => n == "ProgramInformation");
            foreach (string b in mpd.BaseUrls)
            {
                w.WriteElementString("BaseURL", b);
            }

            WriteOthers(w, mpd.OtherElements, static n => n is "Location" or "PatchLocation" or "ServiceDescription" or "InitializationSet" or "InfoDescriptor");
            foreach (DashPeriod p in mpd.Periods)
            {
                WritePeriod(w, p);
            }

            WriteOthers(w, mpd.OtherElements, static n => n is not ("ProgramInformation" or "Location" or "PatchLocation" or "ServiceDescription" or "InitializationSet" or "InfoDescriptor"));
            foreach (DashDescriptor d in mpd.UtcTimings)
            {
                Descriptor(w, d);
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return sw.ToString();
    }

    private static void WriteOthers(XmlWriter w, IReadOnlyList<string> others, Func<string, bool> pick)
    {
        foreach (string x in others)
        {
            if (pick(NameOf(x)))
            {
                w.WriteRaw(x);
            }
        }
    }

    private static string NameOf(string xml)
    {
        int i = 1;
        while (i < xml.Length && xml[i] is not (' ' or '>' or '/' or '\n' or '\t' or '\r'))
        {
            i++;
        }

        string name = xml.Length > 1 ? xml[1..i] : string.Empty;
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    private static void Time(XmlWriter w, string name, MediaTime? t)
    {
        if (t is { } v)
        {
            w.WriteAttributeString(name, Duration(v));
        }
    }

    private static void Number(XmlWriter w, string name, long value, long unset = 0)
    {
        if (value != unset)
        {
            w.WriteAttributeString(name, value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Text(XmlWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteAttributeString(name, value);
        }
    }

    private static void Flag(XmlWriter w, string name, bool value)
    {
        if (value)
        {
            w.WriteAttributeString(name, "true");
        }
    }

    private static void Descriptor(XmlWriter w, DashDescriptor d)
    {
        w.WriteStartElement(d.Element);
        w.WriteAttributeString("schemeIdUri", d.SchemeIdUri);
        Text(w, "value", d.Value);
        Text(w, "id", d.Id);
        w.WriteEndElement();
    }

    // The order of children of a RepresentationBase, AdaptationSet and Representation (Annex A.2): the shared properties first, then
    // Accessibility, Role, Rating, Viewpoint, then BaseURL, then the segment information, then the representations.
    private static int Rank(string name) => name switch
    {
        "FramePacking" => 10,
        "AudioChannelConfiguration" => 11,
        "ContentProtection" => 12,
        "OutputProtection" => 13,
        "EssentialProperty" => 14,
        "SupplementalProperty" => 15,
        "InbandEventStream" => 16,
        "Switching" => 17,
        "RandomAccess" => 18,
        "GroupLabel" => 19,
        "Label" => 20,
        "ProducerReferenceTime" => 21,
        "ContentPopularityRate" => 22,
        "Resync" => 23,
        "Accessibility" => 30,
        "Role" => 31,
        "Rating" => 32,
        "Viewpoint" => 33,
        "ContentComponent" => 34,
        _ => 35,
    };

    private static void WriteSorted(XmlWriter w, IReadOnlyList<DashDescriptor> descriptors, IReadOnlyList<string> others)
    {
        List<(int Rank, int Order, DashDescriptor? Descriptor, string? Xml)> items = [];
        int order = 0;
        foreach (DashDescriptor d in descriptors)
        {
            items.Add((Rank(d.Element), order++, d, null));
        }

        foreach (string x in others)
        {
            items.Add((Rank(NameOf(x)), order++, null, x));
        }

        items.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        foreach ((int _, int _, DashDescriptor? d, string? xml) in items)
        {
            if (d is not null)
            {
                Descriptor(w, d);
            }
            else
            {
                w.WriteRaw(xml!);
            }
        }
    }

    private static void WritePeriod(XmlWriter w, DashPeriod p)
    {
        w.WriteStartElement("Period");
        Text(w, "id", p.Id);
        Time(w, "start", p.Start);
        Time(w, "duration", p.Duration);
        foreach (string b in p.BaseUrls)
        {
            w.WriteElementString("BaseURL", b);
        }

        foreach (string x in p.OtherElements)
        {
            w.WriteRaw(x);
        }

        foreach (DashAdaptationSet a in p.AdaptationSets)
        {
            WriteAdaptationSet(w, a);
        }

        w.WriteEndElement();
    }

    private static void WriteAdaptationSet(XmlWriter w, DashAdaptationSet a)
    {
        w.WriteStartElement("AdaptationSet");
        Text(w, "id", a.Id);
        Text(w, "contentType", a.ContentType);
        Text(w, "lang", a.Language);
        Flag(w, "segmentAlignment", a.SegmentAlignment);
        Flag(w, "subsegmentAlignment", a.SubsegmentAlignment);
        Flag(w, "bitstreamSwitching", a.BitstreamSwitching);
        Number(w, "startWithSAP", a.StartWithSap);
        Text(w, "par", a.PictureAspectRatio);
        Text(w, "mimeType", a.MimeType);
        Text(w, "codecs", a.Codecs);
        Number(w, "maxWidth", a.Width);
        Number(w, "maxHeight", a.Height);
        Text(w, "frameRate", a.FrameRate);
        Number(w, "audioSamplingRate", a.AudioSamplingRate);
        WriteSorted(w, a.Descriptors, a.OtherElements);
        foreach (string b in a.BaseUrls)
        {
            w.WriteElementString("BaseURL", b);
        }

        if (a.SegmentTemplate is { } t)
        {
            Template(w, t);
        }

        foreach (DashRepresentation r in a.Representations)
        {
            WriteRepresentation(w, r);
        }

        w.WriteEndElement();
    }

    private static void WriteRepresentation(XmlWriter w, DashRepresentation r)
    {
        w.WriteStartElement("Representation");
        w.WriteAttributeString("id", r.Id);
        Text(w, "mimeType", r.MimeType);
        Text(w, "codecs", r.Codecs);
        Number(w, "width", r.Width);
        Number(w, "height", r.Height);
        Text(w, "frameRate", r.FrameRate);
        Text(w, "sar", r.SampleAspectRatio);
        Number(w, "audioSamplingRate", r.AudioSamplingRate);
        Number(w, "startWithSAP", r.StartWithSap);
        w.WriteAttributeString("bandwidth", r.Bandwidth.ToString(CultureInfo.InvariantCulture));
        WriteSorted(w, r.Descriptors, r.OtherElements);
        foreach (string b in r.BaseUrls)
        {
            w.WriteElementString("BaseURL", b);
        }

        if (r.SegmentBase is { } sb)
        {
            w.WriteStartElement("SegmentBase");
            Number(w, "timescale", sb.Timescale, 1);
            Number(w, "presentationTimeOffset", sb.PresentationTimeOffset);
            Text(w, "indexRange", sb.IndexRange);
            UrlRange(w, "Initialization", sb.Initialization);
            UrlRange(w, "RepresentationIndex", sb.RepresentationIndex);
            w.WriteEndElement();
        }

        if (r.SegmentList is { } sl)
        {
            w.WriteStartElement("SegmentList");
            Number(w, "timescale", sl.Timescale, 1);
            Number(w, "duration", sl.Duration);
            Number(w, "startNumber", sl.StartNumber, 1);
            Number(w, "presentationTimeOffset", sl.PresentationTimeOffset);
            UrlRange(w, "Initialization", sl.Initialization);
            Timeline(w, sl.Timeline);
            foreach (DashSegmentUrl s in sl.Segments)
            {
                w.WriteStartElement("SegmentURL");
                Text(w, "media", s.Media);
                Text(w, "mediaRange", s.MediaRange);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        if (r.SegmentTemplate is { } t)
        {
            Template(w, t);
        }

        w.WriteEndElement();
    }

    private static void UrlRange(XmlWriter w, string name, DashUrlRange? u)
    {
        if (u is null)
        {
            return;
        }

        w.WriteStartElement(name);
        Text(w, "sourceURL", u.SourceUrl);
        Text(w, "range", u.Range);
        w.WriteEndElement();
    }

    private static void Timeline(XmlWriter w, IReadOnlyList<DashTimelineEntry> timeline)
    {
        if (timeline.Count == 0)
        {
            return;
        }

        w.WriteStartElement("SegmentTimeline");
        foreach (DashTimelineEntry s in timeline)
        {
            w.WriteStartElement("S");
            if (s.Start is { } t)
            {
                w.WriteAttributeString("t", t.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteAttributeString("d", s.Duration.ToString(CultureInfo.InvariantCulture));
            if (s.Repeat != 0)
            {
                w.WriteAttributeString("r", s.Repeat.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void Template(XmlWriter w, DashSegmentTemplate t)
    {
        w.WriteStartElement("SegmentTemplate");
        Number(w, "timescale", t.Timescale, 1);
        Number(w, "duration", t.Duration);
        // The default of 1 is written out: some readers assume another default when a timeline is present.
        w.WriteAttributeString("startNumber", t.StartNumber.ToString(CultureInfo.InvariantCulture));
        if (t.EndNumber is { } e)
        {
            w.WriteAttributeString("endNumber", e.ToString(CultureInfo.InvariantCulture));
        }

        Number(w, "presentationTimeOffset", t.PresentationTimeOffset);
        Text(w, "availabilityTimeOffset", t.AvailabilityTimeOffset);
        Text(w, "media", t.Media);
        Text(w, "initialization", t.Initialization);
        Text(w, "index", t.Index);
        Timeline(w, t.Timeline);
        w.WriteEndElement();
    }

    public static string Duration(MediaTime time)
    {
        if (!time.IsKnown || time.Ticks <= 0)
        {
            return "PT0S";
        }

        // Rounded up to a microsecond: a duration that is too short would cut off the end of the media.
        Int128 micros = ((Int128)time.Ticks * 1_000_000 + time.Timescale - 1) / time.Timescale;
        Int128 nanos = micros * 1000;
        long totalSeconds = (long)(nanos / 1_000_000_000);
        long fraction = (long)(nanos % 1_000_000_000);
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;
        StringBuilder sb = new("PT");
        if (hours > 0)
        {
            sb.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
        }

        if (minutes > 0)
        {
            sb.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
        }

        if (seconds > 0 || fraction > 0 || sb.Length == 2)
        {
            sb.Append(seconds.ToString(CultureInfo.InvariantCulture));
            if (fraction > 0)
            {
                sb.Append('.').Append(fraction.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
            }

            sb.Append('S');
        }

        return sb.ToString();
    }
}

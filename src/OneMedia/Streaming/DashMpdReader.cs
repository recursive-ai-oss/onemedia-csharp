using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Reads MPDs. Elements are matched by local name so a document that uses another prefix or a newer namespace still reads.</summary>
internal static class DashMpdReader
{
    public static DashMpd Parse(string xml, MediaLimits limits)
    {
        limits.CheckAllocation(xml.Length, 0);
        using StringReader sr = new(xml);
        return Parse(XmlReader.Create(sr, Settings(limits)), limits);
    }

    public static DashMpd Parse(byte[] data, MediaLimits limits)
    {
        using MemoryStream ms = new(data, writable: false);
        return Parse(XmlReader.Create(ms, Settings(limits)), limits);
    }

    private static XmlReaderSettings Settings(MediaLimits limits) => new()
    {
        // An MPD never needs a DTD; refusing it closes entity-expansion attacks, and no resolver means nothing is fetched.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = limits.MaxAllocationBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static DashMpd Parse(XmlReader reader, MediaLimits limits)
    {
        XDocument doc;
        try
        {
            using (reader)
            {
                doc = XDocument.Load(reader, LoadOptions.None);
            }
        }
        catch (XmlException e)
        {
            throw new ManifestFormatException("The MPD is not well-formed XML: " + e.Message, e);
        }

        XElement root = doc.Root ?? throw new ManifestFormatException("The MPD has no root element.");
        if (root.Name.LocalName != "MPD")
        {
            throw new ManifestFormatException($"The root element is '{root.Name.LocalName}', not MPD.");
        }

        long count = 0;
        foreach (XElement unused in root.DescendantsAndSelf())
        {
            count++;
            if ((count & 0xFFFF) == 0)
            {
                limits.CheckElementCount(count, -1);
            }
        }

        limits.CheckElementCount(count, -1);
        List<DashPeriod> periods = [];
        List<DashDescriptor> timings = [];
        List<string> other = [];
        List<string> baseUrls = [];
        foreach (XElement e in root.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "Period": periods.Add(ReadPeriod(e)); break;
                case "BaseURL": baseUrls.Add(e.Value.Trim()); break;
                case "UTCTiming": timings.Add(Descriptor(e)); break;
                default: other.Add(Raw(e)); break;
            }
        }

        return new DashMpd
        {
            Type = Attr(root, "type") == "dynamic" ? DashPresentationType.Dynamic : DashPresentationType.Static,
            Profiles = Attr(root, "profiles") ?? string.Empty,
            MinBufferTime = Duration(root, "minBufferTime") ?? MediaTime.Zero,
            MediaPresentationDuration = Duration(root, "mediaPresentationDuration"),
            MinimumUpdatePeriod = Duration(root, "minimumUpdatePeriod"),
            TimeShiftBufferDepth = Duration(root, "timeShiftBufferDepth"),
            SuggestedPresentationDelay = Duration(root, "suggestedPresentationDelay"),
            MaxSegmentDuration = Duration(root, "maxSegmentDuration"),
            AvailabilityStartTime = DateTime(root, "availabilityStartTime"),
            PublishTime = DateTime(root, "publishTime"),
            BaseUrls = baseUrls,
            Periods = periods,
            UtcTimings = timings,
            OtherElements = other,
        };
    }

    private static DashPeriod ReadPeriod(XElement p)
    {
        List<DashAdaptationSet> sets = [];
        List<string> other = [];
        List<string> baseUrls = [];
        foreach (XElement e in p.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "AdaptationSet": sets.Add(ReadAdaptationSet(e)); break;
                case "BaseURL": baseUrls.Add(e.Value.Trim()); break;
                default: other.Add(Raw(e)); break;
            }
        }

        return new DashPeriod { Id = Attr(p, "id"), Start = Duration(p, "start"), Duration = Duration(p, "duration"), BaseUrls = baseUrls, AdaptationSets = sets, OtherElements = other };
    }

    private static readonly HashSet<string> s_descriptorNames =
        ["Role", "Accessibility", "AudioChannelConfiguration", "SupplementalProperty", "EssentialProperty", "Viewpoint", "InbandEventStream", "FramePacking", "Rating"];

    private static DashAdaptationSet ReadAdaptationSet(XElement a)
    {
        List<DashRepresentation> reps = [];
        List<DashDescriptor> descriptors = [];
        List<string> other = [];
        List<string> baseUrls = [];
        DashSegmentTemplate? template = null;
        foreach (XElement e in a.Elements())
        {
            string n = e.Name.LocalName;
            if (n == "Representation")
            {
                reps.Add(ReadRepresentation(e));
            }
            else if (n == "BaseURL")
            {
                baseUrls.Add(e.Value.Trim());
            }
            else if (n == "SegmentTemplate")
            {
                template = Template(e);
            }
            else if (s_descriptorNames.Contains(n) && Attr(e, "schemeIdUri") is not null)
            {
                descriptors.Add(Descriptor(e));
            }
            else
            {
                other.Add(Raw(e));
            }
        }

        return new DashAdaptationSet
        {
            Id = Attr(a, "id"),
            ContentType = Attr(a, "contentType"),
            MimeType = Attr(a, "mimeType"),
            Codecs = Attr(a, "codecs"),
            Language = Attr(a, "lang"),
            SegmentAlignment = Attr(a, "segmentAlignment") == "true",
            SubsegmentAlignment = Attr(a, "subsegmentAlignment") == "true",
            BitstreamSwitching = Attr(a, "bitstreamSwitching") == "true",
            StartWithSap = Int(a, "startWithSAP"),
            Width = Int(a, "width") is > 0 and var w ? w : Int(a, "maxWidth"),
            Height = Int(a, "height") is > 0 and var h ? h : Int(a, "maxHeight"),
            FrameRate = Attr(a, "frameRate") ?? Attr(a, "maxFrameRate"),
            PictureAspectRatio = Attr(a, "par"),
            AudioSamplingRate = Int(a, "audioSamplingRate"),
            BaseUrls = baseUrls,
            Descriptors = descriptors,
            SegmentTemplate = template,
            Representations = reps,
            OtherElements = other,
        };
    }

    private static DashRepresentation ReadRepresentation(XElement r)
    {
        List<DashDescriptor> descriptors = [];
        List<string> other = [];
        List<string> baseUrls = [];
        DashSegmentTemplate? template = null;
        DashSegmentBase? segmentBase = null;
        DashSegmentList? list = null;
        foreach (XElement e in r.Elements())
        {
            string n = e.Name.LocalName;
            if (n == "BaseURL")
            {
                baseUrls.Add(e.Value.Trim());
            }
            else if (n == "SegmentTemplate")
            {
                template = Template(e);
            }
            else if (n == "SegmentBase")
            {
                segmentBase = Base(e);
            }
            else if (n == "SegmentList")
            {
                list = List(e);
            }
            else if (s_descriptorNames.Contains(n) && Attr(e, "schemeIdUri") is not null)
            {
                descriptors.Add(Descriptor(e));
            }
            else
            {
                other.Add(Raw(e));
            }
        }

        return new DashRepresentation
        {
            Id = Attr(r, "id") ?? throw new ManifestFormatException("A Representation has no id."),
            Bandwidth = Long(r, "bandwidth"),
            Codecs = Attr(r, "codecs"),
            MimeType = Attr(r, "mimeType"),
            Width = Int(r, "width"),
            Height = Int(r, "height"),
            FrameRate = Attr(r, "frameRate"),
            SampleAspectRatio = Attr(r, "sar"),
            AudioSamplingRate = Int(r, "audioSamplingRate"),
            StartWithSap = Int(r, "startWithSAP"),
            BaseUrls = baseUrls,
            Descriptors = descriptors,
            SegmentTemplate = template,
            SegmentBase = segmentBase,
            SegmentList = list,
            OtherElements = other,
        };
    }

    private static List<DashTimelineEntry> Timeline(XElement parent)
    {
        List<DashTimelineEntry> list = [];
        foreach (XElement tl in parent.Elements().Where(x => x.Name.LocalName == "SegmentTimeline"))
        {
            foreach (XElement s in tl.Elements().Where(x => x.Name.LocalName == "S"))
            {
                long d = Long(s, "d");
                if (d <= 0)
                {
                    throw new ManifestFormatException("A SegmentTimeline S element has no positive d.");
                }

                long? t = Attr(s, "t") is null ? null : Long(s, "t");
                list.Add(new DashTimelineEntry(t, d, (int)Math.Clamp(Attr(s, "r") is { } r ? Long(s, "r") : 0, -1, int.MaxValue)));
            }
        }

        return list;
    }

    private static DashSegmentTemplate Template(XElement e) => new()
    {
        Timescale = Attr(e, "timescale") is null ? 1 : Math.Max(1, Long(e, "timescale")),
        Duration = Long(e, "duration"),
        StartNumber = Attr(e, "startNumber") is null ? 1 : Long(e, "startNumber"),
        EndNumber = Attr(e, "endNumber") is null ? null : Long(e, "endNumber"),
        PresentationTimeOffset = Long(e, "presentationTimeOffset"),
        Media = Attr(e, "media"),
        Initialization = Attr(e, "initialization"),
        Index = Attr(e, "index"),
        AvailabilityTimeOffset = Attr(e, "availabilityTimeOffset"),
        Timeline = Timeline(e),
    };

    private static DashUrlRange? UrlRange(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName == name) is { } e ? new DashUrlRange(Attr(e, "sourceURL"), Attr(e, "range")) : null;

    private static DashSegmentBase Base(XElement e) => new()
    {
        Timescale = Attr(e, "timescale") is null ? 1 : Math.Max(1, Long(e, "timescale")),
        PresentationTimeOffset = Long(e, "presentationTimeOffset"),
        IndexRange = Attr(e, "indexRange"),
        Initialization = UrlRange(e, "Initialization"),
        RepresentationIndex = UrlRange(e, "RepresentationIndex"),
    };

    private static DashSegmentList List(XElement e) => new()
    {
        Timescale = Attr(e, "timescale") is null ? 1 : Math.Max(1, Long(e, "timescale")),
        Duration = Long(e, "duration"),
        StartNumber = Attr(e, "startNumber") is null ? 1 : Long(e, "startNumber"),
        PresentationTimeOffset = Long(e, "presentationTimeOffset"),
        Initialization = UrlRange(e, "Initialization"),
        Timeline = Timeline(e),
        Segments = e.Elements().Where(x => x.Name.LocalName == "SegmentURL").Select(x => new DashSegmentUrl(Attr(x, "media"), Attr(x, "mediaRange"))).ToList(),
    };

    private static DashDescriptor Descriptor(XElement e) => new(e.Name.LocalName, Attr(e, "schemeIdUri") ?? string.Empty, Attr(e, "value"), Attr(e, "id"));

    // XElement.ToString declares the element's namespace on it; inside the MPD's own namespace that declaration is redundant and would make a rewrite differ from the text it came from.
    private static string Raw(XElement e) => e.ToString(SaveOptions.DisableFormatting).Replace(" xmlns=\"urn:mpeg:dash:schema:mpd:2011\"", string.Empty, StringComparison.Ordinal);

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static long Long(XElement e, string name)
    {
        string? v = Attr(e, name);
        if (v is null)
        {
            return 0;
        }

        if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
        {
            throw new ManifestFormatException($"{e.Name.LocalName}@{name}='{v}' is not an integer.");
        }

        return n;
    }

    private static int Int(XElement e, string name) => (int)Math.Clamp(Long(e, name), int.MinValue, int.MaxValue);

    private static MediaTime? Duration(XElement e, string name)
    {
        string? v = Attr(e, name);
        if (v is null)
        {
            return null;
        }

        if (!TryParseDuration(v, out MediaTime t))
        {
            throw new ManifestFormatException($"{e.Name.LocalName}@{name}='{v}' is not an xs:duration.");
        }

        return t;
    }

    private static DateTimeOffset? DateTime(XElement e, string name)
    {
        string? v = Attr(e, name);
        if (v is null)
        {
            return null;
        }

        if (!ManifestText.TryParseDateTime(v, out DateTimeOffset t))
        {
            throw new ManifestFormatException($"{e.Name.LocalName}@{name}='{v}' is not an xs:dateTime.");
        }

        return t;
    }

    /// <summary>xs:duration: <c>P[nY][nM][nD][T[nH][nM][n[.n]S]]</c>, optionally negative.</summary>
    public static bool TryParseDuration(string? text, out MediaTime time)
    {
        time = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();
        bool negative = false;
        if (s.Length > 0 && s[0] == '-')
        {
            negative = true;
            s = s[1..];
        }

        if (s.Length < 2 || s[0] != 'P')
        {
            return false;
        }

        s = s[1..];
        bool inTime = false;
        bool any = false;
        Int128 nanos = 0;
        while (s.Length > 0)
        {
            if (s[0] == 'T')
            {
                if (inTime)
                {
                    return false;
                }

                inTime = true;
                s = s[1..];
                continue;
            }

            int len = 0;
            while (len < s.Length && (s[len] is >= '0' and <= '9' || s[len] == '.'))
            {
                len++;
            }

            if (len == 0 || len == s.Length)
            {
                return false;
            }

            char unit = s[len];
            if (!ManifestText.TryParseSeconds(s[..len], out MediaTime n))
            {
                return false;
            }

            Int128 value = (Int128)n.Ticks * 1_000_000_000 / n.Timescale;
            switch (unit)
            {
                case 'Y' when !inTime: nanos += value * 365 * 86_400; break;
                case 'M' when !inTime: nanos += value * 30 * 86_400; break;
                case 'D' when !inTime: nanos += value * 86_400; break;
                case 'H' when inTime: nanos += value * 3_600; break;
                case 'M' when inTime: nanos += value * 60; break;
                case 'S' when inTime: nanos += value; break;
                default: return false;
            }

            any = true;
            s = s[(len + 1)..];
        }

        if (!any || nanos > long.MaxValue / 2)
        {
            return false;
        }

        time = new MediaTime(negative ? -(long)nanos : (long)nanos, 1_000_000_000);
        return true;
    }
}

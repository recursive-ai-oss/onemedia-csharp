using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Whether a presentation is complete (<c>static</c>) or still being produced (<c>dynamic</c>), the <c>type</c> of an MPD (ISO/IEC 23009-1 §5.3.1.2).</summary>
public enum DashPresentationType
{
    /// <summary>On demand: every segment exists and the duration is fixed.</summary>
    Static = 0,

    /// <summary>Live: segments appear over time and the MPD is refreshed.</summary>
    Dynamic,
}

/// <summary>A descriptor element: <c>Role</c>, <c>Accessibility</c>, <c>AudioChannelConfiguration</c>, <c>SupplementalProperty</c>, <c>EssentialProperty</c>, <c>Viewpoint</c>, <c>InbandEventStream</c>, <c>UTCTiming</c> and similar, which all carry a scheme and a value.</summary>
/// <param name="Element">The element name without a namespace, for example <c>Role</c>.</param>
/// <param name="SchemeIdUri">The <c>schemeIdUri</c> attribute.</param>
/// <param name="Value">The <c>value</c> attribute, or null.</param>
/// <param name="Id">The <c>id</c> attribute, or null.</param>
public sealed record DashDescriptor(string Element, string SchemeIdUri, string? Value = null, string? Id = null);

/// <summary>A URL with an optional byte range: <c>Initialization</c> or <c>RepresentationIndex</c> (ISO/IEC 23009-1 §5.3.9.2).</summary>
/// <param name="SourceUrl">The <c>sourceURL</c>, or null to mean the resource of the enclosing <c>BaseURL</c>.</param>
/// <param name="Range">The <c>range</c> as written, <c>first-last</c>, or null.</param>
public sealed record DashUrlRange(string? SourceUrl, string? Range = null);

/// <summary>One <c>S</c> element of a <c>SegmentTimeline</c>: a run of segments of equal duration.</summary>
/// <param name="Start">The <c>t</c> attribute: the start time in timescale units, or null to continue after the previous run.</param>
/// <param name="Duration">The <c>d</c> attribute.</param>
/// <param name="Repeat">The <c>r</c> attribute: the number of further segments of the same duration (0 for one segment); -1 repeats until the next run or the end of the period.</param>
public readonly record struct DashTimelineEntry(long? Start, long Duration, int Repeat = 0);

/// <summary>A media segment as a <c>SegmentTemplate</c> or <c>SegmentList</c> resolves it.</summary>
/// <param name="Number">The segment number (<c>$Number$</c>).</param>
/// <param name="Time">The start time in timescale units (<c>$Time$</c>).</param>
/// <param name="Duration">The duration in timescale units.</param>
/// <param name="Uri">The media URL, relative to the <c>BaseURL</c> in effect; for a <c>SegmentList</c> entry, the <c>media</c> URL.</param>
/// <param name="Range">A byte range of <see cref="Uri"/>, or null.</param>
public readonly record struct DashSegmentRef(long Number, long Time, long Duration, string Uri, string? Range = null);

/// <summary>A <c>SegmentTemplate</c>: URLs built from a pattern (ISO/IEC 23009-1 §5.3.9.4).</summary>
public sealed record DashSegmentTemplate
{
    /// <summary>Ticks per second for the times of the template (<c>timescale</c>); 1 when absent.</summary>
    public long Timescale { get; init; } = 1;

    /// <summary>The constant segment duration in timescale units (<c>duration</c>), or 0 when a <see cref="Timeline"/> gives them.</summary>
    public long Duration { get; init; }

    /// <summary>The number of the first segment (<c>startNumber</c>); 1 when absent.</summary>
    public long StartNumber { get; init; } = 1;

    /// <summary>The number of the last segment (<c>endNumber</c>), or null.</summary>
    public long? EndNumber { get; init; }

    /// <summary>The <c>presentationTimeOffset</c>, subtracted from media times to get presentation times.</summary>
    public long PresentationTimeOffset { get; init; }

    /// <summary>The <c>media</c> pattern with <c>$RepresentationID$</c>, <c>$Number$</c>, <c>$Bandwidth$</c> and <c>$Time$</c>, or null.</summary>
    public string? Media { get; init; }

    /// <summary>The <c>initialization</c> pattern (<c>$RepresentationID$</c> and <c>$Bandwidth$</c>), or null.</summary>
    public string? Initialization { get; init; }

    /// <summary>The <c>index</c> pattern, or null.</summary>
    public string? Index { get; init; }

    /// <summary>The <c>S</c> elements of the <c>SegmentTimeline</c>; empty when segments have the constant <see cref="Duration"/>.</summary>
    public IReadOnlyList<DashTimelineEntry> Timeline { get; init; } = [];

    /// <summary>The <c>availabilityTimeOffset</c> in seconds as written, or null.</summary>
    public string? AvailabilityTimeOffset { get; init; }

    /// <summary>Substitutes the identifiers of ISO/IEC 23009-1 §5.3.9.4.4 in a template pattern.</summary>
    /// <param name="pattern">A <c>media</c>, <c>initialization</c> or <c>index</c> pattern.</param>
    /// <param name="representationId">Value of <c>$RepresentationID$</c>.</param>
    /// <param name="bandwidth">Value of <c>$Bandwidth$</c>.</param>
    /// <param name="number">Value of <c>$Number$</c>.</param>
    /// <param name="time">Value of <c>$Time$</c>.</param>
    /// <returns>The URL. <c>$$</c> becomes <c>$</c>; an identifier may carry a <c>%0Nd</c> width, as in <c>$Number%05d$</c>; an unknown identifier is left as written.</returns>
    public static string Expand(string pattern, string representationId, long bandwidth, long number, long time)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        StringBuilder sb = new();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c != '$')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int end = pattern.IndexOf('$', i + 1);
            if (end < 0)
            {
                sb.Append(pattern, i, pattern.Length - i);
                break;
            }

            string token = pattern.Substring(i + 1, end - i - 1);
            i = end + 1;
            if (token.Length == 0)
            {
                sb.Append('$');
                continue;
            }

            string name = token;
            int width = 1;
            int pct = token.IndexOf('%', StringComparison.Ordinal);
            if (pct >= 0)
            {
                name = token[..pct];
                string format = token[(pct + 1)..];
                // %0<width>d is the only format the standard allows.
                if (format.Length >= 2 && format[0] == '0' && format[^1] == 'd' && int.TryParse(format.AsSpan(1, format.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w <= 32)
                {
                    width = w;
                }
            }

            switch (name)
            {
                case "RepresentationID":
                    sb.Append(representationId);
                    break;
                case "Number":
                    sb.Append(number.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    break;
                case "Bandwidth":
                    sb.Append(bandwidth.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    break;
                case "Time":
                    sb.Append(time.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append('$').Append(token).Append('$');
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Lists the media segments the template describes.</summary>
    /// <param name="representationId">The representation's id, for <c>$RepresentationID$</c>.</param>
    /// <param name="bandwidth">The representation's bandwidth, for <c>$Bandwidth$</c>.</param>
    /// <param name="periodDuration">The length of the period; needed only for a constant <see cref="Duration"/> (number of segments = ceiling of the period over the duration) or a timeline whose last run repeats with <c>r=-1</c>. <see cref="MediaTime.Unknown"/> when not known.</param>
    /// <param name="limits">Limits; <see cref="MediaLimits.MaxElementCount"/> bounds the list. Defaults when null.</param>
    /// <returns>The segments in order.</returns>
    /// <exception cref="ManifestFormatException">The template has no <see cref="Media"/> pattern, or no way to know the segment durations.</exception>
    /// <exception cref="MediaLimitExceededException">The template describes more segments than the limit.</exception>
    public IReadOnlyList<DashSegmentRef> Segments(string representationId, long bandwidth, MediaTime periodDuration, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        if (Media is null)
        {
            throw new ManifestFormatException("The SegmentTemplate has no media pattern.");
        }

        List<DashSegmentRef> list = [];
        long scale = Math.Max(Timescale, 1);
        long periodTicks = periodDuration.IsKnown ? periodDuration.Rescale(scale, MediaRounding.Ceiling).Ticks : -1;
        if (Timeline.Count > 0)
        {
            long number = StartNumber;
            long time = 0;
            for (int e = 0; e < Timeline.Count; e++)
            {
                DashTimelineEntry s = Timeline[e];
                time = s.Start ?? time;
                if (s.Duration <= 0)
                {
                    throw new ManifestFormatException("A SegmentTimeline entry has no positive duration.");
                }

                long repeats = s.Repeat;
                if (repeats < 0)
                {
                    // ISO/IEC 23009-1 §5.3.9.6.2: a negative r repeats to the next S element's start or to the end of the period.
                    long until = e + 1 < Timeline.Count && Timeline[e + 1].Start is { } next ? next : periodTicks;
                    if (until < 0)
                    {
                        throw new ManifestFormatException("A SegmentTimeline entry repeats to the end of the period, whose length is not known.");
                    }

                    repeats = Math.Max(0, (until - time + s.Duration - 1) / s.Duration - 1);
                }

                limits.CheckElementCount(list.Count + repeats + 1, -1);
                for (long k = 0; k <= repeats; k++)
                {
                    if (EndNumber is { } last && number > last)
                    {
                        return list;
                    }

                    list.Add(new DashSegmentRef(number, time, s.Duration, Expand(Media, representationId, bandwidth, number, time)));
                    number++;
                    time += s.Duration;
                }
            }

            return list;
        }

        if (Duration <= 0)
        {
            throw new ManifestFormatException("The SegmentTemplate has neither a duration nor a SegmentTimeline.");
        }

        if (periodTicks < 0 && EndNumber is null)
        {
            throw new ManifestFormatException("The SegmentTemplate needs the period duration to count its segments.");
        }

        long count = EndNumber is { } en ? en - StartNumber + 1 : (periodTicks + Duration - 1) / Duration;
        limits.CheckElementCount(count, -1);
        for (long k = 0; k < count; k++)
        {
            long number = StartNumber + k;
            long time = k * Duration;
            list.Add(new DashSegmentRef(number, time, Duration, Expand(Media, representationId, bandwidth, number, time)));
        }

        return list;
    }
}

/// <summary>A <c>SegmentBase</c>: one media file per representation with a segment index (<c>sidx</c>) at <see cref="IndexRange"/> (ISO/IEC 23009-1 §5.3.9.2).</summary>
public sealed record DashSegmentBase
{
    /// <summary>Ticks per second of the index's times; 1 when absent.</summary>
    public long Timescale { get; init; } = 1;

    /// <summary>The <c>presentationTimeOffset</c>.</summary>
    public long PresentationTimeOffset { get; init; }

    /// <summary>The <c>indexRange</c> as written (<c>first-last</c>): where the <c>sidx</c> is in the media resource, or null.</summary>
    public string? IndexRange { get; init; }

    /// <summary>The <c>Initialization</c> element (the byte range of the <c>ftyp</c>/<c>moov</c> in the resource), or null.</summary>
    public DashUrlRange? Initialization { get; init; }

    /// <summary>The <c>RepresentationIndex</c> element, or null.</summary>
    public DashUrlRange? RepresentationIndex { get; init; }
}

/// <summary>One <c>SegmentURL</c> of a <c>SegmentList</c>.</summary>
/// <param name="Media">The <c>media</c> URL, or null for the resource of the base URL.</param>
/// <param name="MediaRange">The <c>mediaRange</c> as written, or null.</param>
public sealed record DashSegmentUrl(string? Media, string? MediaRange = null);

/// <summary>A <c>SegmentList</c>: the segments listed one by one (ISO/IEC 23009-1 §5.3.9.3).</summary>
public sealed record DashSegmentList
{
    /// <summary>Ticks per second; 1 when absent.</summary>
    public long Timescale { get; init; } = 1;

    /// <summary>The constant duration in timescale units, or 0 when a timeline is used.</summary>
    public long Duration { get; init; }

    /// <summary>The number of the first segment; 1 when absent.</summary>
    public long StartNumber { get; init; } = 1;

    /// <summary>The <c>presentationTimeOffset</c>.</summary>
    public long PresentationTimeOffset { get; init; }

    /// <summary>The <c>Initialization</c> element, or null.</summary>
    public DashUrlRange? Initialization { get; init; }

    /// <summary>The <c>SegmentTimeline</c> entries, or empty.</summary>
    public IReadOnlyList<DashTimelineEntry> Timeline { get; init; } = [];

    /// <summary>The <c>SegmentURL</c> elements in order.</summary>
    public IReadOnlyList<DashSegmentUrl> Segments { get; init; } = [];
}

/// <summary>A <c>Representation</c>: one encoding of the content (ISO/IEC 23009-1 §5.3.5).</summary>
public sealed record DashRepresentation
{
    /// <summary>The <c>id</c>, unique in the period.</summary>
    public required string Id { get; init; }

    /// <summary>Bits per second (<c>bandwidth</c>).</summary>
    public long Bandwidth { get; init; }

    /// <summary>The <c>codecs</c> string (RFC 6381), or null when the adaptation set gives it.</summary>
    public string? Codecs { get; init; }

    /// <summary>The <c>mimeType</c>, or null when the adaptation set gives it.</summary>
    public string? MimeType { get; init; }

    /// <summary>Width in pixels, 0 when absent.</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels, 0 when absent.</summary>
    public int Height { get; init; }

    /// <summary>The <c>frameRate</c> as written (<c>25</c> or <c>30000/1001</c>), or null.</summary>
    public string? FrameRate { get; init; }

    /// <summary>The <c>sar</c> (sample aspect ratio) as written, or null.</summary>
    public string? SampleAspectRatio { get; init; }

    /// <summary>The <c>audioSamplingRate</c>, 0 when absent.</summary>
    public int AudioSamplingRate { get; init; }

    /// <summary>The <c>startWithSAP</c>, 0 when absent.</summary>
    public int StartWithSap { get; init; }

    /// <summary>The <c>BaseURL</c> elements, in order.</summary>
    public IReadOnlyList<string> BaseUrls { get; init; } = [];

    /// <summary>Descriptors (<c>AudioChannelConfiguration</c>, <c>SupplementalProperty</c>, <c>EssentialProperty</c>, <c>InbandEventStream</c>, …).</summary>
    public IReadOnlyList<DashDescriptor> Descriptors { get; init; } = [];

    /// <summary>The <c>SegmentTemplate</c>, or null.</summary>
    public DashSegmentTemplate? SegmentTemplate { get; init; }

    /// <summary>The <c>SegmentBase</c>, or null.</summary>
    public DashSegmentBase? SegmentBase { get; init; }

    /// <summary>The <c>SegmentList</c>, or null.</summary>
    public DashSegmentList? SegmentList { get; init; }

    /// <summary>Child elements that are not modelled (<c>ContentProtection</c>, <c>SubRepresentation</c>, …), each as XML text, kept so a rewrite loses nothing.</summary>
    public IReadOnlyList<string> OtherElements { get; init; } = [];
}

/// <summary>An <c>AdaptationSet</c>: representations of one kind of content a player can switch between (ISO/IEC 23009-1 §5.3.3).</summary>
public sealed record DashAdaptationSet
{
    /// <summary>The <c>id</c>, or null.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>contentType</c> (<c>video</c>, <c>audio</c>, <c>text</c>), or null.</summary>
    public string? ContentType { get; init; }

    /// <summary>The <c>mimeType</c>, or null.</summary>
    public string? MimeType { get; init; }

    /// <summary>The <c>codecs</c>, or null.</summary>
    public string? Codecs { get; init; }

    /// <summary>The <c>lang</c> (RFC 5646), or null.</summary>
    public string? Language { get; init; }

    /// <summary>True for <c>segmentAlignment="true"</c>: segment boundaries line up across the representations.</summary>
    public bool SegmentAlignment { get; init; }

    /// <summary>True for <c>subsegmentAlignment="true"</c>.</summary>
    public bool SubsegmentAlignment { get; init; }

    /// <summary>The <c>startWithSAP</c>, 0 when absent.</summary>
    public int StartWithSap { get; init; }

    /// <summary>True for <c>bitstreamSwitching="true"</c>.</summary>
    public bool BitstreamSwitching { get; init; }

    /// <summary>Width of the largest representation, 0 when absent (<c>maxWidth</c> or <c>width</c>).</summary>
    public int Width { get; init; }

    /// <summary>Height, 0 when absent.</summary>
    public int Height { get; init; }

    /// <summary>The <c>frameRate</c> as written, or null.</summary>
    public string? FrameRate { get; init; }

    /// <summary>The <c>par</c> picture aspect ratio as written, or null.</summary>
    public string? PictureAspectRatio { get; init; }

    /// <summary>The <c>audioSamplingRate</c>, 0 when absent.</summary>
    public int AudioSamplingRate { get; init; }

    /// <summary>The <c>BaseURL</c> elements.</summary>
    public IReadOnlyList<string> BaseUrls { get; init; } = [];

    /// <summary>Descriptors (<c>Role</c>, <c>Accessibility</c>, <c>AudioChannelConfiguration</c>, <c>InbandEventStream</c>, …).</summary>
    public IReadOnlyList<DashDescriptor> Descriptors { get; init; } = [];

    /// <summary>A <c>SegmentTemplate</c> shared by the representations, or null.</summary>
    public DashSegmentTemplate? SegmentTemplate { get; init; }

    /// <summary>The representations.</summary>
    public IReadOnlyList<DashRepresentation> Representations { get; init; } = [];

    /// <summary>Child elements that are not modelled, as XML text.</summary>
    public IReadOnlyList<string> OtherElements { get; init; } = [];
}

/// <summary>A <c>Period</c>: a span of the presentation with its own set of adaptation sets (ISO/IEC 23009-1 §5.3.2).</summary>
public sealed record DashPeriod
{
    /// <summary>The <c>id</c>, or null.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>start</c> relative to the start of the presentation, or null (the period starts where the previous ended; the first starts at 0).</summary>
    public MediaTime? Start { get; init; }

    /// <summary>The <c>duration</c>, or null.</summary>
    public MediaTime? Duration { get; init; }

    /// <summary>The <c>BaseURL</c> elements.</summary>
    public IReadOnlyList<string> BaseUrls { get; init; } = [];

    /// <summary>The adaptation sets.</summary>
    public IReadOnlyList<DashAdaptationSet> AdaptationSets { get; init; } = [];

    /// <summary>Child elements that are not modelled (<c>EventStream</c>, <c>AssetIdentifier</c>, …), as XML text.</summary>
    public IReadOnlyList<string> OtherElements { get; init; } = [];
}

/// <summary>A DASH media presentation description (MPD), read and written as XML (ISO/IEC 23009-1, DASH-IF Interoperability Points).</summary>
/// <remarks>Attributes the model does not name are not kept; child elements it does not model are kept as XML text.</remarks>
public sealed record DashMpd
{
    /// <summary>The <c>type</c>: static or dynamic.</summary>
    public DashPresentationType Type { get; init; }

    /// <summary>The <c>profiles</c> string, for example <c>urn:mpeg:dash:profile:isoff-live:2011</c>.</summary>
    public string Profiles { get; init; } = "urn:mpeg:dash:profile:isoff-on-demand:2011";

    /// <summary>The <c>minBufferTime</c>: how much a player should buffer (required by the standard).</summary>
    public MediaTime MinBufferTime { get; init; } = new(2, 1);

    /// <summary>The <c>mediaPresentationDuration</c> of a static presentation, or null.</summary>
    public MediaTime? MediaPresentationDuration { get; init; }

    /// <summary>The <c>minimumUpdatePeriod</c> of a dynamic presentation, or null.</summary>
    public MediaTime? MinimumUpdatePeriod { get; init; }

    /// <summary>The <c>timeShiftBufferDepth</c> (the live window), or null.</summary>
    public MediaTime? TimeShiftBufferDepth { get; init; }

    /// <summary>The <c>suggestedPresentationDelay</c>, or null.</summary>
    public MediaTime? SuggestedPresentationDelay { get; init; }

    /// <summary>The <c>maxSegmentDuration</c>, or null.</summary>
    public MediaTime? MaxSegmentDuration { get; init; }

    /// <summary>The <c>availabilityStartTime</c> of a dynamic presentation, or null.</summary>
    public DateTimeOffset? AvailabilityStartTime { get; init; }

    /// <summary>The <c>publishTime</c>, or null.</summary>
    public DateTimeOffset? PublishTime { get; init; }

    /// <summary>The <c>BaseURL</c> elements.</summary>
    public IReadOnlyList<string> BaseUrls { get; init; } = [];

    /// <summary>The periods.</summary>
    public IReadOnlyList<DashPeriod> Periods { get; init; } = [];

    /// <summary><c>UTCTiming</c> descriptors that tell a live player how to read the clock.</summary>
    public IReadOnlyList<DashDescriptor> UtcTimings { get; init; } = [];

    /// <summary>Child elements that are not modelled (<c>ProgramInformation</c>, <c>Metrics</c>, …), as XML text.</summary>
    public IReadOnlyList<string> OtherElements { get; init; } = [];

    /// <summary>Parses an MPD.</summary>
    /// <param name="xml">The document text.</param>
    /// <param name="limits">Limits on size and element count; defaults when null. DTDs are refused and external entities are never fetched.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ManifestFormatException">The text is not well-formed XML, the root is not <c>MPD</c>, or a value is malformed.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static DashMpd Parse(string xml, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return DashMpdReader.Parse(xml, limits ?? MediaLimits.Default);
    }

    /// <summary>Parses an MPD from UTF-8 (or whatever encoding its XML declaration names).</summary>
    /// <param name="data">The document bytes.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ManifestFormatException">As for <see cref="Parse(string, MediaLimits?)"/>.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static DashMpd Parse(ReadOnlySpan<byte> data, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        limits.CheckAllocation(data.Length, 0);
        return DashMpdReader.Parse(data.ToArray(), limits);
    }

    /// <summary>Parses an MPD without throwing.</summary>
    /// <param name="xml">The document text.</param>
    /// <param name="mpd">The model, or null.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>False when <see cref="Parse(string, MediaLimits?)"/> would have thrown.</returns>
    public static bool TryParse(string? xml, out DashMpd? mpd, MediaLimits? limits = null)
    {
        mpd = null;
        if (xml is null)
        {
            return false;
        }

        try
        {
            mpd = Parse(xml, limits);
            return true;
        }
        catch (Exception e) when (e is ManifestFormatException or MediaLimitExceededException)
        {
            return false;
        }
    }

    /// <summary>The MPD as an XML document, indented, in the MPD namespace.</summary>
    public override string ToString() => DashMpdWriter.Write(this);

    /// <summary>The MPD as UTF-8 bytes without a byte order mark.</summary>
    public byte[] ToUtf8() => new UTF8Encoding(false).GetBytes(ToString());

    /// <summary>Writes a duration as an <c>xs:duration</c> (<c>PT1H2M3.5S</c>).</summary>
    /// <param name="time">The duration; negative or unknown values write <c>PT0S</c>.</param>
    /// <returns>The text.</returns>
    public static string FormatDuration(MediaTime time) => DashMpdWriter.Duration(time);

    /// <summary>Reads an <c>xs:duration</c> with days, hours, minutes and seconds (years count 365 days and months 30, as ISO/IEC 23009-1 §5.4 says to when they appear).</summary>
    /// <param name="text">For example <c>PT0H0M30.040S</c>.</param>
    /// <param name="time">The duration, exact (fractions of a second up to nine digits).</param>
    /// <returns>False when the text is not a duration.</returns>
    public static bool TryParseDuration(string? text, out MediaTime time) => DashMpdReader.TryParseDuration(text, out time);
}

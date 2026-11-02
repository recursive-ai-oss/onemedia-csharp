using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using RecursiveAI.OneMedia.TimedEvents;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>A timed event to announce in the manifests: an ad cue, ID3 timed metadata or any <c>emsg</c>-style message.</summary>
/// <param name="Time">When it happens, on the presentation timeline.</param>
/// <param name="Duration">How long it lasts, or <see cref="MediaTime.Unknown"/>.</param>
/// <param name="SchemeIdUri">The message scheme, for example <c>urn:scte:scte35:2013:bin</c> or <c>https://aomedia.org/emsg/ID3</c>.</param>
/// <param name="Value">The scheme's value, or empty.</param>
/// <param name="Id">An identifier unique within the scheme; it becomes the DASH <c>Event@id</c> and the HLS <c>DATERANGE</c> ID.</param>
/// <param name="Data">The message bytes (for SCTE 35 the <c>splice_info_section</c>).</param>
public sealed record PackagingEvent(MediaTime Time, MediaTime Duration, string SchemeIdUri, string Value, uint Id, ReadOnlyMemory<byte> Data)
{
    /// <summary>Reads the <c>emsg</c> events of an open ISO file (see <see cref="MediaEvents"/>) as packaging events. Streams that hold no <c>emsg</c> boxes contribute nothing.</summary>
    /// <param name="file">The file.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The events in file order.</returns>
    public static IReadOnlyList<PackagingEvent> FromFile(MediaFile file, MediaLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        List<PackagingEvent> events = [];
        foreach (EventChannel channel in MediaEvents.List(file, limits, includeMessages: true, cancellationToken))
        {
            if (channel.Kind == MediaEventKind.Timecode)
            {
                continue;
            }

            foreach (MediaEvent e in channel.Events)
            {
                EventMessageBox? m = e switch
                {
                    Scte35Event s => s.Message,
                    TimedId3Event i => i.Message,
                    MessageEvent g => g.Message,
                    _ => null,
                };
                if (m is not null && e.Time.IsKnown)
                {
                    events.Add(new PackagingEvent(e.Time, e.Duration, m.SchemeIdUri, m.Value, m.Id, m.MessageData));
                }
            }
        }

        return events;
    }
}

/// <summary>Writes events into the manifests.</summary>
internal static class PackagingEvents
{
    // ISO/IEC 23009-1 §5.10.2: an EventStream holds Event elements; messageData carries the message as a string, here base64 of the bytes.
    public static IReadOnlyList<string> DashEventStreams(IReadOnlyList<PackagingEvent> events)
    {
        List<string> result = [];
        foreach (IGrouping<(string, string), PackagingEvent> group in events.GroupBy(e => (e.SchemeIdUri, e.Value)))
        {
            StringBuilder sb = new();
            using (XmlWriter w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment }))
            {
                w.WriteStartElement("EventStream");
                w.WriteAttributeString("schemeIdUri", group.Key.Item1);
                if (group.Key.Item2.Length > 0)
                {
                    w.WriteAttributeString("value", group.Key.Item2);
                }

                w.WriteAttributeString("timescale", "1000");
                foreach (PackagingEvent e in group.OrderBy(e => e.Time))
                {
                    w.WriteStartElement("Event");
                    w.WriteAttributeString("presentationTime", e.Time.ToTicks(1000, MediaRounding.NearestAwayFromZero).ToString(CultureInfo.InvariantCulture));
                    if (e.Duration.IsKnown)
                    {
                        w.WriteAttributeString("duration", e.Duration.ToTicks(1000, MediaRounding.NearestAwayFromZero).ToString(CultureInfo.InvariantCulture));
                    }

                    w.WriteAttributeString("id", e.Id.ToString(CultureInfo.InvariantCulture));
                    if (!e.Data.IsEmpty)
                    {
                        w.WriteAttributeString("messageData", Convert.ToBase64String(e.Data.Span));
                    }

                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            result.Add(sb.ToString());
        }

        return result;
    }

    // RFC 8216 §4.4.5.1: EXT-X-DATERANGE. Client attributes must start with X-. SCTE 35 splice_info_sections use the SCTE35-CMD attribute.
    public static string HlsDateRange(PackagingEvent e, DateTimeOffset origin)
    {
        StringBuilder sb = new("#EXT-X-DATERANGE:");
        sb.Append("ID=\"").Append(Quote(e.SchemeIdUri)).Append('-').Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(",CLASS=\"").Append(Quote(e.SchemeIdUri)).Append('"');
        sb.Append(",START-DATE=\"").Append(Date(origin, e.Time)).Append('"');
        if (e.Duration.IsKnown)
        {
            long ms = e.Duration.ToTicks(1000, MediaRounding.NearestAwayFromZero);
            sb.Append(",DURATION=").Append(string.Create(CultureInfo.InvariantCulture, $"{ms / 1000}.{ms % 1000:D3}"));
        }

        if (e.Value.Length > 0)
        {
            sb.Append(",X-VALUE=\"").Append(Quote(e.Value)).Append('"');
        }

        if (!e.Data.IsEmpty)
        {
            sb.Append(e.SchemeIdUri == EventMessageBox.Scte35Scheme ? ",SCTE35-CMD=0x" : ",X-MESSAGE-DATA=0x").Append(Convert.ToHexString(e.Data.Span));
        }

        return sb.ToString();
    }

    private static string Quote(string s) => s.Replace("\"", "%22", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal);

    public static string Date(DateTimeOffset origin, MediaTime t)
    {
        long ms = t.ToTicks(1000, MediaRounding.NearestAwayFromZero);
        return origin.AddMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}

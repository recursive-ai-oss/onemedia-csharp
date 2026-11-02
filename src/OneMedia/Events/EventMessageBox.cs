using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.TimedEvents;

/// <summary>
/// An event message box (<c>emsg</c>) found between the segments of a fragmented MP4 or CMAF stream: an in-band event with a scheme,
/// a time, a duration and an opaque message.
/// </summary>
/// <remarks>
/// Version 0 gives the time as a delta from the start of the segment that follows; version 1 gives it on the media timeline.
/// The box is defined by the DASH specification (ISO/IEC 23009-1 §5.10.3.3) and carried in ISO base media files.
/// </remarks>
public sealed record EventMessageBox
{
    /// <summary>The scheme for SCTE 35 splice information sections as binary.</summary>
    public const string Scte35Scheme = "urn:scte:scte35:2013:bin";

    /// <summary>The scheme for timed ID3 metadata (the AOM ID3 carriage).</summary>
    public const string Id3Scheme = "https://aomedia.org/emsg/ID3";

    /// <summary>The scheme the HLS-era Apple players used for timed ID3 in fragmented MP4.</summary>
    public const string AppleId3Scheme = "https://developer.apple.com/streaming/emsg-id3";

    /// <summary>The box version, 0 or 1.</summary>
    public int Version { get; init; }

    /// <summary>The <c>scheme_id_uri</c> that says how to read <see cref="MessageData"/>.</summary>
    public string SchemeIdUri { get; init; } = string.Empty;

    /// <summary>The scheme-specific <c>value</c> (a sub-stream selector).</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Ticks per second of the time and duration fields.</summary>
    public uint Timescale { get; init; }

    /// <summary>Version 0: the event start relative to the start of the following segment, in <see cref="Timescale"/> ticks.</summary>
    public uint PresentationTimeDelta { get; init; }

    /// <summary>Version 1: the event start on the media timeline, in <see cref="Timescale"/> ticks.</summary>
    public ulong PresentationTime { get; init; }

    /// <summary>The <c>event_duration</c> in <see cref="Timescale"/> ticks; 0xFFFFFFFF means unknown and 0 means unspecified or instantaneous.</summary>
    public uint EventDuration { get; init; }

    /// <summary>The <c>id</c> that identifies the event; repeats of an event share it.</summary>
    public uint Id { get; init; }

    /// <summary>The message bytes.</summary>
    public ReadOnlyMemory<byte> MessageData { get; init; }

    /// <summary>Where the box starts in the file, or -1.</summary>
    public long Offset { get; init; } = -1;

    /// <summary>The event duration as a time, unknown when <see cref="EventDuration"/> is 0xFFFFFFFF or the timescale is 0.</summary>
    public MediaTime Duration => Timescale == 0 || EventDuration == uint.MaxValue ? MediaTime.Unknown : new MediaTime(EventDuration, Timescale);

    /// <summary>Parses the payload of an <c>emsg</c> box (the bytes after its box header).</summary>
    /// <param name="payload">Version and flags, then the fields.</param>
    /// <param name="boxOffset">Where the box starts in the file, for the result and defects; -1 if unknown.</param>
    /// <param name="box">The box when the result is true.</param>
    /// <param name="defects">Receives deviations, or null.</param>
    /// <returns>False when the version is not 0 or 1, a string is not terminated or fixed fields are missing.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, long boxOffset, out EventMessageBox? box, DefectLog? defects = null)
    {
        box = null;
        if (payload.Length < 4)
        {
            return false;
        }

        int version = payload[0];
        if (version > 1)
        {
            defects?.Add(DefectKind.Unsupported, DefectSeverity.Warning, boxOffset, $"An emsg box has version {version}; only 0 and 1 are defined.", "emsg.version");
            return false;
        }

        int p = 4;
        uint timescale = 0, delta = 0, duration = 0, id = 0;
        ulong time = 0;
        string scheme, value;
        if (version == 0)
        {
            // Version 0: scheme_id_uri, value, then timescale, presentation_time_delta, event_duration, id (four 32-bit fields).
            if (!TryString(payload, ref p, out scheme) || !TryString(payload, ref p, out value) || payload.Length < p + 16)
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, boxOffset, "An emsg box is cut short.", "emsg.truncated");
                return false;
            }

            timescale = BinaryPrimitives.ReadUInt32BigEndian(payload[p..]);
            delta = BinaryPrimitives.ReadUInt32BigEndian(payload[(p + 4)..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(payload[(p + 8)..]);
            id = BinaryPrimitives.ReadUInt32BigEndian(payload[(p + 12)..]);
            p += 16;
        }
        else
        {
            // Version 1: timescale, presentation_time (64 bits), event_duration, id, then the two strings.
            if (payload.Length < p + 20)
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, boxOffset, "An emsg box is cut short.", "emsg.truncated");
                return false;
            }

            timescale = BinaryPrimitives.ReadUInt32BigEndian(payload[p..]);
            time = BinaryPrimitives.ReadUInt64BigEndian(payload[(p + 4)..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(payload[(p + 12)..]);
            id = BinaryPrimitives.ReadUInt32BigEndian(payload[(p + 16)..]);
            p += 20;
            if (!TryString(payload, ref p, out scheme) || !TryString(payload, ref p, out value))
            {
                defects?.Add(DefectKind.Truncated, DefectSeverity.Warning, boxOffset, "An emsg box has an unterminated string.", "emsg.string");
                return false;
            }
        }

        box = new EventMessageBox
        {
            Version = version,
            SchemeIdUri = scheme,
            Value = value,
            Timescale = timescale,
            PresentationTimeDelta = delta,
            PresentationTime = time,
            EventDuration = duration,
            Id = id,
            MessageData = payload[p..].ToArray(), // bounded by the box size, which the caller limits
            Offset = boxOffset,
        };
        return true;
    }

    private static bool TryString(ReadOnlySpan<byte> data, ref int position, out string text)
    {
        int end = data[position..].IndexOf((byte)0);
        if (end < 0)
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(data.Slice(position, end));
        position += end + 1;
        return true;
    }
}

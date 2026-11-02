using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.TimedEvents;

/// <summary>What a stream of timed events carries.</summary>
public enum MediaEventKind
{
    /// <summary>QuickTime timecode (a <c>tmcd</c> track): frame counters that name SMPTE time addresses.</summary>
    Timecode,

    /// <summary>SCTE 35 splice information: a transport stream PID of stream type 0x86, or <c>emsg</c> boxes of the SCTE 35 scheme.</summary>
    Scte35,

    /// <summary>ID3 timed metadata: a transport stream PID of stream type 0x15, or <c>emsg</c> boxes of the ID3 scheme.</summary>
    TimedId3,

    /// <summary>An <c>emsg</c> scheme the library does not decode; the message bytes are kept.</summary>
    Message,
}

/// <summary>One timed event. The concrete type follows the stream's <see cref="EventChannel.Kind"/>.</summary>
public abstract record MediaEvent
{
    /// <summary>
    /// When the event happens, on the same clock as the file's packet timestamps (<see cref="PacketInfo.Pts"/>), so it compares directly with the
    /// packets of the media tracks. For SCTE 35 this is the splice time (with <c>pts_adjustment</c> applied) when the message states one, else the time
    /// the message passed in the stream. <see cref="MediaTime.Unknown"/> when nothing places it.
    /// </summary>
    public MediaTime Time { get; init; } = MediaTime.Unknown;

    /// <summary>How long the event lasts, or <see cref="MediaTime.Unknown"/>.</summary>
    public MediaTime Duration { get; init; } = MediaTime.Unknown;

    /// <summary>Where the event's bytes start in the file, when they sit there unmodified (a zero-length range gives just the start); else <see cref="ByteRange.None"/>.</summary>
    public ByteRange Range { get; init; } = ByteRange.None;
}

/// <summary>A timecode sample: the frame counter at the start of a run of frames, and the time address it names.</summary>
public sealed record TimecodeEvent : MediaEvent
{
    /// <summary>The 32-bit frame counter the sample holds (negative when the track allows negative times).</summary>
    public long FrameNumber { get; init; }

    /// <summary>The time address of the first frame of the sample.</summary>
    public Timecode Start { get; init; }
}

/// <summary>A SCTE 35 splice information message.</summary>
public sealed record Scte35Event : MediaEvent
{
    /// <summary>The parsed section.</summary>
    public required Scte35Section Section { get; init; }

    /// <summary>True when <see cref="MediaEvent.Time"/> is the splice time the command states; false when it is only the time the message was carried.</summary>
    public bool IsSpliceTimeSpecified { get; init; }

    /// <summary>The <c>emsg</c> box that carried it, or null when it came from a transport stream PID.</summary>
    public EventMessageBox? Message { get; init; }
}

/// <summary>An ID3 tag carried as timed metadata.</summary>
public sealed record TimedId3Event : MediaEvent
{
    /// <summary>The tag. Its frame and byte ranges are relative to the start of the tag's own bytes.</summary>
    public required Id3Tag Tag { get; init; }

    /// <summary>The <c>emsg</c> box that carried it, or null when it came from a transport stream PID.</summary>
    public EventMessageBox? Message { get; init; }
}

/// <summary>An <c>emsg</c> event of a scheme the library does not decode.</summary>
public sealed record MessageEvent : MediaEvent
{
    /// <summary>The box, with its scheme, value, id and message bytes.</summary>
    public required EventMessageBox Message { get; init; }

    /// <summary>False when the box is version 0 and no following segment gave a base time, so <see cref="MediaEvent.Time"/> is only the delta from the segment start.</summary>
    public bool TimeIsAbsolute { get; init; }
}

/// <summary>
/// One stream of timed events: a timecode track, a SCTE 35 PID, an ID3 metadata PID, or the <c>emsg</c> boxes of one scheme and value.
/// The events are read on first access to <see cref="Events"/> and kept.
/// </summary>
public sealed class EventChannel
{
    private readonly Func<IReadOnlyList<MediaEvent>> _load;
    private IReadOnlyList<MediaEvent>? _events;

    internal EventChannel(MediaEventKind kind, string scheme, string? value, MediaTrack? track, TimecodeSampleEntry? timecode, Func<IReadOnlyList<MediaEvent>> load)
    {
        Kind = kind;
        Scheme = scheme;
        Value = value;
        Track = track;
        Timecode = timecode;
        _load = load;
    }

    /// <summary>What the stream carries.</summary>
    public MediaEventKind Kind { get; }

    /// <summary>A short name for the carriage: <c>tmcd</c>, <c>scte-35</c>, <c>timed-id3</c>, or the <c>emsg</c> scheme URI.</summary>
    public string Scheme { get; }

    /// <summary>The <c>emsg</c> value of the stream, or null for track-based streams.</summary>
    public string? Value { get; }

    /// <summary>The track that carries the stream, or null for <c>emsg</c> streams, which live between the segments, not in a track.</summary>
    public MediaTrack? Track { get; }

    /// <summary>For a timecode stream, how its counters map to time addresses; otherwise null.</summary>
    public TimecodeSampleEntry? Timecode { get; }

    /// <summary>
    /// The events in file order. Read on first access and kept: a track-based stream reads that track's packets once (a transport stream PID is
    /// found by scanning the file, since splice and ID3 messages are spread through it).
    /// </summary>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source, or its source cannot seek after an earlier read.</exception>
    public IReadOnlyList<MediaEvent> Events => _events ??= _load();

    /// <summary>
    /// For a timecode stream: the time address of the frame shown at a presentation time, counted from the sample that starts at or before it.
    /// </summary>
    /// <param name="time">A time on the track's presentation timeline.</param>
    /// <param name="timecode">The address when the result is true.</param>
    /// <returns>False when this is not a timecode stream, it has no samples or <paramref name="time"/> is before the first sample.</returns>
    public bool TryGetTimecodeAt(MediaTime time, out Timecode timecode)
    {
        timecode = default;
        if (Timecode is not { } entry || !time.IsKnown || entry.FrameTime is { IsKnown: false })
        {
            return false;
        }

        TimecodeEvent? at = null;
        foreach (MediaEvent e in Events)
        {
            if (e is TimecodeEvent t && t.Time.IsKnown && t.Time <= time)
            {
                at = t;
            }
        }

        if (at is null)
        {
            return false;
        }

        // Whole frames elapsed since the sample began: floor((time - start) / frame duration), exact in rational arithmetic
        // (the two times may use different timescales, so the difference is never formed as a MediaTime).
        long frames = FloorFrames(time, at.Time, entry.FrameTime);
        timecode = entry.FromFrameNumber(Saturate((System.Numerics.BigInteger)at.FrameNumber + frames));
        return true;
    }

    private static long FloorFrames(MediaTime time, MediaTime start, MediaTime frame)
    {
        // (t.Ticks/t.Scale - s.Ticks/s.Scale) / (f.Ticks/f.Scale)
        //   = (t.Ticks*s.Scale - s.Ticks*t.Scale) * f.Scale / (t.Scale*s.Scale*f.Ticks)
        System.Numerics.BigInteger numerator = ((System.Numerics.BigInteger)time.Ticks * start.Timescale - (System.Numerics.BigInteger)start.Ticks * time.Timescale) * frame.Timescale;
        System.Numerics.BigInteger denominator = (System.Numerics.BigInteger)time.Timescale * start.Timescale * frame.Ticks;
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        System.Numerics.BigInteger q = System.Numerics.BigInteger.DivRem(numerator, denominator, out System.Numerics.BigInteger r);
        if (r.Sign < 0)
        {
            q -= 1;
        }

        return Saturate(q);
    }

    private static long Saturate(System.Numerics.BigInteger value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;
}

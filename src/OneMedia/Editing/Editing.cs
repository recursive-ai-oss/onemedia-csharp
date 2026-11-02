using System.Collections;

namespace RecursiveAI.OneMedia.Editing;

/// <summary>How a trim treats the requested start.</summary>
public enum TrimMode
{
    /// <summary>
    /// The first frame shown is the first video frame at or after the requested start. Decoding starts at the keyframe before it; the
    /// frames in between stay in the file and the edit list hides them. Audio is cut to the sample the same way.
    /// </summary>
    FrameAccurate,

    /// <summary>
    /// The clip starts on the keyframe at or before the requested start, with nothing hidden: every video frame in the output is shown.
    /// Audio is still aligned to that keyframe through the edit list.
    /// </summary>
    Keyframe,
}

/// <summary>Why an edit operation (trim, split, concat) was refused.</summary>
public enum EditRefusalReason
{
    /// <summary>The range is empty, reversed or lies past the end of the media.</summary>
    EmptyRange = 1,

    /// <summary>The video has no keyframe to start from.</summary>
    NoKeyframe,

    /// <summary>A track's edit list has more than one media segment or a gap in the middle; its presentation cannot be mapped back to media times.</summary>
    ComplexEditList,

    /// <summary>
    /// The inputs of a concatenation differ in a way the output cannot carry without changing the coded data: the named track needs a
    /// re-encode (or a muxer that writes more than one sample description per track).
    /// </summary>
    NeedsReencode,

    /// <summary>The source cannot be cut this way (no seeking, no tracks the muxer can carry).</summary>
    UnsupportedSource,

    /// <summary>A split point list is not usable (not ascending, not positive).</summary>
    InvalidSplit,

    /// <summary>
    /// A frame-accurate start in Matroska needs frames with negative timestamps in front of the first shown one (RFC 9559 §11.2), and files with them cannot be
    /// played by players built on ffmpeg's demuxer when the video is H.264, HEVC, VVC, MPEG-2, MPEG-4 or VC-1 (they report the presentation time as undefined).
    /// Cut at a keyframe (<see cref="TrimMode.Keyframe"/>) or write MP4, where an edit list hides the frames.
    /// </summary>
    ExactStartNotSupported,
}

/// <summary>An edit operation was refused. Nothing is written for the refused part, and the output stream is left as it was.</summary>
public sealed class EditRefusedException : MediaException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="message">What is wrong, in words.</param>
    /// <param name="trackIndex">The source track concerned, or -1.</param>
    /// <param name="codec">The codec concerned, if any.</param>
    public EditRefusedException(EditRefusalReason reason, string message, int trackIndex = -1, CodecId? codec = null) : base(message)
    {
        Reason = reason;
        TrackIndex = trackIndex;
        Codec = codec;
    }

    /// <summary>Why the operation was refused.</summary>
    public EditRefusalReason Reason { get; }

    /// <summary>The index of the source (or first input) track concerned; -1 when none.</summary>
    public int TrackIndex { get; }

    /// <summary>The codec of that track, if known.</summary>
    public CodecId? Codec { get; }
}

/// <summary>What one cut, split part or concatenation did.</summary>
/// <param name="SourceStart">Where the output begins on the source's presentation timeline: the first shown video frame (or the requested start for audio-only media).</param>
/// <param name="Duration">The presentation length of the output.</param>
/// <param name="HiddenVideoPackets">Video packets written before the first shown frame (the edit list hides them). Zero in keyframe mode.</param>
/// <param name="Notes">Everything the operation rounded, moved or dropped, in words.</param>
/// <param name="Mux">The muxer's own report.</param>
public sealed record ClipReport(MediaTime SourceStart, MediaTime Duration, int HiddenVideoPackets, IReadOnlyList<string> Notes, MuxReport Mux)
{
    /// <summary>A one-line summary: where the clip starts and how long it is. The data is in the properties.</summary>
    public override string ToString() => $"Clip: starts at {SourceStart}, lasts {Duration}, {Notes.Count} note{(Notes.Count == 1 ? "" : "s")} (see Notes)";
}

/// <summary>
/// An edit list the muxer reads when the file is finished. An operation that streams does not know a clip's last frame or a track's first
/// packet when the tracks are declared; the muxer takes the edit list as given at finish time (<c>docs/muxing.md</c>), so the list is filled in then.
/// </summary>
internal sealed class DeferredEdits : IReadOnlyList<EditSegment>
{
    private readonly List<EditSegment> _items = [];

    public int Count => _items.Count;

    public EditSegment this[int index] => _items[index];

    public void Set(IEnumerable<EditSegment> segments)
    {
        _items.Clear();
        _items.AddRange(segments);
    }

    public IEnumerator<EditSegment> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Exact sums and differences of times that count in different units: the result uses the finer unit, so nothing is rounded away.</summary>
internal static class EditTime
{
    public static MediaTime Plus(MediaTime a, MediaTime b) => Combine(a, b, 1);

    public static MediaTime Minus(MediaTime a, MediaTime b) => Combine(a, b, -1);

    private static MediaTime Combine(MediaTime a, MediaTime b, int sign)
    {
        long scale = Math.Max(a.Timescale, b.Timescale);
        if (scale % a.Timescale != 0 || scale % b.Timescale != 0)
        {
            return sign > 0 ? a.Add(b, MediaRounding.NearestAwayFromZero) : a.Subtract(b, MediaRounding.NearestAwayFromZero);
        }

        return new MediaTime(checked((a.Ticks * (scale / a.Timescale)) + (sign * b.Ticks * (scale / b.Timescale))), scale);
    }
}

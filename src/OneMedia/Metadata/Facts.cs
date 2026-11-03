using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>
/// One reading of a fact from one place in the file, with enough to judge it: where it came from, what was written, and how it was
/// interpreted.
/// </summary>
/// <typeparam name="T">The type of the fact's value.</typeparam>
/// <param name="Value">The interpreted value.</param>
/// <param name="Source">The place it came from, as <c>system:key</c> (for example <c>mdta:com.apple.quicktime.creationdate</c>, <c>mvhd</c>, <c>tkhd.matrix</c>).</param>
/// <param name="RawValue">The stored value as text (numbers in decimal; binary values as hexadecimal).</param>
/// <param name="Interpretation">How <paramref name="RawValue"/> was turned into <paramref name="Value"/>, in words.</param>
/// <param name="Certainty">For date-times, what the source says about the time zone; <see cref="TimeZoneCertainty.Unknown"/> for other facts.</param>
/// <param name="Range">The bytes in the file the reading came from, or <see cref="ByteRange.None"/>.</param>
public sealed record FactCandidate<T>(T Value, string Source, string RawValue, string Interpretation, TimeZoneCertainty Certainty = TimeZoneCertainty.Unknown, ByteRange Range = default)
{
    /// <summary>The bytes in the file the reading came from, or <see cref="ByteRange.None"/> when no range is known (an omitted range is <see cref="ByteRange.None"/>, not an empty range at offset 0).</summary>
    public ByteRange Range { get; init; } = Range.Offset == 0 && Range.Length == 0 ? ByteRange.None : Range;
}

/// <summary>Creates facts from candidates.</summary>
public static class Fact
{
    /// <summary>
    /// Creates a fact from candidates a resolver has ordered, best first. This is how a custom <see cref="IFactResolver"/> returns its answer.
    /// </summary>
    /// <typeparam name="T">The type of the fact's value.</typeparam>
    /// <param name="candidates">The readings, best first; copied.</param>
    /// <param name="resolution">Which reading won and why, or <c>null</c> when the order is not the result of a resolution.</param>
    /// <returns>The fact; with no candidates it has no value.</returns>
    public static Fact<T> Create<T>(IEnumerable<FactCandidate<T>> candidates, FactResolution? resolution = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        List<FactCandidate<T>> list = [.. candidates];
        return new Fact<T>(list, resolution);
    }
}

/// <summary>
/// A fact with every reading found, best first. The order is the documented interim precedence; a later resolver may reorder or replace it.
/// </summary>
/// <typeparam name="T">The type of the fact's value.</typeparam>
public sealed class Fact<T>
{
    internal Fact(IReadOnlyList<FactCandidate<T>> candidates, FactResolution? resolution = null)
    {
        Candidates = candidates;
        Resolution = resolution;
    }

    /// <summary>Which reading won and why, when a resolver produced this fact; <c>null</c> for the container-only interim order.</summary>
    public FactResolution? Resolution { get; }

    internal static Fact<T> None { get; } = new([]);

    /// <summary>All readings, best first. Empty when the file says nothing.</summary>
    public IReadOnlyList<FactCandidate<T>> Candidates { get; }

    /// <summary>The best reading, or <c>null</c>.</summary>
    public FactCandidate<T>? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>True when there is at least one reading.</summary>
    public bool HasValue => Candidates.Count > 0;

    /// <summary>
    /// The best reading's value. Check <see cref="HasValue"/> first, or use <see cref="TryGetValue"/>: a fact with no readings has no value, and
    /// returning a default would be indistinguishable from a real one (a location of 0,0 is a place in the Atlantic Ocean).
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no reading (<see cref="HasValue"/> is false).</exception>
    public T Value => Candidates.Count > 0 ? Candidates[0].Value : throw new InvalidOperationException("The file states no value for this fact; check HasValue or use TryGetValue.");

    /// <summary>Gets the best reading's value when there is one.</summary>
    /// <param name="value">The best reading's value, or the type's default when this returns false.</param>
    /// <returns>True when there is at least one reading.</returns>
    public bool TryGetValue([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value)
    {
        if (Candidates.Count > 0)
        {
            value = Candidates[0].Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The best reading's value as text, or <c>none</c> when the file states nothing.</summary>
    public override string ToString() => Candidates.Count > 0 ? Candidates[0].Value?.ToString() ?? string.Empty : "none";
}

/// <summary>Display orientation of a video track: a clockwise rotation, then an optional horizontal mirror.</summary>
/// <param name="RotationDegrees">0, 90, 180 or 270 degrees clockwise.</param>
/// <param name="MirrorHorizontal">True when the picture is mirrored left to right after the rotation.</param>
public readonly record struct VideoOrientation(int RotationDegrees, bool MirrorHorizontal);

/// <summary>Picture size of a video track.</summary>
/// <param name="CodedWidth">Width in samples of the first sample description.</param>
/// <param name="CodedHeight">Height in samples of the first sample description.</param>
/// <param name="DisplayWidth">Presentation width from the track header, after any 90 or 270 degree rotation (0 when the header gives none).</param>
/// <param name="DisplayHeight">Presentation height from the track header, after rotation.</param>
public readonly record struct FrameSize(int CodedWidth, int CodedHeight, int DisplayWidth, int DisplayHeight);

/// <summary>The device facts together: maker, model and software, each with its candidates.</summary>
/// <param name="Make">The device maker.</param>
/// <param name="Model">The device model.</param>
/// <param name="Software">The software that recorded or wrote the file.</param>
public sealed record DeviceFacts(Fact<string> Make, Fact<string> Model, Fact<string> Software)
{
    /// <summary>The device's serial number (a per-unit device identifier), with its candidates. Empty when no source states one.</summary>
    public Fact<string> Serial { get; init; } = Fact<string>.None;
}

/// <summary>
/// The basic facts a file states about itself: how long, when and where it was captured, on what, and how it is oriented. Each is a
/// <see cref="Fact{T}"/>: the candidates in the interim precedence order, best first. The interim order for capture time is Apple
/// <c>creationdate</c> (carries an offset), other vendor times with an offset (XMP, <c>creation_time</c>), user-data and iTunes <c>©day</c>,
/// XMP times without an offset, Matroska <c>DateUTC</c>, then the movie header (UTC). Location: <c>location.ISO6709</c>, <c>©xyz</c>, <c>loci</c>, XMP GPS.
/// Make, model and software: key-table metadata, user data, XMP.
/// </summary>
public sealed class MediaFacts
{
    /// <summary>Creates a set of facts; a custom <see cref="IFactResolver"/> returns one of these.</summary>
    /// <param name="duration">Duration.</param>
    /// <param name="captureTime">Capture time.</param>
    /// <param name="location">Location.</param>
    /// <param name="make">Device maker.</param>
    /// <param name="model">Device model.</param>
    /// <param name="software">Recording software.</param>
    /// <param name="orientation">Display orientation.</param>
    /// <param name="frameSize">Picture size.</param>
    /// <param name="serial">Device serial number; none when <c>null</c>.</param>
    /// <param name="profile">The device profile that applied, or <c>null</c>.</param>
    public MediaFacts(Fact<MediaTime> duration, Fact<MediaDateTime> captureTime, Fact<GeoLocation> location, Fact<string> make, Fact<string> model, Fact<string> software,
        Fact<VideoOrientation> orientation, Fact<FrameSize> frameSize, Fact<string>? serial = null, DeviceProfile? profile = null)
    {
        Serial = serial ?? Fact<string>.None;
        Profile = profile;
        Duration = duration;
        CaptureTime = captureTime;
        Location = location;
        Make = make;
        Model = model;
        Software = software;
        Orientation = orientation;
        FrameSize = frameSize;
    }

    /// <summary>Duration of the longest track in presentation time.</summary>
    public Fact<MediaTime> Duration { get; }

    /// <summary>When the recording was made.</summary>
    public Fact<MediaDateTime> CaptureTime { get; }

    /// <summary>Where the recording was made.</summary>
    public Fact<GeoLocation> Location { get; }

    /// <summary>The device maker, model and software together.</summary>
    public DeviceFacts Device => new(Make, Model, Software) { Serial = Serial };

    /// <summary>The device serial number, with its candidates.</summary>
    public Fact<string> Serial { get; }

    /// <summary>The device profile the resolver applied (it decides, for example, whether the movie header is local time), or <c>null</c>.</summary>
    public DeviceProfile? Profile { get; }

    /// <summary>The device maker.</summary>
    public Fact<string> Make { get; }

    /// <summary>The device model.</summary>
    public Fact<string> Model { get; }

    /// <summary>The software that recorded or wrote the file (firmware or OS version, creator tool).</summary>
    public Fact<string> Software { get; }

    /// <summary>The display orientation of the first video track, from its track header matrix.</summary>
    public Fact<VideoOrientation> Orientation { get; }

    /// <summary>The picture size of the first video track.</summary>
    public Fact<FrameSize> FrameSize { get; }
}

/// <summary>Accumulates candidates with a rank (lower is better) and keeps discovery order within a rank.</summary>
internal sealed class CandidateList<T>
{
    private readonly List<(int Rank, int Order, FactCandidate<T> Candidate)> _items = [];

    public void Add(int rank, FactCandidate<T> candidate) => _items.Add((rank, _items.Count, candidate));

    public Fact<T> ToFact()
    {
        if (_items.Count == 0)
        {
            return Fact<T>.None;
        }

        _items.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        List<FactCandidate<T>> list = new(_items.Count);
        foreach ((int _, int _, FactCandidate<T> c) in _items)
        {
            list.Add(c);
        }

        return new Fact<T>(list);
    }
}

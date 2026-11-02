namespace RecursiveAI.OneMedia.Privacy;

/// <summary>What a privacy strip removes.</summary>
[Flags]
public enum StripCategories
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Where the media was made: coordinates, place names, GPS time and fix data, local positions, detected-face positions.</summary>
    Location = 1,

    /// <summary>What made it: camera, body and lens serial numbers, media and content unique identifiers, owner names. Make and model are kept.</summary>
    DeviceIds = 2,

    /// <summary>Both categories.</summary>
    All = Location | DeviceIds,
}

/// <summary>Options for <see cref="PrivacyStrip"/>.</summary>
public sealed class StripOptions
{
    /// <summary>Strip both categories.</summary>
    public static StripOptions Default { get; } = new();

    /// <summary>Strip only location.</summary>
    public static StripOptions LocationOnly { get; } = new() { Categories = StripCategories.Location };

    /// <summary>Strip only device identifiers.</summary>
    public static StripOptions DeviceIdsOnly { get; } = new() { Categories = StripCategories.DeviceIds };

    /// <summary>The categories to strip. Default <see cref="StripCategories.All"/>.</summary>
    public StripCategories Categories { get; init; } = StripCategories.All;

    /// <summary>Resource limits for reading the file; <see cref="MediaLimits.Default"/> when null.</summary>
    public MediaLimits? Limits { get; init; }
}

/// <summary>How a field was neutralised.</summary>
public enum StripAction
{
    /// <summary>The field was taken out of its container (the tag system's own writer removed it).</summary>
    Removed = 1,

    /// <summary>The field's bytes were overwritten with zeros and the field kept its length (a telemetry sample, a binary atom's payload).</summary>
    Zeroed,

    /// <summary>The box or element was turned into padding of the same size (a <c>free</c> box), so no byte after it moved.</summary>
    ConvertedToPadding,

    /// <summary>The bytes were overwritten with a neutral value that is not zero (0xFF in AVCHD camera data, where zeros would need escaping; spaces in text), and the field's tag or key was changed so readers skip it. Length unchanged.</summary>
    Overwritten,

    /// <summary>The text of an XMP packet was rewritten without the properties, padded with white space to its old length.</summary>
    Rewritten,
}

/// <summary>One field or track the strip touched.</summary>
/// <param name="Category">Which category the field belongs to.</param>
/// <param name="System">The tag system (<see cref="Tags.TagSystems"/>) or, for telemetry, <c>gpmd</c>, <c>camm</c>, <c>mebx</c>, or <c>fdsc</c> for the GoPro settings track.</param>
/// <param name="Name">The native key, or the telemetry key (<c>GPS5</c>, <c>camm type 6</c>).</param>
/// <param name="TrackId">The track (0 for the movie or file).</param>
/// <param name="Range">Where the field was in the original file. For telemetry that occurs in many samples: the span from the first to the last occurrence.</param>
/// <param name="Count">How many occurrences were neutralised (1 for a tag; the number of samples for telemetry).</param>
/// <param name="Bytes">Total bytes overwritten, removed or rewritten for this entry.</param>
/// <param name="Action">What was done.</param>
public sealed record StripEntry(StripCategories Category, string System, string Name, long TrackId, ByteRange Range, int Count, long Bytes, StripAction Action);

/// <summary>Something the strip found that holds location or a device identifier and could not take out.</summary>
/// <param name="Category">The category.</param>
/// <param name="System">Where it was found.</param>
/// <param name="Name">The key or field.</param>
/// <param name="TrackId">The track (0 for the movie or file).</param>
/// <param name="Range">Where it is in the original file, or <see cref="ByteRange.None"/>.</param>
/// <param name="Reason">Why it stayed.</param>
public sealed record StripLeftover(StripCategories Category, string System, string Name, long TrackId, ByteRange Range, string Reason);

/// <summary>What a strip did, precisely enough to check against the output.</summary>
public sealed class StripReport
{
    internal StripReport(StripCategories categories, IReadOnlyList<StripEntry> entries, IReadOnlyList<StripLeftover> notRemoved, IReadOnlyList<string> notes)
    {
        Categories = categories;
        Entries = entries;
        NotRemoved = notRemoved;
        Notes = notes;
    }

    /// <summary>The categories that were asked for.</summary>
    public StripCategories Categories { get; }

    /// <summary>Every field and telemetry stream that was neutralised, in file order of first occurrence.</summary>
    public IReadOnlyList<StripEntry> Entries { get; }

    /// <summary>Everything that was found and left in place, with the reason. Empty means nothing the strip knows to look for remains.</summary>
    public IReadOnlyList<StripLeftover> NotRemoved { get; }

    /// <summary>Remarks that are neither entries nor leftovers (a layout that was kept, a tag system the format does not have).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// Areas no strip run examines, the same on every report: free text in tags and comments (outside the listed fields and the text rules for subtitle tracks),
    /// atoms and boxes from vendors the strip has no rule for, and data appended after the last structure (trailers). An empty <see cref="NotRemoved"/> does not
    /// speak for these. docs/strip.md describes each.
    /// </summary>
    public static IReadOnlyList<string> NotExamined { get; } = new[]
    {
        "free text in tags, comments and descriptions",
        "unknown vendor atoms and boxes",
        "trailers: data after the last structure",
    };

    /// <summary>True when <see cref="NotRemoved"/> is empty.</summary>
    public bool IsComplete => NotRemoved.Count == 0;

    /// <summary>The byte-level report once the plan was saved; null before.</summary>
    public WriteReport? Write { get; internal set; }
}

/// <summary>A planned strip: the edit plan that produces the new file, and the report of what it does.</summary>
public sealed class StripPlan
{
    internal StripPlan(EditPlan plan, StripReport report)
    {
        Plan = plan;
        Report = report;
    }

    /// <summary>The plan; apply it with <see cref="SafeWriter"/> or use the save methods of <see cref="PrivacyStrip"/>.</summary>
    public EditPlan Plan { get; }

    /// <summary>What the plan removes.</summary>
    public StripReport Report { get; }
}

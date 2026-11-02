using System.Globalization;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>Which rung of the default capture-time precedence a reading sits on. Lower wins.</summary>
public enum CaptureTimeTier
{
    /// <summary>A time written as text with an explicit UTC offset or <c>Z</c> by a vendor tag, XMP or a sidecar (Apple <c>creationdate</c>, Sony XML, MDPM with zone).</summary>
    OffsetBearingVendor = 1,

    /// <summary>The UTC time of the first GPS fix in an attached telemetry stream, moved back by the fix's position on the media timeline.</summary>
    TelemetryGps = 2,

    /// <summary>A wall-clock time that states no zone, or that a device profile says is local (<c>©day</c>, <c>IDIT</c>, <c>bext</c>, a profiled movie header).</summary>
    ContainerLocal = 3,

    /// <summary>A binary header time (<c>mvhd</c>, Matroska <c>DateUTC</c>, ASF file properties), read as UTC.</summary>
    HeaderUtc = 4,

    /// <summary>A date found in the file name.</summary>
    FileName = 5,

    /// <summary>The file system's modification time.</summary>
    FileSystem = 6,
}

/// <summary>Which candidate a resolver chose for a fact and why.</summary>
/// <param name="Rule">A short stable name of the rule that decided (<c>capture-time.tier-1</c>, <c>order</c>).</param>
/// <param name="Reason">The decision in words, including what the winner beat and any disagreement worth knowing.</param>
/// <param name="Resolver">The name of the resolver that decided.</param>
public sealed record FactResolution(string Rule, string Reason, string Resolver);

/// <summary>
/// Everything a resolver may weigh: the candidate lists the container's readers found, plus telemetry, sidecar readings and what the caller knows about
/// the file's name and file-system time. Candidate lists are in the order the producers gave them (container first, then sidecars); a resolver may reorder.
/// </summary>
public sealed record FactEvidence
{
    /// <summary>Duration readings.</summary>
    public IReadOnlyList<FactCandidate<MediaTime>> Duration { get; init; } = [];

    /// <summary>Capture-time readings.</summary>
    public IReadOnlyList<FactCandidate<MediaDateTime>> CaptureTime { get; init; } = [];

    /// <summary>Location readings.</summary>
    public IReadOnlyList<FactCandidate<GeoLocation>> Location { get; init; } = [];

    /// <summary>Device maker readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Make { get; init; } = [];

    /// <summary>Device model readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Model { get; init; } = [];

    /// <summary>Recording software readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Software { get; init; } = [];

    /// <summary>Device serial number readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Serial { get; init; } = [];

    /// <summary>Orientation readings.</summary>
    public IReadOnlyList<FactCandidate<VideoOrientation>> Orientation { get; init; } = [];

    /// <summary>Picture size readings.</summary>
    public IReadOnlyList<FactCandidate<FrameSize>> FrameSize { get; init; } = [];

    /// <summary>Decoded telemetry attached to the file. The default resolver takes GPS time and the first fix from <see cref="TelemetryKind.Gps"/> streams.</summary>
    public IReadOnlyList<TelemetryChannel> Telemetry { get; init; } = [];

    /// <summary>The file name (without directory) for filename-pattern dates, or <c>null</c> to skip them.</summary>
    public string? FileName { get; init; }

    /// <summary>The file's last-write time in UTC for the last-resort candidate, or <c>null</c> to skip it.</summary>
    public DateTime? FileSystemTimeUtc { get; init; }

    /// <summary>Starts evidence from the container's own facts (the interim order of <see cref="MediaFacts"/>).</summary>
    /// <param name="facts">The container facts.</param>
    /// <returns>Evidence holding the same candidates.</returns>
    public static FactEvidence FromContainer(MediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new FactEvidence
        {
            Duration = facts.Duration.Candidates,
            CaptureTime = facts.CaptureTime.Candidates,
            Location = facts.Location.Candidates,
            Make = facts.Make.Candidates,
            Model = facts.Model.Candidates,
            Software = facts.Software.Candidates,
            Serial = facts.Serial.Candidates,
            Orientation = facts.Orientation.Candidates,
            FrameSize = facts.FrameSize.Candidates,
        };
    }

    /// <summary>Adds the readings of a sidecar after the ones already present.</summary>
    /// <param name="sidecar">The parsed sidecar.</param>
    /// <returns>New evidence with the sidecar's candidates and telemetry appended.</returns>
    public FactEvidence With(EvidenceSet sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        return this with
        {
            CaptureTime = [.. CaptureTime, .. sidecar.CaptureTime],
            Location = [.. Location, .. sidecar.Location],
            Make = [.. Make, .. sidecar.Make],
            Model = [.. Model, .. sidecar.Model],
            Software = [.. Software, .. sidecar.Software],
            Serial = [.. Serial, .. sidecar.Serial],
            Telemetry = [.. Telemetry, .. sidecar.Telemetry],
        };
    }
}

/// <summary>Turns evidence into facts. Implementations must be deterministic and must not drop candidates: the resolver orders and explains, it does not discard.</summary>
public interface IFactResolver
{
    /// <summary>Resolves every fact from the evidence.</summary>
    /// <param name="evidence">What was found.</param>
    /// <returns>The facts: each lists all of its candidates, best first, and states which won and why through <see cref="Fact{T}.Resolution"/>.</returns>
    MediaFacts Resolve(FactEvidence evidence);
}

/// <summary>
/// The documented default resolver (docs/facts.md). Capture time is ordered by <see cref="CaptureTimeTier"/>, then by the order the readers gave; a
/// matching <see cref="DeviceProfile"/> first reinterprets header times. Location lists explicit tags and sidecars before the first telemetry fix.
/// Every other fact keeps the order of its readers. Nothing is dropped.
/// </summary>
public sealed class DefaultFactResolver : IFactResolver
{
    private const string Name = "default";

    /// <summary>Creates a resolver over a profile table.</summary>
    /// <param name="profiles">The device profiles; <see cref="DeviceProfileTable.Default"/> when <c>null</c>.</param>
    public DefaultFactResolver(DeviceProfileTable? profiles = null) => Profiles = profiles ?? DeviceProfileTable.Default;

    /// <summary>A resolver over <see cref="DeviceProfileTable.Default"/>.</summary>
    public static DefaultFactResolver Instance { get; } = new();

    /// <summary>The device profiles in use.</summary>
    public DeviceProfileTable Profiles { get; }

    /// <summary>The tier a reading sits on, before any device profile is applied.</summary>
    /// <param name="candidate">The reading.</param>
    /// <returns>Its tier.</returns>
    public static CaptureTimeTier Classify(FactCandidate<MediaDateTime> candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string s = candidate.Source;
        if (s.StartsWith("telemetry:", StringComparison.Ordinal))
        {
            return CaptureTimeTier.TelemetryGps;
        }

        if (s == "filename")
        {
            return CaptureTimeTier.FileName;
        }

        if (s == "filesystem")
        {
            return CaptureTimeTier.FileSystem;
        }

        if (IsHeaderSource(s))
        {
            return candidate.Certainty == TimeZoneCertainty.Utc ? CaptureTimeTier.HeaderUtc : CaptureTimeTier.ContainerLocal;
        }

        return candidate.Certainty is TimeZoneCertainty.Offset or TimeZoneCertainty.Utc ? CaptureTimeTier.OffsetBearingVendor : CaptureTimeTier.ContainerLocal;
    }

    private static bool IsHeaderSource(string s) =>
        s is "mvhd" or "tkhd" or "mdhd" || s.StartsWith("asf:FileProperties", StringComparison.Ordinal) || s.StartsWith("mkv-info:", StringComparison.Ordinal);

    /// <inheritdoc />
    public MediaFacts Resolve(FactEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        // Telemetry names the device only when nothing else does: its candidates follow the container's and the sidecars'.
        Fact<string> make = Order([.. evidence.Make, .. Telemetry.TelemetryFacts.MakeCandidates(evidence.Telemetry)], "make");
        Fact<string> model = Order([.. evidence.Model, .. Telemetry.TelemetryFacts.ModelCandidates(evidence.Telemetry)], "model");
        DeviceProfile? profile = Profiles.Find(make.Best?.Value, model.Best?.Value, SoftwareForProfile(evidence));

        return new MediaFacts(
            Order(evidence.Duration, "duration"),
            ResolveCaptureTime(evidence, profile),
            ResolveLocation(evidence),
            make,
            model,
            Order(evidence.Software, "software"),
            Order(evidence.Orientation, "orientation"),
            Order(evidence.FrameSize, "frame-size"),
            Order(evidence.Serial, "serial"),
            profile);
    }

    // Any software reading may be a firmware string that names the device; the first one a profile shape accepts decides.
    private string? SoftwareForProfile(FactEvidence evidence)
    {
        foreach (FactCandidate<string> c in evidence.Software)
        {
            foreach (DeviceProfile row in Profiles.Rows)
            {
                if (row.MatchesSoftware(c.Value))
                {
                    return c.Value;
                }
            }
        }

        return null;
    }

    private static Fact<T> Order<T>(IReadOnlyList<FactCandidate<T>> candidates, string what)
    {
        if (candidates.Count == 0)
        {
            return Fact.Create<T>([]);
        }

        string reason = candidates.Count == 1 ? $"the only {what} reading ({candidates[0].Source})"
            : $"first of {candidates.Count} {what} readings in reader order ({candidates[0].Source})";
        return Fact.Create<T>(candidates, new FactResolution("order", reason, Name));
    }

    private static Fact<GeoLocation> ResolveLocation(FactEvidence e)
    {
        List<FactCandidate<GeoLocation>> list = [.. e.Location];
        int explicitCount = list.Count;
        foreach (TelemetryChannel stream in e.Telemetry)
        {
            if (stream.Kind != TelemetryKind.Gps)
            {
                continue;
            }

            foreach (TelemetrySample sample in stream.Samples)
            {
                if (sample is GpsSample { Fix: not GpsFixKind.None } g && Math.Abs(g.Latitude) <= 90 && Math.Abs(g.Longitude) <= 180 && !(g.Latitude == 0 && g.Longitude == 0))
                {
                    list.Add(new FactCandidate<GeoLocation>(new GeoLocation(g.Latitude, g.Longitude, g.Altitude), $"telemetry:{stream.Format}:{stream.Name}",
                        string.Create(CultureInfo.InvariantCulture, $"{g.Latitude} {g.Longitude}"), "first valid fix of the stream", Range: g.Range));
                    break;
                }
            }
        }

        if (list.Count == 0)
        {
            return Fact.Create<GeoLocation>([]);
        }

        string reason = explicitCount > 0 ? $"explicit position from {list[0].Source}, ahead of {list.Count - 1} other reading(s)" : $"first valid fix of {list[0].Source}; the file states no position tag";
        return Fact.Create<GeoLocation>(list, new FactResolution(explicitCount > 0 ? "location.explicit" : "location.telemetry", reason, Name));
    }

    private static Fact<MediaDateTime> ResolveCaptureTime(FactEvidence e, DeviceProfile? profile)
    {
        List<(CaptureTimeTier Tier, int Order, FactCandidate<MediaDateTime> Candidate)> items = [];

        // Evidence beats a profile: when a GPS time in the file says the header already is UTC (they agree within ten minutes), a profile that calls the
        // header local does not apply to this file. Observed: a GoPro Karma clip with the same firmware family as local-time HERO5 files whose header equals its GPS UTC.
        bool profileApplies = profile is { HeaderTimeIsLocal: true };
        bool overridden = false;
        if (profileApplies)
        {
            foreach (TelemetryChannel stream in e.Telemetry)
            {
                if (stream.Kind == TelemetryKind.Gps && TryGpsStart(stream, out FactCandidate<MediaDateTime>? gps))
                {
                    foreach (FactCandidate<MediaDateTime> c in e.CaptureTime)
                    {
                        if (IsHeaderSource(c.Source) && c.Certainty == TimeZoneCertainty.Utc && Math.Abs(c.Value.Value.Ticks - gps.Value.Value.Ticks) < 10L * 60 * 10_000_000)
                        {
                            profileApplies = false;
                            overridden = true;
                        }
                    }
                }
            }
        }

        foreach (FactCandidate<MediaDateTime> c in e.CaptureTime)
        {
            FactCandidate<MediaDateTime> use = c;
            if (profileApplies && IsHeaderSource(c.Source) && c.Certainty == TimeZoneCertainty.Utc)
            {
                // The header's UTC reading is a guess made by the container reader; the profile knows better for this device.
                use = c with
                {
                    Value = new MediaDateTime(DateTime.SpecifyKind(c.Value.Value, DateTimeKind.Unspecified), null, TimeZoneCertainty.Local),
                    Certainty = TimeZoneCertainty.Local,
                    Interpretation = c.Interpretation + $"; device profile '{profile!.Id}': this device stores local time here",
                };
            }

            items.Add((Classify(use), items.Count, use));
        }

        foreach (TelemetryChannel stream in e.Telemetry)
        {
            if (stream.Kind == TelemetryKind.Gps && TryGpsStart(stream, out FactCandidate<MediaDateTime>? t))
            {
                items.Add((CaptureTimeTier.TelemetryGps, items.Count, t));
            }
        }

        if (e.FileName is { } name && FileNameDates.TryParse(name, out MediaDateTime fromName, out string pattern))
        {
            items.Add((CaptureTimeTier.FileName, items.Count, new FactCandidate<MediaDateTime>(fromName, "filename", name, $"date in the file name ({pattern}); no time zone")));
        }

        if (e.FileSystemTimeUtc is { } fs)
        {
            items.Add((CaptureTimeTier.FileSystem, items.Count, new FactCandidate<MediaDateTime>(new MediaDateTime(DateTime.SpecifyKind(fs, DateTimeKind.Utc), null, TimeZoneCertainty.Utc), "filesystem",
                fs.ToString("O", CultureInfo.InvariantCulture), "last-write time of the file; says when it was saved, not recorded")));
        }

        if (items.Count == 0)
        {
            return Fact.Create<MediaDateTime>([]);
        }

        items.Sort(static (a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier) : a.Order.CompareTo(b.Order));
        List<FactCandidate<MediaDateTime>> ordered = [];
        foreach ((CaptureTimeTier _, int _, FactCandidate<MediaDateTime> c) in items)
        {
            ordered.Add(c);
        }

        CaptureTimeTier winnerTier = items[0].Tier;
        string reason = $"{ordered[0].Source} on tier {(int)winnerTier} ({winnerTier})";
        if (items.Count > 1)
        {
            reason += $", ahead of {items.Count - 1} other reading(s); next is {ordered[1].Source} on tier {(int)items[1].Tier}";
            if (ordered[0].Value.ToDateTimeOffset() is { } a && ordered[1].Value.ToDateTimeOffset() is { } b && Math.Abs(a.UtcTicks - b.UtcTicks) > 2 * 10_000_000L)
            {
                reason += $" and it differs by {Math.Abs(a.UtcTicks - b.UtcTicks) / 10_000_000L} s";
            }
        }

        if (profile is not null)
        {
            reason += overridden ? $"; device profile '{profile.Id}' not applied: a GPS time in the file shows the header is UTC" : $"; device profile '{profile.Id}' applied";
        }

        return Fact.Create<MediaDateTime>(ordered, new FactResolution($"capture-time.tier-{(int)winnerTier}", reason, Name));
    }

    // The start of the recording is the first fix's UTC time minus where that fix lies on the media timeline.
    private static bool TryGpsStart(TelemetryChannel stream, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FactCandidate<MediaDateTime>? candidate)
    {
        candidate = null;
        foreach (TelemetrySample sample in stream.Samples)
        {
            if (sample is not GpsSample { Fix: not GpsFixKind.None, GpsTime: { } utc } g || utc.Year < 2000)
            {
                continue;
            }

            long offsetTicks = g.Time.IsKnown ? g.Time.ToTicks(10_000_000, MediaRounding.NearestAwayFromZero) : 0;
            DateTime start = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddTicks(-offsetTicks);
            candidate = new FactCandidate<MediaDateTime>(new MediaDateTime(start, null, TimeZoneCertainty.Utc), $"telemetry:{stream.Format}:{stream.Name}",
                utc.ToString("O", CultureInfo.InvariantCulture), g.Time.IsKnown && offsetTicks != 0 ? "UTC time of the first valid fix, moved back by its position on the media timeline" : "UTC time of the first valid fix at the start of the media",
                TimeZoneCertainty.Utc, g.Range);
            return true;
        }

        return false;
    }
}

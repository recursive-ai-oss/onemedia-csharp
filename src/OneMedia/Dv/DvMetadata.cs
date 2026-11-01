using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Dv;

/// <summary>The packs found in the first DIF sequence of a DV stream, as native tags of the <c>dv</c> system.</summary>
public sealed class DvPackList : ITagContainer
{
    internal DvPackList(ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.Dv;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>Reads DV packs into native tags and facts; shared by the raw DV reader and DV in AVI.</summary>
internal static class DvMetadata
{
    /// <summary>Adds one binary tag per distinct pack of the first frame, keyed <c>Source:Type</c> (for example <c>Vaux:62</c>), and the facts they carry.</summary>
    public static void AddPacks(IMediaSource source, long frameOffset, DvFrameInfo info, MediaLimits limits, DefectLog defects, List<ITagContainer> containers, FactsBuilder facts)
    {
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        HashSet<(DvPackSource, byte)> seen = [];
        foreach (DvPack p in info.Packs)
        {
            if (!seen.Add((p.Source, p.Type)))
            {
                continue;
            }

            long at = frameOffset + p.Offset;
            tags.Add(new NativeTag(io, TagSystems.Dv, $"{p.Source}:{p.Type:X2}", 0, TagValueKind.Binary, 0, 0, null, new ByteRange(at, 5), new ByteRange(at + 1, 4)));
        }

        containers.Add(new DvPackList(new ByteRange(frameOffset, DvDif.SequenceSize), tags));
        AddFacts(info, frameOffset, facts);
    }

    public static void AddFacts(DvFrameInfo info, long frameOffset, FactsBuilder facts)
    {
        if (info.RecordedAt is { } when_)
        {
            string raw = when_.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            facts.CaptureTime.Add(FactsBuilder.RankDayTag + 5, new FactCandidate<MediaDateTime>(new MediaDateTime(when_, null, TimeZoneCertainty.Unknown), "dv:Vaux:62+63", raw,
                "recording date and time packs of the first frame; the camera's own clock, no time zone", TimeZoneCertainty.Unknown, new ByteRange(frameOffset + info.RecordedAtOffset, 5)));
        }
    }
}

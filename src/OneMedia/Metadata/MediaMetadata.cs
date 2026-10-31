using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>A chapter marker: where it starts and its title.</summary>
/// <param name="Start">Start on the file's timeline.</param>
/// <param name="Title">The title; may be empty.</param>
/// <param name="Range">The bytes of the chapter list in the file.</param>
public sealed record Chapter(MediaTime Start, string Title, ByteRange Range);

/// <summary>Implemented by demuxers that can describe the metadata of the file they have open.</summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Reads the file's metadata: native tags in every system the format has, the common view over them, and the basic facts. Only structure
    /// ranges are read; cover art and long text are read when asked for.
    /// </summary>
    /// <param name="cancellationToken">Observed between boxes.</param>
    /// <returns>The metadata. It reads from the demuxer's source, so it is valid while the demuxer is open.</returns>
    MediaMetadata ReadMetadata(CancellationToken cancellationToken = default);
}

/// <summary>
/// What a file says about itself apart from its media: the native tag containers (lossless, with byte ranges), the common view over them,
/// the basic facts with their candidate lists, and chapters. Deviations found while reading go to <see cref="Defects"/>, which is the
/// opened container's own log.
/// </summary>
public sealed class MediaMetadata
{
    internal MediaMetadata(IReadOnlyList<ITagContainer> containers, IReadOnlyList<Chapter> chapters, MediaFacts facts, DefectLog defects)
    {
        Containers = containers;
        Chapters = chapters;
        Facts = facts;
        Defects = defects;
        List<NativeTag> all = [];
        foreach (ITagContainer c in containers)
        {
            all.AddRange(c.Tags);
        }

        NativeTags = all;
        Tags = new CommonTags(all);
    }

    /// <summary>The tag containers in file order: each <c>udta</c>, <c>meta</c> and XMP packet, movie-level and track-level.</summary>
    public IReadOnlyList<ITagContainer> Containers { get; }

    /// <summary>Every native tag of every container, in file order.</summary>
    public IReadOnlyList<NativeTag> NativeTags { get; }

    /// <summary>The common view over the movie-level tags.</summary>
    public CommonTags Tags { get; }

    /// <summary>The basic facts with their candidates.</summary>
    public MediaFacts Facts { get; }

    /// <summary>Chapter markers from a Nero <c>chpl</c> list; empty when there are none.</summary>
    public IReadOnlyList<Chapter> Chapters { get; }

    /// <summary>The XMP packets found.</summary>
    public IEnumerable<XmpPacket> XmpPackets
    {
        get
        {
            foreach (ITagContainer c in Containers)
            {
                if (c is XmpPacket x)
                {
                    yield return x;
                }
            }
        }
    }

    /// <summary>Where deviations in the tags were reported.</summary>
    public DefectLog Defects { get; }

    /// <summary>The first movie-level native tag with the given system and key, or <c>null</c>. This is the raw escape hatch.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key as the system spells it (<c>©day</c>, <c>com.apple.quicktime.make</c>, <c>----:com.apple.iTunes:iTunSMPB</c>).</param>
    public NativeTag? this[string system, string key]
    {
        get
        {
            foreach (NativeTag t in NativeTags)
            {
                if (t.TrackId == 0 && t.System == system && t.Key == key)
                {
                    return t;
                }
            }

            return null;
        }
    }

    /// <summary>Every native tag, at movie or track level, with the given system and key.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key.</param>
    /// <returns>The matching tags in file order.</returns>
    public IEnumerable<NativeTag> Find(string system, string key)
    {
        foreach (NativeTag t in NativeTags)
        {
            if (t.System == system && t.Key == key)
            {
                yield return t;
            }
        }
    }
}

using System.Text;
using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Dv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// The entries of one RIFF <c>INFO</c> list (Microsoft's RIFF registered INFO chunks: <c>INAM</c> name, <c>IART</c> artist, <c>ICMT</c> comment,
/// <c>ICRD</c> creation date, <c>ISFT</c> software…), or a single loose chunk such as <c>IDIT</c>. Keys are the four-character codes.
/// </summary>
public sealed class RiffInfoList : ITagContainer
{
    internal RiffInfoList(ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.RiffInfo;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>Reads the metadata of an AVI file: INFO lists, the <c>IDIT</c> date, stream names' facts and, for DV type 1, the DV packs of the first frame.</summary>
internal static class AviMetadataReader
{
    private static readonly uint InfoType = RiffChunk.Make("INFO");
    private static readonly uint Idit = RiffChunk.Make("IDIT");

    public static MediaMetadata Read(IMediaSource source, AviReader reader, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        List<ITagContainer> containers = [];
        TagIo io = new(source, limits, defects);
        try
        {
            if (reader.Root is { } root)
            {
                CollectInfo(io, root, containers, limits, cancellationToken);
                foreach (IStructureNode n in root.Children)
                {
                    if (n is RiffChunk { IsList: true, ListType: "hdrl" } hdrl)
                    {
                        CollectInfo(io, hdrl, containers, limits, cancellationToken);
                    }
                }
            }
        }
        catch (MediaLimitExceededException e)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        if (reader.Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(reader.Duration, "container", reader.Duration.ToString(), "the longest stream: length times scale over rate"));
        }

        foreach (MediaTrack t in reader.Tracks)
        {
            if (t.Kind == TrackKind.Video && t.Video is { Width: > 0, Height: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "strf", $"{v.Width}x{v.Height}", "BITMAPINFOHEADER width and height"));
                break;
            }
        }

        AddDvFacts(source, reader, containers, facts, limits, defects, cancellationToken);
        return new MediaMetadata(containers, [], facts.Build(), defects, reader.Tracks.Count == 1);
    }

    private static void CollectInfo(TagIo io, RiffChunk parent, List<ITagContainer> into, MediaLimits limits, CancellationToken ct)
    {
        foreach (IStructureNode n in parent.Children)
        {
            ct.ThrowIfCancellationRequested();
            if (n is not RiffChunk c)
            {
                continue;
            }

            if (c.IsList && c.Id == RiffChunk.Ids.List && c.ListType == "INFO")
            {
                List<NativeTag> tags = [];
                foreach (IStructureNode child in c.Children)
                {
                    if (child is RiffChunk e && !e.IsList)
                    {
                        tags.Add(MakeTag(io, e, limits));
                    }
                }

                into.Add(new RiffInfoList(c.Range, tags));
            }
            else if (c.Id == Idit)
            {
                into.Add(new RiffInfoList(c.Range, [MakeTag(io, c, limits)]));
            }
        }
    }

    private static NativeTag MakeTag(TagIo io, RiffChunk e, MediaLimits limits)
    {
        ByteRange value = new(e.Offset + 8, e.PayloadSize);
        NativeTag tag = new(io, TagSystems.RiffInfo, e.FourCc, 0, TagValueKind.Text, 0, 0, null, e.Range, value);
        limits.CheckTag(value.Length, value.Offset);
        tag.SetText(DecodeText(io.Read(value)));
        return tag;
    }

    // INFO strings are zero-terminated and have no declared encoding. UTF-8 when it is valid UTF-8, else Windows Latin-1 (the code
    // page of the Western tools that wrote most of these files); both keep ASCII intact.
    private static string DecodeText(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0);
        ReadOnlySpan<byte> text = end >= 0 ? bytes.AsSpan(0, end) : bytes;
        return System.Text.Unicode.Utf8.IsValid(text) ? Encoding.UTF8.GetString(text) : Encoding.Latin1.GetString(text);
    }

    private static void AddDvFacts(IMediaSource source, AviReader reader, List<ITagContainer> containers, FactsBuilder facts, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        int dvStream = -1;
        for (int i = 0; i < reader.Headers.Streams.Count; i++)
        {
            if (reader.Headers.Streams[i].Header.Type == AviStreamHeader.Iavs)
            {
                dvStream = i;
                break;
            }
        }

        if (dvStream < 0 || !reader.TryFindFirstChunk(dvStream, out long chunkOffset))
        {
            return;
        }

        byte[] first = new byte[DvDif.SequenceSize];
        int got = source.Read(chunkOffset + 8, first, ct);
        if (DvDif.TryParseFrame(first.AsSpan(0, got), out DvFrameInfo? info))
        {
            DvMetadata.AddPacks(source, chunkOffset + 8, info, limits, defects, containers, facts);
        }
    }
}

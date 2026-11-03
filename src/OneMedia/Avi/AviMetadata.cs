using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Dv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Riff;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>Reads the metadata of an AVI file: INFO lists, the <c>IDIT</c> date, stream names' facts and, for DV type 1, the DV packs of the first frame.</summary>
internal static class AviMetadataReader
{
    private static readonly uint Idit = RiffChunk.Make("IDIT");

    public static MediaMetadata Read(IMediaSource source, AviReader reader, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        List<ITagContainer> containers = [];
        try
        {
            if (reader.Root is { } root)
            {
                CollectInfo(source, root, containers, limits, defects, cancellationToken);
                foreach (IStructureNode n in root.Children)
                {
                    if (n is RiffChunk { IsList: true, ListType: "hdrl" } hdrl)
                    {
                        CollectInfo(source, hdrl, containers, limits, defects, cancellationToken);
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

        if (reader.Root?.Find(RiffChunk.Ids.List, "ncdt")?.Find(RiffChunk.Make("nctg")) is { } nctg)
        {
            // Nikon cameras keep their maker, model and software in a LIST ncdt / nctg chunk.
            facts.AddVendorIdentity(VendorCameraRecords.ReadNikonAviTags(nctg.ReadData(4096, cancellationToken)), "riff:ncdt/nctg", nctg.Range);
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

    private static void CollectInfo(IMediaSource source, RiffChunk parent, List<ITagContainer> into, MediaLimits limits, DefectLog defects, CancellationToken ct)
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
                into.Add(RiffTagReader.ReadInfo(source, c.Node, limits, defects));
            }
            else if (c.Id == Idit)
            {
                into.Add(RiffTagReader.ReadInfoEntry(source, c.Node, limits, defects));
            }
        }
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

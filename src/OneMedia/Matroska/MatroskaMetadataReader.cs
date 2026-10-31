using System.Globalization;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Matroska;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Gathers a Matroska file's tag containers: the Segment information, <c>Tags</c> elements and <c>Attachments</c>. Where they sit is taken
/// from the Segment's leading elements and its SeekHead, so a file with tags after the Clusters costs a few header reads, never a scan.
/// </summary>
internal static class MatroskaMetadataReader
{
    private const int MaxLeadingElements = 64;

    public static MediaMetadata Read(IMediaSource? source, long segmentDataOffset, IReadOnlyList<MatroskaSeekEntry> seekEntries, ByteRange tags, ByteRange attachments, MatroskaInfo info,
        IReadOnlyList<MatroskaTrackEntry> entries, IReadOnlyList<MediaTrack> tracks, MediaTime duration, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        List<ITagContainer> containers = [];
        MatroskaInfoTags? infoTags = null;
        if (source is not null)
        {
            try
            {
                Dictionary<long, long> numbers = [];
                foreach (MatroskaTrackEntry e in entries)
                {
                    numbers.TryAdd(e.TrackUid, e.TrackNumber);
                }

                ByteRange infoRange = ByteRange.None;
                List<ByteRange> tagRanges = [], attachmentRanges = [];
                if (!tags.IsNone)
                {
                    tagRanges.Add(tags);
                }

                if (!attachments.IsNone)
                {
                    attachmentRanges.Add(attachments);
                }

                WalkHead(source, segmentDataOffset, ref infoRange, tagRanges, attachmentRanges, ct);
                foreach (MatroskaSeekEntry s in seekEntries)
                {
                    List<ByteRange>? list = s.Id == MatroskaIds.Tags ? tagRanges : s.Id == MatroskaIds.Attachments ? attachmentRanges : null;
                    long at = segmentDataOffset + s.Position;
                    if (list is not null && s.Position >= 0 && !list.Exists(r => r.Offset == at) && TryElementRange(source, at, s.Id, out ByteRange r))
                    {
                        list.Add(r);
                    }
                }

                if (!infoRange.IsNone)
                {
                    infoTags = MatroskaTagReader.ReadInfo(source, infoRange, limits, defects, ct);
                    containers.Add(infoTags);
                }

                foreach (ByteRange r in tagRanges)
                {
                    containers.AddRange(MatroskaTagReader.ReadTags(source, r, numbers, limits, defects, ct));
                }

                foreach (ByteRange r in attachmentRanges)
                {
                    containers.Add(MatroskaTagReader.ReadAttachments(source, r, limits, defects, ct));
                }
            }
            catch (MediaLimitExceededException e)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
            }
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        AddFacts(facts, info, infoTags, entries, duration);
        return new MediaMetadata(containers, [], facts.Build(), defects);
    }

    // The Segment's leading elements up to the first Cluster: Info, and Tags or Attachments when they come early.
    private static void WalkHead(IMediaSource source, long position, ref ByteRange info, List<ByteRange> tags, List<ByteRange> attachments, CancellationToken ct)
    {
        long length = source.Length;
        Span<byte> head = stackalloc byte[Ebml.MaxIdLength + Ebml.MaxSizeLength]; // one element header, at most 12 bytes
        for (int i = 0; i < MaxLeadingElements && position < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            int got = source.Read(position, head[..(int)Math.Min(head.Length, length - position)], ct);
            if (got < 2 || !Ebml.TryReadId(head[..got], out uint id, out int idLength) || !Ebml.TryReadSize(head[idLength..got], out long size, out int sizeLength))
            {
                return;
            }

            if (id == MatroskaIds.Cluster || size < 0)
            {
                return;
            }

            long total = idLength + sizeLength + size;
            ByteRange range = new(position, Math.Min(total, length - position));
            if (id == MatroskaIds.Info && info.IsNone)
            {
                info = range;
            }
            else if (id == MatroskaIds.Tags)
            {
                tags.Add(range);
            }
            else if (id == MatroskaIds.Attachments)
            {
                attachments.Add(range);
            }

            position += total;
        }
    }

    private static bool TryElementRange(IMediaSource source, long at, uint expectedId, out ByteRange range)
    {
        range = default;
        Span<byte> head = stackalloc byte[Ebml.MaxIdLength + Ebml.MaxSizeLength]; // one element header, at most 12 bytes
        long length = source.Length;
        if (at < 0 || at >= length)
        {
            return false;
        }

        int got = source.Read(at, head[..(int)Math.Min(head.Length, length - at)]);
        if (got < 2 || !Ebml.TryReadId(head[..got], out uint id, out int idLength) || id != expectedId || !Ebml.TryReadSize(head[idLength..got], out long size, out int sizeLength) || size < 0)
        {
            return false;
        }

        range = new ByteRange(at, Math.Min(idLength + sizeLength + size, length - at));
        return true;
    }

    private static void AddFacts(FactsBuilder facts, MatroskaInfo info, MatroskaInfoTags? infoTags, IReadOnlyList<MatroskaTrackEntry> entries, MediaTime duration)
    {
        if (duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(duration, "container", duration.ToString(), "Info Duration times TimestampScale"));
        }

        if (info.DateUtc is { } date)
        {
            ByteRange r = ByteRange.None;
            foreach (NativeTag t in infoTags?.Tags ?? [])
            {
                if (t.Key == "DateUTC")
                {
                    r = t.Range;
                }
            }

            facts.CaptureTime.Add(FactsBuilder.RankDateUtc, new FactCandidate<MediaDateTime>(new MediaDateTime(date.UtcDateTime, null, TimeZoneCertainty.Utc), "mkv-info:DateUTC",
                date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), "nanoseconds since 2001-01-01 UTC: the time the file was muxed, which for a remux is not the capture time", TimeZoneCertainty.Utc, r));
        }

        if (!string.IsNullOrEmpty(info.WritingApp))
        {
            facts.Software.Add(FactsBuilder.RankDateUtc, new FactCandidate<string>(info.WritingApp, "mkv-info:WritingApp", info.WritingApp, "the application that wrote the file"));
        }

        if (!string.IsNullOrEmpty(info.MuxingApp))
        {
            facts.Software.Add(FactsBuilder.RankDateUtc + 1, new FactCandidate<string>(info.MuxingApp, "mkv-info:MuxingApp", info.MuxingApp, "the library that muxed the file"));
        }

        foreach (MatroskaTrackEntry e in entries)
        {
            if (e.TrackType == 1 && e.Video is { } v && v.PixelWidth > 0)
            {
                long dw = v.DisplayWidth > 0 ? v.DisplayWidth : v.PixelWidth;
                long dh = v.DisplayHeight > 0 ? v.DisplayHeight : v.PixelHeight;
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize((int)v.PixelWidth, (int)v.PixelHeight, (int)dw, (int)dh), "TrackEntry.Video",
                    $"{v.PixelWidth}x{v.PixelHeight} coded, {dw}x{dh} display", "PixelWidth and PixelHeight; DisplayWidth and DisplayHeight default to them (pixel units)"));
                break;
            }
        }
    }
}

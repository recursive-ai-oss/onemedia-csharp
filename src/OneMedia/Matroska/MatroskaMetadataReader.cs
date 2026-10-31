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

    /// <summary>Where the tag containers sit in the Segment.</summary>
    internal sealed class Located
    {
        public ByteRange Info = ByteRange.None;
        public List<ByteRange> Tags = [];
        public List<ByteRange> Attachments = [];
    }

    public static MediaMetadata Read(IMediaSource? source, long segmentDataOffset, IReadOnlyList<MatroskaSeekEntry> seekEntries, ByteRange tags, ByteRange attachments, MatroskaInfo info,
        IReadOnlyList<MatroskaTrackEntry> entries, IReadOnlyList<MediaTrack> tracks, MediaTime duration, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        Located located = new();
        if (source is not null)
        {
            located.Tags = tags.IsNone ? [] : [tags];
            located.Attachments = attachments.IsNone ? [] : [attachments];
            try
            {
                WalkHead(source, segmentDataOffset, ref located.Info, located.Tags, located.Attachments, ct);
                foreach (MatroskaSeekEntry s in seekEntries)
                {
                    List<ByteRange>? list = s.Id == MatroskaIds.Tags ? located.Tags : s.Id == MatroskaIds.Attachments ? located.Attachments : null;
                    long at = segmentDataOffset + s.Position;
                    if (list is not null && s.Position >= 0 && !list.Exists(r => r.Offset == at) && TryElementRange(source, at, s.Id, out ByteRange r))
                    {
                        list.Add(r);
                    }
                }
            }
            catch (MediaLimitExceededException e)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
            }
        }

        return Build(source, located, info, entries, tracks, duration, limits, defects, ct);
    }

    /// <summary>
    /// Asynchronous counterpart for ranged sources: finds the same elements with header-sized reads, fetches the ones that fit
    /// <see cref="MediaLimits.MaxAttachmentBytes"/> into memory and reads the tags from there. An element over the limit is reported as a defect.
    /// </summary>
    public static async ValueTask<MediaMetadata> ReadAsync(IAsyncMediaSource source, long segmentDataOffset, IReadOnlyList<MatroskaSeekEntry> seekEntries, ByteRange tags, ByteRange attachments,
        MatroskaInfo info, IReadOnlyList<MatroskaTrackEntry> entries, IReadOnlyList<MediaTrack> tracks, MediaTime duration, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        Located located = new()
        {
            Tags = tags.IsNone ? [] : [tags],
            Attachments = attachments.IsNone ? [] : [attachments],
        };
        long length = source.Length;
        byte[] head = new byte[Ebml.MaxIdLength + Ebml.MaxSizeLength]; // one element header, at most 12 bytes
        long position = segmentDataOffset;
        for (int i = 0; i < MaxLeadingElements && position < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            (uint id, ByteRange range, bool ok) = await HeaderAtAsync(source, position, head, length, ct).ConfigureAwait(false);
            if (!ok || id == MatroskaIds.Cluster)
            {
                break;
            }

            AddLocated(located, id, range);
            position = range.End;
        }

        foreach (MatroskaSeekEntry s in seekEntries)
        {
            long at = segmentDataOffset + s.Position;
            if ((s.Id == MatroskaIds.Tags || s.Id == MatroskaIds.Attachments) && s.Position >= 0 && at < length)
            {
                (uint id, ByteRange range, bool ok) = await HeaderAtAsync(source, at, head, length, ct).ConfigureAwait(false);
                if (ok && id == s.Id)
                {
                    AddLocated(located, id, range);
                }
            }
        }

        // Everything the tag readers will touch goes into a sparse in-memory source.
        SparseSource sparse = new(length);
        List<ByteRange> wanted = [];
        if (!located.Info.IsNone)
        {
            wanted.Add(located.Info);
        }

        wanted.AddRange(located.Tags);
        wanted.AddRange(located.Attachments);
        foreach (ByteRange r in wanted)
        {
            if (r.Length > limits.MaxAttachmentBytes)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, r.Offset, "A metadata element is larger than the attachment limit and was not fetched.", "tags.limit");
                continue;
            }

            byte[] data = new byte[r.Length];
            int got = 0;
            while (got < data.Length)
            {
                int n = await source.ReadAsync(r.Offset + got, data.AsMemory(got), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                got += n;
            }

            sparse.Add(r.Offset, got == data.Length ? data : data[..got]);
        }

        return Build(sparse, located, info, entries, tracks, duration, limits, defects, ct);
    }

    private static void AddLocated(Located located, uint id, ByteRange range)
    {
        if (id == MatroskaIds.Info && located.Info.IsNone)
        {
            located.Info = range;
        }
        else if (id == MatroskaIds.Tags && !located.Tags.Exists(r => r.Offset == range.Offset))
        {
            located.Tags.Add(range);
        }
        else if (id == MatroskaIds.Attachments && !located.Attachments.Exists(r => r.Offset == range.Offset))
        {
            located.Attachments.Add(range);
        }
    }

    private static async ValueTask<(uint Id, ByteRange Range, bool Ok)> HeaderAtAsync(IAsyncMediaSource source, long at, byte[] head, long length, CancellationToken ct)
    {
        int want = (int)Math.Min(head.Length, length - at);
        int got = want > 0 ? await source.ReadAsync(at, head.AsMemory(0, want), ct).ConfigureAwait(false) : 0;
        if (got < 2 || !Ebml.TryReadId(head.AsSpan(0, got), out uint id, out int idLength) || !Ebml.TryReadSize(head.AsSpan(idLength, got - idLength), out long size, out int sizeLength) || size < 0)
        {
            return (0, default, false);
        }

        return (id, new ByteRange(at, Math.Min(idLength + sizeLength + size, length - at)), true);
    }

    private static MediaMetadata Build(IMediaSource? source, Located located, MatroskaInfo info, IReadOnlyList<MatroskaTrackEntry> entries, IReadOnlyList<MediaTrack> tracks, MediaTime duration,
        MediaLimits limits, DefectLog defects, CancellationToken ct)
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

                // A ranged read fetches only the elements that fit the limits; the others are not in the sparse source.
                bool Available(ByteRange r) => source is not SparseSource sparse || sparse.Has(r.Offset);
                if (!located.Info.IsNone && Available(located.Info))
                {
                    infoTags = MatroskaTagReader.ReadInfo(source, located.Info, limits, defects, ct);
                    containers.Add(infoTags);
                }

                foreach (ByteRange r in located.Tags)
                {
                    if (Available(r))
                    {
                        containers.AddRange(MatroskaTagReader.ReadTags(source, r, numbers, limits, defects, ct));
                    }
                }

                foreach (ByteRange r in located.Attachments)
                {
                    if (Available(r))
                    {
                        containers.Add(MatroskaTagReader.ReadAttachments(source, r, limits, defects, ct));
                    }
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
        return new MediaMetadata(containers, [], facts.Build(), defects, tracks.Count == 1);
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
                if (!tags.Exists(r => r.Offset == range.Offset))
                {
                    tags.Add(range);
                }
            }
            else if (id == MatroskaIds.Attachments)
            {
                if (!attachments.Exists(r => r.Offset == range.Offset))
                {
                    attachments.Add(range);
                }
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

/// <summary>Bytes of a few fetched ranges of a larger source; reading anywhere else returns nothing, as at the end of a short file.</summary>
internal sealed class SparseSource : IMediaSource
{
    private readonly List<(long Offset, byte[] Data)> _parts = [];

    public SparseSource(long length) => Length = length;

    public long Length { get; }

    public void Add(long offset, byte[] data) => _parts.Add((offset, data));

    public bool Has(long offset) => _parts.Exists(p => p.Offset == offset);

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        foreach ((long start, byte[] data) in _parts)
        {
            if (offset >= start && offset < start + data.Length)
            {
                int n = (int)Math.Min(destination.Length, start + data.Length - offset);
                data.AsSpan((int)(offset - start), n).CopyTo(destination);
                return n;
            }
        }

        return 0;
    }

    public void Dispose()
    {
    }
}

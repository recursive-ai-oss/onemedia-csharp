namespace RecursiveAI.OneMedia;

/// <summary>
/// The resource ceilings every layer enforces before it allocates or recurses, so hostile or damaged input cannot exhaust memory,
/// the stack or time. One instance is passed down from the entry point through every layer. The defaults suit ordinary files;
/// raise them deliberately for unusual ones. All values must be positive.
/// </summary>
/// <remarks>
/// A limit is checked before the work it guards. Breaching one throws <see cref="MediaLimitExceededException"/>, except where a
/// reader can skip the offending part and carry on, in which case it records a <see cref="MediaDefect"/> of kind
/// <see cref="DefectKind.LimitReached"/> instead.
/// </remarks>
public sealed record MediaLimits
{
    private long _maxAllocationBytes = 64L << 20;
    private int _maxStructureDepth = 64;
    private long _maxElementCount = 50_000_000;
    private int _maxTracks = 1024;
    private long _maxSampleTableEntries = 100_000_000;
    private long _maxTagBytes = 16L << 20;
    private long _maxAttachmentBytes = 256L << 20;
    private long _maxPacketBytes = 256L << 20;
    private int _maxLacedFrames = 4096;
    private int _maxDefects = 1000;
    private int _maxCues = 1_000_000;
    private int _maxSegments = 1_000_000;
    private long _maxSegmentBytes = 256L << 20;

    /// <summary>The default limits.</summary>
    public static MediaLimits Default { get; } = new();

    /// <summary>
    /// Largest single in-memory buffer a layer may allocate for structure it reads (a box payload, an element body, a table).
    /// Default 64 MiB.
    /// </summary>
    public long MaxAllocationBytes { get => _maxAllocationBytes; init => _maxAllocationBytes = Positive(value); }

    /// <summary>Deepest nesting of boxes, elements or chunks a reader follows. Default 64.</summary>
    public int MaxStructureDepth { get => _maxStructureDepth; init => _maxStructureDepth = Positive(value); }

    /// <summary>Most boxes, elements or chunks a reader visits in one file. Default 50 million.</summary>
    public long MaxElementCount { get => _maxElementCount; init => _maxElementCount = Positive(value); }

    /// <summary>Most tracks (streams) one file may declare. Default 1024.</summary>
    public int MaxTracks { get => _maxTracks; init => _maxTracks = Positive(value); }

    /// <summary>Most entries one sample table (or any per-track index) may hold in memory. Default 100 million.</summary>
    public long MaxSampleTableEntries { get => _maxSampleTableEntries; init => _maxSampleTableEntries = Positive(value); }

    /// <summary>Largest single tag value or tag block read into memory. Default 16 MiB.</summary>
    public long MaxTagBytes { get => _maxTagBytes; init => _maxTagBytes = Positive(value); }

    /// <summary>Largest attachment or embedded picture read into memory. Default 256 MiB.</summary>
    public long MaxAttachmentBytes { get => _maxAttachmentBytes; init => _maxAttachmentBytes = Positive(value); }

    /// <summary>Largest single packet payload a demuxer will buffer. Default 256 MiB.</summary>
    public long MaxPacketBytes { get => _maxPacketBytes; init => _maxPacketBytes = Positive(value); }

    /// <summary>Most frames one laced block may hold (guards lacing bombs). Default 4096.</summary>
    public int MaxLacedFrames { get => _maxLacedFrames; init => _maxLacedFrames = Positive(value); }

    /// <summary>Most defects a <see cref="DefectLog"/> keeps; later ones are counted but dropped. Default 1000.</summary>
    public int MaxDefects { get => _maxDefects; init => _maxDefects = Positive(value); }

    /// <summary>Throws unless a buffer of <paramref name="bytes"/> may be allocated for structure data.</summary>
    /// <param name="bytes">Requested size.</param>
    /// <param name="offset">File offset of the structure that asked, for the error.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="bytes"/> is negative or above <see cref="MaxAllocationBytes"/>.</exception>
    public void CheckAllocation(long bytes, long offset) => Check(nameof(MaxAllocationBytes), MaxAllocationBytes, bytes, offset);

    /// <summary>Throws unless nesting <paramref name="depth"/> is allowed.</summary>
    /// <param name="depth">Depth reached (the top level is 1).</param>
    /// <param name="offset">File offset where it was reached.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="depth"/> is above <see cref="MaxStructureDepth"/>.</exception>
    public void CheckDepth(int depth, long offset) => Check(nameof(MaxStructureDepth), MaxStructureDepth, depth, offset);

    /// <summary>Throws unless <paramref name="count"/> structure elements may be visited.</summary>
    /// <param name="count">Elements visited so far, including the one about to be read.</param>
    /// <param name="offset">File offset of that element.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="count"/> is above <see cref="MaxElementCount"/>.</exception>
    public void CheckElementCount(long count, long offset) => Check(nameof(MaxElementCount), MaxElementCount, count, offset);

    /// <summary>Throws unless a file may declare <paramref name="count"/> tracks.</summary>
    /// <param name="count">Tracks declared so far.</param>
    /// <param name="offset">File offset of the declaration.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="count"/> is above <see cref="MaxTracks"/>.</exception>
    public void CheckTracks(int count, long offset) => Check(nameof(MaxTracks), MaxTracks, count, offset);

    /// <summary>Throws unless a table of <paramref name="entries"/> entries may be held.</summary>
    /// <param name="entries">Entries the table declares.</param>
    /// <param name="offset">File offset of the table.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="entries"/> is negative or above <see cref="MaxSampleTableEntries"/>.</exception>
    public void CheckSampleTable(long entries, long offset) => Check(nameof(MaxSampleTableEntries), MaxSampleTableEntries, entries, offset);

    /// <summary>Throws unless a tag value of <paramref name="bytes"/> bytes may be read.</summary>
    /// <param name="bytes">Declared size.</param>
    /// <param name="offset">File offset of the tag.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="bytes"/> is negative or above <see cref="MaxTagBytes"/>.</exception>
    public void CheckTag(long bytes, long offset) => Check(nameof(MaxTagBytes), MaxTagBytes, bytes, offset);

    /// <summary>Throws unless an attachment of <paramref name="bytes"/> bytes may be read.</summary>
    /// <param name="bytes">Declared size.</param>
    /// <param name="offset">File offset of the attachment.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="bytes"/> is negative or above <see cref="MaxAttachmentBytes"/>.</exception>
    public void CheckAttachment(long bytes, long offset) => Check(nameof(MaxAttachmentBytes), MaxAttachmentBytes, bytes, offset);

    /// <summary>Throws unless a packet of <paramref name="bytes"/> bytes may be buffered.</summary>
    /// <param name="bytes">Declared payload size.</param>
    /// <param name="offset">File offset of the packet.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="bytes"/> is negative or above <see cref="MaxPacketBytes"/>.</exception>
    public void CheckPacket(long bytes, long offset) => Check(nameof(MaxPacketBytes), MaxPacketBytes, bytes, offset);

    /// <summary>Throws unless a laced block of <paramref name="frames"/> frames is allowed.</summary>
    /// <param name="frames">Frames the block declares.</param>
    /// <param name="offset">File offset of the block.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="frames"/> is above <see cref="MaxLacedFrames"/>.</exception>
    public void CheckLacedFrames(int frames, long offset) => Check(nameof(MaxLacedFrames), MaxLacedFrames, frames, offset);

    /// <summary>Most cues one subtitle document may hold. Default 1 million.</summary>
    public int MaxCues { get => _maxCues; init => _maxCues = Positive(value); }

    /// <summary>Throws unless a subtitle document may hold <paramref name="count"/> cues.</summary>
    /// <param name="count">Cues read so far, including the one about to be added.</param>
    /// <param name="offset">Byte offset of that cue.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="count"/> is above <see cref="MaxCues"/>.</exception>
    public void CheckCues(long count, long offset) => Check(nameof(MaxCues), MaxCues, count, offset);

    /// <summary>Most segments one HLS or DASH presentation may list across its renditions. Default 1 million.</summary>
    public int MaxSegments { get => _maxSegments; init => _maxSegments = Positive(value); }

    /// <summary>Throws unless a presentation may list <paramref name="count"/> segments.</summary>
    /// <param name="count">Segments listed so far, including the one about to be added.</param>
    /// <param name="offset">Byte offset in the playlist or manifest where the entry starts.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="count"/> is above <see cref="MaxSegments"/>.</exception>
    public void CheckSegments(long count, long offset) => Check(nameof(MaxSegments), MaxSegments, count, offset);

    /// <summary>
    /// Largest media segment (with its initialization segment) a presentation source holds in memory while it demultiplexes it.
    /// Default 256 MiB. A segment is read whole, then released when the next one starts, so memory does not grow with the presentation.
    /// </summary>
    public long MaxSegmentBytes { get => _maxSegmentBytes; init => _maxSegmentBytes = Positive(value); }

    /// <summary>Throws unless a segment of <paramref name="bytes"/> may be held in memory.</summary>
    /// <param name="bytes">Segment length in bytes.</param>
    /// <param name="offset">Offset of the segment in its resource.</param>
    /// <exception cref="MediaLimitExceededException"><paramref name="bytes"/> is negative or above <see cref="MaxSegmentBytes"/>.</exception>
    public void CheckSegmentBytes(long bytes, long offset) => Check(nameof(MaxSegmentBytes), MaxSegmentBytes, bytes, offset);

    private static void Check(string name, long limit, long requested, long offset)
    {
        if (requested < 0 || requested > limit)
        {
            throw new MediaLimitExceededException(name, limit, requested, offset);
        }
    }

    private static long Positive(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

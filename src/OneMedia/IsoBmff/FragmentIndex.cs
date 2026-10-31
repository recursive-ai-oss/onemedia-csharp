namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>One reference of a segment index box (ISO/IEC 14496-12 §8.16.3).</summary>
/// <param name="ReferencesIndex">True when the reference points at another segment index box rather than at media.</param>
/// <param name="Size">Size in bytes of the referenced subsegment (31 bits).</param>
/// <param name="Duration">Duration of the subsegment in the box's timescale.</param>
/// <param name="StartsWithSap">True when the subsegment starts with a stream access point.</param>
/// <param name="SapType">The access point type, 0 when unspecified.</param>
/// <param name="SapDeltaTime">Time from the subsegment start to its first access point, in the box's timescale.</param>
public readonly record struct SegmentReference(bool ReferencesIndex, uint Size, uint Duration, bool StartsWithSap, int SapType, uint SapDeltaTime);

/// <summary>
/// A segment index box (<c>sidx</c>, ISO/IEC 14496-12 §8.16.3): where the subsegments of the stream that follow it lie and how long each lasts.
/// Used to find the duration of a fragmented file that declares none and to start a seek close to its target.
/// </summary>
public sealed class SegmentIndexBox
{
    private SegmentIndexBox(uint referenceId, uint timescale, ulong earliestPresentationTime, ulong firstOffset, long boxOffset, long boxEnd, SegmentReference[] references)
    {
        ReferenceId = referenceId;
        Timescale = timescale;
        EarliestPresentationTime = earliestPresentationTime;
        FirstOffset = firstOffset;
        BoxOffset = boxOffset;
        BoxEnd = boxEnd;
        References = references;
    }

    /// <summary>The track (or stream) id the index describes.</summary>
    public uint ReferenceId { get; }

    /// <summary>Ticks per second of the durations and the earliest presentation time.</summary>
    public uint Timescale { get; }

    /// <summary>Presentation time of the first subsegment, in <see cref="Timescale"/> ticks.</summary>
    public ulong EarliestPresentationTime { get; }

    /// <summary>Distance in bytes from the end of this box to the first referenced subsegment.</summary>
    public ulong FirstOffset { get; }

    /// <summary>Absolute offset of the box.</summary>
    public long BoxOffset { get; }

    /// <summary>Absolute offset of the first byte after the box, which is the anchor <see cref="FirstOffset"/> is measured from.</summary>
    public long BoxEnd { get; }

    /// <summary>The references in order.</summary>
    public IReadOnlyList<SegmentReference> References { get; }

    /// <summary>Sum of the reference durations.</summary>
    public MediaTime TotalDuration
    {
        get
        {
            if (Timescale == 0)
            {
                return MediaTime.Unknown;
            }

            long total = 0;
            foreach (SegmentReference r in References)
            {
                total += r.Duration;
            }

            return new MediaTime(total, Timescale);
        }
    }

    /// <summary>Parses the payload of a <c>sidx</c> box.</summary>
    /// <param name="payload">The bytes after the box header.</param>
    /// <param name="boxOffset">Absolute offset of the box header.</param>
    /// <param name="boxEnd">Absolute offset just past the box.</param>
    /// <param name="index">The parsed box when the result is true. Fewer references than declared are returned when the payload is short.</param>
    /// <returns>False when the payload is too short for the fixed fields or the version is unknown.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, long boxOffset, long boxEnd, out SegmentIndexBox? index)
    {
        index = null;
        if (payload.Length < 12 || payload[0] > 1)
        {
            return false;
        }

        bool v1 = payload[0] == 1;
        int pos = 12 + (v1 ? 16 : 8) + 2; // fields up to the reserved word
        if (payload.Length < pos + 2)
        {
            return false;
        }

        uint referenceId = Be.U32(payload, 4);
        uint timescale = Be.U32(payload, 8);
        ulong ept = v1 ? Be.U64(payload, 12) : Be.U32(payload, 12);
        ulong first = v1 ? Be.U64(payload, 20) : Be.U32(payload, 16);
        int declared = Be.U16(payload, pos);
        pos += 2;
        int fit = Math.Min(declared, (payload.Length - pos) / 12);
        SegmentReference[] refs = new SegmentReference[fit];
        for (int i = 0; i < fit; i++, pos += 12)
        {
            uint a = Be.U32(payload, pos);
            uint sap = Be.U32(payload, pos + 8);
            refs[i] = new SegmentReference((a & 0x80000000) != 0, a & 0x7FFFFFFF, Be.U32(payload, pos + 4), (sap & 0x80000000) != 0, (int)((sap >> 28) & 7), sap & 0x0FFFFFFF);
        }

        index = new SegmentIndexBox(referenceId, timescale, ept, first, boxOffset, boxEnd, refs);
        return true;
    }

    /// <summary>
    /// The file offset of the subsegment that starts last at or before <paramref name="target"/>, when every reference points at media
    /// (hierarchical indexes are not followed).
    /// </summary>
    internal bool TryFindSubsegment(MediaTime target, out long offset, out int number)
    {
        offset = -1;
        number = -1;
        if (Timescale == 0 || References.Count == 0)
        {
            return false;
        }

        long time = checked((long)Math.Min(EarliestPresentationTime, long.MaxValue / 4));
        long at = BoxEnd + (long)Math.Min(FirstOffset, long.MaxValue / 4);
        for (int i = 0; i < References.Count; i++)
        {
            SegmentReference r = References[i];
            if (r.ReferencesIndex)
            {
                return false;
            }

            if (new MediaTime(time, Timescale) > target)
            {
                break;
            }

            offset = at;
            number = i;
            at += r.Size;
            time += r.Duration;
        }

        return offset >= 0;
    }
}

/// <summary>One random access point of a track fragment random access box (§8.8.10).</summary>
/// <param name="Time">Presentation time of the sync sample in the track's media timescale.</param>
/// <param name="MoofOffset">Absolute offset of the movie fragment box that holds it.</param>
/// <param name="TrafNumber">1-based index of the track fragment inside that movie fragment.</param>
/// <param name="TrunNumber">1-based index of the track run inside that track fragment.</param>
/// <param name="SampleNumber">1-based index of the sample inside that run.</param>
public readonly record struct RandomAccessEntry(long Time, long MoofOffset, uint TrafNumber, uint TrunNumber, uint SampleNumber);

/// <summary>The random access entries of one track (<c>tfra</c>, ISO/IEC 14496-12 §8.8.10).</summary>
/// <param name="TrackId">The track the entries belong to.</param>
/// <param name="Entries">Entries in the order stored (ascending time in well-formed files).</param>
public sealed record TrackFragmentRandomAccessBox(uint TrackId, IReadOnlyList<RandomAccessEntry> Entries);

/// <summary>
/// The movie fragment random access box (<c>mfra</c>, §8.8.9), found at the end of a file through the <c>mfro</c> box that is last in it.
/// It lists the sync samples of each track with the fragment that holds them.
/// </summary>
public sealed class MovieFragmentRandomAccessBox
{
    internal MovieFragmentRandomAccessBox(long offset, IReadOnlyList<TrackFragmentRandomAccessBox> tracks)
    {
        Offset = offset;
        Tracks = tracks;
    }

    /// <summary>Absolute offset of the <c>mfra</c> box.</summary>
    public long Offset { get; }

    /// <summary>One entry list per track.</summary>
    public IReadOnlyList<TrackFragmentRandomAccessBox> Tracks { get; }

    /// <summary>Size of the closing <c>mfro</c> box (a full box with a 32-bit size field).</summary>
    public const int MfroBoxSize = 16;

    /// <summary>Reads the <c>mfro</c> trailer at the end of a file.</summary>
    /// <param name="trailer">The last <see cref="MfroBoxSize"/> bytes of the file.</param>
    /// <param name="fileLength">Length of the file.</param>
    /// <param name="mfraOffset">Where the <c>mfra</c> box starts when the result is true.</param>
    /// <param name="mfraSize">The size the trailer declares for the <c>mfra</c> box.</param>
    /// <returns>False when the trailer is not a well-formed <c>mfro</c> or points outside the file.</returns>
    public static bool TryReadTrailer(ReadOnlySpan<byte> trailer, long fileLength, out long mfraOffset, out long mfraSize)
    {
        mfraOffset = mfraSize = 0;
        if (trailer.Length < MfroBoxSize || Be.U32(trailer, 0) != MfroBoxSize || Be.U32(trailer, 4) != BoxTypes.Mfro)
        {
            return false;
        }

        mfraSize = Be.U32(trailer, 12);
        mfraOffset = fileLength - mfraSize;
        return mfraSize >= 8 + MfroBoxSize && mfraOffset >= 0;
    }

    /// <summary>Parses a whole <c>mfra</c> box.</summary>
    /// <param name="box">The box bytes including its header.</param>
    /// <param name="offset">Absolute offset of the box.</param>
    /// <param name="limits">Entry-count limit source.</param>
    /// <param name="result">The parsed box when the result is true.</param>
    /// <returns>False when the bytes are not an <c>mfra</c> box. Track entries cut short by the end of the box are dropped.</returns>
    /// <exception cref="MediaLimitExceededException">A track declares more entries than the sample table limit.</exception>
    public static bool TryParse(ReadOnlySpan<byte> box, long offset, MediaLimits limits, out MovieFragmentRandomAccessBox? result)
    {
        result = null;
        if (box.Length < 8 || Be.U32(box, 4) != BoxTypes.Mfra)
        {
            return false;
        }

        List<TrackFragmentRandomAccessBox> tracks = [];
        int pos = 8;
        while (pos + 8 <= box.Length)
        {
            uint size = Be.U32(box, pos);
            uint type = Be.U32(box, pos + 4);
            if (size < 8 || pos + size > box.Length)
            {
                break;
            }

            if (type == BoxTypes.Tfra && TryParseTfra(box.Slice(pos + 8, (int)size - 8), offset + pos, limits, out TrackFragmentRandomAccessBox? t))
            {
                tracks.Add(t!);
            }

            pos += (int)size;
        }

        result = new MovieFragmentRandomAccessBox(offset, tracks);
        return true;
    }

    private static bool TryParseTfra(ReadOnlySpan<byte> p, long offset, MediaLimits limits, out TrackFragmentRandomAccessBox? t)
    {
        t = null;
        if (p.Length < 16 || p[0] > 1)
        {
            return false;
        }

        bool v1 = p[0] == 1;
        uint trackId = Be.U32(p, 4);
        uint lengths = Be.U32(p, 8);
        int trafLen = (int)((lengths >> 4) & 3) + 1, trunLen = (int)((lengths >> 2) & 3) + 1, sampleLen = (int)(lengths & 3) + 1;
        uint declared = Be.U32(p, 12);
        limits.CheckSampleTable(declared, offset);
        int entrySize = (v1 ? 16 : 8) + trafLen + trunLen + sampleLen;
        int count = (int)Math.Min(declared, (uint)((p.Length - 16) / entrySize));
        RandomAccessEntry[] entries = new RandomAccessEntry[count];
        int pos = 16;
        for (int i = 0; i < count; i++)
        {
            long time = v1 ? unchecked((long)Be.U64(p, pos)) : Be.U32(p, pos);
            long moof = v1 ? unchecked((long)Be.U64(p, pos + 8)) : Be.U32(p, pos + 4);
            int q = pos + (v1 ? 16 : 8);
            entries[i] = new RandomAccessEntry(time, moof, Variable(p, q, trafLen), Variable(p, q + trafLen, trunLen), Variable(p, q + trafLen + trunLen, sampleLen));
            pos += entrySize;
        }

        t = new TrackFragmentRandomAccessBox(trackId, entries);
        return true;
    }

    private static uint Variable(ReadOnlySpan<byte> p, int at, int length)
    {
        uint v = 0;
        for (int i = 0; i < length; i++)
        {
            v = (v << 8) | p[at + i];
        }

        return v;
    }
}

/// <summary>The indexes of a fragmented file that let a seek start near its target instead of at the first fragment.</summary>
internal sealed class FragmentIndex
{
    public IReadOnlyList<SegmentIndexBox> SegmentIndexes { get; init; } = [];

    public MovieFragmentRandomAccessBox? RandomAccess { get; init; }

    public bool IsEmpty => SegmentIndexes.Count == 0 && RandomAccess is null;

    /// <summary>
    /// Offset to start scanning fragments from for a seek to <paramref name="target"/> on a track, or -1 for no hint. The random access box is
    /// preferred (it names sync samples); the entry before the best one is used as a safety margin for reordered video.
    /// </summary>
    public long FindStart(uint trackId, MediaTime target, long trackTimescale)
    {
        if (RandomAccess is { } mfra)
        {
            foreach (TrackFragmentRandomAccessBox t in mfra.Tracks)
            {
                if (t.TrackId != trackId || t.Entries.Count == 0)
                {
                    continue;
                }

                int best = 0;
                for (int i = 0; i < t.Entries.Count; i++)
                {
                    if (new MediaTime(t.Entries[i].Time, trackTimescale) > target)
                    {
                        break;
                    }

                    best = i;
                }

                return t.Entries[Math.Max(0, best - 1)].MoofOffset;
            }
        }

        foreach (SegmentIndexBox s in SegmentIndexes)
        {
            if (s.ReferenceId == trackId && s.TryFindSubsegment(target, out long offset, out int number))
            {
                // One subsegment earlier as the margin; the scan skips ahead to the real keyframe.
                if (number == 0)
                {
                    return offset;
                }

                return offset - s.References[number - 1].Size;
            }
        }

        return -1;
    }

    /// <summary>Reads the <c>mfra</c> box at the end of a file: one read of the trailer and one of the box.</summary>
    public static MovieFragmentRandomAccessBox? ReadRandomAccess(IMediaSource source, MediaLimits limits, CancellationToken cancellationToken)
    {
        long length = source.Length;
        if (length < MovieFragmentRandomAccessBox.MfroBoxSize + 8)
        {
            return null;
        }

        Span<byte> trailer = stackalloc byte[MovieFragmentRandomAccessBox.MfroBoxSize]; // fixed 16 bytes
        if (source.Read(length - trailer.Length, trailer, cancellationToken) < trailer.Length
            || !MovieFragmentRandomAccessBox.TryReadTrailer(trailer, length, out long offset, out long size)
            || size > limits.MaxAllocationBytes)
        {
            return null;
        }

        byte[] bytes = new byte[size];
        return source.Read(offset, bytes, cancellationToken) == size && MovieFragmentRandomAccessBox.TryParse(bytes, offset, limits, out MovieFragmentRandomAccessBox? r) ? r : null;
    }

    /// <summary>Asynchronous twin of <see cref="ReadRandomAccess"/>.</summary>
    public static async ValueTask<MovieFragmentRandomAccessBox?> ReadRandomAccessAsync(IAsyncMediaSource source, MediaLimits limits, CancellationToken cancellationToken)
    {
        long length = source.Length;
        if (length < MovieFragmentRandomAccessBox.MfroBoxSize + 8)
        {
            return null;
        }

        byte[] trailer = new byte[MovieFragmentRandomAccessBox.MfroBoxSize];
        if (await source.ReadAsync(length - trailer.Length, trailer, cancellationToken).ConfigureAwait(false) < trailer.Length
            || !MovieFragmentRandomAccessBox.TryReadTrailer(trailer, length, out long offset, out long size)
            || size > limits.MaxAllocationBytes)
        {
            return null;
        }

        byte[] bytes = new byte[size];
        return await source.ReadAsync(offset, bytes, cancellationToken).ConfigureAwait(false) == size && MovieFragmentRandomAccessBox.TryParse(bytes, offset, limits, out MovieFragmentRandomAccessBox? r) ? r : null;
    }
}

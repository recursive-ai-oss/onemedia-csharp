namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>A read-only view of bytes that sit at <c>baseOffset</c> in a larger file; reads outside them return nothing.</summary>
internal sealed class RangeMemorySource(byte[] data, int count, long baseOffset, long fileLength) : IMediaSource
{
    public long Length => fileLength;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        long rel = offset - baseOffset;
        if (rel < 0 || rel >= count)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, count - rel);
        data.AsSpan((int)rel, n).CopyTo(destination);
        return n;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Serves the many tiny reads of box parsing from one read-ahead window, so a ranged or remote source sees a few requests instead of one
/// per box header. Used only while the structure is parsed; the source must not change underneath it.
/// </summary>
internal sealed class ReadAheadSource(IMediaSource inner) : IMediaSource
{
    private const int WindowBytes = 32 * 1024;
    private readonly byte[] _window = new byte[WindowBytes];
    private long _start = -1;
    private int _count;

    public long Length => inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length >= WindowBytes)
        {
            return inner.Read(offset, destination, cancellationToken);
        }

        if (_start < 0 || offset < _start || offset + destination.Length > _start + _count)
        {
            // Refill from the requested offset; a short window at the end of the data is fine.
            _count = inner.Read(offset, _window, cancellationToken);
            _start = offset;
        }

        long rel = offset - _start;
        int n = (int)Math.Max(0, Math.Min(destination.Length, _count - rel));
        _window.AsSpan((int)rel, n).CopyTo(destination);
        return n;
    }

    public void Dispose()
    {
    }
}

/// <summary>What opening a file's structure yields: the movie, where the sample tables can be read, and where fragments continue.</summary>
internal sealed class OpenedMovie
{
    public required FileTypeBox? FileType { get; init; }

    public required IsoMovie Movie { get; init; }

    /// <summary>The source the sample tables are read through: the file itself, or the movie box held in memory for ranged and forward-only sources.</summary>
    public required IMediaSource TableSource { get; init; }

    /// <summary>Offset just past the movie box, where the scan for movie fragments starts.</summary>
    public required long MoovEnd { get; init; }

    /// <summary>Total duration from segment index boxes ahead of the first fragment, or unknown.</summary>
    public MediaTime SidxDuration { get; init; } = MediaTime.Unknown;

    public required long Length { get; init; }

    public bool MediaDataPrecededMovie { get; init; }
}

/// <summary>Finds <c>ftyp</c> and <c>moov</c> with a handful of header reads and parses the movie box.</summary>
internal static class IsoBmffOpener
{
    private const int MaxLeadingBoxes = 4096;

    public static OpenedMovie Open(IMediaSource rawSource, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        ReadAheadSource source = new(rawSource);
        long length = source.Length;
        BoxTree tree = new(source, limits, defects);
        FileTypeBox? ftyp = null;
        BoxNode? moov = null;
        long pos = 0;
        int seen = 0;
        while (pos < length && seen++ < MaxLeadingBoxes && BoxReader.TryRead(source, pos, length, true, defects, out BoxHeader h, cancellationToken))
        {
            if (h.Type is BoxTypes.Ftyp && ftyp is null)
            {
                ftyp = FileTypeBox.Parse(tree.CreateNode(h).ReadPayload(4096));
            }
            else if (h.Type == BoxTypes.Moov)
            {
                moov = tree.CreateNode(h);
                break;
            }

            pos = h.End;
        }

        if (moov is null)
        {
            throw new MediaFormatException(new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, Math.Min(pos, length), "The file has no movie box.", "isobmff.no-moov"));
        }

        IsoMovie movie = MovieReader.Read(moov, source, limits, defects, ftyp?.IsQuickTime ?? false);
        MediaTime sidx = MediaTime.Unknown;
        if (movie.IsFragmented && movie.Header.Duration == 0 && movie.FragmentDuration <= 0)
        {
            sidx = ScanSegmentIndex(source, moov.Header.End, length, limits, defects, cancellationToken);
        }

        return new OpenedMovie { FileType = ftyp, Movie = movie, TableSource = rawSource, MoovEnd = moov.Header.End, SidxDuration = sidx, Length = length };
    }

    public static async ValueTask<OpenedMovie> OpenAsync(IAsyncMediaSource source, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        long length = source.Length;
        byte[] head = new byte[BoxReader.MaxHeaderBytes];
        FileTypeBox? ftyp = null;
        BoxHeader moovHeader = default;
        bool found = false;
        long pos = 0;
        int seen = 0;
        while (pos < length && seen++ < MaxLeadingBoxes)
        {
            int n = await source.ReadAsync(pos, head.AsMemory(0, (int)Math.Min(head.Length, length - pos)), cancellationToken).ConfigureAwait(false);
            if (!BoxReader.TryParse(head.AsSpan(0, n), pos, length, true, defects, out BoxHeader h))
            {
                break;
            }

            if (h.Type == BoxTypes.Ftyp && ftyp is null)
            {
                int take = (int)Math.Min(h.PayloadSize, 4096);
                byte[] p = new byte[take];
                int got = await source.ReadAsync(h.PayloadOffset, p, cancellationToken).ConfigureAwait(false);
                ftyp = FileTypeBox.Parse(p.AsSpan(0, got));
            }
            else if (h.Type == BoxTypes.Moov)
            {
                moovHeader = h;
                found = true;
                break;
            }

            pos = h.End;
        }

        if (!found)
        {
            throw new MediaFormatException(new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, Math.Min(pos, length), "The file has no movie box.", "isobmff.no-moov"));
        }

        int total = FragmentSizing.Check(moovHeader, limits);
        byte[] bytes = new byte[total];
        int read = await source.ReadAsync(moovHeader.Offset, bytes, cancellationToken).ConfigureAwait(false);
        if (read < total)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Error, moovHeader.Offset + read, "The source ends inside the movie box.");
        }

        RangeMemorySource memory = new(bytes, read, moovHeader.Offset, length);
        BoxTree tree = new(memory, limits, defects);
        IsoMovie movie = MovieReader.Read(tree.CreateNode(moovHeader), memory, limits, defects, ftyp?.IsQuickTime ?? false);
        MediaTime sidx = MediaTime.Unknown;
        if (movie.IsFragmented && movie.Header.Duration == 0 && movie.FragmentDuration <= 0)
        {
            sidx = await ScanSegmentIndexAsync(source, moovHeader.End, length, limits, defects, cancellationToken).ConfigureAwait(false);
        }

        return new OpenedMovie { FileType = ftyp, Movie = movie, TableSource = memory, MoovEnd = moovHeader.End, SidxDuration = sidx, Length = length };
    }

    /// <summary>Reads a forward-only source up to and including the movie box.</summary>
    public static OpenedMovie OpenForward(IForwardMediaSource source, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        FileTypeBox? ftyp = null;
        bool mdatFirst = false;
        int seen = 0;
        while (seen++ < MaxLeadingBoxes && ForwardBoxes.TryReadHeader(source, defects, cancellationToken, out BoxHeader h))
        {
            if (h.Type == BoxTypes.Moov)
            {
                int total = FragmentSizing.Check(h, limits);
                byte[] bytes = new byte[total];
                int headerLen = HeaderBytes.Write(h, bytes);
                int rest = total - headerLen;
                if (!source.TryReadExactly(bytes.AsSpan(headerLen, rest), cancellationToken))
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The source ends inside the movie box.");
                    throw new MediaFormatException(new MediaDefect(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The source ends inside the movie box.", "isobmff.truncated"));
                }

                RangeMemorySource memory = new(bytes, total, h.Offset, h.End);
                BoxTree tree = new(memory, limits, defects);
                IsoMovie movie = MovieReader.Read(tree.CreateNode(h), memory, limits, defects, ftyp?.IsQuickTime ?? false);
                return new OpenedMovie { FileType = ftyp, Movie = movie, TableSource = memory, MoovEnd = h.End, Length = long.MaxValue, MediaDataPrecededMovie = mdatFirst };
            }

            if (h.Type == BoxTypes.Ftyp && ftyp is null)
            {
                int take = (int)Math.Min(h.PayloadSize, 4096);
                byte[] p = new byte[take];
                if (source.TryReadExactly(p, cancellationToken))
                {
                    ftyp = FileTypeBox.Parse(p);
                }

                if (!ForwardBoxes.SkipPayload(source, h.PayloadSize - take, cancellationToken))
                {
                    break;
                }

                continue;
            }

            mdatFirst |= h.Type == BoxTypes.Mdat;
            if (h.IsSizeInferred || !ForwardBoxes.SkipPayload(source, h.PayloadSize, cancellationToken))
            {
                break;
            }
        }

        throw new MediaFormatException(new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, source.Position, "The data has no movie box.", "isobmff.no-moov"));
    }

    private static MediaTime ScanSegmentIndex(ReadAheadSource source, long position, long length, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        MediaTime best = MediaTime.Unknown;
        for (int i = 0; i < 64 && position < length && BoxReader.TryRead(source, position, length, true, defects, out BoxHeader h, cancellationToken); i++)
        {
            if (h.Type is BoxTypes.Moof or BoxTypes.Mdat)
            {
                break;
            }

            if (h.Type == BoxTypes.Sidx && h.PayloadSize <= limits.MaxTagBytes)
            {
                byte[] p = new byte[h.PayloadSize];
                int n = source.Read(h.PayloadOffset, p, cancellationToken);
                best = Longer(best, SegmentIndex.TotalDuration(p.AsSpan(0, n)));
            }

            position = h.End;
        }

        return best;
    }

    private static async ValueTask<MediaTime> ScanSegmentIndexAsync(IAsyncMediaSource source, long position, long length, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        MediaTime best = MediaTime.Unknown;
        byte[] head = new byte[BoxReader.MaxHeaderBytes];
        for (int i = 0; i < 64 && position < length; i++)
        {
            int n = await source.ReadAsync(position, head.AsMemory(0, (int)Math.Min(head.Length, length - position)), cancellationToken).ConfigureAwait(false);
            if (!BoxReader.TryParse(head.AsSpan(0, n), position, length, true, defects, out BoxHeader h) || h.Type is BoxTypes.Moof or BoxTypes.Mdat)
            {
                break;
            }

            if (h.Type == BoxTypes.Sidx && h.PayloadSize <= limits.MaxTagBytes)
            {
                byte[] p = new byte[h.PayloadSize];
                int got = await source.ReadAsync(h.PayloadOffset, p, cancellationToken).ConfigureAwait(false);
                best = Longer(best, SegmentIndex.TotalDuration(p.AsSpan(0, got)));
            }

            position = h.End;
        }

        return best;
    }

    private static MediaTime Longer(MediaTime a, MediaTime b) => !a.IsKnown ? b : !b.IsKnown ? a : MediaTime.Max(a, b);
}

/// <summary>The segment index box (ISO/IEC 14496-12 §8.16.3), read only for the total duration it declares.</summary>
internal static class SegmentIndex
{
    public static MediaTime TotalDuration(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            return MediaTime.Unknown;
        }

        bool v1 = payload[0] == 1;
        uint timescale = Be.U32(payload, 8);
        int pos = 12 + (v1 ? 16 : 8);
        pos += 2; // reserved
        uint count = Be.U16(payload, pos);
        pos += 2;
        if (timescale == 0)
        {
            return MediaTime.Unknown;
        }

        long total = 0;
        for (uint i = 0; i < count && pos + 12 <= payload.Length; i++, pos += 12)
        {
            total += Be.U32(payload, pos + 4);
        }

        return new MediaTime(total, timescale);
    }
}

/// <summary>Reading box headers from a source that cannot seek.</summary>
internal static class ForwardBoxes
{
    public static bool TryReadHeader(IForwardMediaSource source, DefectLog defects, CancellationToken cancellationToken, out BoxHeader header)
    {
        Span<byte> buf = stackalloc byte[BoxReader.MaxHeaderBytes]; // one header, at most 32 bytes
        long start = source.Position;
        header = default;
        if (!source.TryReadExactly(buf[..8], cancellationToken))
        {
            return false;
        }

        int have = 8;
        if (Be.U32(buf, 0) == 1)
        {
            if (!source.TryReadExactly(buf.Slice(have, 8), cancellationToken))
            {
                return false;
            }

            have += 8;
        }

        if (Be.U32(buf, 4) == BoxTypes.Uuid)
        {
            if (!source.TryReadExactly(buf.Slice(have, 16), cancellationToken))
            {
                return false;
            }

            have += 16;
        }

        // The end of the data is unknown, so no overrun can be detected here; a short read shows up when the payload is read.
        return BoxReader.TryParse(buf[..have], start, long.MaxValue, true, defects, out header);
    }

    public static bool SkipPayload(IForwardMediaSource source, long count, CancellationToken cancellationToken) =>
        count <= 0 || source.Skip(count, cancellationToken) == count;
}

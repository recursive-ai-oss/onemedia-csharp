using System.Buffers;

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
/// The bytes of a small movie box, read once, serving every later read that falls inside them (box parsing and the sample-table windows)
/// without a call to the source; a read that is not wholly inside goes to the source as before. The bytes sit in a buffer rented from
/// the shared pool and are given back by <see cref="Release"/>, after which every read goes to the source.
/// </summary>
internal sealed class MovieBoxCache : IMediaSource
{
    /// <summary>Movie boxes up to this size are held whole. Larger ones are read through windows, so the cache never grows with the file.</summary>
    public const int MaxBytes = 128 * 1024;

    private readonly IMediaSource _inner;
    private readonly long _start;
    private byte[]? _bytes;
    private int _count;

    private MovieBoxCache(IMediaSource inner, long start, byte[] bytes, int count)
    {
        _inner = inner;
        _start = start;
        _bytes = bytes;
        _count = count;
    }

    /// <summary>Reads the movie box in one request, or returns null when it is too large, cut off, or the source gives fewer bytes than it claims.</summary>
    public static MovieBoxCache? TryLoad(IMediaSource inner, in BoxHeader movie, long fileLength, CancellationToken cancellationToken)
    {
        long size = movie.End - movie.Offset;
        if (size <= 0 || size > MaxBytes || movie.End > fileLength)
        {
            return null;
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent((int)size);
        int got = inner.Read(movie.Offset, bytes.AsSpan(0, (int)size), cancellationToken);
        if (got != size)
        {
            ArrayPool<byte>.Shared.Return(bytes);
            return null;
        }

        return new MovieBoxCache(inner, movie.Offset, bytes, (int)size);
    }

    public long Length => _inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        long relative = offset - _start;
        if (_bytes is { } bytes && relative >= 0 && relative + destination.Length <= _count)
        {
            bytes.AsSpan((int)relative, destination.Length).CopyTo(destination);
            return destination.Length;
        }

        return _inner.Read(offset, destination, cancellationToken);
    }

    /// <summary>Returns the buffer to the pool. The source underneath is not disposed: its owner does that.</summary>
    public void Release()
    {
        if (_bytes is { } bytes)
        {
            _bytes = null;
            _count = 0;
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void Dispose() => Release();
}

/// <summary>
/// Serves the many tiny reads of box parsing from one read-ahead window, so a ranged or remote source sees a few requests instead of one
/// per box header. Used only while the structure is parsed; the source must not change underneath it.
/// </summary>
internal sealed class ReadAheadSource : IMediaSource
{
    private readonly IMediaSource _inner;
    private const int WindowBytes = 32 * 1024;

    // A read far from the previous window is a jump over sample tables or media data, so it fetches a small window rather than 32 KiB
    // it will not use; reads that continue near the last window keep the full size.
    private const int JumpWindowBytes = 4 * 1024;
    private byte[]? _window; // rented on the first refill and returned by Dispose; reads after that go straight to the source
    private long _start = -1;
    private int _count;
    private bool _disposed;

    public ReadAheadSource(IMediaSource inner) => _inner = inner;

    /// <summary>Starts with the first bytes of the source already in the window.</summary>
    public ReadAheadSource(IMediaSource inner, ReadOnlySpan<byte> head)
    {
        _inner = inner;
        if (!head.IsEmpty)
        {
            head = head[..Math.Min(head.Length, WindowBytes)];
            _window = ArrayPool<byte>.Shared.Rent(WindowBytes);
            head.CopyTo(_window);
            _start = 0;
            _count = head.Length;
        }
    }

    public long Length => _inner.Length;

    public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length >= WindowBytes || _disposed)
        {
            return _inner.Read(offset, destination, cancellationToken);
        }

        byte[] window = _window ??= ArrayPool<byte>.Shared.Rent(WindowBytes);
        if (_start < 0 || offset < _start || offset + destination.Length > _start + _count)
        {
            // Refill from the requested offset; a short window at the end of the data is fine.
            bool near = _start < 0 || (offset >= _start && offset - (_start + _count) <= WindowBytes);
            _count = _inner.Read(offset, window.AsSpan(0, near ? WindowBytes : Math.Max(JumpWindowBytes, destination.Length)), cancellationToken);
            _start = offset;
        }

        long rel = offset - _start;
        int n = (int)Math.Max(0, Math.Min(destination.Length, _count - rel));
        window.AsSpan((int)rel, n).CopyTo(destination);
        return n;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_window is { } window)
        {
            _window = null;
            ArrayPool<byte>.Shared.Return(window);
        }
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

    /// <summary>Segment index boxes found between the movie box and the first fragment.</summary>
    public IReadOnlyList<SegmentIndexBox> SegmentIndexes { get; init; } = [];

    public required long Length { get; init; }

    public bool MediaDataPrecededMovie { get; init; }

    /// <summary>Gives back the pooled buffer behind <see cref="TableSource"/>, if there is one. Reads after that go to the file.</summary>
    public void Release() => (TableSource as MovieBoxCache)?.Release();
}

/// <summary>Finds <c>ftyp</c> and <c>moov</c> with a handful of header reads and parses the movie box.</summary>
internal static class IsoBmffOpener
{
    private const int MaxLeadingBoxes = 4096;

    public static OpenedMovie Open(IMediaSource rawSource, ReadOnlySpan<byte> head, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        using ReadAheadSource source = new(rawSource, head);
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

        // A small movie box is read once and parsed from memory; the sample-table windows then read from it too.
        MovieBoxCache? cache = MovieBoxCache.TryLoad(rawSource, moov.Header, length, cancellationToken);
        IMediaSource tables = cache ?? rawSource;
        IsoMovie movie;
        try
        {
            movie = cache is null ? MovieReader.Read(moov, source, limits, defects, ftyp?.IsQuickTime ?? false)
                : MovieReader.Read(new BoxTree(cache, limits, defects).CreateNode(moov.Header), cache, limits, defects, ftyp?.IsQuickTime ?? false);
        }
        catch
        {
            cache?.Release();
            throw;
        }

        List<SegmentIndexBox> sidx = movie.IsFragmented ? ScanSegmentIndex(source, moov.Header.End, length, limits, defects, cancellationToken) : [];
        return new OpenedMovie { FileType = ftyp, Movie = movie, TableSource = tables, MoovEnd = moov.Header.End, SidxDuration = TotalDuration(sidx), SegmentIndexes = sidx, Length = length };
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
        List<SegmentIndexBox> sidx = movie.IsFragmented ? await ScanSegmentIndexAsync(source, moovHeader.End, length, limits, defects, cancellationToken).ConfigureAwait(false) : [];
        return new OpenedMovie { FileType = ftyp, Movie = movie, TableSource = memory, MoovEnd = moovHeader.End, SidxDuration = TotalDuration(sidx), SegmentIndexes = sidx, Length = length };
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

    private static MediaTime TotalDuration(List<SegmentIndexBox> indexes)
    {
        MediaTime best = MediaTime.Unknown;
        foreach (SegmentIndexBox b in indexes)
        {
            MediaTime d = b.TotalDuration;
            best = !best.IsKnown ? d : !d.IsKnown ? best : MediaTime.Max(best, d);
        }

        return best;
    }

    private static List<SegmentIndexBox> ScanSegmentIndex(ReadAheadSource source, long position, long length, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        List<SegmentIndexBox> found = [];
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
                if (SegmentIndexBox.TryParse(p.AsSpan(0, n), h.Offset, h.End, out SegmentIndexBox? box))
                {
                    found.Add(box!);
                }
            }

            position = h.End;
        }

        return found;
    }

    private static async ValueTask<List<SegmentIndexBox>> ScanSegmentIndexAsync(IAsyncMediaSource source, long position, long length, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        List<SegmentIndexBox> found = [];
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
                if (SegmentIndexBox.TryParse(p.AsSpan(0, got), h.Offset, h.End, out SegmentIndexBox? box))
                {
                    found.Add(box!);
                }
            }

            position = h.End;
        }

        return found;
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

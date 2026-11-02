using System.Buffers;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// Sequential-friendly reader over a source for the repair scanners: a window that answers small peeks without a read per call, so a
/// scan that looks only at element or chunk headers and skips payloads costs about one read per window of file.
/// </summary>
internal sealed class RepairInput : IDisposable
{
    private const int WindowSize = 256 * 1024;
    private readonly IMediaSource _source;
    private readonly byte[] _window = ArrayPool<byte>.Shared.Rent(WindowSize);
    private long _windowStart = -1;
    private int _windowLength;
    private readonly CancellationToken _cancellationToken;

    public RepairInput(IMediaSource source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellationToken = cancellationToken;
        Length = source.Length;
    }

    /// <summary>The file size in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// Returns up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer only at the end of the file. The span is valid until
    /// the next call. <paramref name="count"/> must not exceed 64 KiB.
    /// </summary>
    public ReadOnlySpan<byte> Peek(long offset, int count)
    {
        if (offset < 0 || offset >= Length || count <= 0)
        {
            return default;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int want = (int)Math.Min(count, Length - offset);
        if (_windowStart < 0 || offset < _windowStart || offset + want > _windowStart + _windowLength)
        {
            _windowStart = offset;
            _windowLength = _source.Read(offset, _window.AsSpan(0, WindowSize), _cancellationToken);
            if (_windowLength < want)
            {
                want = _windowLength;
            }
        }

        return _window.AsSpan((int)(offset - _windowStart), want);
    }

    /// <summary>Reads exactly the bytes asked for into the buffer, or fewer at the end of the file.</summary>
    public int Read(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int n = _source.Read(offset + total, destination[total..], _cancellationToken);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_window);
}

/// <summary>An output stream that counts what it has written and copies input ranges through a pooled buffer.</summary>
internal sealed class RepairOutput : IDisposable
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly Stream _stream;
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
    private readonly CancellationToken _cancellationToken;

    public RepairOutput(Stream stream, CancellationToken cancellationToken)
    {
        _stream = stream;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Bytes written so far.</summary>
    public long Position { get; private set; }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        Position += bytes.Length;
    }

    /// <summary>Copies input bytes unchanged.</summary>
    public void Copy(RepairInput input, long offset, long length)
    {
        while (length > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int n = input.Read(offset, _buffer.AsSpan(0, (int)Math.Min(CopyBufferSize, length)));
            if (n <= 0)
            {
                throw new EndOfStreamException("The input ended while copying.");
            }

            _stream.Write(_buffer, 0, n);
            Position += n;
            offset += n;
            length -= n;
        }
    }

    /// <summary>Overwrites bytes already written (sizes and counts that were not known when the header went out) and returns to the end.</summary>
    public void Patch(long at, ReadOnlySpan<byte> bytes)
    {
        _stream.Flush();
        _stream.Seek(at, SeekOrigin.Begin);
        _stream.Write(bytes);
        _stream.Seek(Position, SeekOrigin.Begin);
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _stream.Dispose();
    }
}

/// <summary>Collects the items, tracks and warnings of a repair and makes the report.</summary>
internal sealed class RepairReportBuilder(string format, long inputLength)
{
    private readonly List<RepairItem> _items = [];
    private readonly List<string> _warnings = [];

    public List<RepairTrackReport> Tracks { get; } = [];

    /// <summary>True once anything was rebuilt, fixed, dropped or guessed.</summary>
    public bool Changed { get; private set; }

    public void Add(RepairAction action, string structure, int track, long offset, long length, string detail, RepairConfidence confidence)
    {
        if (action != RepairAction.Kept)
        {
            Changed = true;
        }

        _items.Add(new RepairItem(action, structure, track, new ByteRange(offset, length), detail, confidence));
    }

    public void Warn(string text) => _warnings.Add(text);

    public RepairReport Build(RepairStatus status, long outputLength)
    {
        RepairConfidence lowest = RepairConfidence.High;
        foreach (RepairItem i in _items)
        {
            if (i.Action != RepairAction.Kept && i.Action != RepairAction.Dropped && i.Confidence < lowest)
            {
                lowest = i.Confidence;
            }
        }

        if (status == RepairStatus.Unrepairable)
        {
            lowest = RepairConfidence.None;
        }

        return new RepairReport(status, format, inputLength, outputLength, _items, Tracks, lowest, _warnings);
    }
}

/// <summary>Merges adjacent byte ranges as they are added in file order.</summary>
internal sealed class RangeList
{
    private readonly List<ByteRange> _ranges = [];

    public void Add(long offset, long length)
    {
        if (_ranges.Count > 0)
        {
            ByteRange last = _ranges[^1];
            if (last.Offset + last.Length == offset)
            {
                _ranges[^1] = new ByteRange(last.Offset, last.Length + length);
                return;
            }
        }

        _ranges.Add(new ByteRange(offset, length));
    }

    public IReadOnlyList<ByteRange> ToList() => [.. _ranges];
}

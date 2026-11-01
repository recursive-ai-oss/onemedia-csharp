using System.Buffers;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// Buffered sequential access to the bytes of an AVI file, over either a seekable source or a forward-only one. Small reads
/// (chunk headers, small payloads) are served from a 64 KiB block; large payloads are read straight into the destination.
/// </summary>
internal sealed class AviInput : IDisposable
{
    private const int BlockSize = 64 * 1024;

    private readonly IMediaSource? _source;
    private readonly IForwardMediaSource? _forward;
    private readonly CancellationToken _cancellation;
    private byte[]? _block;
    private long _blockStart;
    private int _blockLength;
    private long _position;

    public AviInput(IMediaSource source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellation = cancellationToken;
        _block = ArrayPool<byte>.Shared.Rent(BlockSize);
        Length = source.Length;
    }

    public AviInput(IForwardMediaSource forward, CancellationToken cancellationToken)
    {
        _forward = forward;
        _cancellation = cancellationToken;
        _block = ArrayPool<byte>.Shared.Rent(BlockSize);
        _position = forward.Position;
        _blockStart = _position;
        Length = -1;
    }

    /// <summary>Length of the file, or -1 for a forward-only source.</summary>
    public long Length { get; }

    public bool CanSeek => _source is not null;

    public long Position => _position;

    /// <summary>Moves to <paramref name="position"/>; on a forward-only source only moves ahead.</summary>
    public void Seek(long position)
    {
        if (_source is not null)
        {
            _position = position;
            return;
        }

        if (position < _position)
        {
            throw new InvalidOperationException("A forward-only source cannot move back.");
        }

        Skip(position - _position);
    }

    /// <summary>Skips ahead; false when the data ends first (forward-only) — a seekable source may be moved past its end.</summary>
    public bool Skip(long count)
    {
        if (_source is not null)
        {
            _position += count;
            return true;
        }

        long end = _position + count;
        while (_position < end)
        {
            int inBlock = BufferedAt(_position);
            if (inBlock == 0)
            {
                if (!FillForward())
                {
                    return false;
                }

                continue;
            }

            _position += Math.Min(inBlock, end - _position);
        }

        return true;
    }

    /// <summary>Reads up to the destination's length; returns fewer only at the end of the data.</summary>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (!destination.IsEmpty)
        {
            int inBlock = BufferedAt(_position);
            if (inBlock > 0)
            {
                int n = Math.Min(inBlock, destination.Length);
                _block.AsSpan((int)(_position - _blockStart), n).CopyTo(destination);
                destination = destination[n..];
                _position += n;
                total += n;
                continue;
            }

            if (_source is not null)
            {
                if (destination.Length >= BlockSize / 2)
                {
                    // A large read goes straight to the caller's buffer so the payload is not copied twice.
                    int got = _source.Read(_position, destination, _cancellation);
                    _position += got;
                    return total + got;
                }

                if (!FillSeekable())
                {
                    return total;
                }
            }
            else if (!FillForward())
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>Tests whether the data from the current position covers the span, without consuming it.</summary>
    public int Peek(Span<byte> destination)
    {
        long at = _position;
        int n = Read(destination);
        if (_source is not null)
        {
            _position = at;
        }
        else
        {
            // A forward-only block keeps what was just read, so stepping back inside it is free.
            _position = at;
        }

        return n;
    }

    public void Dispose()
    {
        byte[]? b = _block;
        _block = null;
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private int BufferedAt(long position) =>
        _blockLength > 0 && position >= _blockStart && position < _blockStart + _blockLength ? (int)(_blockStart + _blockLength - position) : 0;

    private bool FillSeekable()
    {
        int got = _source!.Read(_position, _block.AsSpan(0, BlockSize), _cancellation);
        _blockStart = _position;
        _blockLength = got;
        return got > 0;
    }

    // Forward-only: the block always holds the most recent bytes, so a position inside it can be re-read; anything older is gone.
    private bool FillForward()
    {
        _blockStart = _position;
        _blockLength = 0;
        int got = _forward!.Read(_block.AsSpan(0, BlockSize), _cancellation);
        _blockLength = got;
        return got > 0;
    }
}

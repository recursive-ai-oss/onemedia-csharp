namespace RecursiveAI.OneMedia.Audio;

/// <summary>
/// The packets of one MPEG audio track laid end to end as a forward-only byte stream. MPEG audio frames are self-delimiting, so the
/// concatenation is a plain MP3 stream, which is the form OneAudio's MP3 reader takes.
/// </summary>
internal sealed class PacketConcatenationStream : Stream
{
    private readonly IDemuxer _demuxer;
    private readonly int _trackIndex;
    private byte[] _buffer = new byte[4096];
    private int _pos;
    private int _count;
    private bool _ended;

    private readonly IDisposable? _alsoDispose;
    private readonly bool _ownsDemuxer;

    public PacketConcatenationStream(IDemuxer demuxer, int trackIndex, IDisposable? alsoDispose, ReadOnlyMemory<byte> prefix = default, bool ownsDemuxer = true)
    {
        // The Xing/Info frame of an MP3 file is not a packet but carries the frame count and the encoder's delay and padding, which OneAudio's MP3
        // reader uses for gapless playback, so it goes first.
        if (!prefix.IsEmpty)
        {
            _buffer = prefix.ToArray();
            _count = _buffer.Length;
        }

        _alsoDispose = alsoDispose;
        _ownsDemuxer = ownsDemuxer;
        _demuxer = demuxer;
        _trackIndex = trackIndex;
        for (int i = 0; i < demuxer.Tracks.Count; i++)
        {
            demuxer.SetTrackEnabled(i, i == trackIndex);
        }
    }

    /// <summary>The file this stream reads, when its reads must fail once the file is read again.</summary>
    internal MediaFile? Owner { get; init; }

    internal int Generation { get; init; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        while (_pos >= _count)
        {
            Owner?.EnsureCurrentRead(Generation);
            if (_ended || !_demuxer.TryReadPacket(out PacketView view))
            {
                _ended = true;
                return 0;
            }

            if (view.Info.TrackIndex != _trackIndex)
            {
                continue;
            }

            if (_buffer.Length < view.Data.Length)
            {
                _buffer = new byte[view.Data.Length];
            }

            view.Data.CopyTo(_buffer);
            _pos = 0;
            _count = view.Data.Length;
        }

        int n = Math.Min(destination.Length, _count - _pos);
        _buffer.AsSpan(_pos, n).CopyTo(destination);
        _pos += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_ownsDemuxer)
            {
                _demuxer.Dispose();
            }

            _alsoDispose?.Dispose();
        }

        base.Dispose(disposing);
    }
}

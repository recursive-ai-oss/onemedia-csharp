using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Cuts the sample data of a one-stream audio file into packets. The plan says how many bytes the next packet takes and how many
/// frames (samples per channel) they hold; the demuxer does the reading.
/// </summary>
internal abstract class PacketPlan
{
    /// <summary>True when <see cref="SeekTo"/> works.</summary>
    public virtual bool CanSeek => false;

    /// <summary>Gives the next packet's size and duration. <paramref name="remaining"/> is the bytes left in the data.</summary>
    public abstract bool Next(long remaining, out int bytes, out long frames);

    /// <summary>Bytes to jump over before the packet <see cref="Next"/> just returned (junk between frames of a framed stream); 0 for plans that cut equal packets.</summary>
    public virtual int Skip => 0;

    /// <summary>Finds the packet that holds <paramref name="frame"/> (or the first one starting at or after it when <paramref name="next"/> is set); returns its first frame and byte offset in the data.</summary>
    public virtual (long Frame, long Offset) SeekTo(long frame, bool next) => throw new NotSupportedException();
}

/// <summary>
/// A plan with equal packets: <c>unitBytes</c> is one frame of PCM or one block of an ADPCM-style codec, <c>unitFrames</c> the frames in a unit, and a
/// packet holds <c>unitsPerPacket</c> units. The last packet takes whole units only.
/// </summary>
internal sealed class FixedPlan(int unitBytes, long unitFrames, int unitsPerPacket) : PacketPlan
{
    private readonly long _packetBytes = (long)unitBytes * unitsPerPacket;

    public override bool CanSeek => true;

    public override bool Next(long remaining, out int bytes, out long frames)
    {
        long n = Math.Min(_packetBytes, remaining / unitBytes * unitBytes);
        if (n <= 0)
        {
            bytes = 0;
            frames = 0;
            return false;
        }

        bytes = (int)n;
        frames = n / unitBytes * unitFrames;
        return true;
    }

    public override (long Frame, long Offset) SeekTo(long frame, bool next)
    {
        long perPacket = unitsPerPacket * unitFrames;
        long packet = (Math.Max(0, frame) + (next ? perPacket - 1 : 0)) / perPacket;
        return (packet * unitsPerPacket * unitFrames, packet * _packetBytes);
    }
}

/// <summary>
/// The shared body of the demuxers for files with one audio stream stored as one run of sample data: it reads packets from the data range
/// as the plan cuts them, with the track's timescale equal to the sample rate. Every packet is a keyframe.
/// </summary>
public abstract class AudioFileDemuxer : IDemuxer, IMetadataProvider
{
    private readonly IMediaSource _source;
    private readonly IDisposable? _owner;
    private readonly PacketPlan _plan;
    private readonly long _dataStart;
    private readonly long _dataEnd;
    private readonly bool _endIsGuess;
    private readonly long _timescale;
    private readonly MediaTrack _track;
    private readonly DefectLog _defects;
    private byte[] _buffer = [];
    private long _offset;
    private long _frame;
    private bool _enabled = true;
    private bool _finished;

    // A packet the plan has cut but whose bytes have not been read yet (the read of a ranged source can ask to be retried).
    private bool _planned;
    private int _plannedSize;
    private long _plannedFrames;
    private int _plannedSkip;
    private DefectLog? _scratchDefects;

    private protected AudioFileDemuxer(IMediaSource source, IDisposable? owner, MediaTrack track, PacketPlan plan, long dataStart, long dataEnd, bool endIsGuess, DefectLog defects, MediaLimits limits)
    {
        _source = source;
        _owner = owner;
        _track = track;
        _plan = plan;
        _dataStart = dataStart;
        _dataEnd = dataEnd;
        _endIsGuess = endIsGuess;
        _timescale = track.Timescale;
        _defects = defects;
        Limits = limits;
    }

    /// <inheritdoc />
    public abstract MediaFormatId Format { get; }

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => [_track];

    /// <inheritdoc />
    public MediaTime Duration => _track.Duration;

    /// <inheritdoc />
    public DefectLog Defects => _scratchDefects ?? _defects;

    /// <summary>Redirects defects to a throw-away log while a ranged open or metadata read is being repeated; null restores the real log.</summary>
    internal DefectLog? ScratchDefects { set => _scratchDefects = value; }

    /// <summary>The limits the file was opened with.</summary>
    private protected MediaLimits Limits { get; }

    /// <summary>The source the file reads from.</summary>
    private protected IMediaSource Source => _source;

    /// <inheritdoc />
    public bool CanSeek => _plan.CanSeek && !_endIsGuess && _source is not ForwardAsRandomSource;

    /// <summary>The bytes of the packet the last successful read returned.</summary>
    internal ReadOnlySpan<byte> LastPacketBytes(int size) => _buffer.AsSpan(0, size);

    /// <summary>Where the sample data starts in the file.</summary>
    public long DataOffset => _dataStart;

    /// <summary>Length of the sample data in bytes.</summary>
    public long DataLength => _dataEnd - _dataStart;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(trackIndex, 0);
        _enabled = enabled;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        packet = default;
        if (!_enabled || _finished)
        {
            return false;
        }

        long remaining = _dataEnd - _dataStart - _offset;
        int size;
        long frames;
        int skip;
        if (_planned)
        {
            size = _plannedSize;
            frames = _plannedFrames;
            skip = _plannedSkip;
        }
        else
        {
            size = 0;
            frames = 0;
            if (remaining <= 0 || !_plan.Next(remaining, out size, out frames))
            {
                if (remaining > 0 && !_endIsGuess)
                {
                    Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, _dataStart + _offset, $"{remaining} byte(s) at the end of the sample data do not fill a whole frame or block and are not delivered.", "audio.partial-unit");
                }

                _finished = true;
                return false;
            }

            skip = _plan.Skip;
            // Remembered so that a read which has to be retried (ranged sources) does not cut the packet twice.
            _planned = true;
            _plannedSize = size;
            _plannedFrames = frames;
            _plannedSkip = skip;
        }

        Limits.CheckPacket(size, _dataStart + _offset + skip);
        if (_buffer.Length < size)
        {
            _buffer = new byte[Math.Max(size, 4096)];
        }

        long at = _dataStart + _offset + skip;
        int got = _source.Read(at, _buffer.AsSpan(0, size));
        _planned = false;
        _offset += skip;
        if (got < size)
        {
            // The file ends inside the data it promised (or a stream without a stated length has ended).
            if (!_endIsGuess)
            {
                Defects.Add(DefectKind.Truncated, DefectSeverity.Error, at + got, $"The data ends {size - got} byte(s) before the end the chunk states.", "audio.truncated-data");
            }

            _finished = true;
            if (got <= 0)
            {
                return false;
            }

            // Hand out the part that holds whole units only, with the frames it holds.
            _plan.Next(got, out size, out frames);
            if (size <= 0)
            {
                return false;
            }
        }

        MediaTime pts = new(_frame, _timescale);
        packet = new PacketView(
            new PacketInfo(0, pts, pts, new MediaTime(frames, _timescale), PacketAttributes.Keyframe, new ByteRange(at, size), size),
            _buffer.AsSpan(0, size));
        _offset += size;
        _frame += frames;
        return true;
    }

    /// <inheritdoc />
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("This file cannot be sought.");
        }

        long frame = target.IsKnown ? target.Rescale(_timescale, MediaRounding.Floor).Ticks : 0;
        (long f, long o) = _plan.SeekTo(frame, mode == SeekMode.NextKeyframe);
        _planned = false;
        _frame = f;
        _offset = Math.Min(o, _dataEnd - _dataStart);
        _finished = _offset >= _dataEnd - _dataStart;
        return _finished ? MediaTime.Unknown : new MediaTime(_frame, _timescale);
    }

    /// <inheritdoc />
    public abstract MediaMetadata ReadMetadata(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public void Dispose()
    {
        _owner?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Reads the length and duration of one frame from its header; false when the bytes are not the start of a frame.</summary>
internal delegate bool FrameParser(ReadOnlySpan<byte> head, out int length, out int frames);

/// <summary>
/// A plan for streams of self-delimiting frames (MPEG audio, AC-3, ADTS) stored without a container index: each packet is one frame, found from its header.
/// Bytes between frames are skipped, as a decoder resynchronises, and reported.
/// </summary>
internal sealed class FramedPlan(IMediaSource source, long dataStart, long dataEnd, FrameParser parser, DefectLog defects) : PacketPlan
{
    private const int HeadBytes = 16;
    private const int MaxResync = 1 << 20;
    private readonly byte[] _head = new byte[HeadBytes];
    private long _pos;
    private int _skip;

    public override int Skip => _skip;

    public override bool Next(long remaining, out int bytes, out long frames)
    {
        bytes = 0;
        frames = 0;
        _skip = 0;
        int skipped = 0;
        while (_pos + 4 <= dataEnd - dataStart)
        {
            int n = source.Read(dataStart + _pos, _head.AsSpan(0, (int)Math.Min(HeadBytes, dataEnd - dataStart - _pos)));
            if (n >= 4 && parser(_head.AsSpan(0, n), out int length, out int count) && length > 0 && _pos + length <= dataEnd - dataStart)
            {
                if (skipped > 0)
                {
                    defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, dataStart + _pos - skipped, $"{skipped} byte(s) between frames are not a frame and are skipped.", "audio.resync");
                }

                _skip = skipped;
                bytes = length;
                frames = count;
                _pos += length;
                return true;
            }

            if (++skipped > MaxResync || n < 4)
            {
                break;
            }

            _pos++;
        }

        _pos = dataEnd - dataStart;
        return false;
    }
}

using RecursiveAI.OneMedia;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// A tiny demuxer over a block of equally sized packets, used by the tests and the smoke apps to exercise the contracts without a
/// real format: one video track at 1000 ticks per second, 40 ticks per packet, every fourth packet a keyframe.
/// </summary>
internal sealed class FixedPacketDemuxer : IDemuxer
{
    public const long TrackTimescale = 1000;
    public const long PacketTicks = 40;

    private readonly IMediaSource _source;
    private readonly int _packetSize;
    private readonly long _count;
    private readonly byte[] _buffer;
    private long _next;
    private bool _enabled = true;

    public FixedPacketDemuxer(IMediaSource source, int packetSize)
    {
        _source = source;
        _packetSize = packetSize;
        _count = source.Length / packetSize;
        _buffer = new byte[packetSize];
        Tracks =
        [
            new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = CodecId.H264,
                Timescale = TrackTimescale,
                Duration = new MediaTime(_count * PacketTicks, TrackTimescale),
            },
        ];
        Duration = new MediaTime(_count * PacketTicks, TrackTimescale);
    }

    public MediaFormatId Format => new("fixed");

    public IReadOnlyList<MediaTrack> Tracks { get; }

    public MediaTime Duration { get; }

    public DefectLog Defects { get; } = new();

    public bool CanSeek => true;

    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(trackIndex, 0);
        _enabled = enabled;
    }

    public bool TryReadPacket(out PacketView packet)
    {
        if (!_enabled || _next >= _count)
        {
            packet = default;
            return false;
        }

        long offset = _next * _packetSize;
        _source.ReadExactly(offset, _buffer);
        MediaTime time = new(_next * PacketTicks, TrackTimescale);
        PacketInfo info = new(0, time, time, new MediaTime(PacketTicks, TrackTimescale), _next % 4 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, new ByteRange(offset, _packetSize), _packetSize);
        _next++;
        packet = new PacketView(info, _buffer);
        return true;
    }

    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        long index = target.ToTicks(TrackTimescale, MediaRounding.Floor) / PacketTicks;
        index = Math.Clamp(index, 0, _count);
        long keyframe = index / 4 * 4;
        if (mode == SeekMode.NextKeyframe && keyframe < index)
        {
            keyframe += 4;
        }

        _next = Math.Min(keyframe, _count);
        return _next >= _count ? MediaTime.Unknown : new MediaTime(_next * PacketTicks, TrackTimescale);
    }

    public void Dispose() => _source.Dispose();
}

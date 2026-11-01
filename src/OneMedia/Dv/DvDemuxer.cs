using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Dv;

/// <summary>
/// Reads a raw DV file: a plain sequence of DV frames with no container around them (IEC 61834, SMPTE 314M). Every frame is one packet
/// of the single video track; the sound in the frame's audio blocks stays inside the packet.
/// </summary>
public sealed class DvDemuxer : IDemuxer, IMetadataProvider, ICurrentPacket
{
    private readonly AviInput _input;
    private readonly IMediaSource? _source;
    private readonly IDisposable? _owned;
    private readonly MediaLimits _limits;
    private readonly int _frameSize;
    private readonly long _frames;
    private readonly long _length;
    private byte[] _buffer;
    private long _next;
    private bool _enabled = true;
    private PacketInfo _current;

    private DvDemuxer(AviInput input, IMediaSource? source, IDisposable? owned, DvFrameInfo info, MediaLimits limits, DefectLog defects, long length)
    {
        _input = input;
        _source = source;
        _owned = owned;
        _limits = limits;
        Info = info;
        Defects = defects;
        _frameSize = info.FrameSize;
        _length = length;
        _frames = length >= 0 ? length / _frameSize : -1;
        _buffer = new byte[_frameSize];
        MediaTime duration = _frames >= 0 ? new MediaTime(_frames * info.FrameRateDenominator, info.FrameRateNumerator) : MediaTime.Unknown;
        Tracks =
        [
            new MediaTrack
            {
                Index = 0,
                Id = 0,
                Kind = TrackKind.Video,
                Codec = new CodecId("dvvideo"),
                CodecTag = "dvvideo",
                Timescale = info.FrameRateNumerator,
                Duration = duration,
                Video = DvTrackInfo.Video(info),
                Audio = null,
                PacketCount = _frames,
            },
        ];
        Duration = duration;
    }

    /// <summary>What the first frame says about the stream.</summary>
    public DvFrameInfo Info { get; }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Dv;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks { get; }

    /// <inheritdoc />
    public MediaTime Duration { get; }

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _source is not null;

    /// <summary>Opens a raw DV file from a seekable source.</summary>
    /// <param name="source">The file.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="MediaFormatException">The data does not start with a DV frame.</exception>
    public static DvDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        AviInput input = new(source, cancellationToken);
        return Create(input, source, options.LeaveOpen ? null : source, options, defects, source.Length);
    }

    /// <summary>Opens a raw DV stream from a forward-only source; seeking is not available and the duration is unknown.</summary>
    /// <param name="source">The stream.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static DvDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        AviInput input = new(source, cancellationToken);
        return Create(input, null, options.LeaveOpen ? null : source, options, defects, -1);
    }

    private static DvDemuxer Create(AviInput input, IMediaSource? source, IDisposable? owned, MediaOpenOptions options, DefectLog defects, long length)
    {
        try
        {
            byte[] first = new byte[DvDif.SequenceSize];
            int got = input.Read(first);
            if (!DvDif.TryParseFrame(first.AsSpan(0, got), out DvFrameInfo? info))
            {
                throw new MediaFormatException("The data does not start with a DV frame.") { Offset = 0 };
            }

            if (got < DvDif.SequenceSize)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Error, got, "The file ends inside the first DIF sequence.", "dv.truncated-first-frame");
            }

            DvDemuxer d = new(input, source, owned, info, options.Limits, defects, length);
            if (source is null)
            {
                // A forward-only input has already consumed the first sequence: keep it as the start of the first frame.
                first.AsSpan(0, got).CopyTo(d._buffer);
                d._pendingStart = got;
            }
            else
            {
                input.Seek(0);
            }

            return d;
        }
        catch
        {
            input.Dispose();
            owned?.Dispose();
            throw;
        }
    }

    private int _pendingStart;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(trackIndex, 0);

        _enabled = enabled;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        while (true)
        {
            int have = _pendingStart;
            _pendingStart = 0;
            int got = have + _input.Read(_buffer.AsSpan(have, _frameSize - have));
            if (got == 0)
            {
                packet = default;
                return false;
            }

            long index = _next++;
            bool complete = got == _frameSize;
            if (!complete)
            {
                Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, (index * _frameSize) + got, $"The file ends {_frameSize - got} bytes into a {_frameSize}-byte frame.", "dv.truncated-frame");
            }

            if (!_enabled)
            {
                continue;
            }

            MediaTime pts = new(index * Info.FrameRateDenominator, Info.FrameRateNumerator);
            _current = new PacketInfo(0, pts, pts, new MediaTime(Info.FrameRateDenominator, Info.FrameRateNumerator), complete ? PacketAttributes.Keyframe : PacketAttributes.Keyframe | PacketAttributes.Corrupt,
                new ByteRange(index * _frameSize, got), got);
            packet = new PacketView(_current, _buffer.AsSpan(0, got));
            return true;
        }
    }

    PacketView ICurrentPacket.Current => new(_current, _buffer.AsSpan(0, _current.Size));

    /// <summary>Opens a raw DV file from a ranged source; reads wait for each range on the calling thread.</summary>
    /// <param name="source">The ranged source.</param>
    /// <param name="options">Limits, defect log and ownership; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DvDemuxer d = Open(new BlockingMediaSource(source), options with { LeaveOpen = true }, cancellationToken);
        return new ValueTask<IAsyncDemuxer>(new SyncBackedAsyncDemuxer<DvDemuxer>(d, options.LeaveOpen ? null : source));
    }

    /// <inheritdoc />
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (_source is null)
        {
            throw new NotSupportedException("The source is forward-only; seeking is not possible.");
        }

        // Every frame is a key frame, so the frame at or after the target is exact. The time is rounded in the frame-rate numerator's
        // timescale, then divided by the frame period (1 or 1001 ticks).
        long den = Info.FrameRateDenominator;
        long ticks = Math.Max(0, target.Rescale(Info.FrameRateNumerator, mode == SeekMode.PreviousKeyframe ? MediaRounding.Floor : MediaRounding.Ceiling).Ticks);
        long frame = mode == SeekMode.PreviousKeyframe ? ticks / den : (ticks + den - 1) / den;
        if (_frames >= 0 && frame >= _frames)
        {
            if (mode == SeekMode.NextKeyframe)
            {
                _pendingStart = 0;
                _next = _frames;
                _input.Seek(_frames * _frameSize);
                return MediaTime.Unknown;
            }

            frame = Math.Max(0, _frames - 1);
        }

        _pendingStart = 0;
        _next = frame;
        _input.Seek(frame * _frameSize);
        return new MediaTime(frame * Info.FrameRateDenominator, Info.FrameRateNumerator);
    }

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        List<ITagContainer> containers = [];
        FactsBuilder facts = new(Defects);
        if (_source is not null)
        {
            DvMetadata.AddPacks(_source, 0, Info, _limits, Defects, containers, facts);
        }
        else
        {
            DvMetadata.AddFacts(Info, 0, facts);
        }

        if (Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(Duration, "container", Duration.ToString(), "frame count times the frame period"));
        }

        facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(Info.Width, Info.Height, Info.Width, Info.Height), "dv:frame", $"{Info.Width}x{Info.Height}", "picture size of the DV system"));
        return new MediaMetadata(containers, [], facts.Build(), Defects, true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _input.Dispose();
        _owned?.Dispose();
    }
}

/// <summary>Maps DV stream parameters to the neutral track descriptions.</summary>
internal static class DvTrackInfo
{
    public static VideoTrackInfo Video(DvFrameInfo info)
    {
        // The pixel aspect is the display aspect divided by the picture's own width:height. Without the control pack, 4:3 is assumed.
        int aw = info.AspectWidth > 0 ? info.AspectWidth : 4;
        int ah = info.AspectHeight > 0 ? info.AspectHeight : 3;
        long n = (long)aw * info.Height;
        long d = (long)ah * info.Width;
        long g = Gcd(n, d);
        return new VideoTrackInfo(info.Width, info.Height, 0, (int)(n / g), (int)(d / g), info.FrameRateNumerator, info.FrameRateDenominator);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }
}

/// <summary>Registers raw DV with content sniffing.</summary>
public sealed class DvFormat : IMediaFormat, IHeadSeededFormat
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Dv;

    /// <inheritdoc />
    public string DisplayName => "DV (raw)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (!DvDif.LooksLikeFrameStart(head, out bool strong))
        {
            return SniffResult.None;
        }

        // A header block alone is three fixed bytes; with the subcode and VAUX blocks in place it is unmistakable.
        return new SniffResult(strong ? SniffConfidence.Certain : SniffConfidence.Possible);
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => DvDemuxer.Open(source, options, cancellationToken);

    IDemuxer IHeadSeededFormat.Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) => DvDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => DvDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => DvDemuxer.OpenAsync(source, options, cancellationToken);
}

using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Av1;
using RecursiveAI.OneMedia.Codecs.Vpx;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Mpeg;

namespace RecursiveAI.OneMedia.ElementaryStreams;

/// <summary>Which bitstream a raw video file holds.</summary>
public enum VideoStreamKind
{
    /// <summary>H.264 in Annex B byte-stream form (ITU-T H.264 Annex B).</summary>
    H264,

    /// <summary>H.265 in Annex B byte-stream form.</summary>
    Hevc,

    /// <summary>H.266 in Annex B byte-stream form.</summary>
    Vvc,

    /// <summary>MPEG-1 or MPEG-2 video sequence (ISO/IEC 13818-2).</summary>
    MpegVideo,

    /// <summary>AV1 low-overhead bitstream format, as in <c>.obu</c> files (AV1 specification §5.2).</summary>
    Av1Obu,
}

/// <summary>
/// Reads raw video files: Annex B H.264, H.265 and H.266, MPEG-1/2 video and AV1 low-overhead (<c>.obu</c>) streams. One track;
/// each packet is one access unit (a temporal unit for AV1). These files carry no timestamps, so decode times count the
/// units at the stream's frame rate (25 per second when it declares none), and the track timescale is that rate's numerator,
/// which keeps every time exact. Presentation time is left unknown except for AV1, which does not reorder.
/// </summary>
public sealed class VideoStreamDemuxer : IDemuxer, IMetadataProvider
{
    /// <summary>How much of the front is read to describe the stream when its first access unit does not suffice.</summary>
    public const int ProbeLimitBytes = 5_000_000;

    private const int Chunk = 64 * 1024;

    private readonly ByteInput _input;
    private readonly PacketQueue _queue;
    private readonly TrackAssembler _assembler;
    private readonly DefectLog _defects;
    private readonly VideoStreamKind _kind;
    private readonly MediaFormatId _format;
    private MediaTrack[] _tracks = [];
    private bool _eof;
    private bool _enabled = true;
    private MediaMetadata? _metadata;

    private VideoStreamDemuxer(ByteInput input, VideoStreamKind kind, MediaFormatId format, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _kind = kind;
        _format = format;
        _defects = defects;
        _queue = new PacketQueue(options.Limits);
        _assembler = kind switch
        {
            VideoStreamKind.H264 => new NalVideoAssembler(NalCodec.H264, 0, _queue, options.Limits, defects) { ElementaryMode = true },
            VideoStreamKind.Hevc => new NalVideoAssembler(NalCodec.Hevc, 0, _queue, options.Limits, defects) { ElementaryMode = true },
            VideoStreamKind.Vvc => new NalVideoAssembler(NalCodec.Vvc, 0, _queue, options.Limits, defects) { ElementaryMode = true },
            VideoStreamKind.MpegVideo => new Mpeg2VideoAssembler(0, _queue, options.Limits, defects) { ElementaryMode = true },
            _ => new ObuAssembler(0, _queue, options.Limits, defects) { ElementaryMode = true, PtsFollowsDts = true },
        };
    }

    /// <summary>Opens a raw video file.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="kind">The bitstream the file holds.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer, positioned at the first access unit.</returns>
    public static VideoStreamDemuxer Open(IMediaSource source, VideoStreamKind kind, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), kind, options, cancellationToken);
    }

    /// <summary>Opens a raw video stream that can only be read once. Seeking is unavailable.</summary>
    /// <param name="source">The source.</param>
    /// <param name="kind">The bitstream the stream holds.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer.</returns>
    public static VideoStreamDemuxer Open(IForwardMediaSource source, VideoStreamKind kind, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), kind, options, cancellationToken);
    }

    private static VideoStreamDemuxer Create(ByteInput input, VideoStreamKind kind, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        try
        {
            MediaFormatId id = kind switch
            {
                VideoStreamKind.H264 => new MediaFormatId("h264"),
                VideoStreamKind.Hevc => new MediaFormatId("hevc"),
                VideoStreamKind.Vvc => new MediaFormatId("vvc"),
                VideoStreamKind.MpegVideo => new MediaFormatId("mpegvideo"),
                _ => new MediaFormatId("obu"),
            };
            VideoStreamDemuxer d = new(input, kind, id, options, defects);
            d.Probe(cancellationToken);
            return d;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => _format;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc />
    public MediaTime Duration => MediaTime.Unknown;

    /// <inheritdoc />
    public DefectLog Defects => _defects;

    /// <inheritdoc />
    public bool CanSeek => _input.CanSeek;

    /// <summary>The bitstream kind.</summary>
    public VideoStreamKind Kind => _kind;

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
            while (_queue.TryDequeue(out PacketInfo info, out ReadOnlySpan<byte> data))
            {
                if (_enabled)
                {
                    packet = new PacketView(info, data);
                    return true;
                }
            }

            if (_eof)
            {
                packet = default;
                return false;
            }

            Pump();
        }
    }

    /// <inheritdoc />
    /// <remarks>Raw video has no index: the stream is read from the start until the keyframe is found, in constant memory.</remarks>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!_input.CanSeek)
        {
            throw new NotSupportedException("A forward-only video stream cannot be repositioned.");
        }

        Rewind();
        long keyPos = 0;
        MediaTime keyTime = MediaTime.Zero;
        bool found = false;
        while (TryReadPacket(out PacketView p))
        {
            if (!p.Info.IsKeyframe)
            {
                continue;
            }

            if (mode == SeekMode.PreviousKeyframe && p.Info.Dts > target && found)
            {
                break;
            }

            keyPos = p.Info.SourceRange.Offset;
            keyTime = p.Info.Dts;
            found = true;
            if (mode == SeekMode.NextKeyframe && p.Info.Dts >= target)
            {
                break;
            }
        }

        Rewind();
        if (keyPos > 0)
        {
            _input.Seek(keyPos);
        }

        return keyTime;
    }

    /// <inheritdoc />
    public void Dispose() => _input.Dispose();

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        if (_metadata is null)
        {
            DefectLog defects = new();
            FactsBuilder facts = new(defects);
            if (_tracks.Length > 0 && _tracks[0].Video is { Width: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "video parameters", $"{v.Width}x{v.Height}", "size from the first sequence header or parameter set"));
            }

            _metadata = new MediaMetadata([], [], facts.Build(), defects);
        }

        return _metadata;
    }

    private void Rewind()
    {
        _queue.Clear();
        _assembler.Reset();
        _eof = false;
        _input.Seek(0);
    }

    private void Pump()
    {
        long at = _input.Position;
        ReadOnlySpan<byte> got = _input.Peek(Chunk);
        if (got.IsEmpty)
        {
            _assembler.Flush();
            _eof = true;
            return;
        }

        _assembler.Push(got, new PesStamp(-1, -1, at, false, Contiguous: true), true);
        _input.Advance(got.Length);
    }

    private void Probe(CancellationToken cancellationToken)
    {
        // Read until the first access unit has been cut and the stream described, or the bound is reached; the packets stay queued.
        long start = _input.Position;
        while (!_eof && (_assembler.Emitted == 0 || !_assembler.IsDescribed) && _input.Position - start < ProbeLimitBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pump();
        }

        TrackAssembler a = _assembler;
        CodecParameters? p = a.Parameters;
        CodecId codec = _kind switch
        {
            VideoStreamKind.H264 => CodecId.H264,
            VideoStreamKind.Hevc => CodecId.Hevc,
            VideoStreamKind.Vvc => CodecId.Vvc,
            VideoStreamKind.MpegVideo => a.DetectedCodec.IsUnknown ? CodecId.Mpeg2Video : a.DetectedCodec,
            _ => CodecId.Av1,
        };
        VideoTrackInfo? video = p is not null ? new VideoTrackInfo(p.Width, p.Height, 0, Math.Max(1, p.SarWidth), Math.Max(1, p.SarHeight), FrameRates.Reduce(p.FrameRateNumerator, p.FrameRateDenominator).Numerator, FrameRates.Reduce(p.FrameRateNumerator, p.FrameRateDenominator).Denominator) : null;
        _tracks =
        [
            new MediaTrack
            {
                Index = 0,
                Id = 0,
                Kind = TrackKind.Video,
                Codec = codec,
                CodecTag = _format.Name,
                Timescale = a.Timescale,
                Video = video,
                BitRate = p?.BitRate ?? 0,
            },
        ];
    }
}

/// <summary>AV1 temporal units from a low-overhead byte stream: a unit starts at each temporal delimiter OBU (AV1 specification §7.5).</summary>
internal sealed class ObuAssembler : VideoAssembler
{
    private int _scan;
    private bool _open;
    private bool _key;

    public ObuAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    public override void Reset()
    {
        base.Reset();
        _scan = 0;
        _open = _key = false;
    }

    protected override void Scan(bool final)
    {
        while (true)
        {
            ReadOnlySpan<byte> span = Buffered;
            if (_scan >= span.Length)
            {
                break;
            }

            int state = ReadHeader(span[_scan..], out int type, out int total);
            if (state == 0)
            {
                if (!final)
                {
                    return; // the header is not all here yet
                }

                break;
            }

            if (state < 0)
            {
                // Not an OBU with a size field: the stream is damaged or not low-overhead. Skip a byte and look again.
                Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, -1, "An OBU header is malformed; skipping a byte.", "obu.header");
                _scan++;
                continue;
            }

            if (_scan + total > span.Length)
            {
                if (final)
                {
                    break;
                }

                return; // the OBU is not complete yet
            }

            if (type == ObuType.TemporalDelimiter)
            {
                if (_open && _scan > 0)
                {
                    Complete(_scan);
                    span = Buffered;
                }

                _open = true;
            }
            else if (!_open)
            {
                _open = true; // a stream may start without a delimiter
            }

            if (type == ObuType.SequenceHeader)
            {
                _key = true; // a sequence header opens every random access point (AV1 specification §7.5)
            }

            _scan += total;
        }

        if (final && _open && Buffered.Length > 0)
        {
            Complete(Buffered.Length);
            _open = false;
        }
    }

    // AV1 specification §5.3: obu_header (1 byte, +1 with an extension) then obu_size as leb128 when has_size_field is set.
    // Returns 1 with the OBU's total length, 0 when more bytes are needed, -1 when the header is invalid for this framing.
    private static int ReadHeader(ReadOnlySpan<byte> d, out int type, out int total)
    {
        type = 0;
        total = 0;
        if (d.IsEmpty)
        {
            return 0;
        }

        if ((d[0] & 0x80) != 0 || (d[0] & 2) == 0)
        {
            return -1;
        }

        type = (d[0] >> 3) & 0xF;
        int pos = (d[0] & 4) != 0 ? 2 : 1;
        long size = 0;
        for (int i = 0; ; i++)
        {
            if (pos >= d.Length)
            {
                return 0;
            }

            if (i >= 8)
            {
                return -1;
            }

            byte b = d[pos++];
            size |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        if (size > int.MaxValue - pos)
        {
            return -1;
        }

        total = pos + (int)size;
        return 1;
    }

    private void Complete(int length)
    {
        if (Parameters is null && CodecParameterReader.TryDescribeSample(CodecId.Av1, Buffered[..length], out CodecParameters? p) && p is not null)
        {
            Parameters = p;
        }

        CutUnit(length, _key);
        _key = false;
        _scan = 0;
    }
}

/// <summary>Reads IVF files: a 32-byte header, then frames each with a 12-byte header of size and timestamp. Holds VP8, VP9 and AV1.</summary>
public sealed class IvfDemuxer : IDemuxer, IMetadataProvider
{
    private readonly ByteInput _input;
    private readonly DefectLog _defects;
    private readonly MediaLimits _limits;
    private readonly MediaTrack[] _tracks;
    private readonly long _rate;
    private readonly long _scale;
    private readonly long _frames;
    private byte[] _frame = new byte[4096];
    private bool _enabled = true;
    private bool _eof;
    private long _next = 32;
    private MediaMetadata? _metadata;

    private IvfDemuxer(ByteInput input, MediaOpenOptions options, DefectLog defects, MediaTrack track, long rate, long scale, long frames, int headerSize)
    {
        _input = input;
        _limits = options.Limits;
        _defects = defects;
        _tracks = [track];
        _rate = rate;
        _scale = scale;
        _frames = frames;
        _next = headerSize;
    }

    /// <summary>Opens an IVF file.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the first frame is read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The 32-byte header is missing or does not start with DKIF.</exception>
    public static IvfDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    /// <summary>Opens an IVF stream that can only be read once.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the first frame is read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The 32-byte header is missing or does not start with DKIF.</exception>
    public static IvfDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    private static IvfDemuxer Create(ByteInput input, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> h = input.Peek(32);
            if (h.Length < 32 || !h[..4].SequenceEqual("DKIF"u8))
            {
                throw new MediaFormatException("The data is not an IVF file: the 32-byte DKIF header is missing.") { Offset = 0, Kind = DefectKind.InvalidValue };
            }

            // The header: signature, version (2), header length (2), codec FourCC (4), width (2), height (2), rate (4), scale (4), frame count (4).
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            if (headerSize < 32)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 6, "The IVF header length is below 32; 32 is used.", "ivf.header-length");
                headerSize = 32;
            }

            string fourcc = System.Text.Encoding.ASCII.GetString(h.Slice(8, 4));
            int width = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            long rate = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            long scale = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long frames = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            if (rate == 0 || scale == 0)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 16, "The IVF time base is zero; 1/30 is used.", "ivf.time-base");
                rate = 30;
                scale = 1;
            }

            CodecId codec = fourcc switch
            {
                "VP80" => CodecId.Vp8,
                "VP90" => CodecId.Vp9,
                "AV01" => CodecId.Av1,
                _ => CodecId.Unknown,
            };
            input.Advance(headerSize);
            MediaTrack track = new()
            {
                Index = 0,
                Id = 0,
                Kind = TrackKind.Video,
                Codec = codec,
                CodecTag = fourcc,
                Timescale = rate,
                Video = new VideoTrackInfo(width, height, 0, 1, 1, (int)Math.Min(int.MaxValue, rate), (int)Math.Min(int.MaxValue, scale)),
                PacketCount = frames > 0 ? frames : -1,
            };
            IvfDemuxer d = new(input, options, defects, track, rate, scale, frames, headerSize);
            return d;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Ivf;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc />
    public MediaTime Duration => _frames > 0 ? new MediaTime(_frames * _scale, _rate) : MediaTime.Unknown;

    /// <inheritdoc />
    public DefectLog Defects => _defects;

    /// <inheritdoc />
    public bool CanSeek => _input.CanSeek;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(trackIndex, 0);

        _enabled = enabled;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        while (!_eof)
        {
            long at = _input.Position;
            ReadOnlySpan<byte> h = _input.Peek(12);
            if (h.Length < 12)
            {
                if (h.Length > 0)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at, "The file ends inside a frame header.", "ivf.truncated-header");
                }

                _eof = true;
                break;
            }

            long size = BinaryPrimitives.ReadUInt32LittleEndian(h);
            long pts = BinaryPrimitives.ReadInt64LittleEndian(h[4..]);
            _limits.CheckPacket(size, at);
            _input.Advance(12);
            if (_frame.Length < size)
            {
                Array.Resize(ref _frame, (int)Math.Max(size, _frame.Length * 2L));
            }

            int got = _input.Read(_frame.AsSpan(0, (int)size));
            if (got < size)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at, "The file ends inside a frame; the partial frame is delivered flagged corrupt.", "ivf.truncated-frame");
                _eof = true;
            }

            if (!_enabled)
            {
                continue;
            }

            ReadOnlySpan<byte> data = _frame.AsSpan(0, got);
            PacketAttributes flags = IsKey(data) ? PacketAttributes.Keyframe : PacketAttributes.None;
            if (got < size)
            {
                flags |= PacketAttributes.Corrupt;
            }

            MediaTime time = new(pts * _scale, _rate);
            packet = new PacketView(new PacketInfo(0, time, time, new MediaTime(_scale, _rate), flags, new ByteRange(at + 12, got), got), data);
            return true;
        }

        packet = default;
        return false;
    }

    private bool IsKey(ReadOnlySpan<byte> frame)
    {
        switch (_tracks[0].Codec.Name)
        {
            case "vp8":
                return Vp8KeyFrameHeader.IsKeyFrame(frame);
            case "vp9":
                return Vp9FrameHeader.TryParse(frame, out Vp9FrameHeader v) && v.KeyFrame;
            case "av1":
                {
                    int pos = 0;
                    while (pos < frame.Length && ObuHeader.TryParse(frame[pos..], out ObuHeader h))
                    {
                        if (h.Type == ObuType.SequenceHeader)
                        {
                            return true;
                        }

                        pos += h.TotalSize;
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>IVF has no index: frame headers are walked from the start (constant memory) until the keyframe is found.</remarks>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!_input.CanSeek)
        {
            throw new NotSupportedException("A forward-only IVF stream cannot be repositioned.");
        }

        _eof = false;
        _input.Seek(_next);
        long keyOffset = _next;
        MediaTime keyTime = MediaTime.Zero;
        bool found = false;
        while (TryReadPacket(out PacketView p))
        {
            if (!p.Info.IsKeyframe)
            {
                continue;
            }

            if (mode == SeekMode.PreviousKeyframe && p.Info.Pts > target && found)
            {
                break;
            }

            keyOffset = p.Info.SourceRange.Offset - 12;
            keyTime = p.Info.Pts;
            found = true;
            if (mode == SeekMode.NextKeyframe && p.Info.Pts >= target)
            {
                break;
            }
        }

        _eof = false;
        _input.Seek(keyOffset);
        return keyTime;
    }

    /// <inheritdoc />
    public void Dispose() => _input.Dispose();

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        if (_metadata is null)
        {
            DefectLog defects = new();
            FactsBuilder facts = new(defects);
            if (_tracks[0].Video is { Width: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "IVF header", $"{v.Width}x{v.Height}", "width and height from the file header"));
            }

            _metadata = new MediaMetadata([], [], facts.Build(), defects);
        }

        return _metadata;
    }
}

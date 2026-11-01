using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Mpeg;
using RecursiveAI.OneMedia.Sniffing;

namespace RecursiveAI.OneMedia.MpegPs;

/// <summary>
/// Reads MPEG program streams and DVD VOB files (ISO/IEC 13818-1 §2.5, ISO/IEC 11172-1 for MPEG-1 systems streams). Each packet
/// is one access unit or codec frame with times on the 90 kHz clock as stated. Streams are found by reading a bounded prefix
/// (at most <see cref="ProbeLimitBytes"/>); a stream that first appears later is reported as a defect and skipped.
/// </summary>
/// <remarks>
/// Private stream 1 sub-streams (AC-3, E-AC-3, DTS, LPCM, DVD sub-pictures) are separate tracks, and private stream 2 (the
/// DVD navigation packs) is a data track whose packets are the navigation payloads, unchanged. A track's <see cref="MediaTrack.Id"/>
/// is <c>0x100 | stream_id</c> for ordinary streams and the sub-stream id for private stream 1, as the usual probing tools number them.
/// </remarks>
public sealed class MpegPsDemuxer : IDemuxer, IMetadataProvider
{
    /// <summary>The most bytes read from the front to find the streams.</summary>
    public const int ProbeLimitBytes = 5_000_000;

    /// <summary>How much of the end of a seekable source is read to find the duration.</summary>
    public const int TailBytes = 1 << 20;

    private const int MinimumProbeBytes = 256 * 1024;

    private readonly ByteInput _input;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly PacketQueue _queue;
    private readonly List<StreamState> _streams = [];
    private readonly Dictionary<int, byte> _psmTypes = [];
    private MediaTrack[] _tracks = [];
    private bool[] _enabled = [];
    private bool _frozen;
    private bool _eof;
    private bool _mpeg1;
    private bool _versionKnown;
    private long _scrStart = -1;
    private MediaTime _duration = MediaTime.Unknown;
    private bool _reportedLate;
    private long _firstPack = -1;
    private MediaMetadata? _metadata;
    internal int WatchTrack { get; set; } = -1;

    internal long WatchedPts { get; set; } = -1;

    private sealed class StreamState
    {
        public required int Key;          // stream_id, or 0x10000 | sub-stream id for private stream 1
        public required int Id;
        public required TrackAssembler Assembler;
        public required TrackKind Kind;
        public required CodecId Codec;
        public required string Tag;
        public int Skip;                  // sub-stream header bytes ahead of the payload
        public TimestampUnwrapper Unwrap;
        public long FirstPts = -1, LastPts = -1, LastDuration;
    }

    private MpegPsDemuxer(ByteInput input, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _limits = options.Limits;
        _defects = defects;
        _queue = new PacketQueue(options.Limits);
    }

    /// <summary>Opens a seekable program stream.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer, positioned at the first packet.</returns>
    /// <exception cref="MediaFormatException">The data does not start with a pack header.</exception>
    public static MpegPsDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    /// <summary>Opens a program stream that can only be read once. Seeking and the duration are unavailable.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data does not start with a pack header.</exception>
    public static MpegPsDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    private static MpegPsDemuxer Create(ByteInput input, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        try
        {
            ReadOnlySpan<byte> head = input.Peek(4);
            if (head.Length < 4 || head[0] != 0 || head[1] != 0 || head[2] != 1 || head[3] != 0xBA)
            {
                throw new MediaFormatException("The data does not start with a program stream pack header.") { Offset = 0, Kind = DefectKind.InvalidValue };
            }

            MpegPsDemuxer d = new(input, options, defects);
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
    public MediaFormatId Format => MediaFormatId.MpegPs;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc />
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects => _defects;

    /// <inheritdoc />
    public bool CanSeek => _input.CanSeek;

    /// <summary>True for an MPEG-1 system stream (ISO/IEC 11172-1) rather than an MPEG-2 program stream.</summary>
    public bool IsMpeg1 => _mpeg1;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        if ((uint)trackIndex >= (uint)_enabled.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        _enabled[trackIndex] = enabled;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        while (true)
        {
            while (_queue.TryDequeue(out PacketInfo info, out ReadOnlySpan<byte> data))
            {
                if (_enabled[info.TrackIndex])
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

            if (!Pump())
            {
                foreach (StreamState s in _streams)
                {
                    s.Assembler.Flush();
                }

                _eof = true;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Bisects on the presentation time of the first video stream (or the first stream) between pack headers, then reads forward to a keyframe at or before the target.</remarks>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!_input.CanSeek)
        {
            throw new NotSupportedException("A forward-only program stream cannot be repositioned.");
        }

        if (_streams.Count == 0)
        {
            return MediaTime.Unknown;
        }

        int reference = Math.Max(0, _streams.FindIndex(s => s.Kind == TrackKind.Video && s.FirstPts >= 0));
        if (_streams[reference].FirstPts < 0)
        {
            ResetForSeek(0);
            return MediaTime.Unknown;
        }

        long t = target.Rescale(TrackAssembler.Clock, MediaRounding.Floor).Ticks;
        long lo = 0, hi = _input.Length;
        WatchTrack = reference;
        while (hi - lo > (2L << 20))
        {
            long mid = lo + ((hi - lo) / 2);
            ResetForSeek(mid);
            WatchedPts = -1;
            for (int i = 0; i < 4000 && WatchedPts < 0 && Pump(); i++)
            {
            }

            if (WatchedPts < 0 || WatchedPts > t)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        WatchTrack = -1;
        long from = lo;
        long keyPos = -1, keyPts = -1;
        while (true)
        {
            ResetForSeek(from);
            keyPos = -1;
            bool done = false;
            while (!done && TryReadPacket(out PacketView p))
            {
                if (p.Info.TrackIndex != reference)
                {
                    continue;
                }

                long pts = p.Info.Pts.IsKnown ? p.Info.Pts.Ticks : p.Info.Dts.IsKnown ? p.Info.Dts.Ticks : -1;
                if (pts < 0)
                {
                    continue;
                }

                if (p.Info.IsKeyframe && p.Info.SourceRange.Offset >= 0)
                {
                    if (mode == SeekMode.PreviousKeyframe && pts <= t)
                    {
                        keyPos = p.Info.SourceRange.Offset;
                        keyPts = pts;
                    }
                    else if (mode == SeekMode.PreviousKeyframe)
                    {
                        done = true;
                    }
                    else if (pts >= t)
                    {
                        keyPos = p.Info.SourceRange.Offset;
                        keyPts = pts;
                        done = true;
                    }
                }
                else if (mode == SeekMode.PreviousKeyframe && pts > t + (TrackAssembler.Clock * 2))
                {
                    done = true;
                }
            }

            if (keyPos >= 0 || from <= 0)
            {
                break;
            }

            from = Math.Max(0, from - (4L << 20));
        }

        if (keyPos < 0)
        {
            ResetForSeek(0);
            return MediaTime.Unknown;
        }

        ResetForSeek(keyPos);
        return new MediaTime(keyPts, TrackAssembler.Clock);
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
            if (_duration.IsKnown)
            {
                facts.Duration.Add(0, new FactCandidate<MediaTime>(_duration, "container", _duration.ToString(), "last presentation time read from the end of the stream minus the first one"));
            }

            foreach (MediaTrack t in _tracks)
            {
                if (t.Video is { Width: > 0 } v)
                {
                    facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "video parameters", $"{v.Width}x{v.Height}", "size from the first sequence header or parameter set"));
                    break;
                }
            }

            _metadata = new MediaMetadata([], [], facts.Build(), defects);
        }

        return _metadata;
    }

    private void ResetForSeek(long offset)
    {
        _queue.Clear();
        _eof = false;
        foreach (StreamState s in _streams)
        {
            s.Assembler.Reset();
            s.Unwrap.Reset();
            if (s.FirstPts >= 0)
            {
                s.Unwrap.Seed(s.FirstPts);
            }
        }

        _input.Seek(offset);
        SyncToStartCode();
    }

    // ---- reading ----

    private void Probe(CancellationToken cancellationToken)
    {
        long start = _input.Position;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Pump())
            {
                foreach (StreamState s in _streams)
                {
                    s.Assembler.Flush();
                }

                _eof = true;
                break;
            }

            long read = _input.Position - start;
            if (read >= ProbeLimitBytes)
            {
                break;
            }

            if (read >= MinimumProbeBytes && _streams.Count > 0 && _streams.TrueForAll(s => s.Assembler.IsDescribed))
            {
                break;
            }
        }

        Freeze();
    }

    private void Freeze()
    {
        _frozen = true;
        List<MediaTrack> tracks = [];
        foreach (StreamState s in _streams)
        {
            tracks.Add(BuildTrack(s, tracks.Count));
            CodecParameters? p = s.Assembler.Parameters;
            if (p is { SampleRate: > 0, SamplesPerFrame: > 0 })
            {
                s.LastDuration = (long)p.SamplesPerFrame * TrackAssembler.Clock / p.SampleRate;
            }
            else if (p is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 })
            {
                s.LastDuration = ((TrackAssembler.Clock * p.FrameRateDenominator) + (p.FrameRateNumerator / 2)) / p.FrameRateNumerator;
            }
        }

        _tracks = [.. tracks];
        _enabled = new bool[_tracks.Length];
        Array.Fill(_enabled, true);
        if (_input.CanSeek)
        {
            ReadTail();
        }
    }

    private static MediaTrack BuildTrack(StreamState s, int index)
    {
        TrackAssembler a = s.Assembler;
        TrackKind kind = s.Kind;
        CodecId codec = s.Codec;
        if (a is ProbingAssembler { Resolved: { } found })
        {
            kind = found.Kind;
            codec = found.Codec;
            a = ((ProbingAssembler)a).Inner ?? a;
        }

        if (!a.DetectedCodec.IsUnknown)
        {
            codec = a.DetectedCodec;
        }

        CodecParameters? p = a.Parameters;
        VideoTrackInfo? video = kind == TrackKind.Video && p is not null
            ? new VideoTrackInfo(p.Width, p.Height, 0, Math.Max(1, p.SarWidth), Math.Max(1, p.SarHeight), FrameRates.Reduce(p.FrameRateNumerator, p.FrameRateDenominator).Numerator, FrameRates.Reduce(p.FrameRateNumerator, p.FrameRateDenominator).Denominator)
            : null;
        AudioTrackInfo? audio = kind == TrackKind.Audio && p is not null ? new AudioTrackInfo(p.SampleRate, p.Channels) : null;
        return new MediaTrack
        {
            Index = index,
            Id = s.Id,
            Kind = kind,
            Codec = codec,
            CodecTag = s.Tag,
            Configuration = a.Configuration.IsEmpty ? default : new CodecConfiguration(a.Configuration, "AudioSpecificConfig"),
            Timescale = TrackAssembler.Clock,
            Edit = new TrackEditInfo(StartTime: s.FirstPts >= 0 ? new MediaTime(s.FirstPts, TrackAssembler.Clock) : default),
            Video = video,
            Audio = audio,
            BitRate = p?.BitRate ?? 0,
        };
    }

    private void ReadTail()
    {
        IMediaSource? source = _input.Random;
        long length = _input.Length;
        if (source is null || length < 4096)
        {
            return;
        }

        long from = Math.Max(0, length - TailBytes);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)(length - from));
        try
        {
            int n = source.Read(from, buffer.AsSpan(0, (int)(length - from)));
            ReadOnlySpan<byte> data = buffer.AsSpan(0, n);
            // Resume at a pack header so the packets that follow can be told apart from payload bytes.
            int p = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0xBA]);
            while (p >= 0 && p < data.Length - 6)
            {
                int at = p + 4;
                int packLength = (data[at] & 0xC0) == 0x40 ? 10 + (data[Math.Min(data.Length - 1, at + 9)] & 7) : 8;
                int q = p + 4 + packLength;
                // Walk the PES packets that follow this pack header.
                while (q + 9 < data.Length && data[q] == 0 && data[q + 1] == 0 && data[q + 2] == 1 && data[q + 3] >= 0xBB)
                {
                    int id = data[q + 3];
                    int len = BinaryPrimitives.ReadUInt16BigEndian(data[(q + 4)..]);
                    int sub = -1;
                    if (PesHeader.TryParse(data[q..], _mpeg1, out PesHeader h) > 0 && h.Pts >= 0)
                    {
                        if (id == 0xBD && q + h.HeaderLength < data.Length)
                        {
                            sub = data[q + h.HeaderLength];
                        }

                        StreamState? s = Find(id, sub);
                        if (s is not null && s.FirstPts >= 0)
                        {
                            TimestampUnwrapper u = default;
                            u.Seed(s.FirstPts);
                            long pts = u.Unwrap(h.Pts);
                            if (pts > s.LastPts)
                            {
                                s.LastPts = pts;
                            }
                        }
                    }

                    if (len == 0)
                    {
                        break;
                    }

                    q += 6 + len;
                }

                int next = p + 4 < data.Length ? data[(p + 4)..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0xBA]) : -1;
                p = next < 0 ? -1 : p + 4 + next;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        // The presentation range is the audio and video range: a subtitle or data stream that starts early does not move it.
        MediaTime start = MediaTime.Unknown, end = MediaTime.Unknown, anyStart = MediaTime.Unknown, anyEnd = MediaTime.Unknown;
        List<MediaTrack> updated = [];
        for (int i = 0; i < _streams.Count; i++)
        {
            StreamState s = _streams[i];
            MediaTrack track = _tracks[i];
            if (s.FirstPts >= 0 && s.LastPts >= s.FirstPts)
            {
                long last = s.LastPts + s.LastDuration;
                track = track with { Duration = new MediaTime(last - s.FirstPts, TrackAssembler.Clock) };
                MediaTime a = new(s.FirstPts, TrackAssembler.Clock), b = new(last, TrackAssembler.Clock);
                anyStart = anyStart.IsKnown ? MediaTime.Min(anyStart, a) : a;
                anyEnd = anyEnd.IsKnown ? MediaTime.Max(anyEnd, b) : b;
                if (track.Kind is TrackKind.Audio or TrackKind.Video)
                {
                    start = start.IsKnown ? MediaTime.Min(start, a) : a;
                    end = end.IsKnown ? MediaTime.Max(end, b) : b;
                }
            }

            updated.Add(track);
        }

        _tracks = [.. updated];
        if (!start.IsKnown)
        {
            (start, end) = (anyStart, anyEnd);
        }

        if (start.IsKnown && end.IsKnown)
        {
            _duration = end - start;
        }
    }

    private StreamState? Find(int id, int sub)
    {
        foreach (StreamState s in _streams)
        {
            if (id == 0xBD ? s.Key == (0x10000 | sub) : s.Key == id)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Moves to the next start code prefix 00 00 01, reporting skipped bytes once.</summary>
    private void SyncToStartCode()
    {
        long gapStart = -1;
        while (true)
        {
            ReadOnlySpan<byte> w = _input.Peek(4096);
            if (w.Length < 4)
            {
                _input.Advance(w.Length);
                return;
            }

            int i = w[..^3].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (i == 0)
            {
                if (gapStart >= 0)
                {
                    _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, gapStart, $"{_input.Position - gapStart} bytes between packets are skipped.", "ps.skipped-bytes");
                }

                return;
            }

            if (gapStart < 0)
            {
                gapStart = _input.Position;
            }

            _input.Advance(i < 0 ? w.Length - 3 : i);
        }
    }

    internal bool Pump()
    {
        SyncToStartCode();
        ReadOnlySpan<byte> h = _input.Peek(14);
        if (h.Length < 4)
        {
            return false;
        }

        long offset = _input.Position;
        int code = h[3];
        switch (code)
        {
            case 0xBA:
                {
                    if (h.Length < 14)
                    {
                        _input.Advance(h.Length);
                        return false;
                    }

                    bool v2 = (h[4] & 0xC0) == 0x40;
                    if (!_versionKnown)
                    {
                        _versionKnown = true;
                        _mpeg1 = !v2;
                        _firstPack = offset;
                    }

                    // §2.5.3.4: the pack header is 14 bytes plus stuffing in MPEG-2; 12 bytes in MPEG-1.
                    int size = v2 ? 14 + (h[13] & 7) : 12;
                    if (_scrStart < 0)
                    {
                        _scrStart = 0;
                    }

                    _input.Advance(size);
                    return true;
                }

            case 0xB9:
                _input.Advance(4);
                return true;
            default:
                {
                    if (code < 0xBB)
                    {
                        // 0x00-0xB8 are video start codes: a stream that lost its packet framing. Skip the code and look again.
                        _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "A start code outside the system layer where a packet should begin.", "ps.unexpected-start-code");
                        _input.Advance(4);
                        return true;
                    }

                    if (h.Length < 6)
                    {
                        _input.Advance(h.Length);
                        return false;
                    }

                    int length = BinaryPrimitives.ReadUInt16BigEndian(h[4..]);
                    if (length == 0 && code != 0xBB)
                    {
                        // An unbounded packet (video only, §2.4.3.7): it runs to the next start code of a pack.
                        length = UnboundedLength(offset);
                    }

                    ReadOnlySpan<byte> packet = _input.Peek(6 + length);
                    if (packet.Length < 6 + length)
                    {
                        _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "The stream ends inside a PES packet.", "ps.truncated-packet");
                        if (packet.Length > 6)
                        {
                            HandlePacket(code, packet, offset);
                        }

                        _input.Advance(packet.Length);
                        return false;
                    }

                    HandlePacket(code, packet, offset);
                    _input.Advance(6 + length);
                    return true;
                }
        }
    }

    private int UnboundedLength(long offset)
    {
        _ = offset;
        ReadOnlySpan<byte> w = _input.Peek(65536);
        int i = w.Length > 6 ? w[6..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0xBA]) : -1;
        return i < 0 ? Math.Max(0, w.Length - 6) : i;
    }

    private void HandlePacket(int id, ReadOnlySpan<byte> packet, long offset)
    {
        if (id == 0xBB)
        {
            return; // system header: rate and buffer bounds, nothing a demuxer needs
        }

        if (id == 0xBC)
        {
            ReadMap(packet);
            return;
        }

        if (id is 0xBE or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF)
        {
            return; // padding, ECM, EMM, DSM-CC, ancillary, program stream directory
        }

        int r = PesHeader.TryParse(packet, _mpeg1, out PesHeader h);
        if (r <= 0)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "A PES packet header is malformed; the packet is skipped.", "ps.pes-header");
            return;
        }

        ReadOnlySpan<byte> payload = packet[r..];
        int sub = -1;
        if (id == 0xBD)
        {
            if (payload.IsEmpty)
            {
                return;
            }

            sub = payload[0];
        }

        StreamState? s = Find(id, sub);
        if (s is null)
        {
            if (_frozen)
            {
                if (!_reportedLate)
                {
                    _reportedLate = true;
                    _defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, offset, "A stream first appears after the probed prefix and is not exposed as a track.", "ps.late-stream");
                }

                return;
            }

            s = AddStream(id, sub);
            if (s is null)
            {
                return;
            }
        }

        if (s.Skip > 0)
        {
            payload = payload.Length > s.Skip ? payload[s.Skip..] : default;
        }

        long pts = h.Pts >= 0 ? s.Unwrap.Unwrap(h.Pts) : -1;
        long dts = h.Dts >= 0 ? s.Unwrap.Unwrap(h.Dts) : -1;
        int index = _streams.IndexOf(s);
        if (!_enabled.AsSpan().IsEmpty && !_enabled[index])
        {
            return;
        }

        if (pts >= 0 || dts >= 0)
        {
            long first = pts >= 0 ? pts : dts;
            if (!_frozen && (s.FirstPts < 0 || (first < s.FirstPts && first > s.FirstPts - (1L << 31))))
            {
                s.FirstPts = first;
            }

            if (index == WatchTrack)
            {
                WatchedPts = first;
            }
        }

        s.Assembler.Push(payload, new PesStamp(pts, dts, offset, false), true);
    }

    private StreamState? AddStream(int id, int sub)
    {
        _limits.CheckTracks(_streams.Count + 1, -1);
        int index = _streams.Count;
        int key = id == 0xBD ? 0x10000 | sub : id;
        TrackAssembler a;
        TrackKind kind;
        CodecId codec;
        string tag;
        int skip = 0;
        int trackId = 0x100 | id;
        _psmTypes.TryGetValue(id, out byte psmType);
        if (id is >= 0xE0 and <= 0xEF)
        {
            kind = TrackKind.Video;
            switch (psmType)
            {
                case 0x1B:
                    a = new NalVideoAssembler(NalCodec.H264, index, _queue, _limits, _defects);
                    codec = CodecId.H264;
                    tag = "h264";
                    break;
                case 0x24:
                    a = new NalVideoAssembler(NalCodec.Hevc, index, _queue, _limits, _defects);
                    codec = CodecId.Hevc;
                    tag = "hevc";
                    break;
                default:
                    a = new ProbingAssembler(index, _queue, _limits, _defects);
                    codec = CodecId.Mpeg2Video;
                    tag = "video";
                    break;
            }
        }
        else if (id is >= 0xC0 and <= 0xDF)
        {
            kind = TrackKind.Audio;
            codec = CodecId.Mp3;
            tag = "mpegaudio";
            a = psmType == 0x0F ? new AdtsAssembler(index, _queue, _limits, _defects) : new MpegAudioAssembler(index, _queue, _limits, _defects);
            if (psmType == 0x0F)
            {
                codec = CodecId.Aac;
            }
        }
        else if (id == 0xBD)
        {
            trackId = sub;
            // DVD-Video sub-stream ids (private stream 1): 0x20-0x3F sub-pictures, 0x80-0x87 AC-3, 0x88-0x8F DTS, 0xA0-0xA7 LPCM, 0xC0-0xC7 E-AC-3.
            if (sub is >= 0x80 and <= 0x87 or >= 0xC0 and <= 0xC7)
            {
                kind = TrackKind.Audio;
                skip = 4; // sub-stream id, frame count, first access unit pointer (2)
                a = new Ac3Assembler(index, _queue, _limits, _defects);
                codec = sub >= 0xC0 ? CodecId.Eac3 : CodecId.Ac3;
                tag = "ac3";
            }
            else if (sub is >= 0x88 and <= 0x8F or >= 0x98 and <= 0x9F)
            {
                kind = TrackKind.Audio;
                skip = 4;
                a = new PesPassthroughAssembler(index, _queue, _limits, _defects, keyframes: true);
                codec = new CodecId("dts");
                tag = "dts";
            }
            else if (sub is >= 0xA0 and <= 0xAF)
            {
                kind = TrackKind.Audio;
                skip = 7; // as above plus three bytes of sample format
                a = new PesPassthroughAssembler(index, _queue, _limits, _defects, keyframes: true);
                codec = new CodecId("pcm-dvd");
                tag = "lpcm";
            }
            else if (sub is >= 0x20 and <= 0x3F)
            {
                kind = TrackKind.Subtitle;
                skip = 1;
                a = new PesPassthroughAssembler(index, _queue, _limits, _defects, keyframes: true, dvdSubpicture: true);
                codec = new CodecId("dvd-subpicture");
                tag = "dvd-subpicture";
            }
            else
            {
                kind = TrackKind.Data;
                a = new PesPassthroughAssembler(index, _queue, _limits, _defects, keyframes: true);
                codec = CodecId.Unknown;
                tag = "private-1";
            }
        }
        else if (id == 0xBF)
        {
            kind = TrackKind.Data;
            a = new PesPassthroughAssembler(index, _queue, _limits, _defects, keyframes: true);
            codec = new CodecId("dvd-nav");
            tag = "private-2";
        }
        else
        {
            return null;
        }

        StreamState s = new() { Key = key, Id = trackId, Assembler = a, Kind = kind, Codec = codec, Tag = tag, Skip = skip };
        _streams.Add(s);
        return s;
    }

    // ISO/IEC 13818-1 §2.5.4.1: program_stream_map. Only the elementary stream map is used, to learn stream types the stream ids do not give.
    private void ReadMap(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 16)
        {
            return;
        }

        int infoLength = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        int p = 10 + infoLength;
        if (p + 2 > packet.Length)
        {
            return;
        }

        int mapLength = BinaryPrimitives.ReadUInt16BigEndian(packet[p..]);
        p += 2;
        int end = Math.Min(packet.Length - 4, p + mapLength);
        while (p + 4 <= end)
        {
            byte type = packet[p];
            int id = packet[p + 1];
            int len = BinaryPrimitives.ReadUInt16BigEndian(packet[(p + 2)..]);
            _psmTypes[id] = type;
            p += 4 + len;
        }
    }

}

/// <summary>The MPEG program stream format: recognises pack headers (MPEG-1 and MPEG-2) and opens program streams and VOB files.</summary>
public sealed class MpegPsFormat : IMediaFormat
{
    private readonly MpegPsSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MpegPsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MpegPsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        AsyncDemuxer.OpenAsync(source, s => MpegPsDemuxer.Open(s, options, cancellationToken), cancellationToken);
}

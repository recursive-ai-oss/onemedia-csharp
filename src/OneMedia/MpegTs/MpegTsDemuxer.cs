using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Mpeg;

namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>
/// Reads MPEG transport streams (ISO/IEC 13818-1 / ITU-T H.222.0): 188-byte packets, 192-byte M2TS packets with the four-byte
/// TP_extra_header in front, and 204-byte packets with Reed-Solomon parity behind. Each packet it hands out is one access unit
/// or codec frame, with times in the 90 kHz clock exactly as the stream states them (the first presentation time is not
/// moved to zero; <see cref="StartTime"/> says where the streams begin).
/// </summary>
/// <remarks>
/// Opening reads only a bounded prefix: up to <see cref="ProbeLimitBytes"/>, and normally far less, because it stops as soon as every
/// stream is described. With a seekable source it also reads the last <see cref="TailBytes"/> to learn the duration. A transport
/// stream has no index, so <see cref="Seek"/> bisects on presentation time and then scans forward to a keyframe.
/// </remarks>
public sealed class MpegTsDemuxer : IDemuxer, IMetadataProvider
{
    /// <summary>The most bytes the open reads from the front while looking for the program tables and first frames.</summary>
    public const int ProbeLimitBytes = 5_000_000;

    /// <summary>How much of the end of a seekable source is read to find the duration.</summary>
    public const int TailBytes = 1 << 20;

    private const int MaxHeaderBytes = 320;

    private readonly ByteInput _input;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly PacketQueue _queue;
    private readonly PidState?[] _pids = new PidState?[8192];
    private readonly List<ProgramState> _programs = [];
    private readonly List<TrackState> _trackStates = [];
    private readonly Action<ReadOnlySpan<byte>, long> _onSection;
    private readonly int _packetSize;
    private readonly int _prefix;
    private readonly long _firstPacket;
    private MediaTrack[] _tracks = [];
    private bool[] _enabled = [];
    private bool _eof;
    private bool _frozen;
    private MediaTime _duration = MediaTime.Unknown;
    private MediaTime _startTime = MediaTime.Unknown;
    private long _firstPcr = -1, _firstPcrPos, _lastPcr = -1, _lastPcrPos;
    private int _transportStreamId = -1;
    private MediaMetadata? _metadata;
    private readonly TsServiceInfo _info = new();

    private enum PidKind { None, Pat, Pmt, Sdt, Eit, Pes }

    private sealed class PidState(int pid, PidKind kind)
    {
        public int Pid = pid;
        public PidKind Kind = kind;
        public int Cc = -1;
        public SectionAssembler? Sections;
        public int TrackIndex = -1;
        public int ProgramIndex = -1;
        public TimestampUnwrapper Unwrap;
        public bool PesOpen;
        public int Remaining;
        public readonly byte[] Header = new byte[MaxHeaderBytes];
        public int HeaderLength;
        public bool HeaderPending;
        public bool RandomAccess;
        public long HeaderOffset;
    }

    internal sealed class ProgramState
    {
        public int Number;
        public int PmtPid;
        public int PcrPid = -1;
        public bool PmtSeen;
        public List<TransportStreamEntry> Streams = [];
        public List<int> TrackIndexes = [];
        public string? ServiceName, ServiceProvider;
    }

    internal sealed class TrackState
    {
        public required int Pid;
        public required TsStreamKind Kind;
        public required TrackAssembler Assembler;
        public int StreamType;
        public int Program;
        public long FirstPts = -1, LastPts = -1;
        public long LastDuration;
    }

    private MpegTsDemuxer(ByteInput input, MediaOpenOptions options, DefectLog defects, int packetSize, int prefix, long firstPacket)
    {
        _input = input;
        _limits = options.Limits;
        _defects = defects;
        _queue = new PacketQueue(options.Limits);
        _packetSize = packetSize;
        _prefix = prefix;
        _firstPacket = firstPacket;
        _onSection = OnSection;
        _pids[0] = new PidState(0, PidKind.Pat) { Sections = new SectionAssembler() };
        _pids[0x11] = new PidState(0x11, PidKind.Sdt) { Sections = new SectionAssembler() };
        _pids[0x12] = new PidState(0x12, PidKind.Eit) { Sections = new SectionAssembler() };
    }

    /// <summary>Opens a seekable transport stream.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer, positioned at the first packet.</returns>
    /// <exception cref="MediaFormatException">No transport packet sync was found in the first 64 KiB.</exception>
    public static MpegTsDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    /// <summary>Opens a transport stream that can only be read once, front to back. Seeking and the duration are unavailable.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the front of the stream is read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No transport packet sync was found in the first 64 KiB.</exception>
    public static MpegTsDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new ByteInput(source, options.LeaveOpen), options, cancellationToken);
    }

    private static MpegTsDemuxer Create(ByteInput input, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        try
        {
            if (!FindSync(input, out int size, out int prefix, out long first))
            {
                throw new MediaFormatException("No MPEG transport stream packets were found in the first 64 KiB.") { Offset = 0, Kind = DefectKind.InvalidValue };
            }

            if (!input.Seek(first))
            {
                throw new MediaFormatException("The transport stream cannot be positioned at its first packet.");
            }

            MpegTsDemuxer d = new(input, options, defects, size, prefix, first);
            if (first > 0)
            {
                defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, 0, $"{first} bytes before the first transport packet are skipped.", "ts.leading-bytes");
            }

            d.Probe(cancellationToken);
            return d;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>Finds the packet size and where packets start by looking for 0x47 at a constant spacing (ISO/IEC 13818-1 §2.4.3.2; 192 = M2TS, 204 = with parity).</summary>
    private static bool FindSync(ByteInput input, out int size, out int prefix, out long first)
    {
        ReadOnlySpan<byte> head = input.Peek(64 * 1024);
        ReadOnlySpan<int> sizes = [188, 192, 204];
        int best = -1;
        for (int start = 0; start < head.Length && best < 0; start++)
        {
            if (head[start] != 0x47)
            {
                continue;
            }

            foreach (int s in sizes)
            {
                int confirmed = 0;
                int wanted = (head.Length - start) / s;
                wanted = Math.Min(wanted, 6);
                for (int k = 1; k < wanted; k++)
                {
                    if (head[start + (k * s)] != 0x47)
                    {
                        confirmed = -1;
                        break;
                    }

                    confirmed++;
                }

                // Two further sync bytes at the same spacing are enough; a file of two packets needs one.
                if (confirmed >= Math.Min(2, Math.Max(1, wanted - 1)) && confirmed >= 1)
                {
                    best = start;
                    size = s;
                    prefix = s == 192 ? 4 : 0;
                    first = start - prefix;
                    if (first < 0)
                    {
                        // The sync byte of the first M2TS packet is at offset 4 at the earliest.
                        continue;
                    }

                    return true;
                }
            }
        }

        size = prefix = 0;
        first = 0;
        return false;
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.MpegTs;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc />
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects => _defects;

    /// <inheritdoc />
    public bool CanSeek => _input.CanSeek;

    /// <summary>The size of one transport packet in the file: 188, 192 (M2TS) or 204.</summary>
    public int PacketSize => _packetSize;

    /// <summary>The programs announced by the PAT, with their streams and DVB service names.</summary>
    public IReadOnlyList<TransportStreamProgram> Programs
    {
        get
        {
            List<TransportStreamProgram> list = [];
            foreach (ProgramState p in _programs)
            {
                list.Add(new TransportStreamProgram(p.Number, p.PmtPid, p.PcrPid, p.Streams, p.TrackIndexes, p.ServiceName, p.ServiceProvider));
            }

            return list;
        }
    }

    /// <summary>The earliest presentation or decode time of any stream, as read from the front of the file, or unknown.</summary>
    public MediaTime StartTime => _startTime;

    /// <summary>The average multiplex bit rate in bits per second from the first and last program clock references, or 0 when it cannot be told.</summary>
    public long BitRate
    {
        get
        {
            if (_firstPcr < 0 || _lastPcr <= _firstPcr || _lastPcrPos <= _firstPcrPos)
            {
                return 0;
            }

            // PCR counts a 27 MHz clock (§2.4.2.2).
            return (long)((System.Numerics.BigInteger)(_lastPcrPos - _firstPcrPos) * 8 * 27_000_000 / (_lastPcr - _firstPcr));
        }
    }

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
                FlushAll();
                _eof = true;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Bisects on the presentation time of the first video stream (or the first stream), then scans forward for a keyframe at or before the target. The scan reads at most a few GOPs.</remarks>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!_input.CanSeek)
        {
            throw new NotSupportedException("A forward-only transport stream cannot be repositioned.");
        }

        TsSeeker seeker = new(this);
        return seeker.Seek(target, mode);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _input.Dispose();
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
                FlushAll();
                _eof = true;
                break;
            }

            long read = _input.Position - start;
            if (read >= ProbeLimitBytes || ProbeComplete(read))
            {
                break;
            }
        }

        Freeze();
    }

    private bool ProbeComplete(long read)
    {
        if (_programs.Count == 0)
        {
            return false;
        }

        foreach (ProgramState p in _programs)
        {
            if (!p.PmtSeen)
            {
                return false;
            }
        }

        // Streams that never show their first frame are given a while longer, then left undescribed.
        foreach (TrackState t in _trackStates)
        {
            if (!t.Assembler.IsDescribed && read < ProbeLimitBytes)
            {
                return false;
            }
        }

        // SDT and EIT repeat every second or two; a short extra wait lets the service name arrive.
        return read > 188 * 400 || _info.HasServiceNames;
    }

    private void Freeze()
    {
        List<MediaTrack> tracks = [];
        foreach (TrackState t in _trackStates)
        {
            tracks.Add(BuildTrack(t, tracks.Count));
        }

        _tracks = [.. tracks];
        _enabled = new bool[_tracks.Length];
        Array.Fill(_enabled, true);
        _frozen = true;
        foreach (TrackState t in _trackStates)
        {
            CodecParameters? cp = t.Assembler.Parameters;
            if (cp is { SampleRate: > 0, SamplesPerFrame: > 0 })
            {
                t.LastDuration = (long)cp.SamplesPerFrame * TrackAssembler.Clock / cp.SampleRate;
            }
            else if (cp is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 })
            {
                t.LastDuration = ((TrackAssembler.Clock * cp.FrameRateDenominator) + (cp.FrameRateNumerator / 2)) / cp.FrameRateNumerator;
            }
        }
        MediaTime start = MediaTime.Unknown;
        foreach (TrackState t in _trackStates)
        {
            if (t.FirstPts >= 0)
            {
                MediaTime s = new(t.FirstPts, TrackAssembler.Clock);
                start = start.IsKnown ? MediaTime.Min(start, s) : s;
            }
        }

        _startTime = start;
        if (_input.CanSeek)
        {
            ReadTail();
        }
    }

    private static MediaTrack BuildTrack(TrackState t, int index)
    {
        TrackAssembler a = t.Assembler;
        CodecParameters? p = a.Parameters;
        CodecId codec = t.Kind.Codec;
        TrackKind trackKind = t.Kind.Kind;
        if (a is ProbingAssembler { Resolved: { } found })
        {
            trackKind = found.Kind;
            codec = found.Codec;
            a = ((ProbingAssembler)a).Inner ?? a;
        }

        if (!a.DetectedCodec.IsUnknown && t.Kind.Codec.Name != "mpeg2video")
        {
            codec = a.DetectedCodec;
        }

        VideoTrackInfo? video = null;
        AudioTrackInfo? audio = null;
        if (trackKind == TrackKind.Video && p is not null)
        {
            video = new VideoTrackInfo(p.Width, p.Height, 0, Math.Max(1, p.SarWidth), Math.Max(1, p.SarHeight), p.FrameRateNumerator, p.FrameRateDenominator);
        }
        else if (trackKind == TrackKind.Audio && p is not null)
        {
            audio = new AudioTrackInfo(p.SampleRate, p.Channels);
        }

        MediaTime duration = MediaTime.Unknown;
        if (t.FirstPts >= 0 && t.LastPts >= t.FirstPts)
        {
            duration = new MediaTime(t.LastPts - t.FirstPts + t.LastDuration, TrackAssembler.Clock);
        }

        return new MediaTrack
        {
            Index = index,
            Id = t.Pid,
            Kind = trackKind,
            Codec = codec,
            CodecTag = t.Kind.Tag,
            Configuration = a.Configuration.IsEmpty ? default : new CodecConfiguration(a.Configuration, "AudioSpecificConfig"),
            Timescale = TrackAssembler.Clock,
            Duration = duration,
            Edit = new TrackEditInfo(StartTime: t.FirstPts >= 0 ? new MediaTime(t.FirstPts, TrackAssembler.Clock) : default),
            Language = t.Kind.Language,
            Disposition = t.Kind.Disposition,
            Audio = audio,
            Video = video,
            BitRate = p?.BitRate ?? 0,
        };
    }

    /// <summary>Reads the last <see cref="TailBytes"/> for each stream's final presentation time, which with the first one gives the duration.</summary>
    private void ReadTail()
    {
        IMediaSource? source = _input.Random;
        long length = _input.Length;
        if (source is null || length < _firstPacket + (_packetSize * 4L))
        {
            return;
        }

        long from = Math.Max(_firstPacket, length - TailBytes);
        from = _firstPacket + ((from - _firstPacket) / _packetSize * _packetSize);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)(length - from));
        try
        {
            int n = source.Read(from, buffer.AsSpan(0, (int)(length - from)));
            Dictionary<int, TrackState> byPid = [];
            foreach (TrackState t in _trackStates)
            {
                byPid[t.Pid] = t;
            }

            Dictionary<int, TimestampUnwrapper> unwrap = [];
            foreach (TrackState t in _trackStates)
            {
                TimestampUnwrapper u = default;
                if (t.FirstPts >= 0)
                {
                    u.Unwrap(t.FirstPts & ((1L << 33) - 1));
                }

                unwrap[t.Pid] = u;
            }

            for (int off = 0; off + _packetSize <= n; off += _packetSize)
            {
                ReadOnlySpan<byte> ts = buffer.AsSpan(off + _prefix, 188);
                if (ts[0] != 0x47 || (ts[1] & 0x40) == 0 || (ts[3] & 0x10) == 0)
                {
                    continue;
                }

                int pid = ((ts[1] & 0x1F) << 8) | ts[2];
                if (!byPid.TryGetValue(pid, out TrackState? t) || t.FirstPts < 0)
                {
                    continue;
                }

                int pos = 4;
                if ((ts[3] & 0x20) != 0)
                {
                    pos = 5 + ts[4];
                }

                if (pos >= 188 || PesHeader.TryParse(ts[pos..], false, out PesHeader h) <= 0 || (h.Pts < 0 && h.Dts < 0))
                {
                    continue;
                }

                TimestampUnwrapper u = unwrap[pid];
                long pts = u.Unwrap(h.Pts >= 0 ? h.Pts : h.Dts);
                unwrap[pid] = u;
                if (pts > t.LastPts)
                {
                    t.LastPts = pts;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        // Without a second reading of every frame the last unit's length is its stream's frame duration, when it states one.
        MediaTime start = MediaTime.Unknown, end = MediaTime.Unknown;
        List<MediaTrack> updated = [];
        foreach (TrackState t in _trackStates)
        {
            MediaTrack track = _tracks[updated.Count];
            if (t.FirstPts >= 0 && t.LastPts >= t.FirstPts)
            {
                long last = t.LastPts + t.LastDuration;
                track = track with { Duration = new MediaTime(last - t.FirstPts, TrackAssembler.Clock) };
                MediaTime s = new(t.FirstPts, TrackAssembler.Clock), e = new(last, TrackAssembler.Clock);
                start = start.IsKnown ? MediaTime.Min(start, s) : s;
                end = end.IsKnown ? MediaTime.Max(end, e) : e;
            }

            updated.Add(track);
        }

        _tracks = [.. updated];
        if (start.IsKnown && end.IsKnown)
        {
            _duration = end - start;
        }
    }

    internal bool Pump()
    {
        ReadOnlySpan<byte> raw = _input.Peek(_packetSize);
        if (raw.Length < _packetSize)
        {
            if (raw.Length > 0)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, _input.Position, $"The stream ends {raw.Length} bytes into a transport packet.", "ts.partial-packet");
                _input.Advance(raw.Length);
            }

            return false;
        }

        long offset = _input.Position;
        if (raw[_prefix] != 0x47)
        {
            Resync(offset);
            return true;
        }

        Process(raw.Slice(_prefix, 188), offset);
        _input.Advance(_packetSize);
        return true;
    }

    private void Resync(long offset)
    {
        // A sync byte that repeats at the packet spacing twice is taken as the new alignment (§2.4.3.2 asks for four in a row for a decoder to lock).
        _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "Transport packet sync byte missing; searching for the next aligned packet.", "ts.sync-lost");
        foreach (PidState? s in _pids)
        {
            if (s is not null)
            {
                s.PesOpen = false;
                s.HeaderPending = false;
                s.Sections?.Reset();
                s.Cc = -1;
                if (s.TrackIndex >= 0)
                {
                    _trackStates[s.TrackIndex].Assembler.NoteLoss();
                }
            }
        }

        _input.Advance(1);
        while (true)
        {
            ReadOnlySpan<byte> win = _input.Peek(64 * 1024);
            if (win.Length < _packetSize + _prefix)
            {
                _input.Advance(win.Length);
                return;
            }

            int limit = win.Length - (2 * _packetSize);
            for (int i = 0; i < Math.Max(limit, 1) && i + _prefix < win.Length; i++)
            {
                if (win[i + _prefix] == 0x47 && (i + _packetSize + _prefix >= win.Length || win[i + _prefix + _packetSize] == 0x47)
                    && (i + (2 * _packetSize) + _prefix >= win.Length || win[i + _prefix + (2 * _packetSize)] == 0x47))
                {
                    _input.Advance(i);
                    return;
                }
            }

            _input.Advance(Math.Max(limit, 1));
        }
    }

    private void Process(ReadOnlySpan<byte> ts, long offset)
    {
        int pid = ((ts[1] & 0x1F) << 8) | ts[2];
        if (pid == 0x1FFF)
        {
            return; // §2.4.3.3: null packets carry nothing
        }

        PidState? st = _pids[pid];
        bool transportError = (ts[1] & 0x80) != 0;
        bool start = (ts[1] & 0x40) != 0;
        int adaptation = (ts[3] >> 4) & 3;
        int cc = ts[3] & 0xF;
        bool hasPayload = (adaptation & 1) != 0;
        int pos = 4;
        bool discontinuity = false, randomAccess = false;
        if ((adaptation & 2) != 0)
        {
            int length = ts[4];
            if (length > 183 || (adaptation == 2 && length != 183))
            {
                if (st is not null)
                {
                    _defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, offset, "Adaptation field length is inconsistent with the packet.", "ts.adaptation-length");
                }

                length = Math.Min(length, 183);
            }

            pos = 5 + length;
            if (length > 0)
            {
                int flags = ts[5];
                discontinuity = (flags & 0x80) != 0;
                randomAccess = (flags & 0x40) != 0;
                if ((flags & 0x10) != 0 && length >= 7)
                {
                    NotePcr(pid, ts[6..12], offset);
                }
            }
        }

        if (st is null)
        {
            return;
        }

        // §2.4.3.3: continuity_counter advances only on packets with a payload; one repeat of the last value is a legal duplicate.
        bool skip = false;
        if (st.Cc >= 0 && !discontinuity)
        {
            if (hasPayload)
            {
                if (cc == st.Cc)
                {
                    skip = true;
                    _defects.Add(DefectKind.Duplicate, DefectSeverity.Info, offset, $"Duplicate transport packet on PID {pid}.", "ts.duplicate-packet");
                }
                else if (cc != ((st.Cc + 1) & 0xF))
                {
                    int lost = (cc - st.Cc - 1) & 0xF;
                    _defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, offset, $"Continuity counter jumped on PID {pid}: {lost} packet(s) lost.", "ts.continuity");
                    NoteLossOn(st);
                }
            }
            else if (cc != st.Cc)
            {
                _defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, offset, $"Continuity counter changed on PID {pid} in a packet without payload.", "ts.continuity");
            }
        }
        else if (discontinuity && hasPayload)
        {
            NoteLossOn(st);
        }

        st.Cc = cc;
        if (transportError)
        {
            _defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, offset, $"Transport error indicator set on PID {pid}; the payload is dropped.", "ts.transport-error");
            NoteLossOn(st);
            return;
        }

        if (!hasPayload || skip || pos >= 188)
        {
            return;
        }

        ReadOnlySpan<byte> payload = ts[pos..];
        if (st.Kind == PidKind.Pes)
        {
            st.RandomAccess = randomAccess;
            HandlePes(st, payload, start, offset, randomAccess);
        }
        else
        {
            st.Sections?.Push(payload, start, offset, _defects, _onSection);
        }
    }

    private void NoteLossOn(PidState st)
    {
        st.Sections?.Reset();
        if (st.TrackIndex >= 0)
        {
            _trackStates[st.TrackIndex].Assembler.NoteLoss();
            st.PesOpen = st.PesOpen && st.Remaining == int.MaxValue;
        }
    }

    private void NotePcr(int pid, ReadOnlySpan<byte> b, long offset)
    {
        bool known = false;
        foreach (ProgramState p in _programs)
        {
            if (p.PcrPid == pid)
            {
                known = true;
                break;
            }
        }

        if (!known)
        {
            return;
        }

        // §2.4.3.5: program_clock_reference_base (33 bits), six reserved bits, program_clock_reference_extension (9 bits).
        long baseValue = ((long)b[0] << 25) | ((long)b[1] << 17) | ((long)b[2] << 9) | ((long)b[3] << 1) | (long)(b[4] >> 7);
        long ext = ((long)(b[4] & 1) << 8) | b[5];
        long pcr = (baseValue * 300) + ext;
        if (_firstPcr < 0)
        {
            _firstPcr = pcr;
            _firstPcrPos = offset;
        }

        if (pcr > _lastPcr || _lastPcr < 0)
        {
            _lastPcr = pcr;
            _lastPcrPos = offset;
        }
    }

    private void HandlePes(PidState st, ReadOnlySpan<byte> payload, bool start, long offset, bool randomAccess)
    {
        TrackState track = _trackStates[st.TrackIndex];
        if (!_enabled.AsSpan().IsEmpty && st.TrackIndex < _enabled.Length && !_enabled[st.TrackIndex])
        {
            return;
        }

        if (start)
        {
            st.PesOpen = false;
            st.HeaderLength = 0;
            st.HeaderPending = true;
            st.RandomAccess = randomAccess;
            st.HeaderOffset = offset;
        }
        else if (!st.HeaderPending)
        {
            if (st.PesOpen)
            {
                PushPayload(st, track, payload, default, false);
            }

            return;
        }

        int take = Math.Min(payload.Length, MaxHeaderBytes - st.HeaderLength);
        payload[..take].CopyTo(st.Header.AsSpan(st.HeaderLength));
        st.HeaderLength += take;
        int r = PesHeader.TryParse(st.Header.AsSpan(0, st.HeaderLength), false, out PesHeader h);
        if (r < 0 && st.HeaderLength < MaxHeaderBytes)
        {
            return; // the header continues in the next packet
        }

        st.HeaderPending = false;
        if (r <= 0)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, st.HeaderOffset, $"PID {st.Pid}: a payload unit start does not begin with a PES start code; the unit is skipped.", "ts.pes-start");
            return;
        }

        long pts = h.Pts >= 0 ? st.Unwrap.Unwrap(h.Pts) : -1;
        long dts = h.Dts >= 0 ? st.Unwrap.Unwrap(h.Dts) : -1;
        PesStamp stamp = new(pts, dts, st.HeaderOffset, st.RandomAccess);
        if (st.TrackIndex == WatchTrack && (pts >= 0 || dts >= 0))
        {
            WatchedPts = pts >= 0 ? pts : dts;
        }

        st.PesOpen = true;
        // A zero PES_packet_length is allowed for video (§2.4.3.7); otherwise the packet ends where the length says.
        st.Remaining = h.PacketLength == 0 ? int.MaxValue : Math.Max(0, h.PacketLength - (h.HeaderLength - 6));
        ReadOnlySpan<byte> rest = st.Header.AsSpan(r, st.HeaderLength - r);
        PushPayload(st, track, rest, stamp, true);
        if (h.Pts >= 0 || h.Dts >= 0)
        {
            long first = pts >= 0 ? pts : dts;
            if (track.FirstPts < 0 && !_frozen)
            {
                track.FirstPts = first;
            }
            else if (!_frozen && first < track.FirstPts && first > track.FirstPts - (1L << 31))
            {
                track.FirstPts = first;
            }
        }
    }

    private static void PushPayload(PidState st, TrackState track, ReadOnlySpan<byte> payload, in PesStamp stamp, bool startsPes)
    {
        if (payload.Length > st.Remaining)
        {
            payload = payload[..st.Remaining];
        }

        if (st.Remaining != int.MaxValue)
        {
            st.Remaining -= payload.Length;
            if (st.Remaining == 0)
            {
                st.PesOpen = false;
            }
        }

        if (!payload.IsEmpty || startsPes)
        {
            track.Assembler.Push(payload, stamp, startsPes);
        }
    }

    private void FlushAll()
    {
        foreach (TrackState t in _trackStates)
        {
            t.Assembler.Flush();
        }
    }

    // ---- program tables ----

    private void OnSection(ReadOnlySpan<byte> section, long offset)
    {
        if (section.Length < 8)
        {
            return;
        }

        int tableId = section[0];
        bool syntax = (section[1] & 0x80) != 0;
        if (syntax)
        {
            // Annex A: a section with the long syntax ends in a CRC-32 that makes the whole section check to zero.
            if (Mpeg2Crc.Compute(section) != 0)
            {
                _defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, offset, $"Section (table id 0x{tableId:x2}) fails its CRC-32 and is ignored.", "ts.section-crc");
                return;
            }
        }

        switch (tableId)
        {
            case 0x00:
                OnPat(section, offset);
                break;
            case 0x02:
                OnPmt(section, offset);
                break;
            case 0x42:
                _info.OnSdt(section, _programs);
                break;
            case 0x4E:
                _info.OnEit(section);
                break;
        }
    }

    private void OnPat(ReadOnlySpan<byte> s, long offset)
    {
        if (!s[1..].IsEmpty && (s[1] & 0x80) == 0)
        {
            return;
        }

        _transportStreamId = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        int end = s.Length - 4;
        for (int p = 8; p + 4 <= end; p += 4)
        {
            int number = BinaryPrimitives.ReadUInt16BigEndian(s[p..]);
            int pid = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 2)..]) & 0x1FFF;
            if (number == 0)
            {
                continue; // the network information table
            }

            bool known = false;
            foreach (ProgramState q in _programs)
            {
                known |= q.Number == number;
            }

            if (known || _frozen)
            {
                continue;
            }

            _limits.CheckTracks(_programs.Count + 1, offset);
            _programs.Add(new ProgramState { Number = number, PmtPid = pid });
            _pids[pid] ??= new PidState(pid, PidKind.Pmt) { Sections = new SectionAssembler(), ProgramIndex = _programs.Count - 1 };
        }
    }

    private void OnPmt(ReadOnlySpan<byte> s, long offset)
    {
        int number = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        ProgramState? prog = null;
        int programIndex = -1;
        for (int i = 0; i < _programs.Count; i++)
        {
            if (_programs[i].Number == number)
            {
                prog = _programs[i];
                programIndex = i;
            }
        }

        if (prog is null || prog.PmtSeen || _frozen || s.Length < 16)
        {
            return;
        }

        prog.PmtSeen = true;
        prog.PcrPid = BinaryPrimitives.ReadUInt16BigEndian(s[8..]) & 0x1FFF;
        int infoLength = BinaryPrimitives.ReadUInt16BigEndian(s[10..]) & 0x0FFF;
        int p = 12 + infoLength;
        int end = s.Length - 4;
        while (p + 5 <= end)
        {
            int streamType = s[p];
            int pid = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 1)..]) & 0x1FFF;
            int esInfo = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 3)..]) & 0x0FFF;
            if (p + 5 + esInfo > end)
            {
                _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, offset, "A PMT stream entry's descriptors run past the section.", "ts.pmt-descriptors");
                esInfo = Math.Max(0, end - p - 5);
            }

            ReadOnlyMemory<byte> descriptors = s.Slice(p + 5, esInfo).ToArray(); // PMT is read once per program, in the probe
            p += 5 + esInfo;
            prog.Streams.Add(new TransportStreamEntry(streamType, pid, descriptors));
            if (_pids[pid] is { Kind: PidKind.Pes })
            {
                prog.TrackIndexes.Add(_pids[pid]!.TrackIndex);
                continue;
            }

            if (_pids[pid] is not null)
            {
                _defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, offset, $"PID {pid} is announced as an elementary stream but is already used for program tables.", "ts.pid-conflict");
                prog.TrackIndexes.Add(-1);
                continue;
            }

            _limits.CheckTracks(_trackStates.Count + 1, offset);
            TsStreamKind kind = TsStreamTypes.Resolve(streamType, descriptors.Span);
            int index = _trackStates.Count;
            TrackAssembler assembler = CreateAssembler(kind, streamType, index);
            _trackStates.Add(new TrackState { Pid = pid, Kind = kind, Assembler = assembler, StreamType = streamType, Program = programIndex });
            _pids[pid] = new PidState(pid, PidKind.Pes) { TrackIndex = index, ProgramIndex = programIndex };
            prog.TrackIndexes.Add(index);
        }
    }

    private TrackAssembler CreateAssembler(TsStreamKind kind, int streamType, int index)
    {
        string name = kind.Codec.Name;
        return name switch
        {
            "h264" => Wire(new NalVideoAssembler(NalCodec.H264, index, _queue, _limits, _defects)),
            "hevc" => new NalVideoAssembler(NalCodec.Hevc, index, _queue, _limits, _defects),
            "vvc" => new NalVideoAssembler(NalCodec.Vvc, index, _queue, _limits, _defects),
            "mpeg2video" or "mpeg1video" => new Mpeg2VideoAssembler(index, _queue, _limits, _defects),
            "aac" => new AdtsAssembler(index, _queue, _limits, _defects),
            "ac3" or "eac3" => new Ac3Assembler(index, _queue, _limits, _defects),
            "mp3" => new MpegAudioAssembler(index, _queue, _limits, _defects),
            _ when kind.Kind == TrackKind.Data && kind.Codec.IsUnknown && (streamType == 0x06 || streamType >= 0x80) => new ProbingAssembler(index, _queue, _limits, _defects),
            _ => new PesPassthroughAssembler(index, _queue, _limits, _defects, kind.Kind != TrackKind.Video),
        };
    }

    private static NalVideoAssembler Wire(NalVideoAssembler a)
    {
        a.SeiHandler = TsServiceInfo.OnSei;
        return a;
    }

    // ---- metadata ----

    /// <inheritdoc />
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        return _metadata ??= TsMetadata.Build(this, _info, cancellationToken);
    }

    internal IReadOnlyList<ProgramState> ProgramStates => _programs;

    internal int TransportStreamId => _transportStreamId;

    // ---- support for the seeker ----

    internal int WatchTrack { get; set; } = -1;

    internal long WatchedPts { get; set; } = -1;

    internal static void AddAvchd(List<Tags.ITagContainer> containers, Metadata.FactsBuilder facts, Tags.TagIo io, DefectLog defects, CancellationToken cancellationToken)
    {
    }

    internal ByteInput Input => _input;

    internal long FirstPacketOffset => _firstPacket;

    internal int PacketBytes => _packetSize;

    internal int PrefixBytes => _prefix;

    internal long InputLength => _input.Length;

    internal IReadOnlyList<TrackState> TrackStates => _trackStates;

    internal void ResetForSeek(long offset)
    {
        _queue.Clear();
        _eof = false;
        foreach (PidState? s in _pids)
        {
            if (s is not null)
            {
                s.Cc = -1;
                s.PesOpen = false;
                s.HeaderPending = false;
                s.HeaderLength = 0;
                s.Unwrap.Reset();
                if (s.TrackIndex >= 0 && _trackStates[s.TrackIndex].FirstPts >= 0)
                {
                    s.Unwrap.Seed(_trackStates[s.TrackIndex].FirstPts);
                }
                s.Sections?.Reset();
            }
        }

        foreach (TrackState t in _trackStates)
        {
            t.Assembler.Reset();
        }

        _input.Seek(offset);
    }
}

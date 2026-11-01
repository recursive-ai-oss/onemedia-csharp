using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// Writes a Matroska or WebM file (RFC 9559, RFC 8794) from packets in decode order, copying each packet's bytes unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> EBML header, Segment, SeekHead, Info, Tracks, a <c>Void</c> reserve for tags added later, Clusters, then Cues. The
/// header is written when the first Cluster is closed, so the muxer can state a track's <c>DefaultDuration</c> when the first
/// seconds of packets show one (a packet whose duration differs from it, or any packet with a <c>DiscardPadding</c>, is written as a
/// BlockGroup; the others as SimpleBlocks). Packet times are rounded to <see cref="MatroskaMuxOptions.TimestampScale"/>.
/// </para>
/// <para>
/// <b>Live mode</b> (<see cref="MatroskaMuxOptions.Live"/>) writes the header on the first packet and each block as it arrives, with
/// unknown-size Segment and Clusters, and never goes back, so the output may be a pipe. Its <c>DefaultDuration</c> comes from the
/// declared frame rate or a codec's fixed frame length, else every packet states its own duration.
/// </para>
/// <para>
/// <b>Memory.</b> Seekable mode holds at most one Cluster (<see cref="MatroskaMuxOptions.MaxClusterBytes"/>, or one packet if larger)
/// and the Cue list, <see cref="CueBytesPerPoint"/> bytes per cue point (plus about 30 bytes when written). Live mode holds no payload.
/// After the first Cluster the write path allocates nothing per packet.
/// </para>
/// <para>Not thread safe. The caller owns the output stream and disposes it.</para>
/// </remarks>
public sealed class MatroskaMuxer : IMuxer
{
    /// <summary>Bytes of memory the muxer keeps for each cue point until <see cref="Finish"/> writes them.</summary>
    public const int CueBytesPerPoint = 24;

    private const int MaxRelative = 32000; // block offsets are 16-bit (RFC 9559 §10.1); stay clear of the edge
    private const int SeekHeadSpareBytes = 42; // two Seek entries' worth of Void, so a Tags or Chapters entry can be added in place

    private readonly Stream _out;
    private readonly MatroskaMuxOptions _o;
    private readonly MediaLimits _limits;
    private readonly long _origin;
    private readonly byte[] _uuid;
    private readonly List<string> _notes = [];
    private readonly List<MediaTrack> _tracks = [];
    private long _unitsPerSecond;
    private long _scale;
    private long _minClusterUnits;
    private int _cueTrack;
    private bool _begun;

    // Per track (grown by AddTrack, fixed once the first packet is written).
    private MatroskaTrackPlan[] _plans = [];
    private MediaTime[] _lastDts = [];
    private long[] _lastTs = [];
    private long[] _defaultNs = [];
    private long[] _delayUnits = [];
    private bool[] _delayApplies = [];
    private DurationStats[] _stats = [];
    private long[] _packets = [];
    private long[] _payloadBytes = [];
    private long[] _durationUnits = [];
    private int[] _lastPending = [];
    private bool _rounded;

    // Cluster being built.
    private readonly List<Pending> _pending = new(256);
    private byte[] _payload = new byte[64 * 1024];
    private int _payloadLength;
    private bool _clusterOpen;
    private long _firstTs;
    private long _minTs;
    private long _maxTs;
    private long _liveClusterTs;
    private bool _clusterHasCue;
    private long _liveClusterBytes;

    // File state.
    private long _pos;
    private long _segmentSizePos;
    private long _segmentDataStart;
    private long _seekHeadPos;
    private long _infoPos;
    private long _tracksPos;
    private int _seekHeadLength;
    private bool _headerWritten;
    private bool _finished;
    private long _maxEnd;
    private readonly List<CuePoint> _cues = new(1024);
    private readonly EbmlBuffer _scratch = new(4096);
    private byte[] _head = new byte[256];
    private readonly byte[] _tail = new byte[48];

    /// <summary>Creates a muxer over an output stream. Declare the tracks with <see cref="AddTrack"/> before the first packet. Nothing is written until the first Cluster is closed (live: until the first packet).</summary>
    /// <param name="output">Where the file goes. Seekable unless <see cref="MatroskaMuxOptions.Live"/> is set. It starts at the stream's current position.</param>
    /// <param name="options">Layout options; null for the defaults.</param>
    /// <param name="limits">Resource limits (track count, packet size, laced frames); null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is out of range (a TimestampScale that does not divide 10⁹, a SegmentUuid that is not 16 bytes or is all zero).</exception>
    /// <exception cref="MuxRefusedException">The output cannot seek and live mode is off (<see cref="MuxRefusalReason.OutputNotSuitable"/>).</exception>
    public MatroskaMuxer(Stream output, MatroskaMuxOptions? options = null, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _o = options ?? new MatroskaMuxOptions();
        _limits = limits ?? MediaLimits.Default;
        _out = output;
        if (_o.TimestampScale is { } stated && (stated <= 0 || 1_000_000_000L % stated != 0))
        {
            throw new ArgumentException("TimestampScale must be a positive divisor of 1,000,000,000.", nameof(options));
        }

        if (_o.SegmentUuid is { } u && (u.Length != 16 || !u.Span.ContainsAnyExcept((byte)0)))
        {
            throw new ArgumentException("SegmentUuid must be 16 bytes, not all zero.", nameof(options));
        }

        if (_o.ReserveBytes < 0 || _o.ReserveBytes == 1)
        {
            throw new ArgumentException("ReserveBytes must be 0 or at least 2.", nameof(options));
        }

        if (_o.MaxClusterBytes < 1)
        {
            throw new ArgumentException("MaxClusterBytes must be positive.", nameof(options));
        }

        if (!_o.Live && !output.CanSeek)
        {
            throw new MuxRefusedException(MuxRefusalReason.OutputNotSuitable, "The output cannot seek; sizes, Cues and Duration are written after the media. Use live mode for a forward-only output.");
        }

        _origin = output.CanSeek ? output.Position : 0;
        if (_o.SegmentUuid is { } given)
        {
            _uuid = given.ToArray();
        }
        else
        {
            _uuid = new byte[16];
            RandomNumberGenerator.Fill(_uuid);
        }
    }

    /// <summary>Creates a muxer and declares the tracks in one call; see <see cref="MatroskaMuxer(Stream, MatroskaMuxOptions, MediaLimits)"/> and <see cref="AddTrack"/>.</summary>
    /// <param name="output">Where the file goes.</param>
    /// <param name="tracks">The tracks; track <c>i</c> becomes output track <c>i</c>, track number <c>i + 1</c> in the file.</param>
    /// <param name="options">Layout options; null for the defaults.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> or <paramref name="tracks"/> is null.</exception>
    /// <exception cref="MuxRefusedException">As for <see cref="AddTrack"/>, and when there are no tracks.</exception>
    public MatroskaMuxer(Stream output, IReadOnlyList<MediaTrack> tracks, MatroskaMuxOptions? options = null, MediaLimits? limits = null)
        : this(output, options, limits)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "There are no tracks to write.");
        }

        foreach (MediaTrack t in tracks)
        {
            AddTrack(t);
        }
    }

    /// <inheritdoc/>
    public MediaFormatId Format => MediaFormatId.Matroska;

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <summary>The profile being written.</summary>
    public MatroskaProfile Profile => _o.Profile;

    /// <summary>Bytes handed to the output so far.</summary>
    public long BytesWritten => _pos;

    /// <summary>Cue points collected so far (written by <see cref="Finish"/>); each costs <see cref="CueBytesPerPoint"/> bytes of memory.</summary>
    public int CuePointCount => _cues.Count;

    /// <summary>Declares a track (see <see cref="IMuxer.AddTrack"/>). Its CodecID and CodecPrivate are decided here, so a track Matroska cannot carry is refused now.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The output index; the track number in the file is that plus one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    /// <exception cref="MuxRefusedException">The codec has no Matroska CodecID here or WebM forbids it (<see cref="MuxRefusalReason.UnsupportedCodec"/>), it needs CodecPrivate and the track has none (<see cref="MuxRefusalReason.MissingConfiguration"/>) or has it in a form that cannot be repackaged (<see cref="MuxRefusalReason.InvalidConfiguration"/>), the kind is not video, audio or subtitle (<see cref="MuxRefusalReason.InvalidTrack"/>), the track limit is reached (<see cref="MuxRefusalReason.LimitExceeded"/>), or a packet was already written (<see cref="MuxRefusalReason.InvalidState"/>).</exception>
    public int AddTrack(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_begun || _finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "Tracks must be declared before the first packet.");
        }

        int i = _plans.Length;
        if (i >= _limits.MaxTracks)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"The output already has {i} tracks, the limit.", i, track.Codec);
        }

        MatroskaTrackPlan plan = MatroskaTrackWriter.Plan(track, i, _o.Profile);
        int n = i + 1;
        Array.Resize(ref _plans, n);
        Array.Resize(ref _lastDts, n);
        Array.Resize(ref _lastTs, n);
        Array.Resize(ref _defaultNs, n);
        Array.Resize(ref _delayUnits, n);
        Array.Resize(ref _delayApplies, n);
        Array.Resize(ref _stats, n);
        Array.Resize(ref _packets, n);
        Array.Resize(ref _payloadBytes, n);
        Array.Resize(ref _durationUnits, n);
        Array.Resize(ref _lastPending, n);
        _plans[i] = plan;
        _lastDts[i] = MediaTime.Unknown;
        _lastTs[i] = long.MinValue;
        _lastPending[i] = -1;
        _defaultNs[i] = plan.HintDefaultDurationNs;
        // The reader hands out Matroska times with CodecDelay already taken off (RFC 9559 §11.2); a track whose packet times already
        // exclude the priming gets it put back, so the Block timestamp is what the source file stored.
        _delayApplies[i] = plan.CodecDelayNs > 0 && track.Edit.PacketTimesIncludePriming;
        _stats[i].Reset();
        _tracks.Add(track with { Index = i });
        return i;
    }

    // Fixed once the tracks are known: the timestamp scale and the cue track.
    private void Begin()
    {
        if (_begun)
        {
            return;
        }

        if (_plans.Length == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "No track was declared.");
        }

        _begun = true;
        _scale = _o.TimestampScale ?? AutoScale(_tracks);
        _unitsPerSecond = 1_000_000_000L / _scale;
        _minClusterUnits = ToUnits(_o.MinClusterDuration);
        for (int i = 0; i < _plans.Length; i++)
        {
            _delayUnits[i] = (_plans[i].CodecDelayNs + _scale / 2) / _scale;
        }

        _cueTrack = -1;
        for (int i = 0; i < _plans.Length && _cueTrack < 0; i++)
        {
            if (_plans[i].IsVideo)
            {
                _cueTrack = i;
            }
        }

        for (int i = 0; i < _plans.Length && _cueTrack < 0; i++)
        {
            if (_plans[i].Track.Kind != TrackKind.Subtitle)
            {
                _cueTrack = i;
            }
        }
    }

    /// <summary>Abandons the output: whatever was written stays incomplete (its Segment size still says "unknown") and nothing more is written.</summary>
    public void Dispose() => _finished = true;

    /// <summary>Pushes buffered bytes to the stream. Matroska has no fragments, so a Cluster that is still being built stays open.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void Flush(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_finished)
        {
            _out.Flush();
        }
    }

    // One millisecond, unless all tracks already count the same whole number of ticks per second, finer than that: then keep their time base.
    private static long AutoScale(List<MediaTrack> tracks)
    {
        long common = tracks[0].Timescale;
        foreach (MediaTrack t in tracks)
        {
            if (t.Timescale != common)
            {
                return 1_000_000;
            }
        }

        // Finer than a millisecond but not so fine that a Cluster could span only a few packets (block offsets are 16-bit): down to 100 µs.
        return common is > 1000 and <= 10_000 && 1_000_000_000L % common == 0 ? 1_000_000_000L / common : 1_000_000;
    }

    private long ToUnits(MediaTime t) => t.Rescale(_unitsPerSecond, MediaRounding.NearestAwayFromZero).Ticks;

    /// <summary>Writes one packet. Packets must come in decode order; their bytes go into the file unchanged.</summary>
    /// <param name="info">The packet's timing, flags and track index. PTS (or DTS when PTS is unknown) is required; duration, DTS and discard padding are used when known.</param>
    /// <param name="data">The packet's bytes, in the form the track's CodecPrivate describes (for H.264 and HEVC, length-prefixed units). Copied before the call returns.</param>
    /// <param name="cancellationToken">Cancels the call; the output then holds whole elements up to the last closed Cluster.</param>
    /// <exception cref="MuxRefusedException">The track index is out of range, the packet has no usable time, its decode time goes backwards within the track, or the muxer has finished. Nothing of the packet is written.</exception>
    /// <exception cref="MediaLimitExceededException">The packet is larger than <see cref="MediaLimits.MaxPacketBytes"/>.</exception>
    /// <exception cref="IOException">The output failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer has finished; no more packets can be written.", info.TrackIndex);
        }

        Begin();
        int ti = info.TrackIndex;
        if ((uint)ti >= (uint)_plans.Length)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, $"The packet names track {ti}, but there are {_plans.Length} tracks.", ti);
        }

        if (data.Length > _limits.MaxPacketBytes)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {ti}: a packet of {data.Length} bytes is over the {_limits.MaxPacketBytes}-byte limit.", ti, _plans[ti].Track.Codec);
        }

        if (info.Dts.IsKnown && _lastDts[ti].IsKnown && info.Dts < _lastDts[ti])
        {
            throw new MuxRefusedException(MuxRefusalReason.NonMonotonicDts, $"Track {ti}: decode time {info.Dts} is before the previous packet's {_lastDts[ti]}. Packets must arrive in decode order.", ti, _plans[ti].Track.Codec);
        }

        MediaTime when = info.Pts.IsKnown ? info.Pts : info.Dts;
        if (!when.IsKnown)
        {
            throw new MuxRefusedException(MuxRefusalReason.MissingTimestamp, $"Track {ti}: the packet has neither a presentation nor a decode time.", ti, _plans[ti].Track.Codec);
        }

        if (!when.TryRescale(_unitsPerSecond, MediaRounding.NearestAwayFromZero, out MediaTime tsTime))
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {ti}: the packet time does not fit the file's timestamp scale.", ti, _plans[ti].Track.Codec);
        }

        if (!_rounded && (Int128)when.Ticks * _unitsPerSecond % when.Timescale != 0)
        {
            _rounded = true;
        }

        long ts = tsTime.Ticks + (_delayApplies[ti] ? _delayUnits[ti] : 0);
        long durUnits = -1;
        if (info.Duration.IsKnown)
        {
            if (!info.Duration.TryRescale(_unitsPerSecond, MediaRounding.NearestAwayFromZero, out MediaTime d))
            {
                throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {ti}: the packet duration does not fit the file's timestamp scale.", ti, _plans[ti].Track.Codec);
            }

            // A zero duration on audio or video states nothing (a fragment without sample durations gives it); treat it as unknown.
            durUnits = d.Ticks > 0 || _plans[ti].Track.Kind is not (TrackKind.Video or TrackKind.Audio) ? Math.Max(d.Ticks, 0) : -1;
        }

        long padNs = 0;
        if (info.DiscardPadding.IsKnown && info.DiscardPadding.Ticks != 0)
        {
            // RFC 9559 §5.1.3.5.7: DiscardPadding is in nanoseconds, positive at the end of the block, negative at its start.
            if (!info.DiscardPadding.TryRescale(1_000_000_000, MediaRounding.NearestAwayFromZero, out MediaTime pad))
            {
                throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {ti}: the discard padding does not fit.", ti, _plans[ti].Track.Codec);
            }

            padNs = pad.Ticks;
        }

        // Everything that can refuse has run; from here the packet is written.
        if (info.Dts.IsKnown)
        {
            _lastDts[ti] = info.Dts;
        }

        _packets[ti]++;
        _payloadBytes[ti] += data.Length;
        if (durUnits > 0)
        {
            _durationUnits[ti] += durUnits;
        }

        // Contract: a duration that is unknown is the distance to the next packet's decode time. The previous packet of this track is
        // still held when it is in the open Cluster (a Cluster is only closed below, after this), so it can be completed; the last packet
        // of a track, and every packet in live mode (already written), keep an unknown duration. A packet that shares its time with the one
        // before it belongs to a lace and is left alone.
        long dtsUnits = ts;
        if (info.Dts.IsKnown && info.Dts.TryRescale(_unitsPerSecond, MediaRounding.NearestAwayFromZero, out MediaTime dtsT))
        {
            dtsUnits = dtsT.Ticks + (_delayApplies[ti] ? _delayUnits[ti] : 0);
        }

        int lp = _lastPending[ti];
        if (!_o.Live && lp >= 0 && lp < _pending.Count)
        {
            Pending prev = _pending[lp];
            long gap = dtsUnits - prev.DtsUnits;
            if (prev.DurUnits < 0 && !prev.SharesTs && gap > 0 && prev.DiscardPaddingNs == 0)
            {
                prev.DurUnits = gap;
                prev.Dur = new MediaTime(gap, _unitsPerSecond);
                _pending[lp] = prev;
                _durationUnits[ti] += gap;
                if (prev.Ts + gap > _maxEnd)
                {
                    _maxEnd = prev.Ts + gap;
                }
            }
        }

        bool isKey = (info.Flags & PacketAttributes.Keyframe) != 0;
        bool cueCandidate = ti == _cueTrack && (_plans[ti].IsVideo ? isKey : true);
        if (_clusterOpen && ShouldCut(ts, data.Length, cueCandidate))
        {
            CloseCluster(cancellationToken);
        }

        Pending p = new()
        {
            Track = ti,
            Ts = ts,
            DurUnits = durUnits,
            Dur = durUnits < 0 ? MediaTime.Unknown : info.Duration,
            DiscardPaddingNs = padNs,
            Key = isKey,
            Invisible = (info.Flags & PacketAttributes.Discard) != 0,
            Disposable = (info.Flags & PacketAttributes.Disposable) != 0,
            RefDelta = _lastTs[ti] == long.MinValue ? 0 : _lastTs[ti] - ts,
            Length = data.Length,
            DtsUnits = dtsUnits,
            SharesTs = _lastTs[ti] == ts,
        };
        _lastTs[ti] = ts;
        long end = durUnits >= 0 ? ts + durUnits : ts;
        if (end > _maxEnd)
        {
            _maxEnd = end;
        }

        if (!_clusterOpen)
        {
            OpenCluster(ts);
        }

        _minTs = Math.Min(_minTs, ts);
        _maxTs = Math.Max(_maxTs, ts);
        if (cueCandidate && (_plans[ti].IsVideo || !_clusterHasCue))
        {
            p.Cue = true;
            _clusterHasCue = true;
        }

        if (_o.Live)
        {
            WriteLiveBlock(in p, data, cancellationToken);
            return;
        }

        p.PayloadOffset = _payloadLength;
        if (_payload.Length - _payloadLength < data.Length)
        {
            Array.Resize(ref _payload, (int)Math.Min(int.MaxValue / 2, Math.Max((long)_payload.Length * 2, (long)_payloadLength + data.Length)));
        }

        data.CopyTo(_payload.AsSpan(_payloadLength));
        _payloadLength += data.Length;
        _lastPending[ti] = _pending.Count;
        _pending.Add(p);
    }

    private bool ShouldCut(long ts, int size, bool cueCandidate)
    {
        long bytes = _o.Live ? _liveClusterBytes : _payloadLength;
        long lo = _o.Live ? _liveClusterTs : _minTs;
        long hi = _o.Live ? _liveClusterTs : _maxTs;
        // Hard limits, whatever the packet is: a block offset is 16 bits (RFC 9559 §10.1), and memory is bounded.
        if (Math.Max(hi, ts) - Math.Min(lo, ts) > MaxRelative || (bytes > 0 && bytes + size > 4L * _o.MaxClusterBytes))
        {
            return true;
        }

        // Soft limits: start a new Cluster on a video key frame (or, with no video, on any packet of the cue track), so it can be entered there.
        return cueCandidate && (ts - _firstTs >= _minClusterUnits || bytes >= _o.MaxClusterBytes);
    }

    private void OpenCluster(long firstTs)
    {
        _clusterOpen = true;
        _firstTs = firstTs;
        _minTs = firstTs;
        _maxTs = firstTs;
        _clusterHasCue = false;
        _payloadLength = 0;
        _pending.Clear();
        Array.Fill(_lastPending, -1);
        if (_o.Live)
        {
            EnsureHeader();
            _liveClusterTs = Math.Max(firstTs, 0);
            _liveClusterBytes = 0;
            // RFC 8794 §6.2: a Cluster of unknown size runs until the next element that cannot be its child.
            Span<byte> h = stackalloc byte[4 + 8 + 12]; // Cluster ID, 8-byte unknown size, Timestamp element of at most 1 + 1 + 8 bytes
            EbmlWriter.WriteId(h, MatroskaIds.Cluster);
            EbmlWriter.WriteUnknownSize(h[4..], 8);
            int n = 12 + WriteUIntElement(h[12..], MatroskaIds.Timestamp, (ulong)_liveClusterTs);
            Emit(h[..n]);
        }
    }

    private void WriteLiveBlock(in Pending p, ReadOnlySpan<byte> data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int headLen = EncodeBlock(in p, 1, -1, _liveClusterTs, _defaultNs[p.Track], out int tailLen, out _);
        Emit(_head.AsSpan(0, headLen));
        Emit(data);
        if (tailLen > 0)
        {
            Emit(_tail.AsSpan(0, tailLen));
        }

        _liveClusterBytes += headLen + data.Length + tailLen;
    }

    private void Emit(ReadOnlySpan<byte> bytes)
    {
        _out.Write(bytes);
        _pos += bytes.Length;
    }

    /// <summary>
    /// Writes what is still held: the last Cluster, and in seekable mode the Cues, then goes back to fill in the SeekHead, the Duration and the
    /// Segment size. The muxer cannot be used afterwards. The output stream is flushed but not closed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call; the file is then incomplete (its Segment size still says "unknown", which readers accept).</param>
    /// <returns>Bytes written, packets and payload per track, and the normalisations applied (times rounded to the timestamp scale; a lace regrouped).</returns>
    /// <exception cref="MuxRefusedException">No track was declared (<see cref="MuxRefusalReason.InvalidState"/>).</exception>
    /// <exception cref="IOException">The output failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public MuxReport Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_report is not null)
        {
            return _report;
        }

        if (_finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer was disposed before it finished.");
        }

        Begin();
        if (_clusterOpen)
        {
            CloseCluster(cancellationToken);
        }

        EnsureHeader();
        _finished = true;
        if (_o.Live)
        {
            _out.Flush();
        }
        else
        {
            FinishSeekable();
        }

        List<TrackMuxReport> tracks = new(_plans.Length);
        for (int i = 0; i < _plans.Length; i++)
        {
            tracks.Add(new TrackMuxReport(i, _packets[i], _payloadBytes[i], new MediaTime(_durationUnits[i], _unitsPerSecond)));
        }

        if (_rounded)
        {
            _notes.Add($"Packet times were rounded to the nearest TimestampScale unit ({_scale} ns).");
        }

        if (_lacedFrames > 0)
        {
            _notes.Add($"{_lacedFrames} packets that shared a timestamp and stated no duration were written as frames of laced blocks.");
        }

        _report = new MuxReport(_pos, tracks, _notes);
        return _report;
    }

    private MuxReport? _report;
    private long _lacedFrames;

    private void FinishSeekable()
    {
        long cuesPos = -1;
        if (_o.WriteCues && _cues.Count > 0)
        {
            cuesPos = _pos - _segmentDataStart;
            _scratch.Clear();
            BuildCues(_scratch);
            Emit(_scratch.Written);
        }

        long end = _pos;
        // Patch: SeekHead (now knowing the Cues), Info (now knowing the Duration), then the Segment's size.
        _scratch.Clear();
        BuildSeekHead(_scratch, _infoPos - _segmentDataStart, _tracksPos - _segmentDataStart, cuesPos);
        Patch(_seekHeadPos, _scratch.Written);
        _scratch.Clear();
        long duration = _o.Duration is { IsKnown: true } stated && stated.TryRescale(_unitsPerSecond, MediaRounding.NearestAwayFromZero, out MediaTime d) ? Math.Max(d.Ticks, _maxEnd) : _maxEnd;
        BuildInfo(_scratch, duration);
        Patch(_infoPos, _scratch.Written);
        Span<byte> size = stackalloc byte[8];
        EbmlWriter.WriteSize(size, end - _segmentDataStart, 8);
        Patch(_segmentSizePos, size);
        _out.Seek(_origin + end, SeekOrigin.Begin);
        _out.Flush();
    }

    private void Patch(long position, ReadOnlySpan<byte> bytes)
    {
        _out.Seek(_origin + position, SeekOrigin.Begin);
        _out.Write(bytes);
    }

    // The EBML header, Segment start, SeekHead, Info, Tracks and the Void reserve (RFC 9559 §6.2 recommends this order).
    private void EnsureHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;
        if (!_o.Live)
        {
            LearnDefaultDurations();
        }

        EbmlBuffer b = _scratch;
        b.Clear();
        int ebml = b.Begin(MatroskaIds.Ebml);
        b.UInt(MatroskaIds.EbmlVersion, 1);
        b.UInt(MatroskaIds.EbmlReadVersion, 1);
        b.UInt(MatroskaIds.EbmlMaxIdLength, 4);
        b.UInt(MatroskaIds.EbmlMaxSizeLength, 8);
        b.String(MatroskaIds.DocType, _o.Profile == MatroskaProfile.WebM ? "webm" : "matroska");
        b.UInt(MatroskaIds.DocTypeVersion, 4);
        b.UInt(MatroskaIds.DocTypeReadVersion, 2);
        b.End(ebml);
        Emit(b.Written);

        // The Segment is written with an unknown size first; a file cut short stays readable, and Finish patches the real size in.
        Span<byte> seg = stackalloc byte[12];
        EbmlWriter.WriteId(seg, MatroskaIds.Segment);
        EbmlWriter.WriteUnknownSize(seg[4..], 8);
        _segmentSizePos = _pos + 4;
        Emit(seg);
        _segmentDataStart = _pos;

        // Info and Tracks are built first so the SeekHead can name where they land.
        EbmlBuffer info = new(256);
        BuildInfo(info, 0);
        EbmlBuffer tracks = new(1024);
        BuildTracks(tracks);
        _scratch.Clear();
        BuildSeekHead(_scratch, 0, 0, -1);
        _seekHeadLength = _scratch.Length;
        _seekHeadPos = _pos;
        _infoPos = _pos + _seekHeadLength;
        _tracksPos = _infoPos + info.Length;
        _scratch.Clear();
        BuildSeekHead(_scratch, _infoPos - _segmentDataStart, _tracksPos - _segmentDataStart, -1);
        Emit(_scratch.Written);
        Emit(info.Written);
        Emit(tracks.Written);
        if (!_o.Live && _o.ReserveBytes >= 2)
        {
            Emit(EbmlWriter.Void(_o.ReserveBytes));
        }
    }

    private void BuildInfo(EbmlBuffer b, long durationUnits)
    {
        int info = b.Begin(MatroskaIds.Info);
        b.UInt(MatroskaIds.TimestampScale, (ulong)_scale);
        if (!_o.Live)
        {
            // RFC 9559 §5.1.2.9: Duration is a float in TimestampScale units. It is always 8 bytes so it can be rewritten in place.
            b.Float64(MatroskaIds.Duration, durationUnits);
        }

        b.String(MatroskaIds.MuxingApp, _o.WritingApp);
        b.String(MatroskaIds.WritingApp, _o.WritingApp);
        if (_o.Profile != MatroskaProfile.WebM)
        {
            // The WebM element list has no SegmentUUID.
            b.Binary(MatroskaIds.SegmentUuid, _uuid);
        }

        if (_o.DateUtcNanoseconds is { } date)
        {
            b.Int(MatroskaIds.DateUtc, date);
        }

        if (!string.IsNullOrEmpty(_o.Title))
        {
            b.String(MatroskaIds.Title, _o.Title);
        }

        if (_o.Crc32)
        {
            b.InsertCrc(info);
        }

        b.End(info);
    }

    private void BuildTracks(EbmlBuffer b)
    {
        int tracks = b.Begin(MatroskaIds.Tracks);
        ulong uidBase = BinaryPrimitives.ReadUInt64BigEndian(_uuid);
        for (int i = 0; i < _plans.Length; i++)
        {
            ulong uid = (uidBase ^ (ulong)(i + 1) * 0x9E3779B97F4A7C15UL) | 1; // nonzero, distinct per track within the file
            MatroskaTrackWriter.WriteEntry(b, _plans[i], i + 1, uid, _defaultNs[i], _o.Profile);
        }

        if (_o.Crc32)
        {
            b.InsertCrc(tracks);
        }

        b.End(tracks);
    }

    private void BuildSeekHead(EbmlBuffer b, long infoPos, long tracksPos, long cuesPos)
    {
        int head = b.Begin(MatroskaIds.SeekHead);
        Seek(b, MatroskaIds.Info, infoPos);
        Seek(b, MatroskaIds.Tracks, tracksPos);
        if (cuesPos >= 0)
        {
            Seek(b, MatroskaIds.Cues, cuesPos);
        }
        else
        {
            b.Append(EbmlWriter.Void(SeekEntryLength));
        }

        b.Append(EbmlWriter.Void(SeekHeadSpareBytes));
        if (_o.Crc32)
        {
            b.InsertCrc(head);
        }

        b.End(head);
    }

    // A Seek with a 4-byte SeekID and an 8-byte SeekPosition: 2 + 1 + (2 + 1 + 4) + (2 + 1 + 8) bytes.
    private const int SeekEntryLength = 21;

    private static void Seek(EbmlBuffer b, uint id, long position)
    {
        int seek = b.Begin(MatroskaIds.Seek);
        Span<byte> idBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);
        b.Binary(MatroskaIds.SeekId, idBytes);
        b.UInt64Fixed(MatroskaIds.SeekPosition, (ulong)position);
        b.End(seek);
    }

    private void BuildCues(EbmlBuffer b)
    {
        int cues = b.Begin(MatroskaIds.Cues);
        foreach (CuePoint c in _cues)
        {
            int point = b.Begin(MatroskaIds.CuePoint);
            b.UInt(MatroskaIds.CueTime, (ulong)Math.Max(c.Time, 0));
            int pos = b.Begin(MatroskaIds.CueTrackPositions);
            b.UInt(MatroskaIds.CueTrack, (ulong)c.Track);
            b.UInt(MatroskaIds.CueClusterPosition, (ulong)c.ClusterPosition);
            b.UInt(MatroskaIds.CueRelativePosition, (ulong)c.RelativePosition);
            b.End(pos);
            b.End(point);
        }

        if (_o.Crc32)
        {
            b.InsertCrc(cues);
        }

        b.End(cues);
    }

    // A track's DefaultDuration, when the packets of the first Cluster show one: the same duration on every packet but the last (which a
    // stream often ends short), and at least three packets.
    private void LearnDefaultDurations()
    {
        foreach (Pending p in _pending)
        {
            _stats[p.Track].Add(p.Dur);
        }

        for (int i = 0; i < _plans.Length; i++)
        {
            if (_stats[i].TryGet(out MediaTime d) && d.TryRescale(1_000_000_000, MediaRounding.NearestAwayFromZero, out MediaTime ns) && ns.Ticks > 0)
            {
                _defaultNs[i] = ns.Ticks;
            }
        }
    }

    private void CloseCluster(CancellationToken ct)
    {
        _clusterOpen = false;
        if (_o.Live)
        {
            return; // the next Cluster header ends this one
        }

        if (_pending.Count == 0)
        {
            return;
        }

        EnsureHeader();
        long clusterTs = Math.Max(_minTs, 0);
        long clusterPos = _pos - _segmentDataStart;

        // Pass 1: the Cluster's size, the CRC and where each block starts (for the Cues).
        Span<byte> tsElement = stackalloc byte[12];
        int tsLen = WriteUIntElement(tsElement, MatroskaIds.Timestamp, (ulong)clusterTs);
        long payload = tsLen + (_o.Crc32 ? 6 : 0);
        uint crc = EbmlCrc32.Initial;
        if (_o.Crc32)
        {
            crc = EbmlCrc32.Update(crc, tsElement[..tsLen]);
        }

        int count = _pending.Count;
        for (int i = 0; i < count;)
        {
            Pending p = _pending[i];
            int frames = LaceCount(i);
            int headLen = EncodeBlock(in p, frames, i, clusterTs, _defaultNs[p.Track], out int tailLen, out int bytes);
            if (p.Cue && _o.WriteCues)
            {
                _cues.Add(new CuePoint { Time = p.Ts, ClusterPosition = clusterPos, RelativePosition = (int)payload, Track = p.Track + 1 });
            }

            if (_o.Crc32)
            {
                crc = EbmlCrc32.Update(crc, _head.AsSpan(0, headLen));
                crc = EbmlCrc32.Update(crc, _payload.AsSpan(p.PayloadOffset, bytes));
                crc = EbmlCrc32.Update(crc, _tail.AsSpan(0, tailLen));
            }

            payload += headLen + bytes + tailLen;
            i += frames;
        }

        // Pass 2: write it.
        Span<byte> header = stackalloc byte[4 + 8 + 6 + 12];
        EbmlWriter.WriteId(header, MatroskaIds.Cluster);
        EbmlWriter.WriteSize(header[4..], payload, 8);
        int n = 12;
        if (_o.Crc32)
        {
            header[n++] = (byte)MatroskaIds.Crc32;
            header[n++] = 0x84;
            BinaryPrimitives.WriteUInt32LittleEndian(header[n..], EbmlCrc32.Finish(crc));
            n += 4;
        }

        tsElement[..tsLen].CopyTo(header[n..]);
        n += tsLen;
        Emit(header[..n]);
        for (int i = 0; i < count;)
        {
            ct.ThrowIfCancellationRequested();
            Pending p = _pending[i];
            int frames = LaceCount(i);
            int headLen = EncodeBlock(in p, frames, i, clusterTs, _defaultNs[p.Track], out int tailLen, out int bytes);
            Emit(_head.AsSpan(0, headLen));
            Emit(_payload.AsSpan(p.PayloadOffset, bytes));
            if (tailLen > 0)
            {
                Emit(_tail.AsSpan(0, tailLen));
            }

            if (frames > 1)
            {
                _lacedFrames += frames;
            }

            i += frames;
        }

        _pending.Clear();
        _payloadLength = 0;
    }

    // Packets of one audio track that share a timestamp and state no duration are the frames of one laced block in the file they came from
    // (RFC 9559 §10.3: a lace has a single timestamp); written as separate blocks they would overlap, so they go back into one lace.
    // Only the seekable path groups them: it already holds the Cluster.
    private int LaceCount(int index)
    {
        Pending first = _pending[index];
        if (first.DurUnits >= 0 || first.DiscardPaddingNs != 0 || _plans[first.Track].IsVideo || _plans[first.Track].Track.Kind == TrackKind.Subtitle)
        {
            return 1;
        }

        int max = Math.Min(256, _limits.MaxLacedFrames);
        int n = 1;
        while (n < max && index + n < _pending.Count)
        {
            Pending next = _pending[index + n];
            if (next.Track != first.Track || next.Ts != first.Ts || next.DurUnits >= 0 || next.DiscardPaddingNs != 0 || next.Key != first.Key || next.Invisible != first.Invisible || next.Disposable != first.Disposable)
            {
                break;
            }

            n++;
        }

        return n;
    }

    // Encodes everything of a block but its payload: the part before the payload in `_head`, the part after it (BlockGroup members that
    // follow the Block) in `_tail`. `frames` consecutive pending packets starting at `index` form one lace (Xiph lacing, RFC 9559 §10.3.1);
    // their payload is contiguous in `_payload`. A SimpleBlock (§10.2) is used unless the packet needs a BlockGroup (§10.3): a duration the
    // track's DefaultDuration does not give, a DiscardPadding, or a subtitle (whose duration is part of its meaning).
    private int EncodeBlock(in Pending p, int frames, int index, long clusterTs, long defaultNs, out int tailLen, out int payloadBytes)
    {
        long rel = p.Ts - clusterTs;
        int number = p.Track + 1;
        int numberLen = number < 127 ? 1 : 2;
        payloadBytes = p.Length;
        int laceLen = 0;
        if (frames > 1)
        {
            laceLen = 1;
            payloadBytes = 0;
            for (int k = 0; k < frames; k++)
            {
                int len = _pending[index + k].Length;
                payloadBytes += len;
                if (k < frames - 1)
                {
                    laceLen += len / 255 + 1; // Xiph lacing: runs of 255 then the remainder
                }
            }
        }

        if (_head.Length < 64 + laceLen)
        {
            _head = new byte[64 + laceLen];
        }

        Span<byte> head = _head;
        Span<byte> tail = _tail;
        long body = numberLen + 3 + laceLen + payloadBytes;
        bool needsDuration = p.DurUnits >= 0 && !(defaultNs > 0 && Math.Abs(DurationNs(in p) - defaultNs) <= 1);
        bool group = needsDuration || p.DiscardPaddingNs != 0;
        tailLen = 0;
        int n = 0;
        if (!group)
        {
            head[n++] = (byte)MatroskaIds.SimpleBlock;
            n += EbmlWriter.WriteSize(head[n..], body);
        }
        else
        {
            if (needsDuration)
            {
                tailLen += WriteUIntElement(tail[tailLen..], MatroskaIds.BlockDuration, (ulong)p.DurUnits);
            }

            if (!p.Key)
            {
                // RFC 9559 §10.3: a BlockGroup without ReferenceBlock is a key frame.
                tailLen += WriteIntElement(tail[tailLen..], MatroskaIds.ReferenceBlock, p.RefDelta);
            }

            if (p.DiscardPaddingNs != 0)
            {
                tailLen += WriteIntElement(tail[tailLen..], MatroskaIds.DiscardPadding, p.DiscardPaddingNs);
            }

            long inner = 1 + EbmlWriter.SizeLength(body) + body + tailLen;
            head[n++] = (byte)MatroskaIds.BlockGroup;
            n += EbmlWriter.WriteSize(head[n..], inner);
            head[n++] = (byte)MatroskaIds.Block;
            n += EbmlWriter.WriteSize(head[n..], body);
        }

        n += EbmlWriter.WriteSize(head[n..], number); // track number as an EBML variable-length integer (RFC 9559 §10.1)
        BinaryPrimitives.WriteInt16BigEndian(head[n..], (short)rel);
        n += 2;
        byte flags = 0;
        if (!group && p.Key)
        {
            flags |= 0x80;
        }

        if (p.Invisible)
        {
            flags |= 0x08;
        }

        if (!group && p.Disposable)
        {
            flags |= 0x01;
        }

        if (frames > 1)
        {
            flags |= 0x02; // lacing bits 01: Xiph
        }

        head[n++] = flags;
        if (frames > 1)
        {
            head[n++] = (byte)(frames - 1);
            for (int k = 0; k < frames - 1; k++)
            {
                int len = _pending[index + k].Length;
                while (len >= 255)
                {
                    head[n++] = 255;
                    len -= 255;
                }

                head[n++] = (byte)len;
            }
        }

        return n;
    }

    // Duration in nanoseconds from the packet's own time, for comparing with DefaultDuration (which is in nanoseconds).
    private static long DurationNs(in Pending p) => p.Dur.TryRescale(1_000_000_000, MediaRounding.NearestAwayFromZero, out MediaTime ns) ? ns.Ticks : -1;

    private static int WriteUIntElement(Span<byte> d, uint id, ulong value)
    {
        int idLen = EbmlWriter.WriteId(d, id);
        int n = EbmlWriter.UnsignedLength(value);
        d[idLen] = (byte)(0x80 | n);
        for (int i = 0; i < n; i++)
        {
            d[idLen + 1 + i] = (byte)(value >> (8 * (n - 1 - i)));
        }

        return idLen + 1 + n;
    }

    private static int WriteIntElement(Span<byte> d, uint id, long value)
    {
        int idLen = EbmlWriter.WriteId(d, id);
        int n = 1;
        while (n < 8 && (value < -(1L << (8 * n - 1)) || value >= (1L << (8 * n - 1))))
        {
            n++;
        }

        d[idLen] = (byte)(0x80 | n);
        for (int i = 0; i < n; i++)
        {
            d[idLen + 1 + i] = (byte)(value >> (8 * (n - 1 - i)));
        }

        return idLen + 1 + n;
    }

    private struct Pending
    {
        public int Track;
        public long Ts;
        public long DtsUnits;
        public long DurUnits;
        public MediaTime Dur;
        public long DiscardPaddingNs;
        public long RefDelta;
        public bool SharesTs;
        public int PayloadOffset;
        public int Length;
        public bool Key;
        public bool Invisible;
        public bool Disposable;
        public bool Cue;
    }

    private struct CuePoint
    {
        public long Time;
        public long ClusterPosition;
        public int RelativePosition;
        public int Track;
    }

    private struct DurationStats
    {
        private int _count;
        private MediaTime _first;
        private MediaTime _previous;
        private bool _equal;

        public void Reset()
        {
            _count = 0;
            _equal = true;
        }

        public void Add(MediaTime d)
        {
            if (!d.IsKnown)
            {
                _equal = false;
                _count++;
                _previous = d;
                return;
            }

            if (_count == 0)
            {
                _first = d;
            }
            else if (!(_previous == _first))
            {
                _equal = false; // a packet before this one differed (the last packet of a track is allowed to)
            }

            _previous = d;
            _count++;
        }

        public readonly bool TryGet(out MediaTime d)
        {
            d = _first;
            return _count >= 3 && _equal && _first.IsKnown && _first.Ticks > 0;
        }
    }
}

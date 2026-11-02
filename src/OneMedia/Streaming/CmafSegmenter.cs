using System.Buffers;
using System.Buffers.Binary;
using System.Linq;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>A segment's timing and contents, read from its own <c>moof</c> boxes.</summary>
/// <param name="Number">The segment number, from 1 in each track (the value <c>$Number$</c> and the HLS media sequence count from).</param>
/// <param name="StartTicks">The decode time of the first sample (<c>tfdt</c>) in the track's timescale.</param>
/// <param name="DurationTicks">The sum of the sample durations in the track's timescale.</param>
/// <param name="EarliestPresentationTicks">The smallest presentation time of any sample, in the track's timescale (what a <c>sidx</c> calls earliest presentation time).</param>
/// <param name="Samples">The number of samples.</param>
/// <param name="StartsWithSync">True when the first sample is a sync sample (random access point).</param>
public readonly record struct SegmentInfo(long Number, long StartTicks, long DurationTicks, long EarliestPresentationTicks, int Samples, bool StartsWithSync);

/// <summary>One track of a segmented presentation: what its init segment and segments say about it. Filled in when its init segment is delivered; the totals grow with each segment.</summary>
public sealed class SegmentedTrack
{
    internal SegmentedTrack(int index, MediaTrack input) { Index = index; Input = input; }

    /// <summary>The track's number among the segmented tracks, in the order of the input.</summary>
    public int Index { get; }

    /// <summary>The input track it came from.</summary>
    public MediaTrack Input { get; }

    /// <summary>The kind of track.</summary>
    public TrackKind Kind => Input.Kind;

    /// <summary>The track as the init segment declares it (codec configuration, sizes, sample rate), or null before the init segment is delivered.</summary>
    public MediaTrack? Declared { get; internal set; }

    /// <summary>Ticks per second of the segment times, from the init segment; 0 before it is delivered.</summary>
    public long Timescale { get; internal set; }

    /// <summary>The RFC 6381 <c>codecs</c> string of the track as written to the init segment, or null when none exists.</summary>
    public string? Codecs { get; internal set; }

    /// <summary>The number of media segments delivered.</summary>
    public long SegmentCount { get; internal set; }

    /// <summary>Total bytes of the media segments delivered.</summary>
    public long MediaBytes { get; internal set; }

    /// <summary>Total duration of the media segments delivered, in <see cref="Timescale"/> ticks.</summary>
    public long DurationTicks { get; internal set; }

    /// <summary>The highest bit rate of any one segment (bits per second), for the HLS <c>BANDWIDTH</c> and DASH <c>bandwidth</c> values.</summary>
    public long PeakBitsPerSecond { get; internal set; }

    /// <summary>The bit rate over every segment delivered, in bits per second.</summary>
    public long AverageBitsPerSecond => DurationTicks > 0 && Timescale > 0 ? (long)((Int128)MediaBytes * 8 * Timescale / DurationTicks) : 0;
}

/// <summary>Receives what a <see cref="CmafSegmenter"/> produces. Calls come on the thread that feeds the segmenter, in the order the segments complete.</summary>
public interface ISegmentSink
{
    /// <summary>The init segment (<c>ftyp</c> and <c>moov</c>) of a track. Called once per track, before its first media segment.</summary>
    /// <param name="track">The track; <see cref="SegmentedTrack.Declared"/>, <see cref="SegmentedTrack.Timescale"/> and <see cref="SegmentedTrack.Codecs"/> are set.</param>
    /// <param name="data">The bytes; valid only during the call.</param>
    void OnInitSegment(SegmentedTrack track, ReadOnlySpan<byte> data);

    /// <summary>A media segment (one or more <c>moof</c> + <c>mdat</c> pairs) of a track is complete.</summary>
    /// <param name="track">The track; its totals include this segment.</param>
    /// <param name="segment">The segment's timing.</param>
    /// <param name="data">The bytes; valid only during the call.</param>
    void OnMediaSegment(SegmentedTrack track, in SegmentInfo segment, ReadOnlySpan<byte> data);
}

/// <summary>Settings of a <see cref="CmafSegmenter"/>.</summary>
public sealed record SegmenterOptions
{
    /// <summary>The length a segment reaches before the next keyframe of the reference track ends it. Default six seconds. A GOP longer than this makes a longer segment.</summary>
    public MediaTime SegmentDuration { get; init; } = new(6, 1);

    /// <summary>Limits passed to the muxers. Default <see cref="MediaLimits.Default"/>.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>Chooses the tracks to segment; null takes every video and audio track the ISO muxer can carry. Tracks of other kinds are never segmented here.</summary>
    public Func<MediaTrack, bool>? Select { get; init; }

    /// <summary>
    /// The most bytes of other tracks' packets held back while the reference track has not yet reached their time (the interleave skew of the
    /// input). Past it they are written without waiting, which can put a boundary a little off. Default 32 MiB.
    /// </summary>
    public long MaxQueuedBytes { get; init; } = 32L << 20;

    /// <summary>Sample entry boxes, PCM layout and the like the ISO muxer should write for a track, as the remux layer derives them from an ISO or audio-file input.</summary>
    internal Func<Remuxing.RemuxSession, MediaTrack, IsoBmff.IsoTrackOptions?>? SessionIsoOptions { get; init; }

    /// <summary>The creation time written to the init segments; null (the default) writes zero, so the output depends only on the input.</summary>
    public DateTimeOffset? CreationTime { get; init; }
}

/// <summary>What a finished segmenter produced.</summary>
/// <param name="Tracks">One entry per segmented track, with its totals.</param>
/// <param name="Losses">What did not carry over, per input track (tracks left out, fields not written), as the remux layer reports it.</param>
public sealed record SegmenterReport(IReadOnlyList<SegmentedTrack> Tracks, IReadOnlyList<Remuxing.RemuxLoss> Losses)
{
    /// <summary>A one-line summary.</summary>
    public override string ToString() => $"Segmented: {Tracks.Count} track{(Tracks.Count == 1 ? "" : "s")}, {Losses.Count} loss{(Losses.Count == 1 ? "" : "es")} (see Losses)";
}

/// <summary>
/// Cuts packets into CMAF segments: one init segment and a series of media segments per track (ISO/IEC 23000-19), each track in its own
/// stream, the segments of all tracks starting at the same keyframe-aligned times. Payload bytes are copied; the only repackaging is that of
/// the remux layer (Annex B to length-prefixed, ADTS to raw AAC).
/// </summary>
/// <remarks>
/// <para>Give every packet to <see cref="WritePacket"/> in the order a demuxer delivers them, then call <see cref="Finish"/>. The reference track (the
/// first video track, else the first audio track) decides the boundaries: a segment ends at the first keyframe at or after
/// <see cref="SegmenterOptions.SegmentDuration"/> from its start. Packets of the other tracks wait until the reference track has passed their
/// time, then go to the segment they belong to, so a track that runs ahead of the reference in the input costs memory for the difference
/// (bounded by <see cref="SegmenterOptions.MaxQueuedBytes"/>). Memory otherwise holds one segment per track.</para>
/// <para>The sink is called as soon as a segment is complete, so live output works by feeding packets as they arrive. A segmenter is not
/// thread-safe. Disposing it before <see cref="Finish"/> abandons what is in progress.</para>
/// </remarks>
public sealed class CmafSegmenter : IDisposable
{
    private readonly record struct Held(PacketInfo Info, byte[] Data, int Length, MediaTime Time);

    private sealed class Lane(int index, MediaTrack input, SegmentedTrack info, MediaLimits limits)
    {
        public int Index { get; } = index;

        public MediaTrack Input { get; } = input;

        public SegmentedTrack Info { get; } = info;

        public Remuxing.RemuxSession Session { get; set; } = null!;

        public SegmentCapture Output { get; } = new(limits, 1 << 16);

        public Queue<Held> Queue { get; } = new();

        public long QueuedBytes { get; set; }

        public long NextBoundary { get; set; }

        public bool InitDelivered { get; set; }

        public long Number { get; set; }

        public bool Done { get; set; }
    }

    private readonly ISegmentSink _sink;
    private readonly SegmenterOptions _options;
    private readonly List<Lane> _lanes = [];
    private readonly Dictionary<int, Lane> _byInput = [];
    private readonly List<MediaTime> _boundaries = [];
    private long _boundaryBase;
    private Lane _reference;
    private MediaTime _segmentStart = MediaTime.Unknown;
    private MediaTime _frontier = MediaTime.Unknown;
    private bool _finished;

    /// <summary>Creates a segmenter.</summary>
    /// <param name="inputTracks">The tracks of the input, with their own <see cref="MediaTrack.Index"/> values; packets refer to these.</param>
    /// <param name="sink">Receives the init and media segments.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="SegmenterOptions.SegmentDuration"/> is not positive.</exception>
    /// <exception cref="MuxRefusedException">No video or audio track is selected.</exception>
    public CmafSegmenter(IReadOnlyList<MediaTrack> inputTracks, ISegmentSink sink, SegmenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputTracks);
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new SegmenterOptions();
        if (!_options.SegmentDuration.IsKnown || _options.SegmentDuration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The segment duration must be positive.");
        }

        foreach (MediaTrack t in inputTracks)
        {
            if (t.Kind is not (TrackKind.Video or TrackKind.Audio) || t.IsProtected || (_options.Select is { } pick && !pick(t)))
            {
                continue;
            }

            Lane lane = new(_lanes.Count, t, new SegmentedTrack(_lanes.Count, t), _options.Limits);
            _lanes.Add(lane);
            _byInput[t.Index] = lane;
        }

        if (_lanes.Count == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "The input has no video or audio track to segment.");
        }

        _reference = _lanes.Find(l => l.Input.Kind == TrackKind.Video) ?? _lanes[0];
        foreach (Lane lane in _lanes)
        {
            Lane captured = lane;
            lane.Session = new Remuxing.RemuxSession(
                inputTracks,
                lane.Output,
                Remuxing.RemuxFormat.Cmaf,
                new Remuxing.RemuxOptions
                {
                    Select = t => t.Index == captured.Input.Index,
                    Limits = _options.Limits,
                    CreationTime = _options.CreationTime,
                    // The segmenter ends fragments itself, at the boundaries.
                    FragmentDuration = new MediaTime(86_400, 1),
                    Observer = (in PacketInfo p, ReadOnlySpan<byte> _) => Observe(captured, in p),
                });
            if (_options.SessionIsoOptions is { } isoOptions)
            {
                lane.Session.IsoOptions = t => isoOptions(captured.Session, t);
            }
        }
    }

    /// <summary>The segmented tracks, in input order. Their details are filled in as init segments are delivered.</summary>
    public IReadOnlyList<SegmentedTrack> Tracks
    {
        get
        {
            SegmentedTrack[] t = new SegmentedTrack[_lanes.Count];
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = _lanes[i].Info;
            }

            return t;
        }
    }

    /// <summary>Gives the segmenter one packet.</summary>
    /// <param name="info">The packet; <see cref="PacketInfo.TrackIndex"/> is the input track index. Packets of tracks that are not segmented are ignored.</param>
    /// <param name="data">The payload as the reader delivered it. Copied before the call returns.</param>
    /// <param name="cancellationToken">Checked before the packet is accepted.</param>
    /// <exception cref="InvalidOperationException">After <see cref="Finish"/>.</exception>
    /// <exception cref="MuxRefusedException">The muxer refused the packet (a decode time that goes backwards, a missing time).</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new InvalidOperationException("The segmenter is finished.");
        }

        if (!_byInput.TryGetValue(info.TrackIndex, out Lane? lane) || lane.Done)
        {
            return;
        }

        // Every audio packet starts a segment cleanly; readers that do not flag them would otherwise make every sample "non-sync" in the track fragments.
        PacketInfo packet = lane.Input.Kind == TrackKind.Video ? info : info with { Flags = info.Flags | PacketAttributes.Keyframe };
        if (lane == _reference)
        {
            lane.Session.WritePacket(packet, data, cancellationToken);
            CheckReference();
            return;
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 1));
        data.CopyTo(copy);
        MediaTime time = packet.Dts.IsKnown ? packet.Dts : packet.Pts;
        lane.Queue.Enqueue(new Held(packet, copy, data.Length, time));
        lane.QueuedBytes += data.Length;
        Drain(all: false, cancellationToken);
        CheckReference();
    }

    // The reference track's session can drop the track (a codec the muxer cannot carry); the next track then decides the boundaries.
    private void CheckReference()
    {
        IReadOnlyList<bool> wanted = _reference.Session.Wanted;
        if (_reference.Input.Index < wanted.Count && !wanted[_reference.Input.Index] && _reference.Session.Started)
        {
            _reference.Done = true;
            Lane? next = _lanes.Find(l => !l.Done);
            if (next is not null)
            {
                _reference = next;
                _segmentStart = MediaTime.Unknown;
            }
        }
    }

    private void Observe(Lane lane, in PacketInfo p)
    {
        if (lane != _reference)
        {
            return;
        }

        MediaTime t = p.Dts.IsKnown ? p.Dts : p.Pts;
        if (!t.IsKnown)
        {
            return;
        }

        bool sync = lane.Input.Kind != TrackKind.Video || p.IsKeyframe;
        if (!_segmentStart.IsKnown)
        {
            _segmentStart = t;
        }
        else if (sync && t.Subtract(_segmentStart, MediaRounding.NearestAwayFromZero) >= _options.SegmentDuration)
        {
            // The packet about to be written starts the next segment: end the open one first.
            _boundaries.Add(t);
            _segmentStart = t;
            EndSegment(lane);
        }

        _frontier = t;
        Drain(all: false, CancellationToken.None);
    }

    private MediaTime Boundary(long absolute) => _boundaries[(int)(absolute - _boundaryBase)];

    private bool HasBoundary(long absolute) => absolute - _boundaryBase < _boundaries.Count;

    private void Drain(bool all, CancellationToken cancellationToken)
    {
        foreach (Lane lane in _lanes)
        {
            if (lane == _reference)
            {
                continue;
            }

            while (lane.Queue.Count > 0)
            {
                Held h = lane.Queue.Peek();
                bool release = all || (_frontier.IsKnown && h.Time.IsKnown && h.Time <= _frontier) || lane.QueuedBytes > _options.MaxQueuedBytes;
                if (!release)
                {
                    break;
                }

                if (h.Time.IsKnown)
                {
                    while (HasBoundary(lane.NextBoundary) && h.Time >= Boundary(lane.NextBoundary))
                    {
                        EndSegment(lane);
                        lane.NextBoundary++;
                    }
                }

                lane.Queue.Dequeue();
                lane.QueuedBytes -= h.Length;
                try
                {
                    lane.Session.WritePacket(h.Info, h.Data.AsSpan(0, h.Length), cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(h.Data);
                }
            }
        }

        long min = long.MaxValue;
        foreach (Lane lane in _lanes)
        {
            if (lane != _reference && !lane.Done)
            {
                min = Math.Min(min, lane.NextBoundary);
            }
        }

        if (min == long.MaxValue)
        {
            // Only the reference track is active: nobody reads the boundaries.
            _boundaryBase += _boundaries.Count;
            _boundaries.Clear();
        }
        else if (min > _boundaryBase)
        {
            int drop = (int)Math.Min(min - _boundaryBase, _boundaries.Count);
            _boundaries.RemoveRange(0, drop);
            _boundaryBase += drop;
        }
    }

    private void EndSegment(Lane lane)
    {
        lane.Session.EndFragment();
        Deliver(lane);
    }

    // Splits what the muxer wrote into the init segment (everything before the first moof) and the media segment, and hands them to the sink.
    private void Deliver(Lane lane)
    {
        ReadOnlySpan<byte> bytes = lane.Output.Bytes;
        if (bytes.IsEmpty)
        {
            return;
        }

        int firstMoof = FragmentReader.FindFirstMoof(bytes);
        if (!lane.InitDelivered)
        {
            ReadOnlySpan<byte> init = firstMoof < 0 ? bytes : bytes[..firstMoof];
            Describe(lane, init);
            lane.InitDelivered = true;
            _sink.OnInitSegment(lane.Info, init);
        }

        if (firstMoof >= 0)
        {
            ReadOnlySpan<byte> media = bytes[firstMoof..];
            if (FragmentReader.TryRead(media, out long start, out long duration, out long earliest, out int samples, out bool sync))
            {
                lane.Number++;
                SegmentedTrack info = lane.Info;
                info.SegmentCount++;
                info.MediaBytes += media.Length;
                info.DurationTicks += duration;
                if (duration > 0 && info.Timescale > 0)
                {
                    info.PeakBitsPerSecond = Math.Max(info.PeakBitsPerSecond, (long)((Int128)media.Length * 8 * info.Timescale / duration));
                }

                _sink.OnMediaSegment(info, new SegmentInfo(lane.Number, start, duration, earliest, samples, sync), media);
            }
        }

        lane.Output.Reset();
    }

    // The init segment is read with the library's own reader: the codec string, sizes and timescale then describe what was written, not what was read.
    private static void Describe(Lane lane, ReadOnlySpan<byte> init)
    {
        using MediaFile file = Media.Open(init.ToArray());
        if (file.Tracks.Count == 0)
        {
            return;
        }

        MediaTrack written = file.Tracks[0];
        lane.Info.Declared = written;
        lane.Info.Timescale = written.Timescale;
        lane.Info.Codecs = (written with { CodecTag = null }).Rfc6381Codec();
    }

    /// <summary>Ends the last segment of every track and returns what was produced.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The tracks with their totals, and the losses.</returns>
    /// <exception cref="InvalidOperationException">Already finished.</exception>
    /// <exception cref="MuxRefusedException">A muxer could not complete (a track with no packets).</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public SegmenterReport Finish(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The segmenter is already finished.");
        }

        _finished = true;
        List<Remuxing.RemuxLoss> losses = [];
        // The reference track finishes first: its remaining packets (held in a reorder window) are written and may still end segments.
        List<Lane> order = [_reference];
        order.AddRange(_lanes.Where(l => l != _reference));
        foreach (Lane lane in order)
        {
            if (lane.Done && lane != _reference)
            {
                continue;
            }

            if (lane == _reference)
            {
                try
                {
                    FinishLane(lane, losses, cancellationToken);
                }
                catch (MuxRefusedException) when (lane.Info.SegmentCount == 0)
                {
                    lane.Done = true;
                }

                Drain(all: true, cancellationToken);
            }
            else
            {
                Drain(all: true, cancellationToken);
                FinishLane(lane, losses, cancellationToken);
            }
        }

        return new SegmenterReport(Tracks, losses);
    }

    private void FinishLane(Lane lane, List<Remuxing.RemuxLoss> losses, CancellationToken cancellationToken)
    {
        Remuxing.RemuxReport report = lane.Session.Finish(cancellationToken);
        Deliver(lane);
        // Tracks the segmenter itself left out (the other lanes) are not losses.
        foreach (Remuxing.RemuxLoss loss in report.Losses)
        {
            if (loss.Reason != Remuxing.LossReason.Deselected)
            {
                losses.Add(loss);
            }
        }
    }

    /// <summary>Releases the muxers and any packets held back. Without <see cref="Finish"/> the output is incomplete.</summary>
    public void Dispose()
    {
        foreach (Lane lane in _lanes)
        {
            lane.Session.Dispose();
            while (lane.Queue.Count > 0)
            {
                ArrayPool<byte>.Shared.Return(lane.Queue.Dequeue().Data);
            }
        }
    }
}

/// <summary>Reads the timing out of the fragments the ISO muxer writes (ISO/IEC 14496-12 §8.8).</summary>
internal static class FragmentReader
{
    private static bool Box(ReadOnlySpan<byte> data, int at, out uint type, out int headerSize, out int size)
    {
        type = 0;
        headerSize = 8;
        size = 0;
        if (at + 8 > data.Length)
        {
            return false;
        }

        long s = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
        if (s == 1)
        {
            if (at + 16 > data.Length)
            {
                return false;
            }

            s = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
            headerSize = 16;
        }
        else if (s == 0)
        {
            s = data.Length - at;
        }

        if (s < headerSize || at + s > data.Length)
        {
            return false;
        }

        size = (int)s;
        return true;
    }

    private static uint Code(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);

    public static int FindFirstMoof(ReadOnlySpan<byte> data)
    {
        uint moof = Code("moof"u8);
        int at = 0;
        while (Box(data, at, out uint type, out _, out int size))
        {
            if (type == moof)
            {
                return at;
            }

            at += size;
        }

        return -1;
    }

    /// <summary>Walks every <c>moof</c> of a media segment and sums what they hold.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out long start, out long duration, out long earliest, out int samples, out bool startsWithSync)
    {
        start = 0;
        duration = 0;
        earliest = long.MaxValue;
        samples = 0;
        startsWithSync = false;
        bool first = true;
        long next = 0;
        int at = 0;
        uint moof = Code("moof"u8), traf = Code("traf"u8), tfhd = Code("tfhd"u8), tfdt = Code("tfdt"u8), trun = Code("trun"u8);
        while (Box(data, at, out uint type, out int hs, out int size))
        {
            if (type == moof)
            {
                ReadOnlySpan<byte> m = data.Slice(at + hs, size - hs);
                int c = 0;
                while (Box(m, c, out uint ct, out int chs, out int csize))
                {
                    if (ct == traf)
                    {
                        ReadOnlySpan<byte> t = m.Slice(c + chs, csize - chs);
                        long baseTime = next;
                        uint defaultDuration = 0, defaultFlags = 0;
                        int k = 0;
                        while (Box(t, k, out uint tt, out int ths, out int tsize))
                        {
                            ReadOnlySpan<byte> body = t.Slice(k + ths, tsize - ths);
                            if (tt == tfhd && body.Length >= 8)
                            {
                                uint flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                                int p = 8;
                                if ((flags & 0x1) != 0) { p += 8; }
                                if ((flags & 0x2) != 0) { p += 4; }
                                if ((flags & 0x8) != 0 && p + 4 <= body.Length) { defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                                if ((flags & 0x10) != 0) { p += 4; }
                                if ((flags & 0x20) != 0 && p + 4 <= body.Length) { defaultFlags = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); }
                            }
                            else if (tt == tfdt && body.Length >= 8)
                            {
                                baseTime = body[0] == 1 && body.Length >= 12 ? (long)BinaryPrimitives.ReadUInt64BigEndian(body[4..]) : BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                                if (first)
                                {
                                    start = baseTime;
                                }
                            }
                            else if (tt == trun && body.Length >= 8)
                            {
                                byte version = body[0];
                                uint flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                                uint count = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                                int p = 8;
                                if ((flags & 0x1) != 0) { p += 4; }
                                bool firstFlags = (flags & 0x4) != 0;
                                uint firstSampleFlags = 0;
                                if (firstFlags && p + 4 <= body.Length) { firstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); }
                                if (firstFlags) { p += 4; }
                                long dts = baseTime;
                                for (uint s = 0; s < count && p <= body.Length; s++)
                                {
                                    uint d = defaultDuration, f = defaultFlags;
                                    int cto = 0;
                                    if ((flags & 0x100) != 0) { if (p + 4 > body.Length) { break; } d = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                                    if ((flags & 0x200) != 0) { p += 4; }
                                    if ((flags & 0x400) != 0) { if (p + 4 > body.Length) { break; } f = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                                    if ((flags & 0x800) != 0) { if (p + 4 > body.Length) { break; } cto = version == 0 ? (int)BinaryPrimitives.ReadUInt32BigEndian(body[p..]) : BinaryPrimitives.ReadInt32BigEndian(body[p..]); p += 4; }
                                    if (s == 0 && firstFlags) { f = firstSampleFlags; }
                                    if (first && s == 0 && samples == 0)
                                    {
                                        // §8.8.3.1: bit 16 (0x10000) is sample_is_non_sync_sample.
                                        startsWithSync = (f & 0x10000) == 0;
                                    }

                                    earliest = Math.Min(earliest, dts + cto);
                                    dts += d;
                                    samples++;
                                }

                                duration += dts - baseTime;
                                next = dts;
                            }

                            k += tsize;
                        }

                        first = false;
                    }

                    c += csize;
                }
            }

            at += size;
        }

        if (earliest == long.MaxValue)
        {
            earliest = start;
        }

        return samples > 0;
    }
}

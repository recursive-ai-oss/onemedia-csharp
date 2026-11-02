namespace RecursiveAI.OneMedia.Streaming;

/// <summary>
/// Cuts packets into MPEG transport stream segments for HLS (RFC 8216 \u00a73.2): every selected track is multiplexed into one stream, and each segment is a
/// self-contained transport stream that starts with a PAT and a PMT and, for video, with a keyframe. Payload bytes are copied; the only repackaging is that of the
/// remux layer and the muxer (length-prefixed to Annex B, raw AAC to ADTS).
/// </summary>
/// <remarks>
/// The segment clock follows the first video track (else the first audio track): a segment ends at the first keyframe at or after the segment duration from its
/// start. Packets of the other tracks go to the segment that is open when they are written, so an input that interleaves audio well ahead of video puts a little
/// audio in the segment before the one its time belongs to; a player reads segments in order and does not notice. The one <see cref="SegmentedTrack"/> this reports
/// stands for the whole multiplex.
/// </remarks>
internal sealed class TsSegmenter : IDisposable
{
    private const long Clock = 90_000; // H.222.0 \u00a72.4.3.5: timestamps count a 90 kHz clock.

    private readonly ISegmentSink _sink;
    private readonly SegmenterOptions _options;
    private readonly SegmentCapture _capture;
    private readonly Remuxing.RemuxSession _session;
    private readonly SegmentedTrack _track;
    private readonly HashSet<int> _selected = [];
    private readonly int _referenceInput;
    private MediaTime _segmentStart = MediaTime.Unknown;
    private MediaTime _lastEnd = MediaTime.Unknown;
    private int _referenceOut = -1;
    private long _number;
    private int _samples;
    private bool _finished;

    public TsSegmenter(IReadOnlyList<MediaTrack> inputTracks, ISegmentSink sink, SegmenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputTracks);
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new SegmenterOptions();
        _capture = new SegmentCapture(_options.Limits, 1 << 18);
        if (!_options.SegmentDuration.IsKnown || _options.SegmentDuration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The segment duration must be positive.");
        }

        MediaTrack? reference = null;
        foreach (MediaTrack t in inputTracks)
        {
            if (t.Kind is not (TrackKind.Video or TrackKind.Audio) || t.IsProtected || (_options.Select is { } pick && !pick(t)))
            {
                continue;
            }

            _selected.Add(t.Index);
            if (reference is null || (reference.Kind != TrackKind.Video && t.Kind == TrackKind.Video))
            {
                reference = t;
            }
        }

        if (reference is null)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "The input has no video or audio track to segment.");
        }

        _referenceInput = reference.Index;
        _track = new SegmentedTrack(0, reference) { Timescale = Clock };
        _session = new Remuxing.RemuxSession(
            inputTracks,
            _capture,
            Remuxing.RemuxFormat.MpegTs,
            new Remuxing.RemuxOptions
            {
                Select = t => _selected.Contains(t.Index),
                Limits = _options.Limits,
                CreationTime = _options.CreationTime,
                Observer = (in PacketInfo p, ReadOnlySpan<byte> _) => Observe(in p),
            });
        if (_options.SessionIsoOptions is { } isoOptions)
        {
            _session.IsoOptions = t => isoOptions(_session, t);
        }
    }

    /// <summary>The one segmented track, standing for the multiplex.</summary>
    public IReadOnlyList<SegmentedTrack> Tracks => [_track];

    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new InvalidOperationException("The segmenter is finished.");
        }

        if (!_selected.Contains(info.TrackIndex))
        {
            return;
        }

        // An audio-only multiplex is cut at audio packets, which are all random access points.
        bool audioReference = _track.Input.Kind != TrackKind.Video;
        PacketInfo packet = audioReference && info.TrackIndex == _referenceInput ? info with { Flags = info.Flags | PacketAttributes.Keyframe } : info;
        _session.WritePacket(packet, data, cancellationToken);
    }

    // Runs just before each packet reaches the muxer, in the order the muxer sees them.
    private void Observe(in PacketInfo p)
    {
        MediaTime t = p.Dts.IsKnown ? p.Dts : p.Pts;
        if (_referenceOut >= 0 && p.TrackIndex == _referenceOut && t.IsKnown)
        {
            MediaTime end = p.Duration.IsKnown ? t.Add(p.Duration, MediaRounding.NearestAwayFromZero) : t;
            _lastEnd = _lastEnd.IsKnown ? MediaTime.Max(_lastEnd, end) : end;
            _samples++;
        }

        if (!_session.CanStartSegmentAt(p) || !t.IsKnown)
        {
            return;
        }

        _referenceOut = p.TrackIndex;

        if (!_segmentStart.IsKnown)
        {
            _segmentStart = t;
        }
        else if (t.Subtract(_segmentStart, MediaRounding.NearestAwayFromZero) >= _options.SegmentDuration)
        {
            _session.StartSegment(_capture);
            Deliver(t);
            _segmentStart = t;
        }
    }

    private void Deliver(MediaTime end)
    {
        ReadOnlySpan<byte> bytes = _capture.Bytes;
        if (bytes.IsEmpty || !_segmentStart.IsKnown)
        {
            return;
        }

        // A damaged input can carry a timestamp whose 90 kHz value does not fit in 64 bits.
        if (!_segmentStart.TryRescale(Clock, MediaRounding.NearestAwayFromZero, out MediaTime startTime) || !end.TryRescale(Clock, MediaRounding.NearestAwayFromZero, out MediaTime endTime))
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "A timestamp is outside the range a 90 kHz transport stream clock can hold.");
        }

        long start = startTime.Ticks;
        long duration = (long)Int128.Clamp((Int128)endTime.Ticks - start, 0, long.MaxValue);
        if (_track.Declared is null)
        {
            Describe(bytes);
        }

        _number++;
        _track.SegmentCount++;
        _track.MediaBytes += bytes.Length;
        _track.DurationTicks += duration;
        if (duration > 0)
        {
            _track.PeakBitsPerSecond = Math.Max(_track.PeakBitsPerSecond, (long)((Int128)bytes.Length * 8 * Clock / duration));
        }

        _sink.OnMediaSegment(_track, new SegmentInfo(_number, start, duration, start, _samples, true), bytes);
        _capture.Reset();
        _samples = 0;
    }

    // The first segment is read back with the library's own reader, so the codec strings and sizes describe what was written.
    private void Describe(ReadOnlySpan<byte> first)
    {
        try
        {
            using MediaFile file = Media.Open(first.ToArray());
            List<string> codecs = [];
            MediaTrack? video = null;
            foreach (MediaTrack t in file.Tracks)
            {
                if (t.Kind is TrackKind.Video or TrackKind.Audio && (t with { CodecTag = null }).Rfc6381Codec() is { Length: > 0 } c)
                {
                    codecs.Add(c);
                    video ??= t.Kind == TrackKind.Video ? t : null;
                }
            }

            _track.Declared = video ?? (file.Tracks.Count > 0 ? file.Tracks[0] : null);
            _track.Codecs = codecs.Count > 0 ? string.Join(',', codecs) : null;
        }
        catch (MediaException)
        {
            // The manifest then omits CODECS; the segments are still valid.
        }

        _track.Declared ??= _track.Input;
    }

    public SegmenterReport Finish(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The segmenter is already finished.");
        }

        _finished = true;
        Remuxing.RemuxReport report = _session.Finish(cancellationToken);
        // The last segment ends where the reference track's last packet ends.
        Deliver(_lastEnd.IsKnown ? _lastEnd : _segmentStart);
        List<Remuxing.RemuxLoss> losses = [];
        foreach (Remuxing.RemuxLoss loss in report.Losses)
        {
            if (loss.Reason != Remuxing.LossReason.Deselected)
            {
                losses.Add(loss);
            }
        }

        return new SegmenterReport([_track], losses);
    }

    public void Dispose() => _session.Dispose();
}

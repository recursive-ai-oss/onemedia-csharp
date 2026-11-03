using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Repair.Reconstruction;

/// <summary>One sample the scan found. Offsets and lengths are in the input; timing is worked out afterwards.</summary>
internal struct SampleRec
{
    public long Offset;
    public int Length;
    public int OrderLsb;
    public int Samples;
    public byte Track;
    public byte OrderBits;
    public byte Tid;
    public byte Flags;

    public const byte KeyFlag = 1;
    public const byte HasOrder = 2;
    public const byte ResetsOrder = 4;
    public const byte IsReference = 8;
    public const byte FieldPicture = 16;
    public const byte OrderTypeShift = 5; // two bits: pic_order_cnt_type (0..2)

    public readonly bool Key => (Flags & KeyFlag) != 0;
}

/// <summary>Reads windows of the input; the returned span is valid until the next call.</summary>
internal sealed class ByteWindow(IMediaSource source, long limit, CancellationToken cancellationToken)
{
    private byte[] _buffer = new byte[1 << 20];
    private long _start = -1;
    private int _length;

    /// <summary>Bytes of the input at <paramref name="position"/>, up to <paramref name="want"/> of them, fewer at the scan limit.</summary>
    public ReadOnlySpan<byte> Get(long position, int want)
    {
        if (position >= limit)
        {
            return default;
        }

        want = (int)Math.Min(want, limit - position);
        if (_start >= 0 && position >= _start && position + want <= _start + _length)
        {
            return _buffer.AsSpan((int)(position - _start), want);
        }

        int fill = Math.Max(want, 1 << 20);
        fill = (int)Math.Min(fill, limit - position);
        if (_buffer.Length < fill)
        {
            _buffer = new byte[Math.Max(fill, _buffer.Length * 2)];
        }

        int total = 0;
        while (total < fill)
        {
            int n = source.Read(position + total, _buffer.AsSpan(total, fill - total), cancellationToken);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        _start = position;
        _length = total;
        return _buffer.AsSpan(0, Math.Min(want, total));
    }

    /// <summary>True when a window of <paramref name="length"/> bytes at <paramref name="position"/> reaches the scan limit.</summary>
    public bool ReachesEnd(long position, int length) => position + length >= limit;
}

/// <summary>
/// Walks the media data of a file whose index is gone and cuts it into samples. At each position it asks the known tracks' framers,
/// strongest evidence first (video, whose NAL or OBU structure is long and checkable, then audio frame headers). A position no track
/// explains is skipped, and the walk resumes at the next position where a sample starts and is followed by another sample (or the end),
/// so one stray byte or a track the library cannot frame does not lose the rest of the file.
/// </summary>
internal sealed class MediaDataScanner
{
    private const int InitialWindow = 256 << 10;
    private const int ChainSteps = 6;

    private readonly long _start;
    private readonly long _end;
    private readonly ByteWindow _window;
    private readonly MediaLimits _limits;
    private readonly CancellationToken _cancellation;
    private readonly List<TrackFramer> _framers;
    private readonly List<IAudioFrameProbe> _probes;
    private readonly int _guessConfirm;
    private readonly bool _discoverAudio;
    private readonly PcmFramer? _pcm;
    private readonly int _pcmTrack;

    public MediaDataScanner(
        IMediaSource source,
        long start,
        long end,
        List<TrackFramer> framers,
        List<IAudioFrameProbe> probes,
        MediaLimits limits,
        bool discoverAudio,
        int guessConfirm,
        PcmFramer? pcm,
        int pcmTrack,
        CancellationToken cancellationToken)
    {
        _start = start;
        _end = end;
        _window = new ByteWindow(source, end, cancellationToken);
        _framers = framers;
        _probes = probes;
        _limits = limits;
        _cancellation = cancellationToken;
        _discoverAudio = discoverAudio;
        _guessConfirm = Math.Max(2, Math.Min(guessConfirm, ChainSteps));
        _pcm = pcm;
        _pcmTrack = pcmTrack;
    }

    private int[] _fillerGaps = [];
    private int _exactFillerCount;
    private const int GuessedGapMinSample = 64;

    /// <summary>
    /// Sizes (inclusive ranges) of the samples of reference tracks that cannot be framed, such as a timed-metadata track whose samples sit between
    /// the others. A chain of samples allows a gap of exactly such a size between two samples, in addition to the few bytes of <see cref="MaxChainGap"/>.
    /// A track whose samples all have one size is often written as a run of them, so multiples of that size (up to 64 KiB) count too.
    /// </summary>
    public void SetFillerSizes(IReadOnlyList<(int Min, int Max)> sizes)
    {
        SortedSet<int> gaps = [];
        SortedSet<int> widened = [];
        foreach ((int min, int max) in sizes)
        {
            if (min < 1)
            {
                continue;
            }

            if (min == max)
            {
                for (long g = min; g <= (64 << 10) && gaps.Count < 4096; g += min)
                {
                    gaps.Add((int)g);
                }
            }
            else
            {
                for (int g = min; g <= max; g++)
                {
                    gaps.Add(g);
                }

                // A reference from another recording of the same device shows only part of the sizes this recording uses (observed: a
                // timed-metadata track of 210 to 212 bytes in the reference, 206 to 232 in the cut file). Sizes up to 15 % beyond the range
                // are tried too, after the listed ones: the recording itself as the reference never needs them, and trying them first made it
                // take a size inside the item for the gap (a false sample follows by chance).
                int low = Math.Max(1, min - (int)((long)max * 15 / 100));
                int high = (int)Math.Min(64 << 10, max + ((long)max * 15 / 100));
                for (int g = low; g <= high; g++)
                {
                    widened.Add(g);
                }
            }
        }

        widened.ExceptWith(gaps);
        _exactFillerCount = gaps.Count;
        _fillerGaps = [.. gaps, .. widened];
    }

    public List<SampleRec> Samples { get; } = [];

    public List<ByteRange> Used { get; } = [];

    public List<SkippedRange> Skipped { get; } = [];

    public long SkippedCount { get; private set; }

    public long UsedBytes { get; private set; }

    /// <summary>Audio tracks found by trying the probes' guesses: (framer, setup).</summary>
    public List<(TrackFramer Framer, GuessedAudioSetup Setup, CodecId Codec, int SampleRate, int Channels)> Discovered { get; } = [];

    /// <summary>Called when a discovered audio track is added, so the caller can give it an output index.</summary>
    public Func<TrackFramer, int>? OnTrackDiscovered { get; set; }

    private Status TryOne(TrackFramer f, long pos, out FrameInfo info)
    {
        int want = InitialWindow;
        while (true)
        {
            ReadOnlySpan<byte> data = _window.Get(pos, want);
            bool atEnd = _window.ReachesEnd(pos, data.Length);
            FrameStatus s = f.TryFrame(data, atEnd, out info);
            if (s == FrameStatus.NeedMore)
            {
                if (want >= f.MaxSample + (1 << 20) || data.Length < want)
                {
                    // No more is available (or the sample is larger than allowed): treat as cut.
                    return atEnd ? Status.Partial : Status.No;
                }

                want = (int)Math.Min((long)want * 4, f.MaxSample + (1 << 20));
                continue;
            }

            return s switch
            {
                FrameStatus.Ok => Status.Ok,
                FrameStatus.Partial => Status.Partial,
                _ => Status.No,
            };
        }
    }

    private enum Status
    {
        No,
        Partial,
        Ok,
    }

    // Bytes of samples of a track no framer knows (a 16-byte descriptor track, a 4-byte timecode track) that may sit between two samples of
    // the tracks that are framed. A chain of samples tolerates a gap of up to this many bytes after its first sample.
    private const int MaxChainGap = 32;

    // Looks for a run of `steps` samples from pos that the given framers explain, one after the other (with up to maxGap unframed bytes allowed in front of each).
    private bool Chain(long pos, IReadOnlyList<TrackFramer> framers, int steps, TrackFramer? must, int maxGap = 0)
    {
        foreach (TrackFramer f in framers)
        {
            if (f is NalVideoFramer v)
            {
                v.CheckSequence = false;
            }
        }

        try
        {
            return ChainCore(pos, framers, steps, must, maxGap);
        }
        finally
        {
            foreach (TrackFramer f in framers)
            {
                if (f is NalVideoFramer v)
                {
                    v.CheckSequence = true;
                }
            }
        }
    }

    // Depth-first: a framer that accepts at one gap may lead nowhere while another position a few bytes on does, so every candidate is tried.
    private bool ChainCore(long pos, IReadOnlyList<TrackFramer> framers, int steps, TrackFramer? must, int maxGap)
    {
        int budget = 4096; // upper bound on framer trials for one chain, so damaged data cannot make a confirmation expensive
        return ChainStep(pos, framers, steps, must is null, must, maxGap, ref budget);
    }

    private bool ChainStep(long pos, IReadOnlyList<TrackFramer> framers, int stepsLeft, bool usedMust, TrackFramer? must, int maxGap, ref int budget)
    {
        if (stepsLeft == 0)
        {
            return usedMust;
        }

        if (pos >= _end)
        {
            return usedMust; // the input ends on a sample boundary
        }

        // Each sample may follow a few bytes of something unframed. Discovering a track allows none, since a wrong guess about
        // arbitrary data (PCM) must not be confirmed by chance.
        int count = maxGap + 1 + (maxGap > 0 ? _fillerGaps.Length : 0);
        for (int n = 0; n < count; n++)
        {
            int gap = n <= maxGap ? n : _fillerGaps[n - maxGap - 1];
            if (n > maxGap && gap <= maxGap)
            {
                continue;
            }

            if (pos + gap >= _end)
            {
                return usedMust && maxGap > 0;
            }

            foreach (TrackFramer f in framers)
            {
                if (--budget < 0)
                {
                    return false;
                }

                Status status = TryOne(f, pos + gap, out FrameInfo info);
                if (status == Status.Partial)
                {
                    // The input ends inside a sample of a known track: the run holds up to the end.
                    return usedMust;
                }

                if (status == Status.Ok
                    && ChainStep(pos + gap + info.Length, framers, stepsLeft - 1, usedMust || ReferenceEquals(f, must), must, maxGap, ref budget))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The size of the unframed samples at pos, when a confirmed sample of a known track starts right after a run of them.
    private int? FillerHop(long pos, List<TrackFramer> order)
    {
        for (int i = 0; i < _fillerGaps.Length; i++)
        {
            int gap = _fillerGaps[i];
            foreach (TrackFramer f in order)
            {
                // A size beyond the reference's range is a guess. Inside an item of the unframed track a few bytes can parse as a tiny sample that
                // the tolerance for stray bytes then carries to the real next sample, so a guessed size counts only for a sample of some length, with
                // a longer run after it.
                if (TryOne(f, pos + gap, out FrameInfo info) == Status.Ok && (i < _exactFillerCount || info.Length >= GuessedGapMinSample) && ConfirmResync(f, pos + gap, info, order, i >= _exactFillerCount))
                {
                    return gap;
                }
            }
        }

        return null;
    }

    public void Run()
    {
        long pos = _start;
        long unframedStart = -1;
        bool atBoundary = true;
        List<TrackFramer> order = OrderedFramers();
        // Some recorders put a small fixed-size item of a track nobody can frame (GoPro's 16-byte descriptor samples) after every sample. Once
        // the same gap has followed a few samples in a row, the next sample is looked for there first, so bytes inside the item that happen to
        // parse as a tiny sample are not taken for one.
        long lastEnd = -1;
        long lastGap = 0;
        int gapRun = 0;
        long jumpedFrom = -1;
        bool justJumped = false;
        while (pos < _end)
        {
            _cancellation.ThrowIfCancellationRequested();
            TrackFramer? hit = null;
            FrameInfo hitInfo = default;
            TrackFramer? partial = null;
            foreach (TrackFramer f in order)
            {
                Status s = TryOne(f, pos, out FrameInfo info);
                if (s == Status.Ok)
                {
                    if (unframedStart >= 0 && !ConfirmResync(f, pos, info, order))
                    {
                        continue; // a stray match inside damaged data
                    }

                    hit = f;
                    hitInfo = info;
                    break;
                }

                if (s == Status.Partial)
                {
                    partial ??= f;
                }
            }

            if (hit is null && atBoundary && _discoverAudio && unframedStart < 0 && TryDiscoverAudio(pos, order))
            {
                order = OrderedFramers();
                continue;
            }

            if (hit is null && _pcm is not null && (partial is null))
            {
                long run = PcmRun(pos, order);
                if (run > 0)
                {
                    if (unframedStart >= 0)
                    {
                        AddSkipped(unframedStart, pos, SkippedReason.Unframed, -1);
                        unframedStart = -1;
                    }

                    AddPcm(pos, run);
                    pos += run;
                    atBoundary = true;
                    continue;
                }
            }

            if (hit is not null)
            {
                if (unframedStart >= 0)
                {
                    long gap = pos - unframedStart;
                    if (unframedStart == lastEnd && gap == lastGap)
                    {
                        gapRun++;
                    }
                    else if (unframedStart == lastEnd)
                    {
                        lastGap = gap;
                        gapRun = 1;
                    }
                    else
                    {
                        gapRun = 0;
                    }

                    AddSkipped(unframedStart, pos, SkippedReason.Unframed, -1);
                    unframedStart = -1;
                }
                else
                {
                    lastGap = 0;
                    gapRun = 0;
                }

                justJumped = false;
                AddSample(hit, pos, hitInfo);
                pos += hitInfo.Length;
                lastEnd = pos;
                atBoundary = true;
                continue;
            }

            if (unframedStart < 0 && pos == lastEnd && partial is null && _fillerGaps.Length > 0 && FillerHop(pos, order) is { } hop)
            {
                // A sample of a track nobody can frame sits here (its size is in the reference's range): the next sample begins right after it,
                // before any byte inside it that looks like a sample start by chance.
                unframedStart = pos;
                pos += hop;
                atBoundary = false;
                continue;
            }

            if (unframedStart < 0 && pos == lastEnd && gapRun >= 2 && lastGap > 0 && jumpedFrom != lastEnd && pos + lastGap < _end)
            {
                // Nothing starts right here: try where the usual gap ends. If nothing starts there either, come back and scan every position.
                jumpedFrom = lastEnd;
                justJumped = true;
                unframedStart = pos;
                pos += lastGap;
                atBoundary = false;
                continue;
            }

            if (partial is not null)
            {
                // The input ends inside this sample. A framer reports this only for a sample that starts within its largest allowed size
                // of the end, and the rest of the input is that sample's bytes, so nothing after it is scanned for tiny chance matches.
                if (unframedStart >= 0)
                {
                    AddSkipped(unframedStart, pos, SkippedReason.Unframed, -1);
                    unframedStart = -1;
                }

                AddSkipped(pos, _end, SkippedReason.PartialSample, partial.Track);
                pos = _end;
                break;
            }

            if (justJumped)
            {
                // The jump found nothing: scan from the start of the gap, positions one by one, as usual.
                justJumped = false;
                pos = unframedStart;
            }

            if (unframedStart < 0)
            {
                unframedStart = pos;
            }

            pos++;
            atBoundary = false;
        }

        if (unframedStart >= 0 && unframedStart < _end)
        {
            AddSkipped(unframedStart, _end, SkippedReason.PartialSample, -1);
        }
    }

    private List<TrackFramer> OrderedFramers()
    {
        List<TrackFramer> list = [];
        foreach (TrackFramer f in _framers)
        {
            if (f.Kind == TrackKind.Video)
            {
                list.Add(f);
            }
        }

        foreach (TrackFramer f in _framers)
        {
            if (f.Kind != TrackKind.Video)
            {
                list.Add(f);
            }
        }

        return list;
    }

    // After damage, a match counts only when the sample after it is explained too (or the input ends there).
    // After a gap no track explains, a candidate counts only when it is confirmed. Other tracks need a neighbour that is explained. A video
    // access unit is long and checkable but still can occur by chance inside unframed data, so it needs the next video access unit to follow
    // within a short distance and to continue its numbering (H.264 frame_num), or the input to end.
    private bool ConfirmResync(TrackFramer f, long pos, FrameInfo info, List<TrackFramer> order, bool guessedGap = false)
    {
        if (f.Kind != TrackKind.Video || info.Length < 32)
        {
            // A sample of a few bytes (a silent audio frame) parses by chance inside other data far more easily than a long one: it needs a longer run after it.
            return Chain(pos + info.Length, order, guessedGap ? 3 : info.Length < 16 ? 4 : 2, null, MaxChainGap);
        }

        long after = pos + info.Length;
        if (_end - after < 64)
        {
            return true;
        }

        NalVideoFramer? nal = f as NalVideoFramer;
        int snapshot = nal?.SnapshotState() ?? 0;
        if (nal is not null)
        {
            nal.ReadPicOrder(_window.Get(pos, info.Length));
        }

        bool confirmed = false;
        long limit = Math.Min(_end, after + ResyncLookahead);
        for (long q = after; q < limit && !confirmed; q++)
        {
            if (q + 64 > _end)
            {
                confirmed = true; // the input ends before another access unit could be told
            }
            else
            {
                confirmed = TryOne(f, q, out FrameInfo next) == Status.Ok && next.Length >= 32;
            }
        }

        nal?.RestoreState(snapshot);
        return confirmed;
    }

    private const int ResyncLookahead = 256 << 10;

    private void AddSample(TrackFramer f, long pos, FrameInfo info)
    {
        ReadOnlySpan<byte> data = _window.Get(pos, info.Length);
        SampleRec rec = new()
        {
            Offset = pos,
            Length = info.Length,
            Track = (byte)f.Track,
            Samples = info.Samples,
            Flags = info.Key ? SampleRec.KeyFlag : (byte)0,
        };
        if (f is NalVideoFramer nal)
        {
            nal.Accept(data);
            PicOrder o = nal.ReadPicOrder(data);
            if (o.Has)
            {
                rec.Flags |= SampleRec.HasOrder;
                rec.OrderLsb = o.Lsb;
                rec.OrderBits = (byte)o.LsbBits;
                rec.Tid = (byte)o.TemporalId;
                rec.Flags |= (byte)(((o.ResetsOrder ? 1 : 0) * SampleRec.ResetsOrder) | ((o.IsReference ? 1 : 0) * SampleRec.IsReference) | ((o.FieldPicture ? 1 : 0) * SampleRec.FieldPicture) | (o.OrderType << SampleRec.OrderTypeShift));
            }
        }
        else
        {
            f.Accept(data);
        }

        Samples.Add(rec);
        UsedBytes += info.Length;
        if (Used.Count > 0 && Used[^1].End == pos)
        {
            Used[^1] = new ByteRange(Used[^1].Offset, Used[^1].Length + info.Length);
        }
        else
        {
            Used.Add(new ByteRange(pos, info.Length));
        }

        if (Samples.Count > _limits.MaxSampleTableEntries * Math.Max(1, _framers.Count))
        {
            throw new MediaLimitExceededException($"More than {_limits.MaxSampleTableEntries} samples per track.");
        }
    }

    private void AddSkipped(long from, long to, SkippedReason reason, int track)
    {
        if (to <= from)
        {
            return;
        }

        SkippedCount++;
        if (Skipped.Count < ReconstructionReport.MaxListedSkips)
        {
            Skipped.Add(new SkippedRange(new ByteRange(from, to - from), reason, track));
        }
    }

    // PCM has no framing: the bytes up to the next place another track's sample starts belong to it. A chunk of PCM ends between two frames of
    // sound, not on a packet boundary (a chunk of 48,048 frames is cut into packets of 1,024 with a short one at its end), so every frame
    // position is a possible end. A picture found inside sound by chance is common over that many positions, so a picture ends the run only when
    // the next picture follows it, as after any gap no track explains.
    private long PcmRun(long pos, List<TrackFramer> order)
    {
        int align = _pcm!.BlockAlign;
        long maxRun = Math.Min(_end - pos, 4L << 20);
        long run = 0;
        for (long q = align; q <= maxRun; q += align)
        {
            if (pos + q >= _end)
            {
                run = q;
                break;
            }

            bool other = false;
            foreach (TrackFramer f in order)
            {
                if (f.Kind == TrackKind.Video && TryOne(f, pos + q, out FrameInfo video) == Status.Ok && video.Length >= 32 && ConfirmResync(f, pos + q, video, order))
                {
                    other = true;
                    break;
                }
            }

            if (other)
            {
                run = q;
                break;
            }

            run = q;
        }

        // A run that ends where nothing follows (the cut) loses its last partial frame.
        return run - (run % align);
    }

    private void AddPcm(long pos, long run)
    {
        int packetBytes = _pcm!.BlockAlign * _pcm.PacketFrames;
        long at = pos;
        long left = run;
        while (left > 0)
        {
            int len = (int)Math.Min(packetBytes, left);
            Samples.Add(new SampleRec { Offset = at, Length = len, Track = (byte)_pcmTrack, Samples = len / _pcm.BlockAlign, Flags = SampleRec.KeyFlag });
            if (Used.Count > 0 && Used[^1].End == at)
            {
                Used[^1] = new ByteRange(Used[^1].Offset, Used[^1].Length + len);
            }
            else
            {
                Used.Add(new ByteRange(at, len));
            }

            at += len;
            left -= len;
        }

        UsedBytes += run;
    }

    // Without a reference, an audio track announces itself by a header that frames a run of samples interleaved with the known tracks.
    private bool TryDiscoverAudio(long pos, List<TrackFramer> order)
    {
        ReadOnlySpan<byte> head = _window.Get(pos, 64);
        if (head.Length < 4)
        {
            return false;
        }

        foreach ((TrackFramer framer, GuessedAudioSetup setup, CodecId codec, int rate, int channels) in Candidates(head.ToArray()))
        {
            List<TrackFramer> trial = [.. order, framer];
            if (Chain(pos, trial, ChainSteps, framer))
            {
                framer.Track = OnTrackDiscovered?.Invoke(framer) ?? -1;
                if (framer.Track < 0)
                {
                    return false;
                }

                _framers.Add(framer);
                Discovered.Add((framer, setup, codec, rate, channels));
                return true;
            }
        }

        return false;
    }

    private IEnumerable<(TrackFramer, GuessedAudioSetup, CodecId, int, int)> Candidates(byte[] head)
    {
        // MPEG audio: the header itself says the rate and layer, so the framer is exact.
        if (Codecs.MpegAudio.MpegAudioFrameHeader.TryParse(head, out Codecs.MpegAudio.MpegAudioFrameHeader mp) && mp.FrameLength > 0 && mp.Layer == 3 && !AlreadyHave(CodecId.Mp3))
        {
            yield return (AudioFramers.MpegAudio(mp.SamplesPerFrame, mp.SampleRate, mp.Layer), default, mp.Layer == 3 ? CodecId.Mp3 : CodecId.Mp2, mp.SampleRate, mp.Channels);
        }

        if (Codecs.Ac3.Ac3SyncFrame.TryParse(head, out Codecs.Ac3.Ac3SyncFrame ac) && ac.FrameSize > 0 && !AlreadyHave(ac.IsEac3 ? CodecId.Eac3 : CodecId.Ac3))
        {
            yield return (AudioFramers.Ac3(ac.SampleRate, ac.SamplesPerFrame), default, ac.IsEac3 ? CodecId.Eac3 : CodecId.Ac3, ac.SampleRate, ac.Channels);
        }

        if (!AlreadyHave(CodecId.AmrNb) && !AlreadyHave(CodecId.AmrWb))
        {
            yield return (AudioFramers.Amr(false), default, CodecId.AmrNb, 8000, 1);
            yield return (AudioFramers.Amr(true), default, CodecId.AmrWb, 16000, 1);
        }

        foreach (IAudioFrameProbe probe in _probes)
        {
            if (AlreadyHave(probe.Codec))
            {
                continue;
            }

            foreach (GuessedAudioSetup g in probe.Guesses())
            {
                IAudioFramer? f = probe.CreateFramer(g.Config.Span);
                if (f is not null)
                {
                    yield return (AudioFramers.FromPlugin(f), g, probe.Codec, g.SampleRate, g.Channels);
                }
            }
        }
    }

    private bool AlreadyHave(CodecId codec)
    {
        foreach ((TrackFramer _, GuessedAudioSetup _, CodecId c, int _, int _) in Discovered)
        {
            if (c == codec)
            {
                return true;
            }
        }

        return false;
    }
}

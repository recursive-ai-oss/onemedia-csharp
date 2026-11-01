using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Editing;

/// <summary>
/// Cuts ISO base media files (MP4, MOV, 3GP, M4A) without decoding or re-encoding: trims to a time range and splits at keyframes.
/// Packet payloads are copied unchanged; what shows at the start and end of a clip is decided by edit lists
/// (ISO/IEC 14496-12 §8.6.6), so the cut is exact to the frame (video) and to the sample (audio) while the file keeps the frames a decoder needs
/// before the first shown one. Memory use does not depend on the file size: packets stream from the demuxer to the muxer, and only the sample
/// tables of the output are kept. See <c>docs/trim.md</c> for the rules.
/// </summary>
public static class IsoCutter
{
    // Audio codecs need frames before the first shown one to settle their overlap or bit reservoir (Opus: the 80 ms pre-roll of RFC 7845 §4.2).
    private static readonly MediaTime OpusPreroll = new(80, 1000);

    /// <summary>
    /// Writes the part of <paramref name="source"/> between two presentation times to a new file.
    /// </summary>
    /// <param name="source">An open ISO file; it must support seeking. It is read from the keyframe before <paramref name="from"/> to the end of the range.</param>
    /// <param name="output">Where the clip goes. It is written to and flushed, not closed. A refusal before any packet is written leaves it untouched.</param>
    /// <param name="from">Start of the range on the source's presentation timeline (after its edit list). Negative values mean the start.</param>
    /// <param name="to">End of the range, exclusive: frames that start before it are kept.</param>
    /// <param name="mode">Frame-accurate (default) hides the frames between the keyframe and <paramref name="from"/> with an edit list; keyframe mode starts at the keyframe at or before <paramref name="from"/>.</param>
    /// <param name="options">Muxer options; the container flavour follows the source's brand when null. The movie timescale is chosen here.</param>
    /// <param name="cancellationToken">Cancels the cut; the output is then incomplete.</param>
    /// <returns>Where the clip starts, how long it is, and what was rounded or dropped.</returns>
    /// <exception cref="EditRefusedException">The range is empty or past the end, the source cannot seek, no track can be carried, or a track's edit list is too complex to map.</exception>
    /// <exception cref="MuxRefusedException">The muxer refuses the video track.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ClipReport Trim(IsoBmffDemuxer source, Stream output, MediaTime from, MediaTime to, TrimMode mode = TrimMode.FrameAccurate, IsoMuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        RequireSeekable(source);
        if (!from.IsKnown || !to.IsKnown || to <= from)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The end of a trim must be after its start.");
        }

        Plan plan = PlanTrim(source, from, to, mode);
        return new Run(source, output, options, plan, mode, from, cancellationToken).Execute();
    }

    /// <summary>
    /// Splits <paramref name="source"/> into consecutive parts that start on keyframes, at or before each requested time. Packets are
    /// partitioned: every video packet and every audio packet lands in exactly one part, so concatenating the parts gives the source's packets
    /// back (<see cref="IsoJoiner.Concat"/>).
    /// </summary>
    /// <param name="source">An open ISO file; it must support seeking.</param>
    /// <param name="openPart">Called once per part with its index; the cutter writes the part and flushes it, and the caller closes it.</param>
    /// <param name="at">Cut times, ascending, each greater than zero, on the source's presentation timeline. Two times that land on one keyframe give one cut.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part written (at least one).</returns>
    /// <exception cref="EditRefusedException"><paramref name="at"/> is not ascending or positive, or the source cannot be cut.</exception>
    public static IReadOnlyList<ClipReport> Split(IsoBmffDemuxer source, Func<int, Stream> openPart, IReadOnlyList<MediaTime> at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(openPart);
        ArgumentNullException.ThrowIfNull(at);
        RequireSeekable(source);
        MediaTime previous = MediaTime.Zero;
        foreach (MediaTime t in at)
        {
            if (!t.IsKnown || t <= previous)
            {
                throw new EditRefusedException(EditRefusalReason.InvalidSplit, "Split times must be positive and ascending.");
            }

            previous = t;
        }

        List<MediaTime> cuts = [];
        List<string> notes = [];
        int refIdx = RefVideo(source);
        if (refIdx < 0)
        {
            // Audio-only media: every packet is a sync point, so the cut lands on the packet that contains the time.
            foreach (MediaTime t in at)
            {
                MediaTime landing = LandOnPacket(source, t);
                if (landing.IsKnown && (cuts.Count == 0 || landing > cuts[^1]))
                {
                    cuts.Add(landing);
                }
            }
        }
        else
        {
            MediaTrack rt = source.Tracks[refIdx];
            foreach (MediaTime t in at)
            {
                long raw = ToMediaTicks(rt, t, rt.Timescale, MediaRounding.Ceiling);
                (long key, _, bool ok) = FindVideoStart(source, refIdx, raw, accurate: false);
                if (!ok)
                {
                    break;
                }

                MediaTime pres = rt.PresentationTime(new MediaTime(key, rt.Timescale));
                if (cuts.Count == 0 || pres > cuts[^1])
                {
                    cuts.Add(pres);
                }
            }
        }

        return SplitAt(source, openPart, cuts, notes, cancellationToken);
    }

    /// <summary>
    /// Splits <paramref name="source"/> into parts of about <paramref name="maxPartBytes"/> of payload each, cutting at the first keyframe after the
    /// budget is reached. A first pass reads the file once to find the cuts.
    /// </summary>
    /// <param name="source">An open ISO file; it must support seeking and have a video track.</param>
    /// <param name="openPart">Called once per part with its index.</param>
    /// <param name="maxPartBytes">The payload budget of a part, in bytes; a part is larger when the next keyframe is far off.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part written.</returns>
    /// <exception cref="EditRefusedException">The source has no video track to cut on, or cannot seek.</exception>
    public static IReadOnlyList<ClipReport> SplitBySize(IsoBmffDemuxer source, Func<int, Stream> openPart, long maxPartBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(openPart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartBytes);
        RequireSeekable(source);
        int refIdx = RefVideo(source);
        if (refIdx < 0)
        {
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "Splitting by size cuts on video keyframes, and the file has no video track.");
        }

        MediaTrack rt = source.Tracks[refIdx];
        List<MediaTime> cuts = [];
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            source.SetTrackEnabled(i, true);
        }

        source.Seek(new MediaTime(-1, 1), SeekMode.PreviousKeyframe);
        long bytes = 0;
        bool first = true;
        while (source.TryReadPacket(out PacketView v))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (v.Info.TrackIndex == refIdx && v.Info.IsKeyframe)
            {
                if (!first && bytes >= maxPartBytes)
                {
                    cuts.Add(rt.PresentationTime(v.Info.Pts));
                    bytes = 0;
                }

                first = false;
            }

            bytes += v.Data.Length;
        }

        return SplitAt(source, openPart, cuts, [], cancellationToken);
    }

    private static List<ClipReport> SplitAt(IsoBmffDemuxer source, Func<int, Stream> openPart, List<MediaTime> cuts, List<string> notes, CancellationToken cancellationToken)
    {
        // Part i runs from boundary i-1 (or the start) to boundary i (or the end); the video of a part is the decode-order run between two keyframes.
        List<ClipReport> reports = [];
        int refIdx = RefVideo(source);
        for (int i = 0; i <= cuts.Count; i++)
        {
            MediaTime? start = i == 0 ? null : cuts[i - 1];
            MediaTime? end = i == cuts.Count ? null : cuts[i];
            Plan plan = PlanPart(source, refIdx, start, end);
            Stream stream = openPart(i);
            ClipReport r = new Run(source, stream, null, plan, TrimMode.Keyframe, start ?? MediaTime.Zero, cancellationToken).Execute();
            reports.Add(r);
        }

        _ = notes;
        return reports;
    }

    // ---- planning --------------------------------------------------------------------------------------------------------------------

    private sealed record Plan
    {
        /// <summary>Index of the video track that drives the cut, or -1 for media without video.</summary>
        public int Ref { get; init; } = -1;

        /// <summary>Media time (reference track ticks) of the keyframe decoding starts at.</summary>
        public long KeyRaw { get; init; }

        /// <summary>Media time of the first shown video frame.</summary>
        public long FirstRaw { get; init; }

        /// <summary>The first shown frame on the presentation timeline; for media without video the requested start.</summary>
        public MediaTime StartPres { get; init; }

        /// <summary>Video packets whose time reaches this are not kept (trim).</summary>
        public long EndRaw { get; init; } = long.MaxValue;

        /// <summary>Video is cut before the first keyframe at or past this media time (split).</summary>
        public long? NextKeyRaw { get; init; }

        /// <summary>Other tracks: packets starting at or after this presentation time are not kept.</summary>
        public MediaTime? OtherEnd { get; init; }

        /// <summary>Split parts: other tracks keep only packets that start at or after this time (null: from the first packet).</summary>
        public MediaTime? OtherStart { get; init; }

        /// <summary>True for a split part: other tracks are partitioned by start time and get no pre-roll.</summary>
        public bool Partition { get; init; }
    }

    private static void RequireSeekable(IsoBmffDemuxer source)
    {
        if (!source.CanSeek)
        {
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "Cutting needs a source that can seek.");
        }
    }

    private static int RefVideo(IsoBmffDemuxer source)
    {
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            if (source.Tracks[i].Kind == TrackKind.Video && !source.Tracks[i].IsProtected)
            {
                return i;
            }
        }

        return -1;
    }

    private static Plan PlanTrim(IsoBmffDemuxer source, MediaTime from, MediaTime to, TrimMode mode)
    {
        CheckEdits(source);
        int refIdx = RefVideo(source);
        if (refIdx < 0)
        {
            MediaTime start = from < MediaTime.Zero ? MediaTime.Zero : from;
            if (mode == TrimMode.Keyframe)
            {
                MediaTime landing = LandOnPacket(source, start);
                if (landing.IsKnown)
                {
                    start = landing;
                }
            }

            return new Plan { StartPres = start, OtherEnd = to };
        }

        MediaTrack rt = source.Tracks[refIdx];
        long rawFrom = ToMediaTicks(rt, from, rt.Timescale, MediaRounding.Ceiling);
        (long key, long first, bool ok) = FindVideoStart(source, refIdx, rawFrom, mode == TrimMode.FrameAccurate);
        if (!ok)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The requested start is past the last video frame.", refIdx, rt.Codec);
        }

        long rawTo = ToMediaTicks(rt, to, rt.Timescale, MediaRounding.Ceiling);
        long shown = mode == TrimMode.FrameAccurate ? first : key;
        if (rawTo <= shown)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", refIdx, rt.Codec);
        }

        return new Plan
        {
            Ref = refIdx,
            KeyRaw = key,
            FirstRaw = shown,
            StartPres = rt.PresentationTime(new MediaTime(shown, rt.Timescale)),
            EndRaw = rawTo,
            OtherEnd = to,
        };
    }

    private static Plan PlanPart(IsoBmffDemuxer source, int refIdx, MediaTime? start, MediaTime? end)
    {
        CheckEdits(source);
        if (refIdx < 0)
        {
            // Audio-only: the part's packets are the ones that start inside [start, end).
            return new Plan { StartPres = start ?? MediaTime.Zero, OtherStart = start, OtherEnd = end, Partition = true };
        }

        MediaTrack rt = source.Tracks[refIdx];
        long key;
        if (start is null)
        {
            (key, _, bool okFirst) = FindVideoStart(source, refIdx, long.MinValue / 4, accurate: false);
            if (!okFirst)
            {
                throw new EditRefusedException(EditRefusalReason.NoKeyframe, "The video track has no keyframe.", refIdx, rt.Codec);
            }
        }
        else
        {
            long raw = ToMediaTicks(rt, start.Value, rt.Timescale, MediaRounding.Ceiling);
            (key, _, bool ok) = FindVideoStart(source, refIdx, raw, accurate: false);
            if (!ok)
            {
                throw new EditRefusedException(EditRefusalReason.NoKeyframe, "The video track has no keyframe at the cut.", refIdx, rt.Codec);
            }
        }

        long? next = null;
        if (end is MediaTime e)
        {
            next = ToMediaTicks(rt, e, rt.Timescale, MediaRounding.Ceiling);
        }

        return new Plan
        {
            Ref = refIdx,
            KeyRaw = key,
            FirstRaw = key,
            StartPres = rt.PresentationTime(new MediaTime(key, rt.Timescale)),
            NextKeyRaw = next,
            OtherStart = start is null ? null : rt.PresentationTime(new MediaTime(key, rt.Timescale)),
            OtherEnd = end,
            Partition = true,
        };
    }

    // A track whose edit list has several media segments (or a gap after media) maps presentation to media time in pieces; the cut would need to
    // rebuild the list, which is concatenation's job. Refuse instead of guessing.
    private static void CheckEdits(IsoBmffDemuxer source)
    {
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            IReadOnlyList<EditSegment>? edits = source.Tracks[i].Edit.Edits;
            if (edits is null)
            {
                continue;
            }

            int media = 0;
            bool gapAfterMedia = false;
            foreach (EditSegment s in edits)
            {
                if (s.IsEmpty)
                {
                    gapAfterMedia |= media > 0;
                }
                else
                {
                    media++;
                }
            }

            if (media > 1 || gapAfterMedia)
            {
                throw new EditRefusedException(EditRefusalReason.ComplexEditList, $"Track {i} has an edit list with {media} media segments; only one is supported.", i, source.Tracks[i].Codec);
            }
        }
    }

    /// <summary>
    /// Finds where a video clip starting at media time <paramref name="rawFrom"/> begins decoding and showing. Only the video track is read, from the keyframe
    /// before the time to the first frame at or after it (at most one GOP and its reorder window).
    /// </summary>
    private static (long Key, long First, bool Found) FindVideoStart(IsoBmffDemuxer d, int refIdx, long rawFrom, bool accurate)
    {
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            d.SetTrackEnabled(i, i == refIdx);
        }

        MediaTrack rt = d.Tracks[refIdx];
        MediaTime landing = d.Seek(new MediaTime(rawFrom, rt.Timescale), SeekMode.PreviousKeyframe);
        if (!landing.IsKnown)
        {
            return (0, 0, false);
        }

        long minGe = long.MaxValue;
        long landPts = 0;
        bool firstPacket = true;
        List<long> keys = [];
        while (d.TryReadPacket(out PacketView v))
        {
            long pts = Ticks(v.Info.Pts, rt.Timescale);
            long dts = v.Info.Dts.IsKnown ? Ticks(v.Info.Dts, rt.Timescale) : pts;
            if (firstPacket)
            {
                firstPacket = false;
                landPts = pts;
                if (!accurate)
                {
                    return (pts, pts, true);
                }
            }

            if (v.Info.IsKeyframe)
            {
                keys.Add(pts);
            }

            // Frames before the landing keyframe in presentation order (open GOP) cannot be decoded from it.
            if (pts >= rawFrom && pts >= landPts && pts < minGe)
            {
                minGe = pts;
            }

            if (minGe != long.MaxValue && dts >= minGe)
            {
                break;
            }
        }

        if (minGe == long.MaxValue)
        {
            return (0, 0, false);
        }

        long key = landPts;
        foreach (long k in keys)
        {
            if (k <= minGe && k > key)
            {
                key = k;
            }
        }

        return (key, minGe, true);
    }

    private static MediaTime LandOnPacket(IsoBmffDemuxer d, MediaTime pres)
    {
        int first = -1;
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            bool carry = first < 0;
            d.SetTrackEnabled(i, carry);
            if (carry)
            {
                first = i;
            }
        }

        if (first < 0)
        {
            return MediaTime.Unknown;
        }

        MediaTrack t = d.Tracks[first];
        long raw = ToMediaTicks(t, pres, t.Timescale, MediaRounding.Floor);
        MediaTime landing = d.Seek(new MediaTime(raw, t.Timescale), SeekMode.PreviousKeyframe);
        return landing.IsKnown ? t.PresentationTime(landing) : MediaTime.Unknown;
    }

    // ---- time helpers ----------------------------------------------------------------------------------------------------------------

    private static long Ticks(MediaTime t, long scale) =>
        t.Timescale == scale ? t.Ticks : t.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;

    /// <summary>
    /// Maps a presentation time back to the track's media time: the inverse of <see cref="MediaTrack.PresentationTime(MediaTime, MediaRounding)"/>
    /// (media time minus priming plus the leading offset).
    /// </summary>
    private static long ToMediaTicks(MediaTrack t, MediaTime pres, long scale, MediaRounding rounding)
    {
        long ticks = pres.Rescale(scale, rounding).Ticks;
        if (t.Edit.Edits is not null && t.Edit.StartTime.IsKnown && !t.Edit.StartTime.IsZero)
        {
            ticks -= t.Edit.StartTime.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
        }

        if (t.Edit.Priming.IsKnown && !t.Edit.Priming.IsZero && !t.Edit.PacketTimesIncludePriming)
        {
            ticks += t.Edit.Priming.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
        }

        return ticks;
    }

    private static MediaTime PrerollOf(MediaTrack t, MediaTime packetDuration)
    {
        MediaTime one = packetDuration.IsKnown ? packetDuration : new MediaTime(25, 1000);
        return t.Codec.Name switch
        {
            "opus" => OpusPreroll,
            "aac" or "vorbis" or "ac3" or "eac3" => one,
            "mp3" or "mp2" => new MediaTime(one.Ticks * 2, one.Timescale),
            _ => MediaTime.Zero,
        };
    }

    // ---- the cut ---------------------------------------------------------------------------------------------------------------------

    private sealed class TrackState(MediaTrack source, bool isRef)
    {
        public MediaTrack Source { get; } = source;

        public int Output { get; set; }

        public bool IsRef { get; } = isRef;

        public DeferredEdits Edits { get; } = new();

        public bool Done { get; set; }

        public bool Started { get; set; }

        public long Count { get; set; }

        public long FirstPts { get; set; }

        public long MaxEnd { get; set; } = long.MinValue;

        public long MinDropped { get; set; } = long.MaxValue;

        public bool ReachedNextKey { get; set; }

        /// <summary>True for timed text and data: their packet times were rebased to the clip start.</summary>
        public bool Rebased { get; set; }

        public int Hidden { get; set; }
    }

    private sealed class Run
    {
        private readonly IsoBmffDemuxer _d;
        private readonly Stream _output;
        private readonly IsoMuxOptions? _options;
        private readonly Plan _plan;
        private readonly TrimMode _mode;
        private readonly MediaTime _from;
        private readonly CancellationToken _ct;
        private readonly List<string> _notes = [];

        public Run(IsoBmffDemuxer d, Stream output, IsoMuxOptions? options, Plan plan, TrimMode mode, MediaTime from, CancellationToken ct)
        {
            _d = d;
            _output = output;
            _options = options;
            _plan = plan;
            _mode = mode;
            _from = from;
            _ct = ct;
        }

        public ClipReport Execute()
        {
            IReadOnlyList<MediaTrack> tracks = _d.Tracks;
            long movieScale = _plan.Ref >= 0 ? tracks[_plan.Ref].Timescale : FirstScale(tracks);
            IsoMuxOptions options = (_options ?? new IsoMuxOptions { Kind = KindOf(_d.FileType) }) with { MovieTimescale = movieScale };
            using IsoMuxer mux = new(_output, options);
            TrackState?[] states = new TrackState?[tracks.Count];
            for (int i = 0; i < tracks.Count; i++)
            {
                _d.SetTrackEnabled(i, false);
            }

            int carried = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                MediaTrack t = tracks[i];
                if (t.Kind == TrackKind.Video && i != _plan.Ref)
                {
                    _notes.Add($"Track {i}: a further video track is dropped; a cut follows the first video track.");
                    continue;
                }

                try
                {
                    TrackState st = new(t, i == _plan.Ref);
                    st.Output = mux.AddTrack(t with { Edit = t.Edit with { Edits = st.Edits } }, TrackOptions(_d.Movie.Tracks[i]));
                    states[i] = st;
                    _d.SetTrackEnabled(i, true);
                    carried++;
                }
                catch (MuxRefusedException e) when (i != _plan.Ref)
                {
                    _notes.Add($"Track {i}: not carried ({e.Reason}).");
                }
                catch (MuxRefusedException e)
                {
                    throw new EditRefusedException(EditRefusalReason.UnsupportedSource, $"The video track cannot be written: {e.Message}", i, t.Codec);
                }
            }

            if (carried == 0)
            {
                throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "No track can be carried to the output.");
            }

            Stream(mux, states);
            Finalize(states, movieScale, out MediaTime duration);
            MuxReport report = mux.Finish(_ct);
            int hidden = _plan.Ref >= 0 && states[_plan.Ref] is { } r ? r.Hidden : 0;
            return new ClipReport(_plan.StartPres, duration, hidden, _notes, report);
        }

        private void Stream(IsoMuxer mux, TrackState?[] states)
        {
            MediaTrack? rt = _plan.Ref >= 0 ? _d.Tracks[_plan.Ref] : null;
            if (rt is not null)
            {
                _d.Seek(new MediaTime(_plan.KeyRaw, rt.Timescale), SeekMode.PreviousKeyframe);
            }
            else
            {
                // Without video, start a little before the clip so the pre-roll packets are in reach.
                int first = Array.FindIndex(states, s => s is not null);
                MediaTrack t = _d.Tracks[first];
                long raw = ToMediaTicks(t, _plan.StartPres.Subtract(new MediaTime(250, 1000), MediaRounding.NearestAwayFromZero), t.Timescale, MediaRounding.Floor);
                _d.Seek(new MediaTime(raw, t.Timescale), SeekMode.PreviousKeyframe);
            }

            MediaTime margin = FrameMargin(rt);
            List<(PacketInfo Info, byte[] Data)> pending = [];
            while (true)
            {
                bool allDone = true;
                foreach (TrackState? s in states)
                {
                    if (s is not null && !s.Done)
                    {
                        allDone = false;
                        break;
                    }
                }

                if (allDone || !_d.TryReadPacket(out PacketView v))
                {
                    break;
                }

                _ct.ThrowIfCancellationRequested();
                if (states[v.Info.TrackIndex] is not { Done: false } st)
                {
                    continue;
                }

                if (st.IsRef)
                {
                    VideoPacket(mux, st, v, pending);
                }
                else
                {
                    OtherPacket(mux, st, v, margin);
                }
            }
        }

        private void VideoPacket(IsoMuxer mux, TrackState st, PacketView v, List<(PacketInfo Info, byte[] Data)> pending)
        {
            long scale = st.Source.Timescale;
            long pts = Ticks(v.Info.Pts, scale);
            long dts = v.Info.Dts.IsKnown ? Ticks(v.Info.Dts, scale) : pts;
            long dur = v.Info.Duration.IsKnown ? Ticks(v.Info.Duration, scale) : 0;
            if (st.Started && _plan.NextKeyRaw is long next && v.Info.IsKeyframe && pts >= next)
            {
                st.ReachedNextKey = true;
                st.Done = true;
                return;
            }

            st.Started = true;
            PacketInfo info = v.Info with { TrackIndex = st.Output };
            if (pts >= _plan.EndRaw)
            {
                // A frame past the end that was coded before a kept one is written only if a kept frame follows it (it may be a reference); trailing ones are dropped.
                pending.Add((info, v.Data.ToArray()));
                st.MinDropped = Math.Min(st.MinDropped, pts);
            }
            else
            {
                foreach ((PacketInfo p, byte[] data) in pending)
                {
                    mux.WritePacket(p, data, _ct);
                }

                pending.Clear();
                mux.WritePacket(info, v.Data, _ct);
                if (st.Count == 0)
                {
                    st.FirstPts = pts;
                }

                st.Count++;
                st.MaxEnd = Math.Max(st.MaxEnd, pts + dur);
                if (pts < _plan.FirstRaw)
                {
                    st.Hidden++;
                }
            }

            if (st.MinDropped != long.MaxValue && dts >= st.MinDropped)
            {
                st.Done = true;
            }
        }

        private void OtherPacket(IsoMuxer mux, TrackState st, PacketView v, MediaTime margin)
        {
            MediaTrack t = st.Source;
            MediaTime start = t.PresentationTime(v.Info.Pts);
            MediaTime dur = v.Info.Duration.IsKnown ? v.Info.Duration : MediaTime.Zero;
            MediaTime end = start.Add(dur, MediaRounding.NearestAwayFromZero);
            if (_plan.OtherEnd is MediaTime limit)
            {
                MediaTime bound = !_plan.Partition && _plan.Ref >= 0 ? limit.Add(margin, MediaRounding.NearestAwayFromZero) : limit;
                if (start >= bound)
                {
                    st.Done = true;
                    return;
                }
            }

            if (_plan.Partition)
            {
                if (_plan.OtherStart is MediaTime lower && start < lower)
                {
                    return;
                }
            }
            else if (end <= _plan.StartPres.Subtract(PrerollOf(t, v.Info.Duration), MediaRounding.NearestAwayFromZero))
            {
                return;
            }

            long scale = t.Timescale;
            long pts = Ticks(v.Info.Pts, scale);
            PacketInfo info = v.Info with { TrackIndex = st.Output };
            if (t.Kind is TrackKind.Subtitle or TrackKind.Data)
            {
                // Timed text and data carry their own timing: a cue that straddles the start is shortened to begin at the clip's start, one that
                // straddles the end is cut there, and the times are rebased so the track needs no edit that starts inside a sample (readers that round such
                // an edit to the sample's start would shift every cue). The payload is not touched.
                long origin = ToMediaTicks(t, _plan.StartPres, scale, MediaRounding.NearestAwayFromZero);
                long length = v.Info.Duration.IsKnown ? Ticks(v.Info.Duration, scale) : -1;
                long begin = pts;
                if (!_plan.Partition)
                {
                    if (begin < origin)
                    {
                        length = length < 0 ? length : Math.Max(0, length - (origin - begin));
                        begin = origin;
                    }

                    if (_plan.OtherEnd is MediaTime until && length >= 0)
                    {
                        length = Math.Max(0, Math.Min(length, ToMediaTicks(t, until, scale, MediaRounding.NearestAwayFromZero) - begin));
                    }
                }

                pts = begin - origin;
                MediaTime at = new(pts, scale);
                info = info with { Pts = at, Dts = at, Duration = length >= 0 ? new MediaTime(length, scale) : v.Info.Duration };
                st.Rebased = true;
            }

            mux.WritePacket(info, v.Data, _ct);
            if (st.Count == 0)
            {
                st.FirstPts = pts;
            }

            st.Count++;
            long d = info.Duration.IsKnown ? Ticks(info.Duration, scale) : 0;
            st.MaxEnd = Math.Max(st.MaxEnd, pts + d);
        }

        // Edit lists, in the form the muxer reads them at finish time.
        private void Finalize(TrackState?[] states, long movieScale, out MediaTime duration)
        {
            if (_plan.Ref >= 0)
            {
                TrackState rs = states[_plan.Ref]!;
                if (rs.Count == 0)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", _plan.Ref, rs.Source.Codec);
                }

                long scale = rs.Source.Timescale;
                long endRaw = rs.ReachedNextKey ? _plan.NextKeyRaw!.Value : rs.MinDropped != long.MaxValue ? rs.MinDropped : rs.MaxEnd;
                long len = endRaw - _plan.FirstRaw;
                if (len <= 0)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", _plan.Ref, rs.Source.Codec);
                }

                duration = new MediaTime(len, scale);
            }
            else
            {
                MediaTime end = _plan.OtherEnd ?? MediaTime.Zero;
                foreach (TrackState? s in states)
                {
                    if (s is { Count: > 0, Rebased: false })
                    {
                        MediaTime e = s.Source.PresentationTime(new MediaTime(s.MaxEnd, s.Source.Timescale));
                        if (e < end)
                        {
                            end = e;
                        }
                    }
                }

                if (end <= _plan.StartPres)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no media.");
                }

                duration = end.Subtract(_plan.StartPres, MediaRounding.NearestAwayFromZero);
            }

            MediaTime movieDuration = duration.Rescale(movieScale, MediaRounding.NearestAwayFromZero);
            foreach (TrackState? s in states)
            {
                if (s is null)
                {
                    continue;
                }

                MediaTrack t = s.Source;
                long scale = t.Timescale;
                if (s.IsRef)
                {
                    s.Edits.Set([new EditSegment(new MediaTime(_plan.FirstRaw, scale), movieDuration)]);
                    continue;
                }

                if (s.Count == 0)
                {
                    _notes.Add($"Track {t.Index}: no packet falls inside the range.");
                    s.Edits.Set([]);
                    continue;
                }

                long wanted = s.Rebased ? 0 : ToMediaTicks(t, _plan.StartPres, scale, MediaRounding.NearestAwayFromZero);
                if (t.Kind == TrackKind.Audio && _plan.StartPres.Rescale(scale, MediaRounding.Floor) != _plan.StartPres.Rescale(scale, MediaRounding.Ceiling))
                {
                    _notes.Add($"Track {t.Index}: the audio start is rounded to the nearest sample ({scale} Hz).");
                }

                if (s.FirstPts > wanted)
                {
                    // The track begins after the clip does: a gap first, so it stays in step with the video.
                    MediaTime lead = new MediaTime(s.FirstPts - wanted, scale).Rescale(movieScale, MediaRounding.NearestAwayFromZero);
                    s.Edits.Set([new EditSegment(MediaTime.Unknown, lead), new EditSegment(new MediaTime(s.FirstPts, scale), movieDuration - lead)]);
                }
                else
                {
                    s.Edits.Set([new EditSegment(new MediaTime(wanted, scale), movieDuration)]);
                }
            }

            if (_mode == TrimMode.FrameAccurate && _plan.Ref >= 0 && _plan.StartPres > _from)
            {
                _notes.Add($"The first shown frame starts {_plan.StartPres.Subtract(_from, MediaRounding.NearestAwayFromZero).ToTimeSpan().TotalMilliseconds:F3} ms after the requested start.");
            }
            else if (_mode == TrimMode.Keyframe && _plan.Ref >= 0 && _plan.StartPres < _from)
            {
                _notes.Add($"The clip starts on a keyframe {_from.Subtract(_plan.StartPres, MediaRounding.NearestAwayFromZero).ToTimeSpan().TotalMilliseconds:F3} ms before the requested start.");
            }
        }

        private static MediaTime FrameMargin(MediaTrack? rt)
        {
            if (rt?.Video is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } v)
            {
                return new MediaTime(v.FrameRateDenominator, v.FrameRateNumerator);
            }

            return new MediaTime(100, 1000);
        }

        private static long FirstScale(IReadOnlyList<MediaTrack> tracks) => tracks.Count > 0 ? tracks[0].Timescale : 1000;
    }

    // ---- sample entry carry-over (the boxes next to the codec configuration that players read) -----------------------------------------

    private static readonly HashSet<string> CarriedEntryBoxes = ["colr", "clap", "fiel", "gama", "btrt", "chnl", "srat", "mdcv", "clli", "pasp"];

    internal static IsoTrackOptions? TrackOptions(IsoTrack iso)
    {
        if (iso.SampleEntries.Count == 0)
        {
            return null;
        }

        List<ReadOnlyMemory<byte>> boxes = [];
        foreach (ExtensionBox e in iso.SampleEntries[0].Extensions)
        {
            if (CarriedEntryBoxes.Contains(e.TypeName))
            {
                boxes.Add(BoxBuilder.Create(e.Type, e.Payload.Span));
            }
        }

        return boxes.Count == 0 ? null : new IsoTrackOptions { SampleEntryBoxes = boxes };
    }

    internal static IsoFileKind KindOf(FileTypeBox? ft)
    {
        string major = ft?.MajorBrand ?? "isom";
        return major switch
        {
            "qt  " => IsoFileKind.Mov,
            "M4A " => IsoFileKind.M4a,
            _ when major.StartsWith("3gp", StringComparison.Ordinal) => IsoFileKind.ThreeGp,
            _ when major.StartsWith("3g2", StringComparison.Ordinal) => IsoFileKind.ThreeG2,
            _ => IsoFileKind.Mp4,
        };
    }
}

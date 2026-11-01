using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Editing;

/// <summary>
/// Cuts Matroska and WebM files without decoding or re-encoding: trims to a time range and splits at keyframes.
/// Packet payloads are copied unchanged. Matroska has no edit list, so a frame-accurate start is expressed the way RFC 9559 §11.2 allows:
/// the clip starts at the keyframe before the requested frame, the timeline is shifted so the first wanted frame is at zero, and the frames
/// before it carry negative Block timestamps, which a player decodes but does not show. See <c>docs/trim.md</c> for how players treat that.
/// Memory use does not depend on the file size.
/// </summary>
public static class MatroskaCutter
{
    // A decoder holds at most 16 pictures back for reordering, so a frame that shows earlier than one already seen turns up within 16 packets.
    private const int ReorderWindow = 16;

    private static readonly MediaTime OpusPreroll = new(80, 1000);

    /// <summary>Writes the part of <paramref name="source"/> between two presentation times to a new Matroska or WebM file.</summary>
    /// <param name="source">An open Matroska file; it must support seeking.</param>
    /// <param name="output">Where the clip goes (seekable). It is written to and flushed, not closed.</param>
    /// <param name="from">Start of the range; negative values mean the start.</param>
    /// <param name="to">End of the range, exclusive: frames that start before it are kept.</param>
    /// <param name="mode">Frame-accurate (default) keeps the frames from the keyframe before <paramref name="from"/> with negative timestamps; keyframe mode starts at that keyframe, at time zero.</param>
    /// <param name="options">Muxer options; WebM output follows a WebM source when null.</param>
    /// <param name="cancellationToken">Cancels the cut; the output is then incomplete.</param>
    /// <returns>Where the clip starts, how long it is, and what was rounded or dropped.</returns>
    /// <exception cref="EditRefusedException">The range is empty, the source cannot seek, or no track can be carried.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ClipReport Trim(MatroskaDemuxer source, Stream output, MediaTime from, MediaTime to, TrimMode mode = TrimMode.FrameAccurate, MatroskaMuxOptions? options = null, CancellationToken cancellationToken = default)
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
    /// Splits <paramref name="source"/> into consecutive parts that start on keyframes at or before each requested time. Every packet lands in
    /// exactly one part, and each part's timeline starts at zero, so <see cref="MatroskaJoiner.Concat"/> gives the source's packets back.
    /// </summary>
    /// <param name="source">An open Matroska file; it must support seeking.</param>
    /// <param name="openPart">Called once per part with its index; the caller closes the stream.</param>
    /// <param name="at">Cut times, ascending and positive. Two times that land on one keyframe give one cut.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part written.</returns>
    /// <exception cref="EditRefusedException"><paramref name="at"/> is not ascending or positive, or the source cannot be cut.</exception>
    public static IReadOnlyList<ClipReport> Split(MatroskaDemuxer source, Func<int, Stream> openPart, IReadOnlyList<MediaTime> at, CancellationToken cancellationToken = default)
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
        int refIdx = RefVideo(source);
        foreach (MediaTime t in at)
        {
            MediaTime cut;
            if (refIdx < 0)
            {
                cut = LandOnPacket(source, t);
                if (!cut.IsKnown)
                {
                    continue;
                }
            }
            else
            {
                (MediaTime key, _, bool ok) = FindVideoStart(source, refIdx, t, accurate: false);
                if (!ok)
                {
                    break;
                }

                cut = key;
            }

            if (cuts.Count == 0 || cut > cuts[^1])
            {
                cuts.Add(cut);
            }
        }

        return SplitAt(source, openPart, cuts, cancellationToken);
    }

    /// <summary>
    /// Splits <paramref name="source"/> into parts of about <paramref name="maxPartBytes"/> of payload each, cutting at the first video keyframe after
    /// the budget is reached. A first pass reads the file once to find the cuts.
    /// </summary>
    /// <param name="source">An open Matroska file with a video track; it must support seeking.</param>
    /// <param name="openPart">Called once per part with its index.</param>
    /// <param name="maxPartBytes">The payload budget of a part, in bytes.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part written.</returns>
    /// <exception cref="EditRefusedException">The source has no video track to cut on, or cannot seek.</exception>
    public static IReadOnlyList<ClipReport> SplitBySize(MatroskaDemuxer source, Func<int, Stream> openPart, long maxPartBytes, CancellationToken cancellationToken = default)
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
                    cuts.Add(v.Info.Pts);
                    bytes = 0;
                }

                first = false;
            }

            bytes += v.Data.Length;
        }

        return SplitAt(source, openPart, cuts, cancellationToken);
    }

    private static List<ClipReport> SplitAt(MatroskaDemuxer source, Func<int, Stream> openPart, List<MediaTime> cuts, CancellationToken cancellationToken)
    {
        List<ClipReport> reports = [];
        int refIdx = RefVideo(source);
        for (int i = 0; i <= cuts.Count; i++)
        {
            MediaTime? start = i == 0 ? null : cuts[i - 1];
            MediaTime? end = i == cuts.Count ? null : cuts[i];
            Plan plan = PlanPart(source, refIdx, start, end);
            reports.Add(new Run(source, openPart(i), null, plan, TrimMode.Keyframe, start ?? MediaTime.Zero, cancellationToken).Execute());
        }

        return reports;
    }

    // ---- planning --------------------------------------------------------------------------------------------------------------------

    private sealed record Plan
    {
        /// <summary>Index of the video track that drives the cut, or -1 for media without video.</summary>
        public int Ref { get; init; } = -1;

        /// <summary>Presentation time of the keyframe decoding starts at.</summary>
        public MediaTime Key { get; init; }

        /// <summary>The first shown frame; for media without video the requested start. This time becomes zero in the output.</summary>
        public MediaTime Start { get; init; }

        /// <summary>Video frames that start at or after this time are not kept (trim); unknown when the range runs to the end of the file.</summary>
        public MediaTime End { get; init; } = MediaTime.Unknown;

        /// <summary>Split: video is cut before the first keyframe at or after this time.</summary>
        public MediaTime NextKey { get; init; } = MediaTime.Unknown;

        /// <summary>Other tracks: packets starting at or after this time are not kept.</summary>
        public MediaTime OtherEnd { get; init; } = MediaTime.Unknown;

        /// <summary>Split parts: other tracks keep only packets that start at or after this time.</summary>
        public MediaTime OtherStart { get; init; } = MediaTime.Unknown;

        /// <summary>True for a split part: other tracks are partitioned by start time and get no pre-roll.</summary>
        public bool Partition { get; init; }
    }

    private static void RequireSeekable(MatroskaDemuxer source)
    {
        if (!source.CanSeek)
        {
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "Cutting needs a source that can seek.");
        }
    }

    private static int RefVideo(MatroskaDemuxer source)
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

    private static Plan PlanTrim(MatroskaDemuxer source, MediaTime from, MediaTime to, TrimMode mode)
    {
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

            return new Plan { Start = start, OtherEnd = to };
        }

        MediaTrack rt = source.Tracks[refIdx];
        (MediaTime key, MediaTime first, bool ok) = FindVideoStart(source, refIdx, from, mode == TrimMode.FrameAccurate);
        if (!ok)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The requested start is past the last video frame.", refIdx, rt.Codec);
        }

        MediaTime shown = mode == TrimMode.FrameAccurate ? first : key;
        if (to <= shown)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", refIdx, rt.Codec);
        }

        MediaTime end = FindVideoEnd(source, refIdx, to, key, shown);
        if (end <= shown)
        {
            throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", refIdx, rt.Codec);
        }

        return new Plan { Ref = refIdx, Key = key, Start = shown, End = to, OtherEnd = end };
    }

    private static Plan PlanPart(MatroskaDemuxer source, int refIdx, MediaTime? start, MediaTime? end)
    {
        if (refIdx < 0)
        {
            MediaTime s = start ?? MediaTime.Zero;
            return new Plan { Start = s, OtherStart = start ?? MediaTime.Unknown, OtherEnd = end ?? MediaTime.Unknown, Partition = true };
        }

        MediaTrack rt = source.Tracks[refIdx];
        MediaTime key;
        if (start is null)
        {
            (key, _, bool okFirst) = FindVideoStart(source, refIdx, new MediaTime(-1, 1), accurate: false);
            if (!okFirst)
            {
                throw new EditRefusedException(EditRefusalReason.NoKeyframe, "The video track has no keyframe.", refIdx, rt.Codec);
            }
        }
        else
        {
            key = start.Value;
        }

        return new Plan
        {
            Ref = refIdx,
            Key = key,
            Start = key,
            NextKey = end ?? MediaTime.Unknown,
            OtherStart = start is null ? MediaTime.Unknown : key,
            OtherEnd = end ?? MediaTime.Unknown,
            Partition = true,
        };
    }

    /// <summary>
    /// Finds where a clip starting at <paramref name="from"/> begins decoding and showing. Only the video track is read, from a keyframe before
    /// the time to the first frame at or after it (at most one group of pictures and its reorder window).
    /// </summary>
    private static (MediaTime Key, MediaTime First, bool Found) FindVideoStart(MatroskaDemuxer d, int refIdx, MediaTime from, bool accurate)
    {
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            d.SetTrackEnabled(i, i == refIdx);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            // The seek lands at a cue or cluster at or before the time; if no keyframe at or before the first shown frame turns up there, read from the start.
            d.Seek(attempt == 0 ? from : new MediaTime(-1, 1), SeekMode.PreviousKeyframe);
            MediaTime minGe = MediaTime.Unknown;
            int sinceBest = 0;
            List<MediaTime> keys = [];
            bool firstKey = true;
            while (d.TryReadPacket(out PacketView v))
            {
                MediaTime pts = v.Info.Pts;
                if (v.Info.IsKeyframe)
                {
                    keys.Add(pts);
                    if (firstKey)
                    {
                        firstKey = false;
                        if (!accurate && pts >= from)
                        {
                            // The keyframe the seek landed on is already past the time (the time is before the first key): that is the start.
                            return (pts, pts, true);
                        }
                    }
                }

                if (firstKey)
                {
                    continue; // packets before the first keyframe cannot be decoded
                }

                if (!accurate && v.Info.IsKeyframe && pts <= from)
                {
                    // Keep scanning keyframes until one passes the time; the last at or before it is the start.
                    minGe = pts;
                    continue;
                }

                if (!accurate && v.Info.IsKeyframe && pts > from)
                {
                    break;
                }

                if (accurate && pts >= from && (!minGe.IsKnown || pts < minGe))
                {
                    minGe = pts;
                    sinceBest = 0;
                }
                else if (accurate && minGe.IsKnown && ++sinceBest >= ReorderWindow)
                {
                    break;
                }
            }

            if (!accurate)
            {
                MediaTime best = MediaTime.Unknown;
                foreach (MediaTime k in keys)
                {
                    if (k <= from && (!best.IsKnown || k > best))
                    {
                        best = k;
                    }
                }

                if (best.IsKnown)
                {
                    return (best, best, true);
                }

                continue;
            }

            if (!minGe.IsKnown)
            {
                return (MediaTime.Zero, MediaTime.Zero, false);
            }

            MediaTime key = MediaTime.Unknown;
            foreach (MediaTime k in keys)
            {
                if (k <= minGe && (!key.IsKnown || k > key))
                {
                    key = k;
                }
            }

            if (key.IsKnown)
            {
                return (key, minGe, true);
            }
        }

        return (MediaTime.Zero, MediaTime.Zero, false);
    }

    /// <summary>The end of the last video frame kept when the clip stops at the first frame, in decode order, that shows at or after <paramref name="to"/>.</summary>
    private static MediaTime FindVideoEnd(MatroskaDemuxer d, int refIdx, MediaTime to, MediaTime key, MediaTime floor)
    {
        for (int i = 0; i < d.Tracks.Count; i++)
        {
            d.SetTrackEnabled(i, i == refIdx);
        }

        MediaTrack rt = d.Tracks[refIdx];
        MediaTime margin = FrameMargin(rt);
        // The first seek lands near the end; if it finds no frame before it (no cue points), the second starts from the clip's own keyframe.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            d.Seek(attempt == 0 ? EditTime.Minus(to, margin) : key, SeekMode.PreviousKeyframe);
            MediaTime end = MediaTime.Unknown;
            bool haveKey = false;
            while (d.TryReadPacket(out PacketView v))
            {
                haveKey |= v.Info.IsKeyframe;
                if (!haveKey)
                {
                    continue;
                }

                MediaTime pts = v.Info.Pts;
                if (pts >= to)
                {
                    break; // the cut follows decode order: nothing coded after this frame is kept
                }

                MediaTime dur = v.Info.Duration.IsKnown && !v.Info.Duration.IsZero ? v.Info.Duration : margin;
                MediaTime e = EditTime.Plus(pts, dur);
                if (!end.IsKnown || e > end)
                {
                    end = e;
                }
            }

            if (end.IsKnown && end > floor)
            {
                return end;
            }
        }

        return floor;
    }

    private static MediaTime LandOnPacket(MatroskaDemuxer d, MediaTime pres)
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

        MediaTime landing = d.Seek(pres, SeekMode.PreviousKeyframe);
        return landing;
    }

    private static MediaTime FrameMargin(MediaTrack? rt)
    {
        if (rt?.Video is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } v)
        {
            return new MediaTime(v.FrameRateDenominator, v.FrameRateNumerator);
        }

        return new MediaTime(100, 1000);
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

        public bool Done { get; set; }

        public bool Started { get; set; }

        public long Count { get; set; }

        public int Hidden { get; set; }

        public MediaTime MaxEnd { get; set; } = MediaTime.Unknown;

        public bool ReachedNextKey { get; set; }

        public bool CutByReorder { get; set; }
    }

    private sealed class Run(MatroskaDemuxer d, Stream output, MatroskaMuxOptions? options, Plan plan, TrimMode mode, MediaTime from, CancellationToken ct)
    {
        private readonly List<string> _notes = [];

        public ClipReport Execute()
        {
            IReadOnlyList<MediaTrack> tracks = d.Tracks;
            MatroskaMuxOptions opts = options ?? new MatroskaMuxOptions { Profile = d.Header.IsWebM ? MatroskaProfile.WebM : MatroskaProfile.Matroska };
            using MatroskaMuxer mux = new(output, opts);
            TrackState?[] states = new TrackState?[tracks.Count];
            for (int i = 0; i < tracks.Count; i++)
            {
                d.SetTrackEnabled(i, false);
            }

            int carried = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                MediaTrack t = tracks[i];
                if (t.Kind == TrackKind.Video && i != plan.Ref)
                {
                    _notes.Add($"Track {i}: a further video track is dropped; a cut follows the first video track.");
                    continue;
                }

                try
                {
                    TrackState st = new(t, i == plan.Ref);
                    st.Output = mux.AddTrack(t);
                    states[i] = st;
                    d.SetTrackEnabled(i, true);
                    carried++;
                }
                catch (MuxRefusedException e) when (i != plan.Ref)
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
            MediaTime duration = Finalize(states);
            MuxReport report = mux.Finish(ct);
            int hidden = plan.Ref >= 0 && states[plan.Ref] is { } r ? r.Hidden : 0;
            return new ClipReport(plan.Start, duration, hidden, _notes, report);
        }

        private void Stream(MatroskaMuxer mux, TrackState?[] states)
        {
            MediaTrack? rt = plan.Ref >= 0 ? d.Tracks[plan.Ref] : null;
            MediaTime margin = FrameMargin(rt);
            bool seekedToKey = false;
            if (rt is not null)
            {
                d.Seek(plan.Key, SeekMode.PreviousKeyframe);
            }
            else
            {
                // Without video, start a little before the clip so the pre-roll packets are in reach.
                d.Seek(EditTime.Minus(plan.Start, new MediaTime(250, 1000)), SeekMode.PreviousKeyframe);
                seekedToKey = true;
            }

            _ = seekedToKey;
            bool videoStarted = rt is null;
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

                if (allDone || !d.TryReadPacket(out PacketView v))
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                if (states[v.Info.TrackIndex] is not { Done: false } st)
                {
                    continue;
                }

                if (st.IsRef)
                {
                    if (!videoStarted)
                    {
                        // The seek may land before the keyframe the clip starts at; earlier packets belong to the previous group of pictures.
                        if (!(v.Info.IsKeyframe && v.Info.Pts == plan.Key))
                        {
                            continue;
                        }

                        videoStarted = true;
                    }

                    VideoPacket(mux, st, v);
                }
                else
                {
                    OtherPacket(mux, st, v, margin);
                }
            }

        }

        private PacketInfo Shifted(PacketInfo info, int output, MediaTime pts)
        {
            MediaTime dts = info.Dts.IsKnown ? EditTime.Minus(info.Dts, plan.Start) : MediaTime.Unknown;
            return info with { TrackIndex = output, Pts = pts, Dts = dts };
        }

        private void VideoPacket(MatroskaMuxer mux, TrackState st, PacketView v)
        {
            MediaTime pts = v.Info.Pts;
            if (st.Started && plan.NextKey.IsKnown && v.Info.IsKeyframe && pts >= plan.NextKey)
            {
                st.ReachedNextKey = true;
                st.Done = true;
                return;
            }

            st.Started = true;
            if (plan.End.IsKnown && pts >= plan.End)
            {
                // Matroska cannot hide a frame at the end, and a frame past the end that was coded before a kept one cannot be dropped without
                // breaking that one. The clip therefore stops at the first frame, in decode order, that shows at or after the end: frames that
                // show earlier but are coded later (they refer to it) go with it.
                st.Done = true;
                st.CutByReorder = true;
                return;
            }

            PacketInfo info = Shifted(v.Info, st.Output, EditTime.Minus(pts, plan.Start));
            mux.WritePacket(info, v.Data, ct);
            st.Count++;
            MediaTime dur = v.Info.Duration.IsKnown ? v.Info.Duration : MediaTime.Zero;
            MediaTime end = EditTime.Plus(pts, dur);
            if (!st.MaxEnd.IsKnown || end > st.MaxEnd)
            {
                st.MaxEnd = end;
            }

            if (pts < plan.Start)
            {
                st.Hidden++;
            }
        }

        private void OtherPacket(MatroskaMuxer mux, TrackState st, PacketView v, MediaTime margin)
        {
            _ = margin;
            MediaTrack t = st.Source;
            MediaTime start = v.Info.Pts;
            MediaTime dur = v.Info.Duration.IsKnown ? v.Info.Duration : MediaTime.Zero;
            MediaTime end = EditTime.Plus(start, dur);
            if (plan.OtherEnd.IsKnown && start >= plan.OtherEnd)
            {
                st.Done = true;
                return;
            }

            if (plan.Partition)
            {
                if (plan.OtherStart.IsKnown && start < plan.OtherStart)
                {
                    return;
                }
            }
            else if (end <= EditTime.Minus(plan.Start, PrerollOf(t, v.Info.Duration)))
            {
                return;
            }

            PacketInfo info = Shifted(v.Info, st.Output, EditTime.Minus(start, plan.Start));
            if (t.Kind is TrackKind.Subtitle or TrackKind.Data)
            {
                // Timed text carries its own timing: a cue that straddles the start begins at the clip's start, one that straddles the end is cut there.
                MediaTime begin = start;
                MediaTime length = v.Info.Duration;
                if (!plan.Partition)
                {
                    if (begin < plan.Start)
                    {
                        if (length.IsKnown)
                        {
                            length = EditTime.Minus(length, EditTime.Minus(plan.Start, begin));
                            if (length < MediaTime.Zero)
                            {
                                length = MediaTime.Zero;
                            }
                        }

                        begin = plan.Start;
                    }

                    if (plan.OtherEnd.IsKnown && length.IsKnown && EditTime.Plus(begin, length) > plan.OtherEnd)
                    {
                        length = EditTime.Minus(plan.OtherEnd, begin);
                    }
                }

                MediaTime at = EditTime.Minus(begin, plan.Start);
                info = info with { Pts = at, Dts = MediaTime.Unknown, Duration = length };
            }

            mux.WritePacket(info, v.Data, ct);
            st.Count++;
            if (!st.MaxEnd.IsKnown || end > st.MaxEnd)
            {
                st.MaxEnd = end;
            }
        }

        private MediaTime Finalize(TrackState?[] states)
        {
            MediaTime duration;
            if (plan.Ref >= 0)
            {
                TrackState rs = states[plan.Ref]!;
                if (rs.Count == 0)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", plan.Ref, rs.Source.Codec);
                }

                MediaTime endTime = rs.ReachedNextKey ? plan.NextKey : rs.MaxEnd;
                duration = EditTime.Minus(endTime, plan.Start);
                if (duration <= MediaTime.Zero)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no video frame.", plan.Ref, rs.Source.Codec);
                }
            }
            else
            {
                MediaTime end = plan.OtherEnd.IsKnown ? plan.OtherEnd : MediaTime.Unknown;
                foreach (TrackState? s in states)
                {
                    if (s is { Count: > 0 } && s.MaxEnd.IsKnown && (!end.IsKnown || s.MaxEnd < end))
                    {
                        end = s.MaxEnd;
                    }
                }

                if (!end.IsKnown || end <= plan.Start)
                {
                    throw new EditRefusedException(EditRefusalReason.EmptyRange, "The range holds no media.");
                }

                duration = EditTime.Minus(end, plan.Start);
            }

            if (plan.Ref >= 0 && plan.End.IsKnown && states[plan.Ref] is { CutByReorder: true, MaxEnd.IsKnown: true } vs && vs.MaxEnd < plan.End)
            {
                _notes.Add($"The clip ends {plan.End.Subtract(vs.MaxEnd, MediaRounding.NearestAwayFromZero).ToTimeSpan().TotalMilliseconds:F3} ms before the requested end: frames coded after the first frame that shows at or after it depend on that frame, and Matroska cannot hide it.");
            }

            foreach (TrackState? s in states)
            {
                if (s is { Count: 0, IsRef: false })
                {
                    _notes.Add($"Track {s.Source.Index}: no packet falls inside the range.");
                }
            }

            if (mode == TrimMode.FrameAccurate && plan.Ref >= 0 && plan.Start > from)
            {
                _notes.Add($"The first shown frame starts {plan.Start.Subtract(from, MediaRounding.NearestAwayFromZero).ToTimeSpan().TotalMilliseconds:F3} ms after the requested start.");
            }
            else if (mode == TrimMode.Keyframe && plan.Ref >= 0 && plan.Start < from)
            {
                _notes.Add($"The clip starts on a keyframe {from.Subtract(plan.Start, MediaRounding.NearestAwayFromZero).ToTimeSpan().TotalMilliseconds:F3} ms before the requested start.");
            }

            return duration;
        }
    }
}

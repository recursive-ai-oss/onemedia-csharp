using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Editing;

/// <summary>
/// Joins ISO base media files end to end without re-encoding. Packets are copied unchanged and their times are shifted so each input follows the
/// one before it on the track's media timeline; every input's edit list is kept (shifted) in the output's, so priming, hidden lead-in frames and
/// start offsets of each input stay as they were. Inputs must match track for track in everything the sample description holds
/// (codec, configuration bytes, timescale, channel layout, frame size); anything else is refused with the track named, because carrying it would
/// need a second sample description on one track (which the ISO muxer does not write) or a re-encode.
/// </summary>
public static class IsoJoiner
{
    /// <summary>Writes the inputs, in order, as one file.</summary>
    /// <param name="sources">Open ISO files; tracks are matched by index. The first one decides which tracks are carried and the container flavour.</param>
    /// <param name="output">Where the result goes; flushed, not closed.</param>
    /// <param name="options">Muxer options; the first input's brand decides the flavour when null.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    /// <returns>The result's length and what was dropped.</returns>
    /// <exception cref="EditRefusedException">An input lacks a track the first one has, or differs in codec, configuration, timescale, audio layout or frame size (<see cref="EditRefusalReason.NeedsReencode"/>; <see cref="EditRefusedException.TrackIndex"/> names the track).</exception>
    /// <exception cref="ArgumentException">No input was given.</exception>
    public static ClipReport Concat(IReadOnlyList<IsoBmffDemuxer> sources, Stream output, IsoMuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(output);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one input is needed.", nameof(sources));
        }

        IsoBmffDemuxer first = sources[0];
        long movieScale = first.Movie.Header.Timescale > 0 ? first.Movie.Header.Timescale : 1000;
        IsoMuxOptions opts = (options ?? new IsoMuxOptions { Kind = IsoCutter.KindOf(first.FileType) }) with { MovieTimescale = movieScale };
        List<string> notes = [];

        // Which tracks the muxer carries is decided by the first input; the others must match them. Everything is checked before a packet is written.
        using IsoMuxer mux = new(output, opts);
        Joined?[] joined = new Joined?[first.Tracks.Count];
        bool anyEdits = false;
        foreach (IsoBmffDemuxer s in sources)
        {
            foreach (MediaTrack t in s.Tracks)
            {
                anyEdits |= t.Edit.Edits is { Count: > 0 };
            }
        }

        for (int j = 0; j < first.Tracks.Count; j++)
        {
            MediaTrack t = first.Tracks[j];
            Joined st = new(t);
            try
            {
                st.Output = mux.AddTrack(t with { Edit = t.Edit with { Edits = st.Edits } }, IsoCutter.TrackOptions(first.Movie.Tracks[j]));
                joined[j] = st;
            }
            catch (MuxRefusedException e)
            {
                notes.Add($"Track {j}: not carried ({e.Reason}).");
            }
        }

        if (Array.TrueForAll(joined, j => j is null))
        {
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "No track can be carried to the output.");
        }

        for (int s = 1; s < sources.Count; s++)
        {
            for (int j = 0; j < joined.Length; j++)
            {
                if (joined[j] is null)
                {
                    continue;
                }

                if (j >= sources[s].Tracks.Count)
                {
                    throw new EditRefusedException(EditRefusalReason.NeedsReencode, $"Input {s} has no track {j} ({first.Tracks[j].Codec}).", j, first.Tracks[j].Codec);
                }

                string? difference = Difference(first.Tracks[j], sources[s].Tracks[j]);
                if (difference is not null)
                {
                    throw new EditRefusedException(EditRefusalReason.NeedsReencode, $"Track {j} ({first.Tracks[j].Codec}) of input {s} needs a re-encode to join input 0: {difference}.", j, first.Tracks[j].Codec);
                }
            }
        }

        for (int s = 0; s < sources.Count; s++)
        {
            AppendSource(mux, sources[s], joined, anyEdits, s > 0, cancellationToken);
        }

        MediaTime duration = MediaTime.Zero;
        foreach (Joined? j in joined)
        {
            if (j is null)
            {
                continue;
            }

            MediaTime sum = new(0, movieScale);
            foreach (EditSegment seg in j.Edits)
            {
                sum += seg.Duration.Rescale(movieScale, MediaRounding.NearestAwayFromZero);
            }

            if (sum > duration)
            {
                duration = sum;
            }
        }

        MuxReport report = mux.Finish(cancellationToken);
        return new ClipReport(MediaTime.Zero, duration, 0, notes, report);
    }

    private sealed class Joined(MediaTrack source)
    {
        public MediaTrack Source { get; } = source;

        public int Output { get; set; }

        public DeferredEdits Edits { get; } = new();

        public List<EditSegment> All { get; } = [];

        /// <summary>Where the next input starts on the output's media timeline, in track ticks.</summary>
        public long Offset { get; set; }
    }

    private static void AppendSource(IsoMuxer mux, IsoBmffDemuxer d, Joined?[] joined, bool anyEdits, bool rewind, CancellationToken ct)
    {
        int n = d.Tracks.Count;
        long[] firstDts = new long[n];
        long[] end = new long[n];
        long[] lastGap = new long[n];
        long[] lastDts = new long[n];
        bool[] seen = new bool[n];
        for (int i = 0; i < n; i++)
        {
            d.SetTrackEnabled(i, i < joined.Length && joined[i] is not null);
        }

        if (rewind && d.CanSeek)
        {
            d.Seek(new MediaTime(-1, 1), SeekMode.PreviousKeyframe);
        }

        while (d.TryReadPacket(out PacketView v))
        {
            ct.ThrowIfCancellationRequested();
            int i = v.Info.TrackIndex;
            if (i >= joined.Length || joined[i] is not { } j)
            {
                continue;
            }

            long scale = j.Source.Timescale;
            long pts = Ticks(v.Info.Pts, scale);
            long dts = v.Info.Dts.IsKnown ? Ticks(v.Info.Dts, scale) : pts;
            if (!seen[i])
            {
                seen[i] = true;
                firstDts[i] = dts;
            }
            else
            {
                lastGap[i] = dts - lastDts[i];
            }

            lastDts[i] = dts;
            long shift = j.Offset - firstDts[i];
            long dur = v.Info.Duration.IsKnown ? Ticks(v.Info.Duration, scale) : lastGap[i];
            end[i] = Math.Max(end[i], dts + shift + dur);
            PacketInfo info = v.Info with
            {
                TrackIndex = j.Output,
                Pts = new MediaTime(pts + shift, scale),
                Dts = new MediaTime(dts + shift, scale),
            };
            mux.WritePacket(info, v.Data, ct);
        }

        for (int i = 0; i < joined.Length; i++)
        {
            if (joined[i] is not { } j || !seen[i])
            {
                continue;
            }

            MediaTrack t = d.Tracks[i];
            long scale = j.Source.Timescale;
            long length = end[i] - j.Offset;
            long shift = j.Offset - firstDts[i];
            if (t.Edit.Edits is { Count: > 0 } edits)
            {
                foreach (EditSegment seg in edits)
                {
                    if (seg.IsEmpty)
                    {
                        j.All.Add(seg);
                        continue;
                    }

                    long start = Ticks(seg.MediaStart, scale);
                    MediaTime duration = seg.Duration;
                    if (duration.Ticks == 0)
                    {
                        // A last segment of length zero means "to the end of the media".
                        duration = new MediaTime(Math.Max(0, length - (start - firstDts[i])), scale);
                    }

                    j.All.Add(new EditSegment(new MediaTime(start + shift, scale), duration));
                }
            }
            else if (anyEdits)
            {
                j.All.Add(new EditSegment(new MediaTime(j.Offset, scale), new MediaTime(length, scale)));
            }

            j.Offset = end[i];
        }

        foreach (Joined? j in joined)
        {
            j?.Edits.Set(Merge(j.All, j.Source.Timescale));
        }
    }

    // Neighbouring segments that continue each other in media time are one segment. Joining the parts of a split then gives back the edit list the
    // whole file had, and readers that only follow one segment (or repeat samples at every boundary) see a plain file. A gap of exactly the media the
    // first segment stopped short of (a split part ends mid-packet and the next one starts a few samples later) is closed the same way.
    private static List<EditSegment> Merge(List<EditSegment> segments, long scale)
    {
        List<EditSegment> merged = [];
        int i = 0;
        while (i < segments.Count)
        {
            EditSegment a = segments[i++];
            while (!a.IsEmpty && i < segments.Count)
            {
                int next = i;
                long gap = 0;
                if (segments[next].IsEmpty)
                {
                    gap = segments[next].Duration.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
                    next++;
                }

                if (next >= segments.Count || segments[next].IsEmpty)
                {
                    break;
                }

                EditSegment b = segments[next];
                long aEnd = a.MediaStart.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks + a.Duration.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
                long bStart = b.MediaStart.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;
                long tolerance = Math.Max(1, scale / Math.Max(1, a.Duration.Timescale)) + 1;
                if (Math.Abs(bStart - gap - aEnd) > tolerance)
                {
                    break;
                }

                long total = a.Duration.Ticks + (gap > 0 ? segments[i].Duration.Rescale(a.Duration.Timescale, MediaRounding.NearestAwayFromZero).Ticks : 0) + b.Duration.Rescale(a.Duration.Timescale, MediaRounding.NearestAwayFromZero).Ticks;
                a = new EditSegment(a.MediaStart, new MediaTime(total, a.Duration.Timescale));
                i = next + 1;
            }

            merged.Add(a);
        }

        return merged;
    }

    private static long Ticks(MediaTime t, long scale) =>
        t.Timescale == scale ? t.Ticks : t.Rescale(scale, MediaRounding.NearestAwayFromZero).Ticks;

    private static string? Difference(MediaTrack a, MediaTrack b)
    {
        if (a.Kind != b.Kind)
        {
            return $"the track kind differs ({a.Kind} and {b.Kind})";
        }

        if (a.Codec != b.Codec || !string.Equals(a.CodecTag, b.CodecTag, StringComparison.Ordinal))
        {
            return $"the codec differs ({a.CodecTag ?? a.Codec.Name} and {b.CodecTag ?? b.Codec.Name})";
        }

        if (a.Timescale != b.Timescale)
        {
            return $"the timescale differs ({a.Timescale} and {b.Timescale})";
        }

        if (!a.Configuration.Data.Span.SequenceEqual(b.Configuration.Data.Span))
        {
            return "the codec configuration differs (parameter sets or setup bytes); one track would need two sample descriptions";
        }

        if (a.Audio != b.Audio)
        {
            return $"the audio format differs ({a.Audio?.SampleRate} Hz x {a.Audio?.Channels} and {b.Audio?.SampleRate} Hz x {b.Audio?.Channels})";
        }

        if (a.Video is { } va && b.Video is { } vb && (va.Width != vb.Width || va.Height != vb.Height || va.RotationDegrees != vb.RotationDegrees
            || va.PixelAspectNumerator != vb.PixelAspectNumerator || va.PixelAspectDenominator != vb.PixelAspectDenominator))
        {
            return $"the frame size, rotation or aspect differs ({va.Width}x{va.Height} and {vb.Width}x{vb.Height})";
        }

        return null;
    }
}

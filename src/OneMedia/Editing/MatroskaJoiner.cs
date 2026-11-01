using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Editing;

/// <summary>
/// Joins Matroska and WebM files whose tracks match into one file without re-encoding. Each input's timeline is placed after the
/// previous one's end; payloads are copied unchanged. A track whose codec configuration differs is refused (Matroska keeps one CodecPrivate
/// per track), naming the track.
/// </summary>
public static class MatroskaJoiner
{
    /// <summary>Writes the inputs, in order, to one Matroska or WebM file.</summary>
    /// <param name="sources">Open Matroska files; the first decides which tracks are carried, the others must match them track for track (by index). Inputs after the first must support seeking.</param>
    /// <param name="output">Where the result goes (seekable). It is written to and flushed, not closed.</param>
    /// <param name="options">Muxer options; WebM output follows a WebM first input when null.</param>
    /// <param name="cancellationToken">Cancels the join; the output is then incomplete.</param>
    /// <returns>The joined length and the muxer's report.</returns>
    /// <exception cref="EditRefusedException">An input has fewer tracks or a track whose codec, configuration, frame size or audio format differs (<see cref="EditRefusalReason.NeedsReencode"/>); nothing is written then.</exception>
    public static ClipReport Concat(IReadOnlyList<MatroskaDemuxer> sources, Stream output, MatroskaMuxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(output);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one input is needed.", nameof(sources));
        }

        MatroskaDemuxer first = sources[0];
        MatroskaMuxOptions opts = options ?? new MatroskaMuxOptions { Profile = first.Header.IsWebM ? MatroskaProfile.WebM : MatroskaProfile.Matroska };
        List<string> notes = [];
        using MatroskaMuxer mux = new(output, opts);
        int[] map = new int[first.Tracks.Count];
        for (int j = 0; j < map.Length; j++)
        {
            map[j] = -1;
            try
            {
                map[j] = mux.AddTrack(first.Tracks[j]);
            }
            catch (MuxRefusedException e)
            {
                notes.Add($"Track {j}: not carried ({e.Reason}).");
            }
        }

        if (Array.TrueForAll(map, m => m < 0))
        {
            throw new EditRefusedException(EditRefusalReason.UnsupportedSource, "No track can be carried to the output.");
        }

        for (int s = 1; s < sources.Count; s++)
        {
            for (int j = 0; j < map.Length; j++)
            {
                if (map[j] < 0)
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

        MediaTime offset = MediaTime.Zero;
        for (int s = 0; s < sources.Count; s++)
        {
            offset = Append(mux, sources[s], map, offset, s > 0, cancellationToken);
        }

        MuxReport report = mux.Finish(cancellationToken);
        return new ClipReport(MediaTime.Zero, offset, 0, notes, report);
    }

    // Returns the offset the next input starts at: this input's offset plus its length (the end of its video, or of its longest track without video).
    private static MediaTime Append(MatroskaMuxer mux, MatroskaDemuxer d, int[] map, MediaTime offset, bool rewind, CancellationToken ct)
    {
        int refIdx = -1;
        for (int i = 0; i < d.Tracks.Count && i < map.Length; i++)
        {
            d.SetTrackEnabled(i, map[i] >= 0);
            if (refIdx < 0 && map[i] >= 0 && d.Tracks[i].Kind == TrackKind.Video)
            {
                refIdx = i;
            }
        }

        for (int i = map.Length; i < d.Tracks.Count; i++)
        {
            d.SetTrackEnabled(i, false);
        }

        if (rewind && d.CanSeek)
        {
            d.Seek(new MediaTime(-1, 1), SeekMode.PreviousKeyframe);
        }

        MediaTime videoEnd = MediaTime.Unknown;
        MediaTime anyEnd = MediaTime.Unknown;
        while (d.TryReadPacket(out PacketView v))
        {
            ct.ThrowIfCancellationRequested();
            int i = v.Info.TrackIndex;
            if (i >= map.Length || map[i] < 0)
            {
                continue;
            }

            MediaTime dur = v.Info.Duration.IsKnown ? v.Info.Duration : MediaTime.Zero;
            MediaTime end = EditTime.Plus(v.Info.Pts, dur);
            if (!anyEnd.IsKnown || end > anyEnd)
            {
                anyEnd = end;
            }

            if (i == refIdx && (!videoEnd.IsKnown || end > videoEnd))
            {
                videoEnd = end;
            }

            PacketInfo info = v.Info with
            {
                TrackIndex = map[i],
                Pts = EditTime.Plus(v.Info.Pts, offset),
                Dts = v.Info.Dts.IsKnown ? EditTime.Plus(v.Info.Dts, offset) : MediaTime.Unknown,
            };
            mux.WritePacket(info, v.Data, ct);
        }

        MediaTime length = videoEnd.IsKnown ? videoEnd : anyEnd;
        return length.IsKnown ? EditTime.Plus(offset, length) : offset;
    }

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

        if (!a.Configuration.Data.Span.SequenceEqual(b.Configuration.Data.Span))
        {
            return "the codec configuration differs (parameter sets or setup bytes); the track would need two CodecPrivate values";
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

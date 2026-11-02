namespace RecursiveAI.OneMedia.Captions;

/// <summary>A document moved on the timeline, with what the move cost.</summary>
/// <param name="Document">The shifted document.</param>
/// <param name="Report">Cues dropped because they end before time zero, and cues whose start was clamped to zero.</param>
public sealed record AlignedSubtitles(SubtitleDocument Document, ConversionReport Report);

/// <summary>
/// Moves subtitle timing onto another timeline: by an explicit offset, or to where a media file's first frame is presented.
/// </summary>
public static class SubtitleAlignment
{
    /// <summary>Adds <paramref name="offset"/> to every cue time and word time. Cues that end at or before zero afterwards are dropped; a start before zero becomes zero.</summary>
    /// <param name="document">The document.</param>
    /// <param name="offset">The amount to add; negative moves cues earlier. Exact: the result keeps a timescale in which the sum is whole where one exists, otherwise it is rounded to the nearest tick of the cue's own timescale.</param>
    /// <returns>The shifted document and a report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static AlignedSubtitles Shift(SubtitleDocument document, MediaTime offset)
    {
        ArgumentNullException.ThrowIfNull(document);
        var report = new ConversionReport { Source = document.Format, Target = document.Format ?? "" };
        var cues = new List<SubtitleCue>(document.Cues.Count);
        for (int i = 0; i < document.Cues.Count; i++)
        {
            SubtitleCue cue = document.Cues[i];
            MediaTime start = Plus(cue.Start, offset);
            MediaTime end = Plus(cue.End, offset);
            if (end <= MediaTime.Zero && offset < MediaTime.Zero)
            {
                report.Add(i, SubtitleFeatures.Timing, LossKind.Dropped, "The cue ends before time zero after the shift.");
                continue;
            }

            if (start < MediaTime.Zero)
            {
                report.Add(i, SubtitleFeatures.Timing, LossKind.Approximated, "The cue starts before time zero after the shift; its start is now zero.");
                start = MediaTime.Zero;
            }

            var spans = new List<SubtitleSpan>(cue.Spans.Count);
            foreach (SubtitleSpan s in cue.Spans)
            {
                spans.Add(s.WordStart is null ? s : s with
                {
                    WordStart = Plus(s.WordStart.Value, offset),
                    WordEnd = s.WordEnd is { } we ? Plus(we, offset) : null,
                });
            }

            cues.Add(cue with { Start = start, End = end, Spans = SubtitleList.From(spans) });
        }

        return new AlignedSubtitles(document with { Cues = SubtitleList.From(cues) }, report);
    }

    /// <summary>
    /// The offset that puts sidecar time zero on a media file's timeline: where the first presented frame of the video track (or, without
    /// video, of the first track) sits, from its edit list or start offset. Zero when the container says nothing.
    /// </summary>
    /// <param name="media">An opened media file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is null.</exception>
    public static MediaTime OffsetFor(MediaFile media)
    {
        ArgumentNullException.ThrowIfNull(media);
        MediaTrack? track = media.Video ?? (media.Tracks.Count > 0 ? media.Tracks[0] : null);
        if (track is null)
        {
            return MediaTime.Zero;
        }

        MediaTime start = track.Edit.StartTime;
        return start.IsKnown ? start : MediaTime.Zero;
    }

    /// <summary>Shifts a sidecar document by <see cref="OffsetFor"/>, for a sidecar timed from the first presented frame of <paramref name="media"/>.</summary>
    /// <param name="document">The sidecar.</param>
    /// <param name="media">The media file whose timeline to use.</param>
    public static AlignedSubtitles ToMediaTimeline(SubtitleDocument document, MediaFile media) => Shift(document, OffsetFor(media));

    private static MediaTime Plus(MediaTime a, MediaTime b) => SubtitleTimes.Add(a, b);
}

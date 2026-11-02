using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Repair;

internal static partial class IsoRepair
{
    private const double MinRecoveredFraction = 0.02;

    // A file without a (whole) movie box is rebuilt from its media data by IsoReconstructor; this maps its report onto the common one.
    // The output is a new, complete file, so the status is Repaired unless a track the reference described came back empty or the scan
    // left a large share of the media data unframed.
    private static RepairReport Reconstruct(RepairInput input, Stream destination, MediaLimits limits, IMediaSource? reference, ReconstructionOptions? reconstruction, RepairReportBuilder report, CancellationToken ct)
    {
        ReconstructionOptions options = (reconstruction ?? new ReconstructionOptions()) with { Limits = limits };
        ReconstructionReport r;
        try
        {
            r = IsoReconstructor.Reconstruct(input.Source, destination, reference, options, ct);
        }
        catch (ReconstructionException e)
        {
            report.Warn("The movie box (moov) is missing or cut off and could not be rebuilt from the media data: " + e.Message);
            if (reference is null)
            {
                report.Warn("A reference file from the same device (onemedia repair <in> <out> --reference <file>) may let the recording be rebuilt.");
            }

            return report.Build(RepairStatus.Unrepairable, 0);
        }

        // Almost nothing framed: the codec setup found in the stream was probably a coincidence (or the reference does not belong to this
        // recording), and a file of a few stray samples helps nobody.
        if (r.RecoveredFraction < MinRecoveredFraction)
        {
            destination.SetLength(0);
            destination.Position = 0;
            report.Warn($"Only {100.0 * r.RecoveredFraction:F2} % of the media data could be cut into samples; the codec setup is probably wrong or missing. A reference file from the same device may let the recording be rebuilt.");
            return report.Build(RepairStatus.Unrepairable, 0);
        }

        long at = r.ScanEnd;
        report.Add(RepairAction.Rebuilt, "moov", -1, at, 0, "The movie box was rebuilt from the media data: " + r.Tracks.Count + " track(s), "
            + (reference is null ? "codec setup taken from the stream" : "codec setup taken from the reference file") + ".", ToRepair(r.Confidence));
        bool emptyTrack = false;
        foreach (ReconstructedTrack t in r.Tracks)
        {
            long dropped = 0;
            foreach (SkippedRange s in r.SkippedRanges)
            {
                if (s.Reason == SkippedReason.PartialSample && s.TrackIndex == t.Index)
                {
                    dropped++;
                }
            }

            emptyTrack |= t.Samples == 0;
            report.Tracks.Add(new RepairTrackReport(t.Index, t.Codec.Name, t.Samples, dropped, ToRepair(t.Confidence), t.Ranges ?? []));
            foreach (string n in t.Notes)
            {
                report.Warn("track " + t.Index + ": " + n);
            }
        }

        foreach (SkippedRange s in r.SkippedRanges)
        {
            string why = s.Reason switch
            {
                SkippedReason.PartialSample => "A sample cut off by the end of the file was left out.",
                SkippedReason.AfterMediaData => "Bytes after the media data that are not samples (a cut-off movie box) were left out.",
                _ => "Bytes that are not the start of a sample of any known track were left out.",
            };
            report.Add(RepairAction.Dropped, "mdat", s.TrackIndex, s.Range.Offset, s.Range.Length, why, RepairConfidence.High);
        }

        // Whatever follows the scanned media data (a cut-off movie box) is not copied.
        bool tailListed = false;
        foreach (SkippedRange s in r.SkippedRanges)
        {
            tailListed |= s.Range.Offset + s.Range.Length >= r.InputLength;
        }

        if (r.ScanEnd < r.InputLength && !tailListed)
        {
            report.Add(RepairAction.Dropped, "moov", -1, r.ScanEnd, r.InputLength - r.ScanEnd, "The cut-off movie box after the media data was left out; the new one is built from the samples.", RepairConfidence.High);
        }

        if (r.SkippedRangeCount > r.SkippedRanges.Count)
        {
            report.Warn((r.SkippedRangeCount - r.SkippedRanges.Count) + " more skipped ranges are not listed.");
        }

        foreach (string n in r.Notes)
        {
            report.Warn(n);
        }

        RepairStatus status = emptyTrack || r.RecoveredFraction < 0.9 ? RepairStatus.PartiallyRepaired : RepairStatus.Repaired;
        return report.Build(status, r.OutputLength) with { Reconstruction = r };
    }

    // The two confidence scales have the same four steps and the same meaning.
    private static RepairConfidence ToRepair(ReconstructionConfidence c) => (RepairConfidence)(int)c;
}

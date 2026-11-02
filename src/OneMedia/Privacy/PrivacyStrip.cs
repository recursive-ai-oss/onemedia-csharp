using System.Linq;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Removes location and device identifiers from a media file: from every tag system the format has (QuickTime <c>©xyz</c>, <c>mdta</c> location and
/// content-identifier keys, <c>loci</c>, GoPro serial and unique-id atoms, XMP and EXIF GPS and serial properties, Matroska, ID3, Vorbis, APE, RIFF and
/// ASF fields whose names say location or serial) and from timed telemetry (GoPro GPMF, Google CAMM, QuickTime <c>mebx</c>, DJI <c>djmd</c> and per-frame text, text-track coordinates, AVCHD camera data in transport streams). Make and model are kept.
/// The result is written to a new file by default; the media payload is copied without change, so the per-track payload hash of the output equals the input's (except in transport streams, where the AVCHD camera data lives inside the video packets and its position entries are overwritten there).
/// </summary>
/// <remarks>
/// <para>For MP4, MOV and 3GP every change is made in place in the copy: a field is overwritten with zeros, an atom becomes a <c>free</c> box of its own size,
/// and no other byte moves (<see cref="EditPlan.IsSameLayout"/> is true, so <see cref="SaveInPlace"/> works too). Other formats go through the tag writers,
/// which rewrite the tag area. <c>docs/strip.md</c> lists every rule, the neutral values and what is not covered.</para>
/// <para>What the strip does not know to look for stays. The <see cref="StripReport"/> lists what was changed and, in <see cref="StripReport.NotRemoved"/>,
/// what it found and could not take out.</para>
/// </remarks>
public static class PrivacyStrip
{
    /// <summary>Plans a strip of the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What to strip; both categories when null.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and the report of what it does.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised.</exception>
    /// <exception cref="MediaFormatException">The file is damaged beyond what the planner can read.</exception>
    public static StripPlan Plan(string path, StripOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileMediaSource source = FileMediaSource.Open(path);
        return Plan(source, options, cancellationToken);
    }

    /// <summary>Plans a strip of a source (a file or memory). The source is read during planning and is not disposed.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">What to strip; both categories when null.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and the report of what it does. Apply the plan with <see cref="SafeWriter.Write"/> to the same source.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised.</exception>
    /// <exception cref="MediaFormatException">The file is damaged beyond what the planner can read.</exception>
    public static StripPlan Plan(IMediaSource source, StripOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= StripOptions.Default;
        using MediaFile file = MediaFile.Open(source, options: new MediaOpenOptions { Limits = options.Limits ?? MediaLimits.Default, LeaveOpen = true }, cancellationToken: cancellationToken);
        if (file.Format == MediaFormatId.IsoBmff)
        {
            return IsoStrip.Plan(source, file, options, cancellationToken);
        }

        if (file.Format == MediaFormatId.MpegTs)
        {
            return TsStrip.Plan(source, file, options, cancellationToken);
        }

        return OtherStrip.Plan(source, file, options, cancellationToken);
    }

    /// <summary>Writes the stripped file to a new path; the original is untouched.</summary>
    /// <param name="path">The original.</param>
    /// <param name="destination">The new file; refused if it exists unless <paramref name="overwrite"/>.</param>
    /// <param name="options">What to strip; both categories when null.</param>
    /// <param name="overwrite">Replace an existing destination.</param>
    /// <param name="cancellationToken">Cancels the write; the partial file is deleted.</param>
    /// <returns>The report, with <see cref="StripReport.Write"/> set.</returns>
    /// <exception cref="IOException">The destination exists, or the disk failed.</exception>
    public static StripReport SaveAs(string path, string destination, StripOptions? options = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        StripPlan plan = Plan(path, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveAs(plan.Plan, path, destination, overwrite, cancellationToken);
        return plan.Report;
    }

    /// <summary>Replaces the file with its stripped version atomically (temporary file, then rename).</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What to strip; both categories when null.</param>
    /// <param name="cancellationToken">Cancels before the rename.</param>
    /// <returns>The report, with <see cref="StripReport.Write"/> set.</returns>
    public static StripReport Save(string path, StripOptions? options = null, CancellationToken cancellationToken = default)
    {
        StripPlan plan = Plan(path, options, cancellationToken);
        plan.Report.Write = SafeWriter.Save(plan.Plan, path, cancellationToken);
        return plan.Report;
    }

    /// <summary>Overwrites only the changed ranges of the file itself. Not atomic; refused when the strip would move bytes.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What to strip; both categories when null.</param>
    /// <param name="cancellationToken">Cancels before the first byte is written.</param>
    /// <returns>The report, with <see cref="StripReport.Write"/> set.</returns>
    /// <exception cref="PaddingRequiredException">The strip changes the layout (formats other than ISO base media); the file is untouched.</exception>
    public static StripReport SaveInPlace(string path, StripOptions? options = null, CancellationToken cancellationToken = default)
    {
        StripPlan plan = Plan(path, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveInPlace(plan.Plan, path, cancellationToken);
        return plan.Report;
    }
}

/// <summary>The strip for every format but ISO base media: the tag writers remove the matching native keys.</summary>
internal static class OtherStrip
{
    public static StripPlan Plan(IMediaSource source, MediaFile file, StripOptions options, CancellationToken ct)
    {
        StripCategories cats = options.Categories;
        List<StripEntry> entries = [];
        List<StripLeftover> left = [];
        List<string> notes = [];
        TagEdits edits = new();
        HashSet<(string, string)> seen = [];
        List<NativeTag> matched = [];
        foreach (NativeTag tag in file.Metadata.NativeTags)
        {
            StripCategories c = StripRules.Classify(tag.System, tag.Key) & cats;
            if (c != 0)
            {
                matched.Add(tag);
                if (seen.Add((tag.System, tag.Key)))
                {
                    edits.RemoveNative(tag.System, tag.Key);
                }
            }
        }

        EditPlan plan;
        if (matched.Count == 0)
        {
            PieceList same = new();
            same.AddCopy(0, source.Length);
            plan = same.ToPlan(source.Length, 0, 0, 0);
        }
        else
        {
            try
            {
                TagWritePlan written = TagWriter.Plan(source, edits, TagWriteOptions.Minimal, ct);
                plan = written.Plan;
                notes.AddRange(written.Report.Notes);
                foreach (NativeTag t in matched)
                {
                    entries.Add(new StripEntry(StripRules.Classify(t.System, t.Key) & cats, t.System, t.Key, t.TrackId, t.Range, 1, t.Range.IsNone ? 0 : t.Range.Length, StripAction.Removed));
                }
            }
            catch (UnsupportedMediaException e)
            {
                PieceList same = new();
                same.AddCopy(0, source.Length);
                plan = same.ToPlan(source.Length, 0, 0, 0);
                foreach (NativeTag t in matched)
                {
                    left.Add(new StripLeftover(StripRules.Classify(t.System, t.Key) & cats, t.System, t.Key, t.TrackId, t.Range, "The tag writer cannot edit this file: " + e.Message));
                }
            }
        }

        ScanSubtitleTracks(file, options, left, ct);
        notes.Add("Only tag fields and subtitle text are inspected in this format; position or serial data inside the other streams is not.");
        return new StripPlan(plan, new StripReport(cats, entries, left, notes));
    }

    // A subtitle block sits inside a cluster or chunk whose checksum or size a same-length edit would have to maintain, so text that looks like a
    // position or serial number is reported, not edited.
    private static void ScanSubtitleTracks(MediaFile file, StripOptions options, List<StripLeftover> left, CancellationToken ct)
    {
        if (file.Demuxer is not { } demuxer)
        {
            return;
        }

        foreach (MediaTrack track in demuxer.Tracks.Where(t => t.Kind == TrackKind.Subtitle))
        {
            foreach (MediaTrack t in demuxer.Tracks)
            {
                demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
            }

            int generation = file.BeginRead();
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    file.EnsureCurrentRead(generation);
                    if (!demuxer.TryReadPacket(out PacketView view))
                    {
                        break;
                    }

                    if (view.Data.Length <= 65536 && TextScrub.Any(view.Data, options.Categories))
                    {
                        left.Add(new StripLeftover(options.Categories, "text", "subtitle text", track.Id, view.Info.SourceRange,
                            "Subtitle text holds what looks like a position or serial number; the block lies in a structure whose checksum or size would change, so it was not edited."));
                        break;
                    }
                }
            }
            finally
            {
                foreach (MediaTrack t in demuxer.Tracks)
                {
                    demuxer.SetTrackEnabled(t.Index, true);
                }
            }
        }
    }
}

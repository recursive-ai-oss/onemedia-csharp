using System.Linq;
using RecursiveAI.OneMedia.Asf;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Writing;

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
/// <para>What the strip has no rule for stays, and is named: the <see cref="StripReport"/> lists what was changed and, in <see cref="StripReport.NotRemoved"/>,
/// what it found and could not take out, including vendor boxes, chunks, objects and opaque values from what the file holds. Padding that holds bytes is zeroed,
/// and so is an Exif block inside a vendor atom. AVI files get the same treatment for the Fujifilm <c>strd</c> chunk.</para>
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
        PatchSet patches = new();
        StripSink sink = new(patches);
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
                    edits.RemoveNativeEverywhere(tag.System, tag.Key);
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
        if (file.Format == MediaFormatId.Matroska)
        {
            FindUnreadTags(source, file, cats, left, ct);
        }

        if (file.Format == MediaFormatId.Avi)
        {
            // Camera data in the stream headers is edited in place (same length) on top of whatever the tag writer plans; what cannot be edited is reported.
            AviStrip.Scan(source, options.Limits ?? MediaLimits.Default, cats, sink, ct);
        }

        // What the strip has no rule for is named in the report, from what this file holds.
        StripInventory.OpaqueTags(source, file.Metadata, cats, static t => StripRules.Classify(t.System, t.Key) != StripCategories.None, left, ct);
        InventoryFormat(source, file, cats, left, sink, ct);
        if (patches.Count > 0)
        {
            plan = patches.Overlay(plan, out List<ByteRange> uncovered);
            foreach (ByteRange r in uncovered)
            {
                sink.Leftover(cats, "riff", "padding or stream data", 0, r, "The tag rewrite replaced this range, so the change planned for it could not be made.");
            }
        }

        entries.AddRange(sink.Entries());
        left.AddRange(sink.Leftovers);
        notes.AddRange(sink.Notes);
        notes.Add("Only tag fields and subtitle text are inspected in this format; anything else the file holds is listed in NotRemoved.");
        return new StripPlan(plan, new StripReport(cats, entries, left, notes));
    }

    private static readonly HashSet<Guid> AsfKnown =
    [
        AsfGuids.FileProperties, AsfGuids.StreamProperties, AsfGuids.HeaderExtension, AsfGuids.CodecList, AsfGuids.ContentDescription, AsfGuids.ExtendedContentDescription,
        AsfGuids.StreamBitrateProperties, AsfGuids.Padding, AsfGuids.ScriptCommand, AsfGuids.Metadata, AsfGuids.MetadataLibrary, AsfGuids.ExtendedStreamProperties,
        AsfGuids.LanguageList, AsfGuids.Compatibility, AsfGuids.StreamPrioritization, AsfGuids.Data, AsfGuids.SimpleIndex, AsfGuids.Index, AsfGuids.IndexParameters,

        // Seen in published samples (docs/asf.md), 34 to 88 bytes each: too small to hold text, and the reader treats them as structure. Listed by GUID so that
        // an object nobody has looked at is still named in the report.
        new("75B22635-668E-11CF-A6D9-00AA0062CE6C"), new("D6E229DC-35DA-11D1-9034-00A0C90349BE"), new("A08649CF-4775-4670-8A16-6E35357566CD"),
        new("AF5FD596-E6A0-4A5C-830B-DC2D4CF07E92"), new("D9AADE20-7C17-4F9C-BC28-8555DD98E2A2"),
    ];

    private static void InventoryFormat(IMediaSource source, MediaFile file, StripCategories cats, List<StripLeftover> left, StripSink sink, CancellationToken ct)
    {
        MediaFormatId f = file.Format;
        if (f == MediaFormatId.Wav)
        {
            byte[] head = new byte[4];
            bool riff = source.Read(0, head, ct) == 4 && (head.AsSpan().SequenceEqual("RIFF"u8) || head.AsSpan().SequenceEqual("RF64"u8) || head.AsSpan().SequenceEqual("BW64"u8));
            StripInventory.FlatChunks(source, riff ? StripInventory.ChunkFlavour.Riff : StripInventory.ChunkFlavour.Wave64, cats, left, sink, ct);
        }
        else if (f == MediaFormatId.Aiff)
        {
            StripInventory.FlatChunks(source, StripInventory.ChunkFlavour.Aiff, cats, left, sink, ct);
        }
        else if (f == MediaFormatId.Caf)
        {
            StripInventory.FlatChunks(source, StripInventory.ChunkFlavour.Caf, cats, left, sink, ct);
        }
        else if (f == MediaFormatId.Asf)
        {
            StripInventory.AsfObjects(source, AsfKnown, cats, left, ct);
        }

        if (f != MediaFormatId.Avi)
        {
            StripInventory.DataTracks(file, cats, left);
        }
    }

    // The Matroska reader finds Tags and Attachments through the SeekHead and the elements before the first Cluster; a writer that appends a Tags
    // element after the last Cluster without a SeekHead entry hides it from the reader and so from the strip. One header read per top-level element
    // (clusters are hopped over by size) finds such elements, which are then reported, not edited.
    private static void FindUnreadTags(IMediaSource source, MediaFile file, StripCategories cats, List<StripLeftover> left, CancellationToken ct)
    {
        const uint Segment = 0x18538067, Tags = 0x1254C367;
        byte[] head = new byte[16];
        long at = 0;
        long segmentEnd = source.Length;
        bool inSegment = false;
        for (int hops = 0; hops < 10_000_000 && at < segmentEnd; hops++)
        {
            ct.ThrowIfCancellationRequested();
            int got = source.Read(at, head, ct);
            if (got < 2 || !Matroska.Ebml.TryReadId(head.AsSpan(0, got), out uint id, out int idLength)
                || !Matroska.Ebml.TryReadSize(head.AsSpan(idLength, got - idLength), out long size, out int sizeLength))
            {
                return;
            }

            long start = at + idLength + sizeLength;
            if (id == Segment && !inSegment)
            {
                inSegment = true;
                at = start;
                segmentEnd = size < 0 ? source.Length : Math.Min(source.Length, start + size);
                continue;
            }

            if (size < 0)
            {
                return; // an unknown-size element (live Cluster): the rest cannot be hopped over
            }

            if (inSegment && id == Tags && !file.Metadata.Containers.Any(c => !c.Range.IsNone && c.Range.Offset < start + size && c.Range.End > at))
            {
                left.Add(new StripLeftover(cats, TagSystems.Matroska, "Tags", 0, new ByteRange(at, start + size - at),
                    "A Tags element that no SeekHead entry points to (after the last Cluster) is not read by the tag layer, so it was not inspected or edited."));
            }

            if (inSegment && !KnownMatroskaIds.Contains(id))
            {
                left.Add(new StripLeftover(cats, TagSystems.Matroska, "element 0x" + id.ToString("X", System.Globalization.CultureInfo.InvariantCulture), 0, new ByteRange(at, start + size - at),
                    $"A top-level element with id 0x{id:X} ({size} bytes) that RFC 9559 does not list for a Segment was not inspected or edited."));
            }

            at = start + size;
        }

        if (inSegment && segmentEnd < source.Length && at >= segmentEnd && !StripInventory.IsAllZero(source, segmentEnd, source.Length - segmentEnd, ct))
        {
            left.Add(new StripLeftover(cats, TagSystems.Matroska, "trailer", 0, new ByteRange(segmentEnd, source.Length - segmentEnd), $"{source.Length - segmentEnd} bytes follow the Segment and are not inspected."));
        }
    }

    // RFC 9559 §5.1: the children of a Segment (SeekHead, Info, Tracks, Cluster, Cues, Attachments, Chapters, Tags), the EBML Void and CRC-32 elements.
    private static readonly HashSet<uint> KnownMatroskaIds = [0x114D9B74, 0x1549A966, 0x1654AE6B, 0x1F43B675, 0x1C53BB6B, 0x1941A469, 0x1043A770, 0x1254C367, 0xEC, 0xBF];

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

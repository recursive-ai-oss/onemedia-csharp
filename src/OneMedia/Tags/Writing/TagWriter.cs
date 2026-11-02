using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Writes tags losslessly. A write is planned against the file's own tag layers (<see cref="Plan(IMediaSource, TagEdits, TagWriteOptions?, CancellationToken)"/>): the plan names
/// the metadata byte ranges that change and copies everything else, so media payload bytes are never read into memory or re-serialised, and applied with the safe writer
/// (<see cref="Save"/>, <see cref="SaveAs"/>, <see cref="SaveInPlace"/>). Edits are expressed on the common view and written to every tag system the file has, or to the
/// systems named in the options; the returned <see cref="TagWriteReport"/> lists per system what was written, removed, and could not be represented.
/// </summary>
/// <remarks>
/// Supported: MP4/MOV/3GP (iTunes item list, QuickTime key-table metadata, © atoms), Matroska/WebM (SimpleTags, Segment title, cover attachments),
/// MP3 (ID3v2.3/2.4, ID3v1, APEv2), FLAC, Ogg Vorbis/Opus/FLAC, WAVE/RF64 (LIST INFO, bext, ID3 chunk) AIFF (text chunks, ID3 chunk) ASF/WMA/WMV (Content Description, Extended Content Description), XMP packets in ISO BMFF <c>uuid</c> boxes and the WAVE <c>iXML</c> chunk (native edits by element path).
/// Other formats raise <see cref="UnsupportedMediaException"/>.
/// </remarks>
public static class TagWriter
{
    /// <summary>Plans a tag write for the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="options">Options; <see cref="TagWriteOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and its report.</returns>
    /// <exception cref="UnsupportedMediaException">The format cannot be written, or the file's layout cannot be edited safely.</exception>
    /// <exception cref="MediaFormatException">The file is damaged beyond the structure the writer needs.</exception>
    public static TagWritePlan Plan(string path, TagEdits edits, TagWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        using FileMediaSource source = FileMediaSource.Open(path);
        return Plan(source, edits, options, cancellationToken);
    }

    /// <summary>Plans a tag write for a source.</summary>
    /// <param name="source">The source; read only during planning.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="options">Options; <see cref="TagWriteOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and its report.</returns>
    /// <exception cref="UnsupportedMediaException">The format cannot be written, or the file's layout cannot be edited safely.</exception>
    public static TagWritePlan Plan(IMediaSource source, TagEdits edits, TagWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edits);
        options ??= TagWriteOptions.Default;
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        TagFamily family = Sniff(source, out long audioStart);
        if (edits.IsEmpty && family != TagFamily.None)
        {
            // Nothing was asked for, so nothing changes: not even an old ID3v2 tag is upgraded or an extended header dropped. The one exception is
            // leftover bytes from earlier edits in ID3v2 padding, which are zeroed: they may be a value that was removed.
            if (family == TagFamily.Mp3)
            {
                return Mp3TagWriter.CleanPlan(source, limits, cancellationToken);
            }

            PieceList same = new();
            same.AddCopy(0, source.Length);
            return new TagWritePlan(same.ToPlan(source.Length, 0, 0, 0), new TagWriteReport([], []));
        }

        TagWritePlan planned = family switch
        {
            TagFamily.Iso => IsoTagWriter.Plan(source, edits, options, limits, cancellationToken),
            TagFamily.Matroska => MatroskaTagWriter.Plan(source, edits, options, limits, cancellationToken),
            TagFamily.Riff => RiffTagWriter.Plan(source, edits, options, limits, cancellationToken),
            TagFamily.Ogg => OggTagWriter.Plan(source, edits, options, limits, cancellationToken),
            TagFamily.Flac => FlacTagWriter.Plan(source, audioStart, edits, options, limits, cancellationToken),
            TagFamily.Mp3 => Mp3TagWriter.Plan(source, edits, options, limits, cancellationToken),
            TagFamily.Asf => AsfTagWriter.Plan(source, edits, options, limits, cancellationToken),
            _ => throw new UnsupportedMediaException("Tag writing is not supported for this format."),
        };
        planned.Report.Warnings = DateWarnings.Find(source, edits, planned.Report, limits);
        return planned;
    }

    /// <summary>Writes the edits to a new file at <paramref name="destination"/>; the original is untouched.</summary>
    /// <param name="path">The source file.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="options">Options.</param>
    /// <param name="overwrite">True to replace an existing destination.</param>
    /// <param name="cancellationToken">Cancels before the file is complete.</param>
    /// <returns>The report, with <see cref="TagWriteReport.Write"/> set.</returns>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is false, or the disk failed.</exception>
    public static TagWriteReport SaveAs(string path, string destination, TagEdits edits, TagWriteOptions? options = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        TagWritePlan plan = Plan(path, edits, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveAs(plan.Plan, path, destination, overwrite, cancellationToken);
        return plan.Report;
    }

    /// <summary>Writes the edits by atomic replacement of the file (a temporary file in the same directory, then a rename).</summary>
    /// <param name="path">The file.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels before the rename.</param>
    /// <returns>The report, with <see cref="TagWriteReport.Write"/> set.</returns>
    public static TagWriteReport Save(string path, TagEdits edits, TagWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        TagWritePlan plan = Plan(path, edits, options, cancellationToken);
        plan.Report.Write = SafeWriter.Save(plan.Plan, path, cancellationToken);
        return plan.Report;
    }

    /// <summary>Writes the edits into the file itself, only when the result fits the padding the file already has.</summary>
    /// <param name="path">The file.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels before the first byte is written.</param>
    /// <returns>The report, with <see cref="TagWriteReport.Write"/> set.</returns>
    /// <exception cref="PaddingRequiredException">The edit does not fit; the file is untouched.</exception>
    public static TagWriteReport SaveInPlace(string path, TagEdits edits, TagWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        TagWritePlan plan = Plan(path, edits, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveInPlace(plan.Plan, path, cancellationToken);
        return plan.Report;
    }

    /// <summary>
    /// Looks for bytes of removed or stale values that an earlier edit left in the file. For ID3v2 at the start of a file (MP3 and other ID3-led audio) it
    /// reports padding that is not all zeros, frames sitting in the padding, and a frame right after the tag where audio should start. Other tag
    /// systems write their padding as zeros on every edit and are not scanned. It does not look at the fields that are still in the tag: a removed value that remains an ordinary frame is not residue (see <see cref="FindPresent"/>). Saving the file with any edit, or none, removes what this finds in padding.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was found; empty when the file is clean or has no ID3v2 tag. Values are never copied into the result.</returns>
    public static IReadOnlyList<TagResidue> CheckResidue(string path, CancellationToken cancellationToken = default)
    {
        using FileMediaSource source = FileMediaSource.Open(path);
        return CheckResidue(source, cancellationToken);
    }

    /// <summary>Looks for leftover removed or stale values in a source; see <see cref="CheckResidue(string, CancellationToken)"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was found.</returns>
    public static IReadOnlyList<TagResidue> CheckResidue(IMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Mp3TagWriter.FindResidue(source, MediaLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Lists the values of the given common fields that a file still holds, in any tag system it has (ID3v2, ID3v1, APEv2, <c>ilst</c>, Matroska tags,
    /// Vorbis comments, RIFF INFO…), read through the common view. This is the check <see cref="CheckResidue(string, CancellationToken)"/> does not make: a value
    /// that was removed badly but is still an ordinary field of the tag is present here. Each result names the field and where it lives
    /// (<see cref="CommonValue.Source"/>) and carries the text, which the caller decides whether to show.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="fields">The fields that should be absent.</param>
    /// <returns>One entry per value found; empty when none of the fields is present.</returns>
    /// <exception cref="UnsupportedMediaException">The file is not in a format with tags this library reads.</exception>
    public static IReadOnlyList<CommonValue> FindPresent(string path, IEnumerable<CommonField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using MediaFile media = Media.Open(path);
        List<CommonValue> found = [];
        foreach (CommonField field in fields.Distinct())
        {
            found.AddRange(media.Tags.Values(field));
        }

        return found;
    }

    private enum TagFamily
    {
        None,
        Iso,
        Matroska,
        Riff,
        Ogg,
        Flac,
        Mp3,
        Asf,
    }

    private static TagFamily Sniff(IMediaSource src, out long audioStart)
    {
        audioStart = 0;
        Span<byte> head = stackalloc byte[16]; // enough for every signature checked here
        int n = src.Read(0, head);
        ReadOnlySpan<byte> h = head[..n];
        if (n >= 8)
        {
            ReadOnlySpan<byte> t = h[4..8];
            if (t.SequenceEqual("ftyp"u8) || t.SequenceEqual("moov"u8) || t.SequenceEqual("mdat"u8) || t.SequenceEqual("free"u8) || t.SequenceEqual("skip"u8) || t.SequenceEqual("wide"u8) || t.SequenceEqual("styp"u8))
            {
                return TagFamily.Iso;
            }
        }

        if (n >= 4 && h[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return TagFamily.Matroska;
        }

        if (n >= 4 && h[..4].SequenceEqual("OggS"u8))
        {
            return TagFamily.Ogg;
        }

        if (n >= 4 && h[..4].SequenceEqual("fLaC"u8))
        {
            return TagFamily.Flac;
        }

        if (n >= 16 && h[..4].SequenceEqual(new byte[] { 0x30, 0x26, 0xB2, 0x75 }))
        {
            return TagFamily.Asf;
        }

        if (ChunkTreeDetect(h))
        {
            return TagFamily.Riff;
        }

        if (n >= 10 && h[..3].SequenceEqual("ID3"u8) && Tags.Id3.Id3Header.TryParse(h[..10], out Tags.Id3.Id3Header id3))
        {
            Span<byte> next = stackalloc byte[4]; // the marker after the tag
            if (src.Read(id3.TotalLength, next) == 4 && next.SequenceEqual("fLaC"u8))
            {
                audioStart = id3.TotalLength;
                return TagFamily.Flac;
            }

            return TagFamily.Mp3;
        }

        if (n >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)
        {
            return TagFamily.Mp3;
        }

        Span<byte> tail = stackalloc byte[3]; // an ID3v1 marker
        if (src.Length >= 128 && src.Read(src.Length - 128, tail) == 3 && tail.SequenceEqual("TAG"u8) && OpensAsMpegAudio(src))
        {
            // Only an ID3v1 marker at the end: any file can end in "TAG" by chance, so the reader must agree that this is MPEG audio before a tag is added in front of it.
            return TagFamily.Mp3;
        }

        return TagFamily.None;
    }

    private static bool OpensAsMpegAudio(IMediaSource src)
    {
        try
        {
            using MediaFile file = MediaFile.Open(src, null, new MediaOpenOptions { LeaveOpen = true });
            return file.Format == MediaFormatId.MpegAudio || file.Format == MediaFormatId.Id3;
        }
        catch (MediaException)
        {
            return false;
        }
    }

    private static bool ChunkTreeDetect(ReadOnlySpan<byte> head) => ChunkWalker.Detect(head) is not null;
}

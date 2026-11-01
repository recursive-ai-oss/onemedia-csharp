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
        return family switch
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
            using MediaFile file = MediaFile.Open(src);
            return file.Format == MediaFormatId.MpegAudio || file.Format == MediaFormatId.Id3;
        }
        catch (MediaException)
        {
            return false;
        }
    }

    private static bool ChunkTreeDetect(ReadOnlySpan<byte> head) => ChunkWalker.Detect(head) is not null;
}

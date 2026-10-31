using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tags.Matroska;

/// <summary>What a Matroska <c>Tag</c> element is about (the <c>Targets</c> element, RFC 9559 §5.1.8.1.1).</summary>
/// <param name="TargetTypeValue">The logical level: 70 collection, 60 edition/issue/season, 50 album/movie/episode (the default), 40 part/session, 30 track/song/chapter, 20 subtrack/movement/scene, 10 shot.</param>
/// <param name="TargetType">The optional level name (<c>MOVIE</c>, <c>TRACK</c>…), or <c>null</c>.</param>
/// <param name="TrackUids">Tracks the tag applies to (<c>TagTrackUID</c>); empty means all.</param>
/// <param name="EditionUids">Editions the tag applies to.</param>
/// <param name="ChapterUids">Chapters the tag applies to.</param>
/// <param name="AttachmentUids">Attachments the tag applies to.</param>
public sealed record MatroskaTagTargets(long TargetTypeValue, string? TargetType, IReadOnlyList<long> TrackUids, IReadOnlyList<long> EditionUids, IReadOnlyList<long> ChapterUids, IReadOnlyList<long> AttachmentUids)
{
    /// <summary>The targets of a tag without a <c>Targets</c> element: the whole file at the default level 50.</summary>
    public static MatroskaTagTargets Default { get; } = new(50, null, [], [], [], []);

    /// <summary>True when no UID is listed: the tag applies to everything at its level.</summary>
    public bool IsGlobal => TrackUids.Count == 0 && EditionUids.Count == 0 && ChapterUids.Count == 0 && AttachmentUids.Count == 0;
}

/// <summary>
/// One Matroska <c>Tag</c> element: its targets and its <c>SimpleTag</c>s. Each SimpleTag that carries a string or binary value is one
/// <see cref="NativeTag"/>; a nested SimpleTag is keyed by the path from its outermost parent (<c>TITLE/SORT_WITH</c>). A SimpleTag that only
/// groups others yields no tag of its own. <see cref="NativeTag.Language"/> is the BCP 47 language when the tag has one, else the ISO 639-2
/// language unless it is <c>und</c>.
/// </summary>
public sealed class MatroskaTagSet : ITagContainer
{
    internal MatroskaTagSet(ByteRange range, long trackId, MatroskaTagTargets targets, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        TrackId = trackId;
        Targets = targets;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.Matroska;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>What the tag is about.</summary>
    public MatroskaTagTargets Targets { get; }
}

/// <summary>The text elements of the Segment's <c>Info</c>: <c>Title</c>, <c>MuxingApp</c>, <c>WritingApp</c> and <c>DateUTC</c> (as ISO 8601 UTC text).</summary>
public sealed class MatroskaInfoTags : ITagContainer
{
    internal MatroskaInfoTags(ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.MatroskaInfo;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>
/// The Segment's <c>Attachments</c>: one tag per attached file, keyed by its file name, with the media type, the description in the tag's
/// language slot unused, and the file data as <see cref="NativeTag.ValueRange"/> (read lazily). Image attachments are
/// <see cref="TagValueKind.Image"/>, so cover art appears in <see cref="CommonTags.Pictures"/>.
/// </summary>
public sealed class MatroskaAttachments : ITagContainer
{
    internal MatroskaAttachments(ByteRange range, IReadOnlyList<NativeTag> tags, IReadOnlyList<string?> descriptions)
    {
        Range = range;
        Tags = tags;
        Descriptions = descriptions;
    }

    /// <inheritdoc />
    public string System => TagSystems.Matroska;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>The <c>FileDescription</c> of each attachment, parallel to <see cref="Tags"/>; <c>null</c> where the file gives none.</summary>
    public IReadOnlyList<string?> Descriptions { get; }
}

/// <summary>Reads the Matroska tag-bearing elements from byte ranges found by the Matroska reader.</summary>
public static class MatroskaTagReader
{
    private const uint TagId = 0x7373;
    private const uint TargetsId = 0x63C0;
    private const uint SimpleTagId = 0x67C8;
    private const uint TargetTypeValueId = 0x68CA;
    private const uint TargetTypeId = 0x63CA;
    private const uint TagTrackUidId = 0x63C5;
    private const uint TagEditionUidId = 0x63C9;
    private const uint TagChapterUidId = 0x63C4;
    private const uint TagAttachmentUidId = 0x63C6;
    private const uint TagNameId = 0x45A3;
    private const uint TagLanguageId = 0x447A;
    private const uint TagLanguageBcp47Id = 0x447B;
    private const uint TagStringId = 0x4487;
    private const uint TagBinaryId = 0x4485;
    private const uint AttachedFileId = 0x61A7;
    private const uint FileDescriptionId = 0x467E;
    private const uint FileNameId = 0x466E;
    private const uint FileMediaTypeId = 0x4660;
    private const uint FileDataId = 0x465C;
    private const int EagerValueBytes = 64 * 1024;

    /// <summary>
    /// Reads a <c>Tags</c> element. Malformed children are defects (<c>tags.mkv-*</c>) and what could be read is kept; a name that is missing or
    /// not valid UTF-8, and nesting beyond <see cref="MediaLimits.MaxStructureDepth"/>, are reported with their offsets.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="tagsElement">The whole <c>Tags</c> element, header included.</param>
    /// <param name="trackNumbersByUid">Track numbers keyed by TrackUID, used to give targeted tags a track id.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>One set per <c>Tag</c> element.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<MatroskaTagSet> ReadTags(IMediaSource source, ByteRange tagsElement, IReadOnlyDictionary<long, long> trackNumbersByUid, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackNumbersByUid);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        List<MatroskaTagSet> sets = [];
        TagIo io = new(source, limits, defects);
        foreach (EbmlElement tag in Children(source, tagsElement, limits, defects, cancellationToken))
        {
            if (tag.Id != TagId)
            {
                continue;
            }

            MatroskaTagTargets targets = MatroskaTagTargets.Default;
            List<NativeTag> tags = [];
            foreach (EbmlElement child in tag.ChildElements)
            {
                if (child.Id == TargetsId)
                {
                    targets = ReadTargets(child, defects);
                }
            }

            long trackId = TrackIdOf(targets, trackNumbersByUid);
            foreach (EbmlElement child in tag.ChildElements)
            {
                if (child.Id == SimpleTagId)
                {
                    ReadSimpleTag(child, string.Empty, 1, io, trackId, targets.TargetTypeValue, tags);
                }
            }

            sets.Add(new MatroskaTagSet(tag.Range, trackId, targets, tags));
        }

        return sets;
    }

    /// <summary>Reads the <c>Info</c> element's text fields as tags (<see cref="TagSystems.MatroskaInfo"/>).</summary>
    /// <param name="source">The source.</param>
    /// <param name="infoElement">The whole <c>Info</c> element, header included.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The Segment information tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static MatroskaInfoTags ReadInfo(IMediaSource source, ByteRange infoElement, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        foreach (EbmlElement e in Children(source, infoElement, limits, defects, cancellationToken))
        {
            string? key = e.Id switch { 0x7BA9 => "Title", 0x4D80 => "MuxingApp", 0x5741 => "WritingApp", 0x4461 => "DateUTC", _ => null };
            if (key is null)
            {
                continue;
            }

            if (e.Id == 0x4461)
            {
                // RFC 9559 §5.1.2.7: a signed count of nanoseconds since 2001-01-01T00:00:00 UTC.
                byte[]? p = ReadSmall(e, 8, io);
                if (p is not null && Ebml.TryReadSigned(p, out long ns))
                {
                    long ticks = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + (ns / 100);
                    if (ticks is > 0 and < 3_155_378_975_999_999_999L)
                    {
                        NativeTag date = new(io, TagSystems.MatroskaInfo, key, 0, TagValueKind.Text, 1, 0, null, e.Range, e.Payload);
                        date.SetText(new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                        tags.Add(date);
                        continue;
                    }
                }

                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, e.Offset, "The DateUTC element is not a valid date.", "tags.mkv-date");
                continue;
            }

            NativeTag t = new(io, TagSystems.MatroskaInfo, key, 0, TagValueKind.Text, 1, 0, null, e.Range, e.Payload);
            byte[]? text = ReadSmall(e, EagerValueBytes, io);
            if (text is not null)
            {
                t.SetText(TextDecoder.Decode(text, TagTextEncoding.Utf8, e.Payload.Offset, defects));
            }

            tags.Add(t);
        }

        return new MatroskaInfoTags(infoElement, tags);
    }

    /// <summary>Reads an <c>Attachments</c> element. The file data is not read.</summary>
    /// <param name="source">The source.</param>
    /// <param name="attachmentsElement">The whole <c>Attachments</c> element, header included.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The attachments.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static MatroskaAttachments ReadAttachments(IMediaSource source, ByteRange attachmentsElement, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        List<string?> descriptions = [];
        foreach (EbmlElement file in Children(source, attachmentsElement, limits, defects, cancellationToken))
        {
            if (file.Id != AttachedFileId)
            {
                continue;
            }

            string name = string.Empty;
            string? mime = null;
            string? description = null;
            ByteRange data = ByteRange.None;
            foreach (EbmlElement f in file.ChildElements)
            {
                switch (f.Id)
                {
                    case FileNameId:
                        name = ReadString(f, io) ?? string.Empty;
                        break;
                    case FileMediaTypeId:
                        mime = ReadString(f, io);
                        break;
                    case FileDescriptionId:
                        description = ReadString(f, io);
                        break;
                    case FileDataId:
                        data = f.Payload;
                        break;
                }
            }

            if (data.IsNone)
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, file.Offset, "An attached file has no FileData element.", "tags.mkv-attachment");
                continue;
            }

            bool image = mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            tags.Add(new NativeTag(io, TagSystems.Matroska, name, 0, image ? TagValueKind.Image : TagValueKind.Binary, 0, 0, null, file.Range, data) { MimeType = mime });
            descriptions.Add(description);
        }

        return new MatroskaAttachments(attachmentsElement, tags, descriptions);
    }

    private static List<EbmlElement> Children(IMediaSource source, ByteRange element, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        Span<byte> head = stackalloc byte[Ebml.MaxIdLength + Ebml.MaxSizeLength]; // one element header, at most 12 bytes
        int got = source.Read(element.Offset, head, ct);
        if (got < 2 || !Ebml.TryReadId(head[..got], out _, out int idLength) || !Ebml.TryReadSize(head[idLength..got], out _, out int sizeLength))
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, element.Offset, "A tag-bearing element has no readable header.", "tags.mkv-header");
            return [];
        }

        EbmlTreeContext context = new(source, limits, defects, ct);
        long start = element.Offset + idLength + sizeLength;
        return EbmlElement.ParseChildren(context, null, start, Math.Max(start, element.End), -1, 3, out _);
    }

    private static MatroskaTagTargets ReadTargets(EbmlElement targets, DefectLog defects)
    {
        long value = 50;
        string? type = null;
        List<long> tracks = [], editions = [], chapters = [], attachments = [];
        Span<byte> buffer = stackalloc byte[64]; // integers and short names
        foreach (EbmlElement e in targets.ChildElements)
        {
            int n = e.ReadPayload(buffer);
            ReadOnlySpan<byte> p = buffer[..n];
            switch (e.Id)
            {
                case TargetTypeValueId when Ebml.TryReadUnsigned(p, out ulong v):
                    value = (long)Math.Min(v, long.MaxValue);
                    break;
                case TargetTypeId:
                    type = System.Text.Encoding.UTF8.GetString(p).TrimEnd('\0');
                    break;
                case TagTrackUidId when Ebml.TryReadUnsigned(p, out ulong u):
                    tracks.Add(unchecked((long)u));
                    break;
                case TagEditionUidId when Ebml.TryReadUnsigned(p, out ulong u):
                    editions.Add(unchecked((long)u));
                    break;
                case TagChapterUidId when Ebml.TryReadUnsigned(p, out ulong u):
                    chapters.Add(unchecked((long)u));
                    break;
                case TagAttachmentUidId when Ebml.TryReadUnsigned(p, out ulong u):
                    attachments.Add(unchecked((long)u));
                    break;
            }
        }

        if (value % 10 != 0 || value is < 10 or > 70)
        {
            defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, targets.Offset, $"TargetTypeValue {value} is not one of the levels 10 to 70.", "tags.mkv-target-level");
        }

        return new MatroskaTagTargets(value, type, tracks, editions, chapters, attachments);
    }

    private static long TrackIdOf(MatroskaTagTargets t, IReadOnlyDictionary<long, long> numbers)
    {
        if (t.IsGlobal)
        {
            return 0;
        }

        if (t.TrackUids.Count > 0 && t.EditionUids.Count == 0 && t.ChapterUids.Count == 0 && t.AttachmentUids.Count == 0 && numbers.TryGetValue(t.TrackUids[0], out long number))
        {
            return number;
        }

        return -1;
    }

    private static void ReadSimpleTag(EbmlElement st, string parentPath, int depth, TagIo io, long trackId, long level, List<NativeTag> into)
    {
        try
        {
            io.Limits.CheckDepth(depth + 2, st.Offset);
        }
        catch (MediaLimitExceededException e)
        {
            io.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, e.Offset, "SimpleTag nesting is deeper than the limit; the rest is skipped.", "tags.mkv-depth");
            return;
        }

        string? name = null;
        string? lang = null;
        string? bcp47 = null;
        ByteRange stringRange = ByteRange.None, binaryRange = ByteRange.None;
        foreach (EbmlElement e in st.ChildElements)
        {
            switch (e.Id)
            {
                case TagNameId:
                    name = ReadString(e, io);
                    break;
                case TagLanguageId:
                    lang = ReadString(e, io);
                    break;
                case TagLanguageBcp47Id:
                    bcp47 = ReadString(e, io);
                    break;
                case TagStringId:
                    stringRange = e.Payload;
                    break;
                case TagBinaryId:
                    binaryRange = e.Payload;
                    break;
            }
        }

        if (name is null)
        {
            io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, st.Offset, "A SimpleTag has no TagName.", "tags.mkv-no-name");
            name = string.Empty;
        }

        string path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        string? language = !string.IsNullOrEmpty(bcp47) ? bcp47 : (!string.IsNullOrEmpty(lang) && lang != "und" ? lang : null);
        if (!stringRange.IsNone || !binaryRange.IsNone)
        {
            bool text = !stringRange.IsNone;
            NativeTag tag = new(io, TagSystems.Matroska, path, trackId, text ? TagValueKind.Text : TagValueKind.Binary, text ? 1u : 0u, 0, language, st.Range, text ? stringRange : binaryRange)
            {
                TargetTypeValue = level,
            };
            if (text && stringRange.Length <= EagerValueBytes)
            {
                tag.SetText(TextDecoder.Decode(io.Read(stringRange), TagTextEncoding.Utf8, stringRange.Offset, io.Defects));
            }

            into.Add(tag);
        }

        foreach (EbmlElement e in st.ChildElements)
        {
            if (e.Id == SimpleTagId)
            {
                ReadSimpleTag(e, path, depth + 1, io, trackId, level, into);
            }
        }
    }

    private static string? ReadString(EbmlElement e, TagIo io)
    {
        byte[]? p = ReadSmall(e, 4096, io);
        return p is null ? null : TextDecoder.Decode(p, TagTextEncoding.Utf8, e.Payload.Offset, io.Defects);
    }

    private static byte[]? ReadSmall(EbmlElement e, long max, TagIo io)
    {
        if (e.PayloadSize > max)
        {
            io.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, e.Offset, "An element is larger than expected and is not read.", "tags.mkv-large");
            return null;
        }

        byte[] bytes = new byte[e.PayloadSize];
        int n = e.ReadPayload(bytes);
        return n == bytes.Length ? bytes : bytes.AsSpan(0, n).ToArray();
    }
}

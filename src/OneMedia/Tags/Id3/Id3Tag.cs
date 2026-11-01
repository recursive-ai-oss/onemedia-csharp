using System.Globalization;
using System.IO.Compression;

namespace RecursiveAI.OneMedia.Tags.Id3;

/// <summary>The flags of an ID3v2 tag header (id3v2.4.0-structure §3.1; the same bits in 2.3 without the footer, and in 2.2 with a compression bit).</summary>
[Flags]
public enum Id3TagAttributes : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The tag as a whole is unsynchronised: every 0xFF followed by a byte of 0xE0 or more, or by 0x00, has a 0x00 inserted after it.</summary>
    Unsynchronisation = 0x80,

    /// <summary>An extended header follows the tag header (2.3 and 2.4).</summary>
    ExtendedHeader = 0x40,

    /// <summary>The tag is experimental.</summary>
    Experimental = 0x20,

    /// <summary>A footer ends the tag (2.4 only).</summary>
    Footer = 0x10,
}

/// <summary>The ten-byte header that starts an ID3v2 tag (id3v2.4.0-structure §3.1).</summary>
/// <param name="MajorVersion">2, 3 or 4 (the tag is ID3v2.<c>MajorVersion</c>.<c>Revision</c>).</param>
/// <param name="Revision">The revision byte.</param>
/// <param name="Flags">The header flags.</param>
/// <param name="BodySize">The size of the tag after the header, extended header and padding included, footer excluded.</param>
public readonly record struct Id3Header(int MajorVersion, int Revision, Id3TagAttributes Flags, int BodySize)
{
    /// <summary>The length of the tag header and of the footer.</summary>
    public const int HeaderSize = 10;

    /// <summary>True when the tag ends with a ten-byte footer.</summary>
    public bool HasFooter => (Flags & Id3TagAttributes.Footer) != 0 && MajorVersion == 4;

    /// <summary>The whole tag in the file: header, body and footer.</summary>
    public long TotalLength => HeaderSize + (long)BodySize + (HasFooter ? HeaderSize : 0);

    /// <summary>Reads a tag header.</summary>
    /// <param name="data">At least ten bytes starting at the identifier <c>ID3</c>.</param>
    /// <param name="header">The header when true.</param>
    /// <returns>False when the bytes are not the start of an ID3v2.2, 2.3 or 2.4 tag: the identifier, a version byte of 0xFF, or a size byte with its top bit set is wrong.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out Id3Header header)
    {
        header = default;
        if (data.Length < HeaderSize || !data.StartsWith("ID3"u8) || data[3] is < 2 or > 4 || data[4] == 0xFF || ((data[6] | data[7] | data[8] | data[9]) & 0x80) != 0)
        {
            return false;
        }

        // Four 7-bit groups, most significant first ("synchsafe"): the size never contains a false sync.
        int size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
        header = new Id3Header(data[3], data[4], (Id3TagAttributes)data[5], size);
        return true;
    }
}

/// <summary>The per-frame flags of ID3v2.3 and ID3v2.4, in one set (2.3's bits are mapped onto the 2.4 meanings).</summary>
[Flags]
public enum Id3FrameAttributes
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Discard the frame if the tag is altered and the frame is not understood.</summary>
    TagAlterPreservation = 1,

    /// <summary>Discard the frame if the file (not the tag) is altered and the frame is not understood.</summary>
    FileAlterPreservation = 2,

    /// <summary>The frame is meant to be read-only.</summary>
    ReadOnly = 4,

    /// <summary>The frame belongs to a group; <see cref="Id3Frame.GroupId"/> says which.</summary>
    Grouping = 8,

    /// <summary>The frame data is compressed with zlib.</summary>
    Compressed = 16,

    /// <summary>The frame data is encrypted. It is exposed as stored, never decrypted.</summary>
    Encrypted = 32,

    /// <summary>The frame data is unsynchronised (2.4; in 2.3 the whole tag is).</summary>
    Unsynchronised = 64,

    /// <summary>A data length indicator precedes the data (2.4; in 2.3 compression adds a decompressed-size field).</summary>
    DataLengthIndicator = 128,
}

/// <summary>A chapter frame (<c>CHAP</c>, ID3v2 chapter frame addendum §3.1).</summary>
/// <param name="ElementId">The element identifier other frames refer to.</param>
/// <param name="StartMilliseconds">Start time in milliseconds.</param>
/// <param name="EndMilliseconds">End time in milliseconds.</param>
/// <param name="StartOffset">Start byte offset in the audio, or 0xFFFFFFFF when not given.</param>
/// <param name="EndOffset">End byte offset in the audio, or 0xFFFFFFFF when not given.</param>
/// <param name="SubFrames">The frames embedded in the chapter (usually <c>TIT2</c>).</param>
public sealed record Id3Chapter(string ElementId, uint StartMilliseconds, uint EndMilliseconds, uint StartOffset, uint EndOffset, IReadOnlyList<Id3Frame> SubFrames);

/// <summary>A table of contents frame (<c>CTOC</c>, ID3v2 chapter frame addendum §3.2).</summary>
/// <param name="ElementId">The element identifier.</param>
/// <param name="IsTopLevel">True for the root of the table.</param>
/// <param name="IsOrdered">True when the children are in playing order.</param>
/// <param name="ChildIds">Element identifiers of the entries.</param>
/// <param name="SubFrames">The frames embedded in the table (usually <c>TIT2</c>).</param>
public sealed record Id3TableOfContents(string ElementId, bool IsTopLevel, bool IsOrdered, IReadOnlyList<string> ChildIds, IReadOnlyList<Id3Frame> SubFrames);

/// <summary>
/// One ID3v2 frame as stored: where it is, its flags, and what could be decoded from it. Frames the reader does not understand are kept with
/// their identifier and byte range, so nothing is lost. Padding is not a frame.
/// </summary>
public sealed class Id3Frame
{
    internal Id3Frame(string id, ByteRange range, ByteRange dataRange, Id3FrameAttributes flags)
    {
        Id = id;
        Range = range;
        DataRange = dataRange;
        Flags = flags;
    }

    /// <summary>The frame identifier as stored: four characters in 2.3 and 2.4, three in 2.2.</summary>
    public string Id { get; }

    /// <summary>The frame in the file, header included.</summary>
    public ByteRange Range { get; }

    /// <summary>The frame data as stored (after the header), including the group, encryption and length fields some flags add.</summary>
    public ByteRange DataRange { get; }

    /// <summary>The frame flags.</summary>
    public Id3FrameAttributes Flags { get; }

    /// <summary>The group identifier when <see cref="Id3FrameAttributes.Grouping"/> is set, else <c>null</c>.</summary>
    public int? GroupId { get; internal set; }

    /// <summary>The encryption method byte when the frame is encrypted, else <c>null</c>.</summary>
    public int? EncryptionMethod { get; internal set; }

    /// <summary>The size the data has once decompressed and de-unsynchronised, when the frame states it, else <c>null</c>.</summary>
    public long? DecodedLength { get; internal set; }

    /// <summary>The native tag for the frame; <c>null</c> for frames whose data could not be understood at all (their range is still here).</summary>
    public NativeTag? Native { get; internal set; }

    /// <summary>For text frames, the separate strings (2.4 allows several in one frame); empty for other frames.</summary>
    public IReadOnlyList<string> Values { get; internal set; } = [];

    /// <summary>The chapter, for <c>CHAP</c> frames.</summary>
    public Id3Chapter? Chapter { get; internal set; }

    /// <summary>The table of contents, for <c>CTOC</c> frames.</summary>
    public Id3TableOfContents? TableOfContents { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{Id} at {Range.Offset} ({Range.Length} bytes)";
}

/// <summary>
/// An ID3v2.2, 2.3 or 2.4 tag read from a source (id3.org: id3v2-00, id3v2.3.0, id3v2.4.0-structure and -frames). The tag holds the byte range
/// of everything it found: the header, extended header, each frame, the padding and the footer. Text and numbers are decoded as the tag is read;
/// pictures and other large values are read only when asked for through <see cref="NativeTag.ReadValue"/>.
/// </summary>
public sealed class Id3Tag : ITagContainer
{
    private readonly List<Id3Frame> _frames = [];
    private readonly List<NativeTag> _tags = [];

    private Id3Tag(Id3Header header, ByteRange range)
    {
        Header = header;
        Range = range;
    }

    /// <summary>The tag header.</summary>
    public Id3Header Header { get; }

    /// <inheritdoc />
    public string System => TagSystems.Id3v2;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <summary>The whole tag in the file: header, body and footer. A writer replaces or edits this range.</summary>
    public ByteRange Range { get; }

    /// <summary>The extended header, or <see cref="ByteRange.None"/>.</summary>
    public ByteRange ExtendedHeader { get; private set; } = ByteRange.None;

    /// <summary>The zero padding after the last frame (the room an in-place edit can use), or <see cref="ByteRange.None"/>.</summary>
    public ByteRange Padding { get; private set; } = ByteRange.None;

    /// <summary>Bytes of the body after the last frame that are neither padding nor a frame (a damaged frame header), or <see cref="ByteRange.None"/>.</summary>
    public ByteRange Unparsed { get; private set; } = ByteRange.None;

    /// <summary>The frames in file order.</summary>
    public IReadOnlyList<Id3Frame> Frames => _frames;

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags => _tags;

    /// <summary>
    /// Reads the tag that starts at <paramref name="offset"/>. For files with the tag at the start pass offset 0; WAV, AIFF and MP4 pass the
    /// payload offset of their <c>id3 </c> chunk or <c>ID32</c> box (after its language field).
    /// </summary>
    /// <param name="source">The source holding the tag; the returned tags read their large values from it later, so keep it open.</param>
    /// <param name="offset">Absolute offset of the <c>ID3</c> identifier.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null. A tag-wide unsynchronised 2.2/2.3 tag is read whole, within <see cref="MediaLimits.MaxTagBytes"/>.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <param name="cancellationToken">Observed before reading.</param>
    /// <returns>The tag.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="MediaFormatException">There is no ID3v2 header at <paramref name="offset"/>.</exception>
    /// <exception cref="MediaLimitExceededException">A tag-wide unsynchronised tag is larger than <see cref="MediaLimits.MaxTagBytes"/>.</exception>
    public static Id3Tag Read(IMediaSource source, long offset, MediaLimits? limits = null, DefectLog? defects = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        Span<byte> head = stackalloc byte[Id3Header.HeaderSize]; // the fixed tag header
        if (source.Read(offset, head, cancellationToken) < head.Length || !Id3Header.TryParse(head, out Id3Header header))
        {
            throw new MediaFormatException("There is no ID3v2 tag header here.") { Offset = offset, Kind = DefectKind.InvalidValue };
        }

        return Parse(source, offset, header, limits, defects);
    }

    /// <summary>Reads the tag at <paramref name="offset"/>, or reports that there is none.</summary>
    /// <param name="source">The source holding the tag.</param>
    /// <param name="offset">Absolute offset of the <c>ID3</c> identifier.</param>
    /// <param name="tag">The tag when true.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>False when there is no ID3v2 header at <paramref name="offset"/> or a limit stops the read (the limit is recorded as a defect).</returns>
    public static bool TryRead(IMediaSource source, long offset, out Id3Tag? tag, MediaLimits? limits = null, DefectLog? defects = null)
    {
        tag = null;
        defects ??= new DefectLog(limits ?? MediaLimits.Default);
        try
        {
            tag = Read(source, offset, limits, defects);
            return true;
        }
        catch (MediaLimitExceededException e)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "id3.limit");
            return false;
        }
        catch (MediaFormatException)
        {
            return false;
        }
    }

    /// <summary>Reads a tag held in memory: the <c>ID32</c> box payload of an MP4 or a chunk body already read.</summary>
    /// <param name="data">The bytes, starting at <c>ID3</c>.</param>
    /// <param name="baseOffset">The absolute offset of <c>data[0]</c> in the file, so that ranges point into the file.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>The tag; large values are read from <paramref name="data"/>, which the caller keeps alive.</returns>
    /// <exception cref="MediaFormatException">There is no ID3v2 header at the start.</exception>
    public static Id3Tag Read(ReadOnlyMemory<byte> data, long baseOffset = 0, MediaLimits? limits = null, DefectLog? defects = null)
    {
        // A shifted view: absolute offsets the caller asked for map onto the memory by subtraction.
        return Read(new OffsetMemorySource(data, baseOffset), baseOffset, limits, defects, CancellationToken.None);
    }

    private sealed class OffsetMemorySource(ReadOnlyMemory<byte> data, long baseOffset) : IMediaSource
    {
        public long Length => baseOffset + data.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            long rel = offset - baseOffset;
            if (rel < 0 || rel >= data.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, data.Length - rel);
            data.Span.Slice((int)rel, n).CopyTo(destination);
            return n;
        }

        public void Dispose()
        {
        }
    }

    private static Id3Tag Parse(IMediaSource source, long offset, Id3Header header, MediaLimits limits, DefectLog defects)
    {
        long available = source.Length >= 0 ? Math.Max(0, source.Length - offset - Id3Header.HeaderSize) : long.MaxValue;
        long bodySize = header.BodySize;
        long total = header.TotalLength;
        if (bodySize > available)
        {
            defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, offset, "The ID3v2 tag is larger than the file; the part that exists is read.", "id3.size-exceeds-file");
            bodySize = available;
            total = Id3Header.HeaderSize + bodySize;
        }

        Id3Tag tag = new(header, new ByteRange(offset, total));
        if ((header.Flags & ~(Id3TagAttributes.Unsynchronisation | Id3TagAttributes.ExtendedHeader | Id3TagAttributes.Experimental | Id3TagAttributes.Footer)) != 0)
        {
            defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, offset, "The ID3v2 header sets flag bits that are reserved.", "id3.header-flags");
        }

        if (header.MajorVersion == 2 && (header.Flags & Id3TagAttributes.ExtendedHeader) != 0)
        {
            // id3v2-00 §3.1: bit 6 of the 2.2 header flags means compression, and no compression scheme was defined; such a tag is to be ignored.
            defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, offset, "The ID3v2.2 tag sets the compression bit, for which no scheme exists; its frames are not read.", "id3.v22-compression");
            tag.Unparsed = new ByteRange(offset + Id3Header.HeaderSize, bodySize);
            return tag;
        }

        TagIo io = new(source, limits, defects);
        long bodyStart = offset + Id3Header.HeaderSize;
        bool tagUnsync = (header.Flags & Id3TagAttributes.Unsynchronisation) != 0;
        Id3Body body;
        if (tagUnsync && header.MajorVersion < 4)
        {
            // In 2.2 and 2.3 frame sizes count de-unsynchronised bytes, so the frame boundaries are only known after undoing it for the whole body.
            limits.CheckTag(bodySize, offset);
            byte[] raw = new byte[bodySize];
            int got = source.Read(bodyStart, raw);
            if (got < raw.Length)
            {
                defects.Add(DefectKind.Truncated, DefectSeverity.Error, bodyStart + got, "The source ends inside the ID3v2 tag.", "id3.truncated");
            }

            body = Id3Body.FromUnsynchronised(raw.AsSpan(0, got), bodyStart);
        }
        else
        {
            body = Id3Body.Direct(new TagWindow(source, bodyStart, bodySize), bodyStart);
        }

        Id3FrameReader reader = new(io, header.MajorVersion, tagUnsync && header.MajorVersion == 4, tagUnsync && header.MajorVersion < 4);
        long position = 0;
        if ((header.Flags & Id3TagAttributes.ExtendedHeader) != 0 && header.MajorVersion >= 3)
        {
            position = reader.SkipExtendedHeader(body, tag);
        }

        reader.ReadFrames(body, position, tag._frames, tag);
        foreach (Id3Frame f in tag._frames)
        {
            if (f.Native is not null)
            {
                tag._tags.Add(f.Native);
            }
        }

        if (header.HasFooter)
        {
            Span<byte> footer = stackalloc byte[Id3Header.HeaderSize]; // the fixed footer
            long footerAt = offset + Id3Header.HeaderSize + bodySize;
            if (source.Read(footerAt, footer) < footer.Length || !footer.StartsWith("3DI"u8))
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, footerAt, "The ID3v2.4 header announces a footer, but it is not there.", "id3.footer");
            }
        }

        return tag;
    }

    internal void SetExtendedHeader(ByteRange range) => ExtendedHeader = range;

    internal void SetPadding(ByteRange range) => Padding = range;

    internal void SetUnparsed(ByteRange range) => Unparsed = range;

    /// <summary>Finds the chapters the tag declares, in the order of their start times.</summary>
    /// <returns>The chapters with their titles (empty when a chapter has no title frame).</returns>
    public IReadOnlyList<(Id3Chapter Chapter, string Title)> Chapters()
    {
        List<(Id3Chapter, string)> list = [];
        foreach (Id3Frame f in _frames)
        {
            if (f.Chapter is { } c)
            {
                string title = string.Empty;
                foreach (Id3Frame sub in c.SubFrames)
                {
                    if (sub.Id is "TIT2" or "TT2" && sub.Values.Count > 0)
                    {
                        title = sub.Values[0];
                        break;
                    }
                }

                list.Add((c, title));
            }
        }

        list.Sort(static (a, b) => a.Item1.StartMilliseconds.CompareTo(b.Item1.StartMilliseconds));
        return list;
    }

    internal static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);

    internal static byte[] Inflate(ReadOnlySpan<byte> compressed, long expected, MediaLimits limits, long offset)
    {
        limits.CheckTag(expected, offset);
        using MemoryStream input = new(compressed.ToArray());
        using ZLibStream z = new(input, CompressionMode.Decompress);
        byte[] output = new byte[expected];
        int total = 0;
        while (total < output.Length)
        {
            int n = z.Read(output, total, output.Length - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total == output.Length ? output : output.AsSpan(0, total).ToArray();
    }
}

/// <summary>Picks the genre name out of an ID3v2 <c>TCON</c> value.</summary>
public static class Id3Genre
{
    /// <summary>
    /// Resolves a content type string: a bare number (2.4), <c>(17)</c> references with an optional refinement text (2.3), <c>RX</c> and <c>CR</c>
    /// (remix and cover), and <c>((</c> as an escaped bracket. A refinement replaces the references, as the 2.3 text says; several references are joined with a slash.
    /// </summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The genre as text, or <c>null</c> when the text is empty.</returns>
    public static string? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string rest = text.Trim();
        List<string> names = [];
        while (rest.Length > 0 && rest[0] == '(' && !rest.StartsWith("((", StringComparison.Ordinal))
        {
            int close = rest.IndexOf(')', StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            string reference = rest[1..close];
            names.Add(reference switch { "RX" => "Remix", "CR" => "Cover", _ => int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && Id3Genres.Name(n) is { } g ? g : reference });
            rest = rest[(close + 1)..];
        }

        if (rest.StartsWith("((", StringComparison.Ordinal))
        {
            rest = rest[1..];
        }

        if (rest.Length > 0)
        {
            return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && names.Count == 0 && Id3Genres.Name(n) is { } name ? name : rest;
        }

        return names.Count == 0 ? text.Trim() : string.Join('/', names);
    }
}

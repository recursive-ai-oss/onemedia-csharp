using System.Buffers.Binary;
using RecursiveAI.OneMedia.Matroska;
using System.Text;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>
/// The decoded Header Object of an ASF file: file properties, streams, codec names, protection and where the tag-carrying objects sit.
/// Everything is read once when the file is opened; the tree and the tag ranges refer back to the file.
/// </summary>
public sealed class AsfHeader
{
    internal AsfHeader(AsfObjectNode root)
    {
        Root = root;
    }

    /// <summary>The Header Object with its children (and the Header Extension's children under it).</summary>
    public AsfObjectNode Root { get; }

    /// <summary>The File Properties object; null when the file has none (a defect is reported).</summary>
    public AsfFileProperties? FileProperties { get; internal set; }

    /// <summary>The streams in the order their Stream Properties objects appear.</summary>
    public IReadOnlyList<AsfStreamEntry> Streams { get; internal set; } = [];

    /// <summary>The Codec List entries.</summary>
    public IReadOnlyList<AsfCodecEntry> Codecs { get; internal set; } = [];

    /// <summary>The Language List: language tags (such as <c>en-US</c>) that streams refer to by index.</summary>
    public IReadOnlyList<string> Languages { get; internal set; } = [];

    /// <summary>How the content is protected, or <see cref="AsfProtection.None"/>.</summary>
    public AsfProtection Protection { get; internal set; }

    /// <summary>The offset of the Data Object, which follows the Header Object; -1 when the file has none there.</summary>
    public long DataObjectOffset { get; internal set; } = -1;

    /// <summary>The size of the Data Object as stated (header included); 0 when unstated.</summary>
    public long DataObjectSize { get; internal set; }

    /// <summary>The number of data packets the Data Object states; 0 when unknown.</summary>
    public long DataPacketCount { get; internal set; }

    /// <summary>The offset of the first data packet.</summary>
    public long DataStart => DataObjectOffset < 0 ? -1 : DataObjectOffset + 50;

    /// <summary>The objects whose bytes carry tags (content description, extended content description, metadata, metadata library), in file order.</summary>
    internal List<(AsfObjectKind Kind, ByteRange Range)> TagObjects { get; } = [];

    /// <summary>The Stream Bitrate Properties records read: stream number to average bit rate.</summary>
    internal Dictionary<int, uint> Bitrates { get; } = [];

    /// <summary>For a source that cannot be read again, the bytes of the tag objects, kept as the header went by.</summary>
    internal SparseSource? RetainedTags { get; set; }

    /// <summary>Ranges of Index Parameters, Simple Index and Index objects found after the data, filled in on demand.</summary>
    internal List<(AsfObjectKind Kind, ByteRange Range)> IndexObjects { get; } = [];
}

internal static class AsfHeaderParser
{
    private const int ObjectHeaderSize = 24;
    private const int HeaderObjectSize = 30;
    private const int HeaderExtensionHeaderSize = 46;
    private const int DataObjectHeaderSize = 50;
    private const long MaxObjectBodyBytes = 1 << 20;

    public static async ValueTask<AsfHeader> ParseAsync(AsfInput input, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        byte[] head = new byte[HeaderObjectSize];
        if (await input.ReadAsync(0, head, ct).ConfigureAwait(false) < HeaderObjectSize || new Guid(head.AsSpan(0, 16)) != AsfGuids.Header)
        {
            throw new MediaFormatException(new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The data does not start with an ASF Header Object."));
        }

        ulong stated = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
        uint childCount = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24));
        long length = input.Length;
        long headerEnd = stated > long.MaxValue ? long.MaxValue : (long)stated;
        bool inferred = false;
        if (headerEnd < HeaderObjectSize)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, 16, $"The Header Object states a size of {stated}, less than its own 30-byte header.");
            throw new MediaFormatException(new MediaDefect(DefectKind.InvalidSize, DefectSeverity.Error, 16, "The ASF Header Object has an invalid size."));
        }

        if (length >= 0 && headerEnd > length)
        {
            defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, 16, $"The Header Object states {stated} bytes but the file has {length}.");
            headerEnd = length;
            inferred = true;
        }

        limits.CheckAllocation(Math.Min(headerEnd, MaxObjectBodyBytes), 0);
        List<IStructureNode> children = [];
        ParseState state = new(input, limits, defects, ct);
        long position = HeaderObjectSize;
        int found = 0;
        while (position + ObjectHeaderSize <= headerEnd)
        {
            (AsfObjectNode? node, long next) = await ReadChildAsync(state, position, headerEnd, depth: 1).ConfigureAwait(false);
            if (node is null)
            {
                break;
            }

            children.Add(node);
            found++;
            limits.CheckElementCount(found, position);
            position = next;
        }

        if (found != childCount)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, 24, $"The Header Object states {childCount} child objects but {found} were found.", "asf.header-count");
        }

        AsfObjectNode root = new(AsfGuids.Header, 0, HeaderObjectSize, headerEnd - HeaderObjectSize, children, inferred);
        AsfHeader header = new(root);
        state.CopyTo(header);
        await FinishAsync(header, input, headerEnd, defects, ct).ConfigureAwait(false);
        return header;
    }

    private sealed class ParseState(AsfInput input, MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        public AsfInput Input { get; } = input;

        public MediaLimits Limits { get; } = limits;

        public DefectLog Defects { get; } = defects;

        public CancellationToken Ct { get; } = ct;

        public AsfFileProperties? FileProperties { get; set; }

        public List<AsfStreamEntry> Streams { get; } = [];

        public List<(int Stream, long Start, long End, uint Bitrate, int Language, long FrameTime, List<string> Names)> Extended { get; } = [];

        public List<AsfCodecEntry> Codecs { get; } = [];

        public List<string> Languages { get; } = [];

        public AsfProtection Protection { get; set; }

        public List<(AsfObjectKind Kind, ByteRange Range)> Tags { get; } = [];

        public Dictionary<int, uint> Bitrates { get; } = [];

        public SparseSource? Retained { get; set; }

        public void CopyTo(AsfHeader h)
        {
            h.FileProperties = FileProperties;
            foreach (var e in Extended)
            {
                foreach (AsfStreamEntry s in Streams)
                {
                    if (s.StreamNumber == e.Stream && !s.HasExtendedProperties)
                    {
                        s.HasExtendedProperties = true;
                        s.StartTimeMs = e.Start;
                        s.EndTimeMs = e.End;
                        s.DataBitrate = e.Bitrate;
                        s.LanguageIndex = e.Language;
                        s.AverageTimePerFrame100ns = e.FrameTime;
                        s.Names = e.Names;
                    }
                }
            }

            foreach (AsfStreamEntry s in Streams)
            {
                if (Bitrates.TryGetValue(s.StreamNumber, out uint b))
                {
                    s.AverageBitrate = b;
                }
            }

            // Ascending stream number: streams described only in Extended Stream Properties join the others in a stable order.
            for (int i = 1; i < Streams.Count; i++)
            {
                AsfStreamEntry moving = Streams[i];
                int j = i - 1;
                for (; j >= 0 && Streams[j].StreamNumber > moving.StreamNumber; j--)
                {
                    Streams[j + 1] = Streams[j];
                }

                Streams[j + 1] = moving;
            }

            h.Streams = Streams;
            h.Codecs = Codecs;
            h.Languages = Languages;
            h.Protection = Protection;
            h.TagObjects.AddRange(Tags);
            foreach (KeyValuePair<int, uint> kv in Bitrates)
            {
                h.Bitrates[kv.Key] = kv.Value;
            }

            h.RetainedTags = Retained;
        }
    }

    // Reads one child object header at 'position' and, for the objects the model needs, its body. Returns the node and the offset of the next object.
    private static async ValueTask<(AsfObjectNode? Node, long Next)> ReadChildAsync(ParseState s, long position, long parentEnd, int depth)
    {
        s.Limits.CheckDepth(depth, position);
        byte[] head = new byte[ObjectHeaderSize];
        if (await s.Input.ReadAsync(position, head, s.Ct).ConfigureAwait(false) < ObjectHeaderSize)
        {
            s.Defects.Add(DefectKind.Truncated, DefectSeverity.Error, position, "The file ends inside an object header.");
            return (null, parentEnd);
        }

        Guid guid = new(head.AsSpan(0, 16));
        ulong stated = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
        if (stated < ObjectHeaderSize)
        {
            s.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, position + 16, $"An object states a size of {stated}, less than its 24-byte header; the rest of the parent is skipped.", "asf.object-size");
            return (null, parentEnd);
        }

        long size = stated > (ulong)(parentEnd - position) ? parentEnd - position : (long)stated;
        bool inferred = (ulong)size != stated;
        if (inferred)
        {
            s.Defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, position + 16, $"An object states {stated} bytes but its parent has {parentEnd - position} left.", "asf.object-size");
        }

        AsfObjectKind kind = AsfGuids.KindOf(guid);
        long bodyStart = position + ObjectHeaderSize;
        long bodyLength = size - ObjectHeaderSize;
        List<IStructureNode>? children = null;
        int headerSize = ObjectHeaderSize;
        switch (kind)
        {
            case AsfObjectKind.FileProperties:
                await ReadFilePropertiesAsync(s, position, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.StreamProperties:
                await ReadStreamPropertiesAsync(s, position, size, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.CodecList:
                await ReadCodecListAsync(s, position, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.StreamBitrateProperties:
                await ReadBitratesAsync(s, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.ContentEncryption:
                s.Protection |= AsfProtection.ContentEncryption;
                break;
            case AsfObjectKind.ExtendedContentEncryption:
                s.Protection |= AsfProtection.ExtendedContentEncryption;
                break;
            case AsfObjectKind.ContentDescription or AsfObjectKind.ExtendedContentDescription or AsfObjectKind.Metadata or AsfObjectKind.MetadataLibrary:
                await NoteTagObjectAsync(s, kind, position, size).ConfigureAwait(false);
                break;
            case AsfObjectKind.LanguageList:
                await ReadLanguageListAsync(s, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.ExtendedStreamProperties:
                await ReadExtendedStreamPropertiesAsync(s, position, bodyStart, bodyLength).ConfigureAwait(false);
                break;
            case AsfObjectKind.HeaderExtension:
                headerSize = HeaderExtensionHeaderSize;
                children = await ReadHeaderExtensionAsync(s, position, bodyStart, size, depth).ConfigureAwait(false);
                break;
        }

        return (new AsfObjectNode(guid, position, headerSize, size - headerSize, children, inferred), position + size);
    }

    private static async ValueTask<byte[]?> ReadBodyAsync(ParseState s, long offset, long length, long at, string what)
    {
        if (length < 0 || length > MaxObjectBodyBytes)
        {
            s.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, at, $"The {what} object is {length} bytes, more than the {MaxObjectBodyBytes} this reader accepts; it is not read.", "asf.object-too-large");
            return null;
        }

        s.Limits.CheckAllocation(length, at);
        byte[] body = new byte[length];
        int got = await s.Input.ReadAsync(offset, body, s.Ct).ConfigureAwait(false);
        if (got < length)
        {
            s.Defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset + got, $"The file ends inside the {what} object.");
            return null;
        }

        return body;
    }

    private static async ValueTask ReadFilePropertiesAsync(ParseState s, long position, long bodyStart, long bodyLength)
    {
        if (s.FileProperties is not null)
        {
            s.Defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, position, "A second File Properties object is ignored.", "asf.duplicate-file-properties");
            return;
        }

        if (bodyLength < 80)
        {
            s.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, position, $"File Properties has {bodyLength} bytes of data; 80 are needed.", "asf.file-properties");
            return;
        }

        byte[]? b = await ReadBodyAsync(s, bodyStart, 80, position, "File Properties").ConfigureAwait(false);
        if (b is null)
        {
            return;
        }

        // Field layout as ExifTool's documentation lists it by index, confirmed on sample files: ffprobe's duration, start time and the packet size match.
        s.FileProperties = new AsfFileProperties(
            new Guid(b.AsSpan(0, 16)),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(24))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(40))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(48))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(56))),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(64)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(68)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(72)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(76)),
            new ByteRange(position, bodyLength + ObjectHeaderSize));
    }

    private static long Clamp(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;

    private static async ValueTask ReadStreamPropertiesAsync(ParseState s, long position, long size, long bodyStart, long bodyLength)
    {
        if (bodyLength < 54)
        {
            s.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, position, $"Stream Properties has {bodyLength} bytes of data; 54 are needed.", "asf.stream-properties");
            return;
        }

        byte[]? b = await ReadBodyAsync(s, bodyStart, bodyLength, position, "Stream Properties").ConfigureAwait(false);
        if (b is null)
        {
            return;
        }

        uint typeLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40));
        uint correctionLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(48));
        if (typeLength > b.Length - 54)
        {
            s.Defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, position, "The type-specific data of a stream is longer than its object; it is cut.", "asf.stream-properties");
            typeLength = (uint)(b.Length - 54);
        }

        if (correctionLength > b.Length - 54 - typeLength)
        {
            correctionLength = (uint)(b.Length - 54 - typeLength);
        }

        int number = flags & 0x7F;
        if (number == 0)
        {
            s.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, position + ObjectHeaderSize + 48, "A stream has the reserved stream number 0.", "asf.stream-number");
        }

        foreach (AsfStreamEntry existing in s.Streams)
        {
            if (existing.StreamNumber == number)
            {
                s.Defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, position, $"Stream number {number} appears twice; payloads of that number go to the first.", "asf.duplicate-stream");
            }
        }

        bool encrypted = (flags & 0x8000) != 0;
        if (encrypted)
        {
            s.Protection |= AsfProtection.EncryptedStream;
        }

        s.Limits.CheckTracks(s.Streams.Count + 1, position);
        s.Streams.Add(new AsfStreamEntry(number, new Guid(b.AsSpan(0, 16)), new Guid(b.AsSpan(16, 16)), (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32)), long.MaxValue),
            encrypted, b.AsSpan(54, (int)typeLength).ToArray(), (int)correctionLength, new ByteRange(position, size)));
    }

    private static async ValueTask ReadCodecListAsync(ParseState s, long position, long bodyStart, long bodyLength)
    {
        byte[]? b = await ReadBodyAsync(s, bodyStart, bodyLength, position, "Codec List").ConfigureAwait(false);
        if (b is null || b.Length < 20)
        {
            return;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        int at = 20;
        for (uint i = 0; i < count && at + 4 <= b.Length; i++)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            at += 2;
            string name = ReadCountedString(b, ref at, 2, wide: true);
            string description = ReadCountedString(b, ref at, 2, wide: true);
            if (at + 2 > b.Length)
            {
                break;
            }

            int infoLength = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            at += 2 + infoLength;
            s.Codecs.Add(new AsfCodecEntry(type == 1, type, name, description));
        }
    }

    // A length (in characters when wide) followed by UTF-16LE text; the length counts the terminating NUL.
    private static string ReadCountedString(byte[] b, ref int at, int lengthBytes, bool wide)
    {
        if (at + lengthBytes > b.Length)
        {
            at = b.Length;
            return string.Empty;
        }

        int chars = lengthBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : b[at];
        at += lengthBytes;
        int bytes = wide ? chars * 2 : chars;
        if (at + bytes > b.Length)
        {
            bytes = b.Length - at;
        }

        string text = Encoding.Unicode.GetString(b, at, bytes & ~1).TrimEnd('\0');
        at += bytes;
        return text;
    }

    private static async ValueTask ReadBitratesAsync(ParseState s, long bodyStart, long bodyLength)
    {
        byte[]? b = await ReadBodyAsync(s, bodyStart, bodyLength, bodyStart, "Stream Bitrate Properties").ConfigureAwait(false);
        if (b is null || b.Length < 2)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(b);
        for (int i = 0, at = 2; i < count && at + 6 <= b.Length; i++, at += 6)
        {
            s.Bitrates[BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) & 0x7F] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 2));
        }
    }

    private static async ValueTask NoteTagObjectAsync(ParseState s, AsfObjectKind kind, long position, long size)
    {
        ByteRange range = new(position, size);
        s.Tags.Add((kind, range));
        if (s.Input.CanSeek)
        {
            return;
        }

        // A forward-only source cannot be read again, so the tag bytes are kept while they go by (up to the tag limit).
        if (size > s.Limits.MaxTagBytes)
        {
            s.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, position, "A tag object is larger than the tag limit and, because the source cannot be read again, is not kept.", "tags.value-too-large");
            return;
        }

        byte[] bytes = new byte[size];
        int got = await s.Input.ReadAsync(position, bytes, s.Ct).ConfigureAwait(false);
        s.Retained ??= new SparseSource(long.MaxValue);
        s.Retained.Add(position, got == bytes.Length ? bytes : bytes[..got]);
    }

    private static async ValueTask ReadLanguageListAsync(ParseState s, long bodyStart, long bodyLength)
    {
        byte[]? b = await ReadBodyAsync(s, bodyStart, bodyLength, bodyStart, "Language List").ConfigureAwait(false);
        if (b is null || b.Length < 2)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(b);
        int at = 2;
        for (int i = 0; i < count && at < b.Length; i++)
        {
            s.Languages.Add(ReadCountedString(b, ref at, 1, wide: true));
        }
    }

    private static async ValueTask ReadExtendedStreamPropertiesAsync(ParseState s, long position, long bodyStart, long bodyLength)
    {
        if (bodyLength < 64)
        {
            s.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, position, "Extended Stream Properties is shorter than its fixed fields.", "asf.extended-stream-properties");
            return;
        }

        byte[]? b = await ReadBodyAsync(s, bodyStart, bodyLength, position, "Extended Stream Properties").ConfigureAwait(false);
        if (b is null)
        {
            return;
        }

        long start = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0)));
        long end = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8)));
        uint bitrate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        int stream = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(48)) & 0x7F;
        int language = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(50));
        long frameTime = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(52)));
        int nameCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(60));
        int extensionCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(62));
        List<string> names = [];
        int at = 64;
        for (int i = 0; i < nameCount && at + 4 <= b.Length; i++)
        {
            at += 2; // the name's language index
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            at += 2;
            if (at + nameLength > b.Length)
            {
                break;
            }

            names.Add(Encoding.Unicode.GetString(b, at, nameLength & ~1).TrimEnd('\0'));
            at += nameLength;
        }

        // Payload extension systems: GUID (16), data size (2), info length (4), info. They describe replicated-data layouts this reader does not need.
        for (int i = 0; i < extensionCount && at + 22 <= b.Length; i++)
        {
            uint infoLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 18));
            if (infoLength > b.Length - at - 22)
            {
                at = b.Length;
                break;
            }

            at += 22 + (int)infoLength;
        }

        s.Extended.Add((stream, start, end, bitrate, language, frameTime, names));

        // Observed in files written by the Windows Media tools: a stream may be described only here, by a complete Stream Properties object
        // after the extension systems.
        if (at + ObjectHeaderSize <= b.Length && new Guid(b.AsSpan(at, 16)) == AsfGuids.StreamProperties)
        {
            ulong embeddedSize = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at + 16));
            if (embeddedSize >= ObjectHeaderSize + 54 && embeddedSize <= (ulong)(b.Length - at))
            {
                await ReadStreamPropertiesAsync(s, bodyStart + at, (long)embeddedSize, bodyStart + at + ObjectHeaderSize, (long)embeddedSize - ObjectHeaderSize).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<List<IStructureNode>> ReadHeaderExtensionAsync(ParseState s, long position, long bodyStart, long size, int depth)
    {
        List<IStructureNode> children = [];
        if (size < HeaderExtensionHeaderSize)
        {
            s.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, position, "The Header Extension is shorter than its fixed fields.", "asf.header-extension");
            return children;
        }

        byte[] fixedPart = new byte[22];
        if (await s.Input.ReadAsync(bodyStart, fixedPart, s.Ct).ConfigureAwait(false) < 22)
        {
            s.Defects.Add(DefectKind.Truncated, DefectSeverity.Error, bodyStart, "The file ends inside the Header Extension.");
            return children;
        }

        if (new Guid(fixedPart.AsSpan(0, 16)) != AsfGuids.HeaderExtensionReserved)
        {
            s.Defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, bodyStart, "The Header Extension's reserved GUID is not the usual one.", "asf.header-extension");
        }

        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(18));
        long end = position + size;
        if (dataSize != size - HeaderExtensionHeaderSize)
        {
            s.Defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, bodyStart + 18, $"The Header Extension states {dataSize} bytes of data but its object holds {size - HeaderExtensionHeaderSize}.", "asf.header-extension");
        }

        long at = position + HeaderExtensionHeaderSize;
        int count = 0;
        while (at + ObjectHeaderSize <= end)
        {
            (AsfObjectNode? node, long next) = await ReadChildAsync(s, at, end, depth + 1).ConfigureAwait(false);
            if (node is null)
            {
                break;
            }

            children.Add(node);
            s.Limits.CheckElementCount(++count, at);
            at = next;
        }

        return children;
    }

    // The Data Object follows the Header Object. Its fixed fields give the packet count.
    private static async ValueTask FinishAsync(AsfHeader header, AsfInput input, long headerEnd, DefectLog defects, CancellationToken ct)
    {
        if (header.FileProperties is null)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The header has no File Properties object.", "asf.no-file-properties");
        }

        byte[] data = new byte[DataObjectHeaderSize];
        int got = await input.ReadAsync(headerEnd, data, ct).ConfigureAwait(false);
        if (got < DataObjectHeaderSize || new Guid(data.AsSpan(0, 16)) != AsfGuids.Data)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, headerEnd, "No Data Object follows the Header Object; the file has no packets.", "asf.no-data-object");
            return;
        }

        header.DataObjectOffset = headerEnd;
        header.DataObjectSize = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)));
        header.DataPacketCount = Clamp(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40)));
        if (header.FileProperties is { } fp && fp.DataPacketCount != header.DataPacketCount && fp.DataPacketCount != 0 && header.DataPacketCount != 0)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, headerEnd + 40, $"The Data Object counts {header.DataPacketCount} packets, File Properties {fp.DataPacketCount}.", "asf.packet-count");
        }

        if (header.FileProperties is { } p && new Guid(data.AsSpan(24, 16)) != p.FileId)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, headerEnd + 24, "The Data Object's file ID differs from the File Properties file ID.", "asf.file-id");
        }
    }
}

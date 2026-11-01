using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using System.Xml;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tags.Riff;

/// <summary>
/// The tags one chunk of an audio file holds: a <c>LIST INFO</c>, a <c>bext</c>, an <c>iXML</c>, an AIFF text chunk, the CAF <c>info</c> chunk…
/// Each tag carries the byte range of its value in the file.
/// </summary>
public sealed class ChunkTagSet : ITagContainer
{
    internal ChunkTagSet(string system, string chunkId, ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        System = system;
        ChunkId = chunkId;
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System { get; }

    /// <summary>The id of the chunk the tags come from (<c>LIST</c>, <c>bext</c>, <c>iXML</c>, <c>NAME</c>, <c>info</c>).</summary>
    public string ChunkId { get; }

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>
/// Reads the native tag layers of WAVE, AIFF and CAF files from the chunks the structure readers found: RIFF <c>LIST INFO</c>, the Broadcast Wave
/// <c>bext</c> chunk (EBU Tech 3285, versions 0 to 2), <c>iXML</c> (the iXML specification), <c>axml</c>, the <c>cart</c> chunk (AES46), AIFF text chunks and
/// the CAF <c>info</c> chunk. Nothing is lost: every field of a chunk becomes a tag with its range, and ID3 chunks are kept as raw binary tags.
/// </summary>
public static class RiffTagReader
{
    private const int EagerValueBytes = 64 * 1024;

    /// <summary>Reads a <c>LIST</c> chunk of type <c>INFO</c>: one tag per sub-chunk, keyed by its four-character id (<c>INAM</c>, <c>IART</c>…).</summary>
    /// <param name="source">The source.</param>
    /// <param name="list">The <c>LIST</c> chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadInfo(IMediaSource source, ChunkNode list, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(list);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        foreach (ChunkNode c in list.Chunks)
        {
            ByteRange value = c.Payload;
            if (value.Length == 0 || c.Id is "JUNK" or "PAD " or "FLLR")
            {
                continue;
            }

            NativeTag t = new(io, TagSystems.RiffInfo, c.Id, 0, TagValueKind.Text, 1, 0, null, c.Range, value);
            if (value.Length <= EagerValueBytes)
            {
                string text = Decode(io.Read(value), value.Offset, defects, nulTerminated: true); // INFO strings are ZSTR: the text ends at the first NUL
                if (text.Length == 0)
                {
                    continue;
                }

                t.SetText(text);
            }

            tags.Add(t);
        }

        return new ChunkTagSet(TagSystems.RiffInfo, "LIST", list.Range, tags);
    }

    /// <summary>Reads a Broadcast Wave <c>bext</c> chunk: its fixed fields (empty text fields and unset loudness values are left out) and the coding history.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The <c>bext</c> chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadBext(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        long size = chunk.PayloadSize;
        long at = chunk.Payload.Offset;
        if (size < 602)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, chunk.Offset, $"The bext chunk has {size} bytes; the fixed fields need 602.", "riff.bext-short");
        }

        // EBU Tech 3285 v2 §2: the fixed part of bext is 602 bytes; the coding history follows.
        int head = (int)Math.Min(size, 602);
        byte[] b = io.Read(new ByteRange(at, head));
        void Str(string key, int offset, int length)
        {
            if (offset + length > b.Length)
            {
                return;
            }

            string s = Decode(b.AsSpan(offset, length), at + offset, defects, nulTerminated: true);
            if (s.Length > 0)
            {
                tags.Add(Text(io, TagSystems.Bext, key, chunk.Range, new ByteRange(at + offset, length), s));
            }
        }

        Str("Description", 0, 256);
        Str("Originator", 256, 32);
        Str("OriginatorReference", 288, 32);
        Str("OriginationDate", 320, 10);
        Str("OriginationTime", 330, 8);
        if (b.Length >= 346)
        {
            ulong reference = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(338)) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(342)) << 32);
            tags.Add(new NativeTag(io, TagSystems.Bext, "TimeReference", 0, TagValueKind.Number, 0, 0, null, chunk.Range, new ByteRange(at + 338, 8)) { Number = reference > long.MaxValue ? long.MaxValue : (long)reference });
        }

        int version = b.Length >= 348 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(346)) : 0;
        if (b.Length >= 348)
        {
            tags.Add(new NativeTag(io, TagSystems.Bext, "BextVersion", 0, TagValueKind.Number, 0, 0, null, chunk.Range, new ByteRange(at + 346, 2)) { Number = version });
        }

        if (version >= 1 && b.Length >= 412 && Array.Exists(b[348..412], x => x != 0))
        {
            tags.Add(new NativeTag(io, TagSystems.Bext, "UMID", 0, TagValueKind.Binary, 0, 0, null, chunk.Range, new ByteRange(at + 348, 64)));
        }

        if (version >= 2 && b.Length >= 422)
        {
            string[] names = ["LoudnessValue", "LoudnessRange", "MaxTruePeakLevel", "MaxMomentaryLoudness", "MaxShortTermLoudness"];
            for (int i = 0; i < names.Length; i++)
            {
                short v = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(412 + (i * 2)));
                if (v != 0x7FFF)
                {
                    // Tech 3285 v2: loudness values are in hundredths of LU, LUFS or dBTP; 0x7FFF means "not set".
                    tags.Add(new NativeTag(io, TagSystems.Bext, names[i], 0, TagValueKind.Real, 0, 0, null, chunk.Range, new ByteRange(at + 412 + (i * 2), 2)) { RealValue = v / 100.0 });
                }
            }
        }

        if (size > 602)
        {
            ByteRange history = new(at + 602, size - 602);
            NativeTag t = new(io, TagSystems.Bext, "CodingHistory", 0, TagValueKind.Text, 1, 0, null, chunk.Range, history);
            if (history.Length <= EagerValueBytes)
            {
                string s = Decode(io.Read(history), history.Offset, defects, nulTerminated: false);
                if (s.Length == 0)
                {
                    return new ChunkTagSet(TagSystems.Bext, "bext", chunk.Range, tags);
                }

                t.SetText(s);
            }

            tags.Add(t);
        }

        return new ChunkTagSet(TagSystems.Bext, "bext", chunk.Range, tags);
    }

    /// <summary>
    /// Reads an <c>iXML</c> chunk (the iXML specification: a BWFXML document). Each element that holds text becomes a tag keyed by its path below
    /// the root (<c>PROJECT</c>, <c>SPEED/TAPE</c>, <c>TRACK_LIST/TRACK/NAME</c>); a repeated element gives repeated tags. XML has no byte offsets, so every
    /// tag's range is the whole chunk payload. A document that is not well-formed yields one binary tag with the chunk and a defect.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The <c>iXML</c> chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadIXml(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        ByteRange payload = chunk.Payload;
        if (payload.Length > limits.MaxTagBytes)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, chunk.Offset, "The iXML chunk is larger than the tag limit and is not read.", "riff.ixml-large");
            tags.Add(new NativeTag(io, TagSystems.IXml, "iXML", 0, TagValueKind.Binary, 0, 0, null, chunk.Range, payload));
            return new ChunkTagSet(TagSystems.IXml, chunk.Id, chunk.Range, tags);
        }

        byte[] xml = io.Read(payload);
        // Writers pad the chunk with NULs and some start with a byte-order mark.
        int length = xml.Length;
        while (length > 0 && xml[length - 1] == 0)
        {
            length--;
        }

        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.MaxTagBytes, IgnoreComments = true, IgnoreProcessingInstructions = true };
            using MemoryStream ms = new(xml, 0, length, writable: false);
            using XmlReader reader = XmlReader.Create(ms, settings);
            List<string> path = [];
            List<StringBuilder> texts = [];
            List<bool> hasChild = [];
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (path.Count >= limits.MaxStructureDepth)
                        {
                            throw new XmlException("The iXML document is nested deeper than the limit.");
                        }

                        if (hasChild.Count > 0)
                        {
                            hasChild[^1] = true;
                        }

                        path.Add(reader.LocalName);
                        texts.Add(new StringBuilder());
                        hasChild.Add(false);
                        if (reader.IsEmptyElement)
                        {
                            Pop(path, texts, hasChild);
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                        if (texts.Count > 0)
                        {
                            texts[^1].Append(reader.Value);
                        }

                        break;
                    case XmlNodeType.EndElement:
                        string value = texts[^1].ToString().Trim();
                        if (!hasChild[^1] && value.Length > 0 && path.Count > 1)
                        {
                            tags.Add(Text(io, TagSystems.IXml, string.Join('/', path.GetRange(1, path.Count - 1)), chunk.Range, payload, value));
                        }

                        Pop(path, texts, hasChild);
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, payload.Offset, "The iXML chunk is not well-formed XML: " + e.Message, "riff.ixml-xml");
            tags.Clear();
            tags.Add(new NativeTag(io, TagSystems.IXml, "iXML", 0, TagValueKind.Binary, 0, 0, null, chunk.Range, payload));
        }

        return new ChunkTagSet(TagSystems.IXml, chunk.Id, chunk.Range, tags);
    }

    /// <summary>Reads an <c>axml</c> chunk (EBU Tech 3285 supplement 5: an XML document of the Audio Definition Model) as one text tag read on demand.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The <c>axml</c> chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadAxml(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        return new ChunkTagSet(TagSystems.AXml, chunk.Id, chunk.Range, [new NativeTag(io, TagSystems.AXml, "axml", 0, TagValueKind.Text, 1, 0, null, chunk.Range, chunk.Payload)]);
    }

    /// <summary>Reads a <c>cart</c> chunk (AES46-2002 broadcast cart): the 2048 fixed bytes as named fields and the free text after them.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The <c>cart</c> chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadCart(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        long at = chunk.Payload.Offset;
        if (chunk.PayloadSize < 2048)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, chunk.Offset, $"The cart chunk has {chunk.PayloadSize} bytes; the fixed fields need 2048.", "riff.cart-short");
        }

        byte[] b = io.Read(new ByteRange(at, Math.Min(chunk.PayloadSize, 2048)));
        void Str(string key, int offset, int length)
        {
            if (offset + length > b.Length)
            {
                return;
            }

            string s = Decode(b.AsSpan(offset, length), at + offset, defects, nulTerminated: true);
            if (s.Length > 0)
            {
                tags.Add(Text(io, TagSystems.Cart, key, chunk.Range, new ByteRange(at + offset, length), s));
            }
        }

        // AES46-2002 §5: version, then the fixed text fields in this order.
        Str("Version", 0, 4);
        Str("Title", 4, 64);
        Str("Artist", 68, 64);
        Str("CutID", 132, 64);
        Str("ClientID", 196, 64);
        Str("Category", 260, 64);
        Str("Classification", 324, 64);
        Str("OutCue", 388, 64);
        Str("StartDate", 452, 10);
        Str("StartTime", 462, 8);
        Str("EndDate", 470, 10);
        Str("EndTime", 480, 8);
        Str("ProducerAppID", 488, 64);
        Str("ProducerAppVersion", 552, 64);
        Str("UserDef", 616, 64);
        if (b.Length >= 684)
        {
            tags.Add(new NativeTag(io, TagSystems.Cart, "LevelReference", 0, TagValueKind.Number, 0, 0, null, chunk.Range, new ByteRange(at + 680, 4)) { Number = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(680)) });
        }

        Str("URL", 1024, 1024);
        if (chunk.PayloadSize > 2048)
        {
            ByteRange text = new(at + 2048, chunk.PayloadSize - 2048);
            NativeTag t = new(io, TagSystems.Cart, "TagText", 0, TagValueKind.Text, 1, 0, null, chunk.Range, text);
            tags.Add(t);
        }

        return new ChunkTagSet(TagSystems.Cart, chunk.Id, chunk.Range, tags);
    }

    /// <summary>Keeps an ID3v2 tag stored in a chunk (<c>id3 </c> or <c>ID3 </c>) as one raw binary tag; the ID3 layer reads inside it.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadId3Chunk(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        return new ChunkTagSet(TagSystems.Id3Chunk, chunk.Id, chunk.Range, [new NativeTag(io, TagSystems.Id3Chunk, chunk.Id, 0, TagValueKind.Binary, 0, 0, null, chunk.Range, chunk.Payload)]);
    }

    /// <summary>Reads one AIFF text chunk (<c>NAME</c>, <c>AUTH</c>, <c>(c) </c>, <c>ANNO</c>) as a tag keyed by its id.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags (none when the text is empty).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadAiffText(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        ByteRange value = chunk.Payload;
        if (value.Length > 0)
        {
            NativeTag t = new(io, TagSystems.Aiff, chunk.Id, 0, TagValueKind.Text, 1, 0, null, chunk.Range, value);
            if (value.Length <= EagerValueBytes)
            {
                string s = Decode(io.Read(value), value.Offset, defects, nulTerminated: false);
                if (s.Length > 0)
                {
                    t.SetText(s);
                    tags.Add(t);
                }
            }
            else
            {
                tags.Add(t);
            }
        }

        return new ChunkTagSet(TagSystems.Aiff, chunk.Id, chunk.Range, tags);
    }

    /// <summary>Reads the AIFF <c>COMT</c> chunk: each comment becomes a text tag keyed <c>COMT</c>, the timestamp and marker are dropped from the text.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ChunkTagSet ReadAiffComments(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        limits.CheckTag(chunk.PayloadSize, chunk.Offset);
        byte[] b = io.Read(chunk.Payload);
        long at = chunk.Payload.Offset;
        if (b.Length >= 2)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(b);
            int p = 2;
            for (int i = 0; i < count && p + 8 <= b.Length; i++)
            {
                // AIFF 1.3 "Comments Chunk": time stamp (4), marker id (2), count (2), text padded to an even length.
                int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 6));
                if (p + 8 + n > b.Length)
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at + p, "A comment in the COMT chunk runs past the chunk.", "aiff.comt");
                    break;
                }

                string s = Decode(b.AsSpan(p + 8, n), at + p + 8, defects, nulTerminated: false);
                if (s.Length > 0)
                {
                    tags.Add(Text(io, TagSystems.Aiff, "COMT", new ByteRange(at + p, 8 + n), new ByteRange(at + p + 8, n), s));
                }

                p += 8 + n + (n & 1);
            }
        }

        return new ChunkTagSet(TagSystems.Aiff, chunk.Id, chunk.Range, tags);
    }

    /// <summary>Reads the CAF <c>info</c> chunk (Core Audio Format specification): a count, then pairs of NUL-terminated UTF-8 key and value strings.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The whole chunk, header included.</param>
    /// <param name="payload">The chunk's data.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The tags.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MediaLimitExceededException">The chunk is larger than <see cref="MediaLimits.MaxTagBytes"/>.</exception>
    public static ChunkTagSet ReadCafInfo(IMediaSource source, ByteRange chunk, ByteRange payload, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<NativeTag> tags = [];
        limits.CheckTag(payload.Length, payload.Offset);
        byte[] b = io.Read(payload);
        if (b.Length >= 4)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(b);
            int p = 4;
            for (uint i = 0; i < count && p < b.Length; i++)
            {
                int keyEnd = Array.IndexOf(b, (byte)0, p);
                if (keyEnd < 0)
                {
                    break;
                }

                int valueStart = keyEnd + 1;
                int valueEnd = valueStart < b.Length ? Array.IndexOf(b, (byte)0, valueStart) : -1;
                if (valueEnd < 0)
                {
                    valueEnd = b.Length;
                }

                string key = Encoding.UTF8.GetString(b, p, keyEnd - p);
                ByteRange value = new(payload.Offset + valueStart, Math.Max(0, valueEnd - valueStart));
                string text = Decode(b.AsSpan(valueStart, (int)value.Length), value.Offset, defects, nulTerminated: false);
                tags.Add(Text(io, TagSystems.CafInfo, key, new ByteRange(payload.Offset + p, valueEnd - p), value, text));
                p = valueEnd + 1;
            }
        }

        return new ChunkTagSet(TagSystems.CafInfo, "info", chunk, tags);
    }

    private static void Pop(List<string> path, List<StringBuilder> texts, List<bool> hasChild)
    {
        path.RemoveAt(path.Count - 1);
        texts.RemoveAt(texts.Count - 1);
        hasChild.RemoveAt(hasChild.Count - 1);
    }

    internal static NativeTag Text(TagIo io, string system, string key, ByteRange range, ByteRange value, string text)
    {
        NativeTag t = new(io, system, key, 0, TagValueKind.Text, 1, 0, null, range, value);
        t.SetText(text);
        return t;
    }

    /// <summary>
    /// Decodes tag text the way these formats are written in practice: UTF-8 when it is valid, otherwise Latin-1 (RIFF names an ANSI code page, which
    /// the BCL cannot assume), with trailing NULs removed, or everything after the first NUL for fixed-width fields.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, long offset, DefectLog defects, bool nulTerminated)
    {
        if (nulTerminated)
        {
            int nul = bytes.IndexOf((byte)0);
            if (nul >= 0)
            {
                bytes = bytes[..nul];
            }
        }
        else
        {
            while (bytes.Length > 0 && bytes[^1] == 0)
            {
                bytes = bytes[..^1];
            }
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        if (Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, offset, "The text is not valid UTF-8; it is read as Latin-1.", "riff.text-latin1");
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The date a <c>bext</c> chunk's OriginationDate and OriginationTime state, as a wall-clock reading without a zone.</summary>
    /// <param name="date">Text like <c>2020-01-31</c> (EBU Tech 3285 uses <c>yyyy-mm-dd</c>).</param>
    /// <param name="time">Text like <c>14:03:59</c>; may be empty.</param>
    /// <param name="value">The reading.</param>
    /// <returns>False when the text is not a date.</returns>
    public static bool TryParseOrigination(string date, string time, out DateTime value)
    {
        ArgumentNullException.ThrowIfNull(date);
        ArgumentNullException.ThrowIfNull(time);
        value = default;
        if (!DateTime.TryParseExact(date.Replace('/', '-').Replace('.', '-'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            return false;
        }

        if (time.Length > 0 && TimeSpan.TryParseExact(time.Replace('-', ':').Replace('.', ':'), "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out TimeSpan t))
        {
            d += t;
        }

        value = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
        return true;
    }
}

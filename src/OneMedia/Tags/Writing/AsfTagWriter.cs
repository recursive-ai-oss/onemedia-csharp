using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Text;
using RecursiveAI.OneMedia.Asf;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Plans tag edits in ASF/WMA/WMV files: the Content Description (title, author, copyright, description, rating) and the Extended Content
/// Description (named, typed attributes). Every other header object is copied unchanged; the Header Object's size and child count, the File
/// Properties' file size and the Header Extension's size are the only other bytes that change, and the media data is copied by range. The
/// data packets and the index objects refer to packets and to positions inside the Data Object, never to file offsets, so a header that
/// grows or shrinks moves nothing that needs fixing.
/// </summary>
/// <remarks>
/// When the Header Extension holds a Padding object, a smaller or larger description is absorbed by it and the file keeps its layout, so
/// <see cref="TagWriter.SaveInPlace"/> works. Pictures are written as <c>WM/Picture</c> in the Extended Content Description, whose values are
/// limited to 65,535 bytes; a larger picture is reported and not written. Attributes in Metadata and Metadata Library objects (per-stream
/// attributes, large pictures) are read but not edited. Files that carry content encryption are refused.
/// </remarks>
internal static class AsfTagWriter
{
    private const int ObjectHeader = 24;
    private const int HeaderObjectSize = 30;
    private const int HeaderExtensionHeader = 46;
    private const int TypeUnicode = 0, TypeBytes = 1, TypeDword = 3;
    private const int MaxValue = ushort.MaxValue;
    private static readonly string[] ContentKeys = ["Title", "Author", "Copyright", "Description", "Rating"];

    private sealed record Child(Guid Id, long Offset, long Size);

    private sealed class Attribute(string name, int type, byte[] value)
    {
        public string Name { get; } = name;

        public int Type { get; } = type;

        public byte[] Value { get; } = value;
    }

    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        long length = src.Length;
        Span<byte> head = stackalloc byte[HeaderObjectSize]; // the fixed Header Object prefix
        if (src.Read(0, head, ct) < HeaderObjectSize || new Guid(head[..16]) != AsfGuids.Header)
        {
            throw new MediaFormatException("Not an ASF file.");
        }

        long headerSize = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(head[16..]), long.MaxValue);
        if (headerSize < HeaderObjectSize || headerSize > length)
        {
            throw new UnsupportedMediaException("The ASF Header Object has an invalid size; tags cannot be written safely.");
        }

        List<Child> children = [];
        byte[] hdr = new byte[ObjectHeader];
        for (long at = HeaderObjectSize; at < headerSize;)
        {
            limits.CheckElementCount(children.Count + 1, at);
            if (at + ObjectHeader > headerSize || src.Read(at, hdr, ct) < ObjectHeader)
            {
                throw new UnsupportedMediaException("An object inside the ASF header is cut short; tags cannot be written safely.");
            }

            long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(16)), long.MaxValue);
            if (size < ObjectHeader || at + size > headerSize)
            {
                throw new UnsupportedMediaException("An object inside the ASF header has an invalid size; tags cannot be written safely.");
            }

            children.Add(new Child(new Guid(hdr.AsSpan(0, 16)), at, size));
            at += size;
        }

        if (children.Any(c => c.Id == AsfGuids.ContentEncryption || c.Id == AsfGuids.ExtendedContentEncryption))
        {
            throw new UnsupportedMediaException("The ASF file carries content protection; its tags are not edited.");
        }

        Child? cdChild = children.FirstOrDefault(c => c.Id == AsfGuids.ContentDescription);
        Child? ecdChild = children.FirstOrDefault(c => c.Id == AsfGuids.ExtendedContentDescription);
        string[] content = cdChild is null ? new string[5] : ReadContent(src, cdChild, limits, ct);
        List<Attribute> attributes = ecdChild is null ? [] : ReadExtended(src, ecdChild, limits, ct);

        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(
            TagSystems.Asf,
            edits,
            key => ExistingText(content, attributes, key),
            f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Combined : PairStyle.None,
            null,
            ops);
        List<string> conv = [.. rep.Conversions];
        List<string> notes = [];
        foreach (KeyOp op in ops)
        {
            Apply(op, content, attributes, conv);
        }

        ApplyPictures(edits, attributes, limits, notes);
        if (attributes.Any(a => a.Name == "ID3"))
        {
            notes.Add("asf: the file carries an ID3 tag in its 'ID3' attribute; it is kept as it was, so values it holds still show in the common view");
        }

        byte[]? newCd = content.Any(s => !string.IsNullOrEmpty(s)) ? BuildContent(content) : null;
        byte[]? newEcd = attributes.Count > 0 ? BuildExtended(attributes) : null;
        long oldTags = (cdChild?.Size ?? 0) + (ecdChild?.Size ?? 0);
        long newTags = (newCd is null ? 0 : newCd.Length + ObjectHeader) + (newEcd is null ? 0 : newEcd.Length + ObjectHeader);
        long delta = newTags - oldTags;

        // Header Extension: a Padding object inside it takes up the difference so that nothing after the header moves.
        Child? ext = children.FirstOrDefault(c => c.Id == AsfGuids.HeaderExtension);
        long extDelta = 0;
        Child? pad = null;
        long newPad = 0;
        if (ext is not null)
        {
            pad = FindPadding(src, ext, limits, ct);
            if (pad is not null)
            {
                long fit = pad.Size - delta;
                newPad = fit >= ObjectHeader || (fit == 0 && delta > 0) ? fit : Math.Max(options.PaddingBytes, ObjectHeader);
                extDelta = newPad - pad.Size;
            }
            else if (delta > 0 && options.PaddingBytes >= ObjectHeader)
            {
                newPad = options.PaddingBytes;
                extDelta = newPad;
            }
        }

        long newHeaderSize = headerSize - oldTags + newTags + extDelta;
        int newCount = children.Count - (cdChild is null ? 0 : 1) - (ecdChild is null ? 0 : 1) + (newCd is null ? 0 : 1) + (newEcd is null ? 0 : 1);
        long fileDelta = newHeaderSize - headerSize;

        PieceList list = new();
        byte[] objHead = head.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(objHead.AsSpan(16), (ulong)newHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(objHead.AsSpan(24), (uint)newCount);
        list.AddData(objHead);

        bool tagsPlaced = false;
        Child? insertBefore = cdChild is null && ecdChild is null ? children.FirstOrDefault(c => c.Id == AsfGuids.HeaderExtension || c.Id == AsfGuids.CodecList) : null;
        void PlaceTags()
        {
            if (tagsPlaced)
            {
                return;
            }

            tagsPlaced = true;
            long start = list.Length;
            if (newCd is not null)
            {
                list.AddData(Wrap(AsfGuids.ContentDescription, newCd));
            }

            if (newEcd is not null)
            {
                list.AddData(Wrap(AsfGuids.ExtendedContentDescription, newEcd));
            }

            if (list.Length > start || oldTags > 0)
            {
                list.NoteChange("ASF content description", new ByteRange(cdChild?.Offset ?? ecdChild?.Offset ?? insertBefore?.Offset ?? headerSize, oldTags), start);
            }
        }

        foreach (Child c in children)
        {
            if (c == insertBefore)
            {
                PlaceTags();
            }

            if (c == cdChild || c == ecdChild)
            {
                // The new objects take the place of the first of the two that exists.
                if (c == (cdChild ?? ecdChild))
                {
                    PlaceTags();
                }

                continue;
            }

            if (c.Id == AsfGuids.FileProperties && fileDelta != 0 && c.Size >= ObjectHeader + 24)
            {
                byte[] fp = new byte[ObjectHeader + 24];
                src.ReadExactly(c.Offset, fp, ct);
                long oldFileSize = BinaryPrimitives.ReadInt64LittleEndian(fp.AsSpan(40));
                if (oldFileSize == length)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(fp.AsSpan(40), length + fileDelta);
                }

                list.AddData(fp);
                list.AddCopy(c.Offset + fp.Length, c.Size - fp.Length);
                continue;
            }

            if (ext is not null && c == ext && (extDelta != 0 || pad is not null && newPad != pad.Size))
            {
                EmitExtension(src, list, ext, pad, newPad, extDelta, ct);
                continue;
            }

            list.AddCopy(c.Offset, c.Size);
        }

        PlaceTags();
        list.AddCopy(headerSize, length - headerSize);
        long paddingAvail = pad?.Size ?? 0;
        long used = Math.Max(0, Math.Min(delta, paddingAvail));
        EditPlan plan = list.ToPlan(length, used, paddingAvail, 0);
        if (!plan.IsSameLayout)
        {
            // Bytes beyond what the padding could take.
            plan = list.ToPlan(length, used, paddingAvail, Math.Max(1, delta - Math.Max(0, paddingAvail - ObjectHeader)));
        }

        return new TagWritePlan(plan, new TagWriteReport([rep with { Conversions = conv, Created = cdChild is null && ecdChild is null }], notes));
    }

    // The Header Extension's children start after its 46-byte header: a reserved GUID, a reserved word and a 32-bit data size.
    private static Child? FindPadding(IMediaSource src, Child ext, MediaLimits limits, CancellationToken ct)
    {
        byte[] h = new byte[ObjectHeader];
        int n = 0;
        for (long at = ext.Offset + HeaderExtensionHeader; at + ObjectHeader <= ext.Offset + ext.Size;)
        {
            limits.CheckElementCount(++n, at);
            if (src.Read(at, h, ct) < ObjectHeader)
            {
                return null;
            }

            long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(16)), long.MaxValue);
            if (size < ObjectHeader || at + size > ext.Offset + ext.Size)
            {
                return null;
            }

            if (new Guid(h.AsSpan(0, 16)) == AsfGuids.Padding)
            {
                return new Child(AsfGuids.Padding, at, size);
            }

            at += size;
        }

        return null;
    }

    private static void EmitExtension(IMediaSource src, PieceList list, Child ext, Child? pad, long newPad, long extDelta, CancellationToken ct)
    {
        byte[] h = new byte[HeaderExtensionHeader];
        src.ReadExactly(ext.Offset, h, ct);
        long newSize = ext.Size + extDelta;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(16), (ulong)newSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), (uint)(newSize - HeaderExtensionHeader));
        list.AddData(h);
        long extEnd = ext.Offset + ext.Size;
        if (pad is null)
        {
            list.AddCopy(ext.Offset + HeaderExtensionHeader, extEnd - ext.Offset - HeaderExtensionHeader);
            AddPaddingObject(list, newPad);
            return;
        }

        list.AddCopy(ext.Offset + HeaderExtensionHeader, pad.Offset - ext.Offset - HeaderExtensionHeader);
        if (newPad > 0)
        {
            AddPaddingObject(list, newPad);
        }

        list.AddCopy(pad.Offset + pad.Size, extEnd - pad.Offset - pad.Size);
    }

    // The padding object: its header, then zeros that are generated when the file is written.
    private static void AddPaddingObject(PieceList list, long size)
    {
        byte[] b = new byte[ObjectHeader];
        AsfGuids.Padding.TryWriteBytes(b);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), (ulong)size);
        list.AddPadding(b, size - ObjectHeader);
    }

    private static byte[] Wrap(Guid id, byte[] body)
    {
        byte[] b = new byte[ObjectHeader + body.Length];
        id.TryWriteBytes(b);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), (ulong)b.Length);
        body.CopyTo(b, ObjectHeader);
        return b;
    }

    private static string[] ReadContent(IMediaSource src, Child c, MediaLimits limits, CancellationToken ct)
    {
        string[] values = new string[5];
        if (c.Size < ObjectHeader + 10)
        {
            return values;
        }

        limits.CheckAllocation(c.Size, c.Offset);
        byte[] b = new byte[c.Size - ObjectHeader];
        src.ReadExactly(c.Offset + ObjectHeader, b, ct);
        int at = 10;
        for (int i = 0; i < 5; i++)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2));
            if (at + n > b.Length)
            {
                break;
            }

            values[i] = Encoding.Unicode.GetString(b, at, n).TrimEnd('\0');
            at += n;
        }

        return values;
    }

    private static List<Attribute> ReadExtended(IMediaSource src, Child c, MediaLimits limits, CancellationToken ct)
    {
        List<Attribute> list = [];
        if (c.Size < ObjectHeader + 2)
        {
            return list;
        }

        limits.CheckAllocation(c.Size, c.Offset);
        byte[] b = new byte[c.Size - ObjectHeader];
        src.ReadExactly(c.Offset + ObjectHeader, b, ct);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b);
        int at = 2;
        for (int i = 0; i < count; i++)
        {
            if (at + 2 > b.Length)
            {
                throw new UnsupportedMediaException("The ASF Extended Content Description is damaged; tags cannot be written safely.");
            }

            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            if (at + 2 + nameLen + 4 > b.Length)
            {
                throw new UnsupportedMediaException("The ASF Extended Content Description is damaged; tags cannot be written safely.");
            }

            string name = Encoding.Unicode.GetString(b, at + 2, nameLen).TrimEnd('\0');
            int type = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 2 + nameLen));
            int valueLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 4 + nameLen));
            int valueAt = at + 6 + nameLen;
            if (valueAt + valueLen > b.Length)
            {
                throw new UnsupportedMediaException("The ASF Extended Content Description is damaged; tags cannot be written safely.");
            }

            list.Add(new Attribute(name, type, b.AsSpan(valueAt, valueLen).ToArray()));
            at = valueAt + valueLen;
        }

        return list;
    }

    private static string? ExistingText(string[] content, List<Attribute> attributes, string key)
    {
        int ci = Array.IndexOf(ContentKeys, key);
        if (ci >= 0)
        {
            return content[ci];
        }

        Attribute? a = attributes.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
        return a is null ? null : a.Type == TypeUnicode ? Encoding.Unicode.GetString(a.Value).TrimEnd('\0') : a.Type == TypeDword && a.Value.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(a.Value).ToString(CultureInfo.InvariantCulture) : null;
    }

    private static void Apply(KeyOp op, string[] content, List<Attribute> attributes, List<string> conv)
    {
        foreach (string k in op.Aliases.Prepend(op.Key).Where(k => op.Values is null || k == op.Key))
        {
            if (op.Values is null)
            {
                int i = Array.IndexOf(ContentKeys, k);
                if (i >= 0)
                {
                    content[i] = "";
                }

                attributes.RemoveAll(a => string.Equals(a.Name, k, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (op.Values is null)
        {
            return;
        }

        int ci = Array.IndexOf(ContentKeys, op.Key);
        if (ci >= 0)
        {
            if (op.Values.Count > 1)
            {
                conv.Add($"{op.Key}: {op.Values.Count} values joined with '; ' (the Content Description holds one string)");
            }

            content[ci] = string.Join("; ", op.Values);
            // The extended description may repeat a Content Description field under its own name (writers such as ffmpeg do, and WM/Author is the
            // same attribute as the author); a stale copy would contradict the new text.
            attributes.RemoveAll(a => string.Equals(a.Name, op.Key, StringComparison.OrdinalIgnoreCase) || (ci == 1 && string.Equals(a.Name, "WM/Author", StringComparison.OrdinalIgnoreCase)));

            return;
        }

        attributes.RemoveAll(a => string.Equals(a.Name, op.Key, StringComparison.OrdinalIgnoreCase));
        foreach (string v in op.Values)
        {
            if (op.Key == "WM/SharedUserRating" && uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out uint rating))
            {
                byte[] d = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(d, rating);
                attributes.Add(new Attribute(op.Key, TypeDword, d));
                continue;
            }

            attributes.Add(new Attribute(op.Key, TypeUnicode, Encoding.Unicode.GetBytes(v + "\0")));
        }
    }

    private static void ApplyPictures(TagEdits edits, List<Attribute> attributes, MediaLimits limits, List<string> notes)
    {
        foreach (TagEdit e in edits.PictureEdits)
        {
            if (e.Kind == TagEditKind.RemovePictures)
            {
                attributes.RemoveAll(a => a.Name == "WM/Picture" && (e.PictureRole is null || PictureRole(a.Value) == (int)e.PictureRole));
                continue;
            }

            limits.CheckAttachment(e.PictureData.Length, 0);
            byte[] mime = Encoding.Unicode.GetBytes((e.MimeType ?? "image/jpeg") + "\0");
            byte[] desc = Encoding.Unicode.GetBytes(e.Description + "\0");
            long total = 5L + mime.Length + desc.Length + e.PictureData.Length;
            if (total > MaxValue)
            {
                notes.Add($"asf: a picture of {e.PictureData.Length} bytes was not written; the Extended Content Description holds at most {MaxValue} bytes per value");
                continue;
            }

            byte[] v = new byte[total];
            v[0] = (byte)(e.PictureRole ?? PictureType.FrontCover);
            BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(1), (uint)e.PictureData.Length);
            mime.CopyTo(v, 5);
            desc.CopyTo(v, 5 + mime.Length);
            e.PictureData.CopyTo(v.AsMemory(5 + mime.Length + desc.Length));
            attributes.RemoveAll(a => a.Name == "WM/Picture" && PictureRole(a.Value) == v[0]);
            attributes.Add(new Attribute("WM/Picture", TypeBytes, v));
        }
    }

    private static int PictureRole(byte[] value) => value.Length > 0 ? value[0] : -1;

    private static byte[] BuildContent(string[] content)
    {
        byte[][] enc = content.Select(s => string.IsNullOrEmpty(s) ? [] : Encoding.Unicode.GetBytes(s + "\0")).ToArray();
        byte[] b = new byte[10 + enc.Sum(e => e.Length)];
        int at = 10;
        for (int i = 0; i < 5; i++)
        {
            if (enc[i].Length > MaxValue)
            {
                throw new UnsupportedMediaException($"The {ContentKeys[i]} text is longer than the 65,535 bytes the ASF Content Description allows.");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)enc[i].Length);
            enc[i].CopyTo(b, at);
            at += enc[i].Length;
        }

        return b;
    }

    private static byte[] BuildExtended(List<Attribute> attributes)
    {
        if (attributes.Count > ushort.MaxValue)
        {
            throw new UnsupportedMediaException("The ASF Extended Content Description holds at most 65,535 attributes.");
        }

        using MemoryStream ms = new();
        Span<byte> w = stackalloc byte[2]; // one 16-bit field at a time
        BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)attributes.Count);
        ms.Write(w);
        foreach (Attribute a in attributes)
        {
            byte[] name = Encoding.Unicode.GetBytes(a.Name + "\0");
            if (name.Length > MaxValue || a.Value.Length > MaxValue)
            {
                throw new UnsupportedMediaException($"The attribute '{a.Name}' is too long for the ASF Extended Content Description (65,535 bytes).");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)name.Length);
            ms.Write(w);
            ms.Write(name);
            BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)a.Type);
            ms.Write(w);
            BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)a.Value.Length);
            ms.Write(w);
            ms.Write(a.Value);
        }

        return ms.ToArray();
    }
}

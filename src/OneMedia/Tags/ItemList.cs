using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>A group of native tags that sit together in the file: one <c>udta</c>, one <c>ilst</c>, one XMP packet.</summary>
public interface ITagContainer
{
    /// <summary>The tag system, one of <see cref="TagSystems"/>.</summary>
    string System { get; }

    /// <summary>The track the container belongs to, or 0 for the whole movie.</summary>
    long TrackId { get; }

    /// <summary>The box that holds the group, header included; a writer replaces or edits this range.</summary>
    ByteRange Range { get; }

    /// <summary>The tags in file order.</summary>
    IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>
/// Reads the items of an <c>ilst</c> box. The same layout serves iTunes metadata (item type is a four-character code) and QuickTime
/// metadata with a key table (item type is a 1-based index into the keys).
/// </summary>
internal static class ItemListParser
{
    private const int EagerValueBytes = 64 * 1024;

    /// <param name="ilst">The item list box.</param>
    /// <param name="io">Where values are read from later.</param>
    /// <param name="system">The system name given to the tags.</param>
    /// <param name="trackId">The track (0 for the movie).</param>
    /// <param name="keys">For key-table metadata, the key strings (index 0 is key number 1); otherwise <c>null</c>.</param>
    /// <param name="into">Receives the tags.</param>
    public static void Parse(BoxNode ilst, TagIo io, string system, long trackId, IReadOnlyList<string>? keys, List<NativeTag> into)
    {
        foreach (BoxNode item in ilst.EnumerateChildren())
        {
            string key = ItemKey(item, io, keys);
            string? mean = null;
            string? name = null;
            bool any = false;
            ByteRange itemRange = new(item.Offset, item.Header.HeaderSize + item.PayloadSize);
            foreach (BoxNode child in item.EnumerateChildren())
            {
                if (child.TypeName == "mean" || child.TypeName == "name")
                {
                    string? text = ReadFullBoxText(child, io);
                    if (child.TypeName == "mean")
                    {
                        mean = text;
                    }
                    else
                    {
                        name = text;
                    }
                }
                else if (child.TypeName == "data")
                {
                    if (keys is null && item.TypeName == "----")
                    {
                        key = $"----:{mean}:{name}";
                    }

                    any = true;
                    ParseData(child, io, system, key, trackId, itemRange, into);
                }
            }

            if (!any)
            {
                io.Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, item.Offset, $"The metadata item '{key}' has no data box.", "tags.item-no-data");
            }
        }
    }

    private static string ItemKey(BoxNode item, TagIo io, IReadOnlyList<string>? keys)
    {
        if (keys is null)
        {
            return item.TypeName;
        }

        // QuickTime File Format, metadata: the item's type is the 1-based index of its key in the keys box.
        uint index = item.Type;
        if (index == 0 || index > keys.Count)
        {
            io.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, item.Offset, $"The metadata item refers to key {index}, but the key table has {keys.Count} entries.", "tags.keys-index");
            return "#" + index;
        }

        return keys[(int)index - 1];
    }

    private static string? ReadFullBoxText(BoxNode box, TagIo io)
    {
        if (box.PayloadSize < 4 || box.PayloadSize > 1024)
        {
            io.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, box.Offset, $"The '{box.TypeName}' box of a freeform item has an unusable size.", "tags.freeform-size");
            return null;
        }

        byte[] p = box.ReadPayload(1024);
        return p.Length < 4 ? null : TextDecoder.Decode(p.AsSpan(4), TagTextEncoding.Utf8, box.Header.PayloadOffset + 4, io.Defects);
    }

    private static void ParseData(BoxNode data, TagIo io, string system, string key, long trackId, ByteRange itemRange, List<NativeTag> into)
    {
        if (data.PayloadSize < 8)
        {
            io.Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, data.Offset, $"The data box of '{key}' is shorter than its type and locale fields.", "tags.data-short");
            return;
        }

        // Apple's metadata format: one byte of type set (0 = well-known types), a 24-bit type code, then a 32-bit locale.
        byte[] head = data.ReadPayload(8);
        if (head.Length < 8)
        {
            return;
        }

        uint typeSet = head[0];
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head) & 0xFFFFFF;
        uint locale = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (typeSet != 0)
        {
            io.Defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, data.Offset, $"The data box of '{key}' uses type set {typeSet}; its type code is kept as is.", "tags.type-set");
        }

        string? language = (locale & 0xFFFF) is var lang and not 0 ? LanguageCodes.Decode((ushort)lang) : null;
        ByteRange value = new(data.Header.PayloadOffset + 8, data.PayloadSize - 8);
        DataTypes.Describe(type, value.Length, out TagValueKind kind, out TagTextEncoding encoding, out string? mime);
        NativeTag tag = new(io, system, key, trackId, kind, type, locale, language, itemRange, value)
        {
            Encoding = encoding,
            MimeType = mime,
        };

        if (kind is TagValueKind.Text or TagValueKind.Number or TagValueKind.Real && value.Length <= EagerValueBytes)
        {
            byte[] bytes = io.Read(value);
            if (kind == TagValueKind.Text)
            {
                tag.SetText(TextDecoder.Decode(bytes, encoding, value.Offset, io.Defects));
            }
            else
            {
                DataTypes.DecodeNumber(type, bytes, tag);
            }
        }
        else if (kind == TagValueKind.Binary && type is 21 or 22 or 23 or 24 or 65 or 66 or 67 or 74 or 75 or 76 or 77 or 78)
        {
            io.Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, value.Offset, $"The numeric value of '{key}' has an impossible length.", "tags.int-length");
        }

        if (type is not (0 or 1 or 2 or 4 or 5 or 13 or 14 or 21 or 22 or 23 or 24 or 27 or 65 or 66 or 67 or 74 or 75 or 76 or 77 or 78 or 12 or 3))
        {
            io.Defects.Add(DefectKind.Unsupported, DefectSeverity.Info, data.Offset, $"The data box of '{key}' has type code {type}, which is kept as binary.", "tags.data-type");
        }

        into.Add(tag);
    }
}

/// <summary>The well-known data type codes of Apple's metadata formats (QuickTime File Format, "Well-known types").</summary>
internal static class DataTypes
{
    public static void Describe(uint type, long length, out TagValueKind kind, out TagTextEncoding encoding, out string? mime)
    {
        kind = TagValueKind.Binary;
        encoding = TagTextEncoding.Utf8;
        mime = null;
        switch (type)
        {
            case 1 or 4:
                kind = TagValueKind.Text;
                break;
            case 2 or 5:
                kind = TagValueKind.Text;
                encoding = TagTextEncoding.Utf16BigEndian;
                break;
            case 12 or 13 or 14 or 27:
                kind = TagValueKind.Image;
                mime = type switch { 12 => "image/gif", 13 => "image/jpeg", 14 => "image/png", _ => "image/bmp" };
                break;
            case 21 or 22 or 65 or 66 or 67 or 74 or 75 or 76 or 77 or 78:
                kind = IntegerLengthOk(length) ? TagValueKind.Number : TagValueKind.Binary;
                break;
            case 23 when length == 4:
            case 24 when length == 8:
                kind = TagValueKind.Real;
                break;
        }
    }

    private static bool IntegerLengthOk(long length) => length is 1 or 2 or 3 or 4 or 8;

    public static void DecodeNumber(uint type, ReadOnlySpan<byte> v, NativeTag tag)
    {
        if (tag.Kind == TagValueKind.Real)
        {
            tag.RealValue = v.Length == 4 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(v)) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(v));
            return;
        }

        bool signed = type is 21 or 65 or 66 or 67 or 74;
        ulong u = 0;
        foreach (byte b in v)
        {
            u = (u << 8) | b;
        }

        long result;
        if (signed)
        {
            int bits = v.Length * 8;
            result = bits == 64 ? unchecked((long)u) : (unchecked((long)u) << (64 - bits)) >> (64 - bits);
        }
        else if (u > long.MaxValue)
        {
            // An unsigned 64-bit value above 2^63-1 does not fit; keep the bytes and drop the number.
            return;
        }
        else
        {
            result = (long)u;
        }

        tag.Number = result;
    }
}

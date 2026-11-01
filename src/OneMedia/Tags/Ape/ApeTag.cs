using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tags.Ape;

/// <summary>The global flags of an APE tag header or footer (APEv2 specification, "Ape Tags Flags").</summary>
[Flags]
public enum ApeTagAttributes : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The tag is read-only.</summary>
    ReadOnly = 1,

    /// <summary>The tag has a header.</summary>
    HasHeader = 0x80000000,

    /// <summary>The tag has no footer (bit 30 is set when the footer is absent).</summary>
    NoFooter = 0x40000000,

    /// <summary>This block is the header, not the footer.</summary>
    IsHeader = 0x20000000,
}

/// <summary>How an APE item's value is to be read (item flag bits 1 and 2).</summary>
public enum ApeItemType
{
    /// <summary>UTF-8 text; several values are separated by a zero byte.</summary>
    Text = 0,

    /// <summary>Binary data.</summary>
    Binary = 1,

    /// <summary>A locator (URL or path) of externally stored information, as UTF-8.</summary>
    Locator = 2,

    /// <summary>The reserved value 3.</summary>
    Reserved = 3,
}

/// <summary>
/// An APEv1 or APEv2 tag (APEv2 specification on the Hydrogenaudio knowledge base): an optional 32-byte header, items, and a 32-byte footer.
/// Items keep the key as stored, their flags and their byte ranges; text items are decoded as the tag is read, binary items (cover art) only
/// on demand.
/// </summary>
public sealed class ApeTag : ITagContainer
{
    /// <summary>The length of the header and of the footer.</summary>
    public const int BlockSize = 32;

    private const int MaxKeyBytes = 255;
    private readonly List<NativeTag> _tags = [];
    private readonly List<ApeItem> _items = [];

    private ApeTag(ByteRange range) => Range = range;

    /// <inheritdoc />
    public string System => TagSystems.Ape;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <summary>The whole tag in the file: header (when there is one), items and footer.</summary>
    public ByteRange Range { get; }

    /// <summary>1000 for APEv1, 2000 for APEv2.</summary>
    public int Version { get; private set; }

    /// <summary>The flags of the footer (or of the header when the tag has no footer).</summary>
    public ApeTagAttributes Flags { get; private set; }

    /// <summary>The item count the tag states.</summary>
    public int DeclaredItemCount { get; private set; }

    /// <summary>The items in file order.</summary>
    public IReadOnlyList<ApeItem> Items => _items;

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags => _tags;

    /// <summary>Recognises an APE header or footer.</summary>
    /// <param name="block">At least 32 bytes starting at <c>APETAGEX</c>.</param>
    /// <param name="version">The version number (1000 or 2000).</param>
    /// <param name="size">The tag size: items plus footer, header excluded.</param>
    /// <param name="itemCount">The item count.</param>
    /// <param name="flags">The flags.</param>
    /// <returns>False when the preamble is wrong, the version is not 1000 or 2000, or the size is smaller than a footer.</returns>
    public static bool TryParseBlock(ReadOnlySpan<byte> block, out int version, out long size, out int itemCount, out ApeTagAttributes flags)
    {
        version = 0;
        size = 0;
        itemCount = 0;
        flags = 0;
        if (block.Length < BlockSize || !block.StartsWith("APETAGEX"u8))
        {
            return false;
        }

        version = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        size = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(block[16..]);
        flags = (ApeTagAttributes)BinaryPrimitives.ReadUInt32LittleEndian(block[20..]);
        itemCount = (int)Math.Min(count, int.MaxValue);
        return (version is 1000 or 2000) && size >= BlockSize;
    }

    /// <summary>Reads the tag whose footer starts at <paramref name="footerOffset"/> (the usual place: the end of the file, or just before an ID3v1 tag).</summary>
    /// <param name="source">The source.</param>
    /// <param name="footerOffset">Absolute offset of the footer's <c>APETAGEX</c>.</param>
    /// <param name="tag">The tag when true.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>False when there is no valid footer there or the tag does not fit in front of it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryReadFromFooter(IMediaSource source, long footerOffset, out ApeTag? tag, MediaLimits? limits = null, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        tag = null;
        Span<byte> block = stackalloc byte[BlockSize]; // the fixed footer
        if (footerOffset < 0 || source.Read(footerOffset, block) < BlockSize || !TryParseBlock(block, out int version, out long size, out int count, out ApeTagAttributes flags) || (flags & ApeTagAttributes.IsHeader) != 0)
        {
            return false;
        }

        // APEv2 specification: the tag size counts the items and the footer but not the header.
        long itemsStart = footerOffset + BlockSize - size;
        if (itemsStart < 0)
        {
            return false;
        }

        bool header = false;
        long start = itemsStart;
        if ((flags & ApeTagAttributes.HasHeader) != 0 && itemsStart >= BlockSize)
        {
            Span<byte> h = stackalloc byte[BlockSize]; // the fixed header
            if (source.Read(itemsStart - BlockSize, h) == BlockSize && h.StartsWith("APETAGEX"u8))
            {
                header = true;
                start = itemsStart - BlockSize;
            }
        }

        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        ApeTag t = new(new ByteRange(start, footerOffset + BlockSize - start)) { Version = version, Flags = flags, DeclaredItemCount = count };
        if ((flags & ApeTagAttributes.HasHeader) != 0 && !header)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, itemsStart - BlockSize, "The APE footer announces a header, but it is not there.", "ape.header");
        }

        t.ReadItems(source, itemsStart, footerOffset - itemsStart, limits, defects);
        tag = t;
        return true;
    }

    /// <summary>Reads the tag whose header starts at <paramref name="headerOffset"/> (a tag in front of the audio).</summary>
    /// <param name="source">The source.</param>
    /// <param name="headerOffset">Absolute offset of the header's <c>APETAGEX</c>.</param>
    /// <param name="tag">The tag when true.</param>
    /// <param name="limits">Ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <param name="defects">Where deviations go; a new log if null.</param>
    /// <returns>False when there is no valid header there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static bool TryReadFromHeader(IMediaSource source, long headerOffset, out ApeTag? tag, MediaLimits? limits = null, DefectLog? defects = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        tag = null;
        Span<byte> block = stackalloc byte[BlockSize]; // the fixed header
        if (headerOffset < 0 || source.Read(headerOffset, block) < BlockSize || !TryParseBlock(block, out int version, out long size, out int count, out ApeTagAttributes flags))
        {
            return false;
        }

        bool hasFooter = (flags & ApeTagAttributes.NoFooter) == 0;
        long itemsStart = headerOffset + BlockSize;
        long itemsLength = size - (hasFooter ? BlockSize : 0);
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        if (source.Length >= 0 && itemsStart + size > source.Length)
        {
            defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, headerOffset, "The APE tag is larger than the file.", "ape.size");
            return false;
        }

        ApeTag t = new(new ByteRange(headerOffset, BlockSize + size)) { Version = version, Flags = flags, DeclaredItemCount = count };
        t.ReadItems(source, itemsStart, itemsLength, limits, defects);
        tag = t;
        return true;
    }

    private void ReadItems(IMediaSource source, long start, long length, MediaLimits limits, DefectLog defects)
    {
        TagIo io = new(source, limits, defects);
        TagWindow window = new(source, start, length);
        long p = 0;
        int read = 0;
        while (p + 11 <= length)
        {
            limits.CheckElementCount(read + 1, start + p);
            ReadOnlySpan<byte> h = window.Get(p, 8 + 3);
            if (h.Length < 8 + 3)
            {
                break;
            }

            long valueLength = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint itemFlags = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            // The key is 2 to 255 printable ASCII characters ended by a zero byte.
            ReadOnlySpan<byte> keyArea = window.Get(p + 8, (int)Math.Min(MaxKeyBytes + 1, length - p - 8));
            int z = keyArea.IndexOf((byte)0);
            if (z < 2 || !IsPrintable(keyArea[..z]))
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, start + p + 8, "An APE item key is missing, too short, too long or not printable ASCII; the rest of the tag is not read.", "ape.key");
                return;
            }

            string key = Encoding.ASCII.GetString(keyArea[..z]);
            long valueStart = p + 8 + z + 1;
            if (valueStart + valueLength > length)
            {
                defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, start + p, $"The APE item '{key}' reaches past the end of the tag; it and the items after it are not read.", "ape.item-size");
                return;
            }

            ApeItemType type = (ApeItemType)((itemFlags >> 1) & 3);
            ByteRange itemRange = new(start + p, valueStart + valueLength - p);
            ByteRange valueRange = new(start + valueStart, valueLength);
            NativeTag native = MakeTag(io, window, key, type, valueStart, valueLength, itemRange, valueRange);
            _items.Add(new ApeItem(key, type, (itemFlags & 1) != 0, itemRange, valueRange, native));
            _tags.Add(native);
            p = valueStart + valueLength;
            read++;
        }

        if (read < DeclaredItemCount)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, start + p, $"The APE tag declares {DeclaredItemCount} items, but {read} were found.", "ape.item-count");
        }
        else if (p < length)
        {
            defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, start + p, "Bytes remain in the APE tag after its last declared item.", "ape.trailing");
        }
    }

    private static NativeTag MakeTag(TagIo io, TagWindow window, string key, ApeItemType type, long valueStart, long valueLength, ByteRange itemRange, ByteRange valueRange)
    {
        // Cover art is stored as a binary item whose value is a file name, a zero byte, then the image.
        if (type == ApeItemType.Binary && key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> head = window.Get(valueStart, (int)Math.Min(valueLength, 512));
            int z = head.IndexOf((byte)0);
            if (z >= 0)
            {
                long imageStart = valueStart + z + 1;
                ReadOnlySpan<byte> magic = window.Get(imageStart, 8);
                string mime = magic.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg" : magic.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ? "image/png" : magic.StartsWith("GIF8"u8) ? "image/gif" : magic.StartsWith("BM"u8) ? "image/bmp" : "application/octet-stream";
                string name = Encoding.UTF8.GetString(head[..z]);
                return new NativeTag(io, TagSystems.Ape, key, 0, TagValueKind.Image, 0, 0, null, itemRange, new ByteRange(valueRange.Offset + z + 1, valueLength - z - 1))
                {
                    MimeType = mime,
                    Description = name,
                    PictureRole = key.Contains("Front", StringComparison.OrdinalIgnoreCase) ? PictureType.FrontCover : key.Contains("Back", StringComparison.OrdinalIgnoreCase) ? PictureType.BackCover : PictureType.Other,
                };
            }
        }

        if (type is ApeItemType.Text or ApeItemType.Locator)
        {
            if (valueLength <= 64 * 1024)
            {
                ReadOnlySpan<byte> v = window.Get(valueStart, (int)valueLength);
                string text = Encoding.UTF8.GetString(v).TrimEnd('\0').Replace('\0', ';');
                if (!global::System.Text.Unicode.Utf8.IsValid(v))
                {
                    io.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, valueRange.Offset, $"The text of the APE item '{key}' is not valid UTF-8; invalid sequences are replaced.", "ape.utf8");
                }

                NativeTag t = new(io, TagSystems.Ape, key, 0, TagValueKind.Text, 1, 0, null, itemRange, valueRange);
                t.SetText(text);
                return t;
            }

            return new NativeTag(io, TagSystems.Ape, key, 0, TagValueKind.Text, 1, 0, null, itemRange, valueRange);
        }

        return new NativeTag(io, TagSystems.Ape, key, 0, TagValueKind.Binary, 0, 0, null, itemRange, valueRange);
    }

    private static bool IsPrintable(ReadOnlySpan<byte> key)
    {
        foreach (byte b in key)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One item of an APE tag.</summary>
/// <param name="Key">The key as stored (APE keys are case-insensitive for readers, case-preserving in the file).</param>
/// <param name="Type">How the value is coded.</param>
/// <param name="ReadOnly">True when the item is marked read-only.</param>
/// <param name="Range">The item in the file: lengths, flags, key and value.</param>
/// <param name="ValueRange">The value bytes.</param>
/// <param name="Native">The native tag for the item.</param>
public sealed record ApeItem(string Key, ApeItemType Type, bool ReadOnly, ByteRange Range, ByteRange ValueRange, NativeTag Native);
